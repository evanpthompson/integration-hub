// Command synthetic is a deterministic stand-in upstream for end-to-end testing.
//
// One dataset, several protocol adapters, each independently switchable:
//
//	-rest     REST API on the HTTP port
//	-graphql  GraphQL (real schema, introspection works) on the HTTP port
//	-grpc     a stand-in for the Worker service, on the gRPC port
//
// Every adapter shares the same generated data and the same fault injector, so a
// test can prove the orchestrator behaves identically no matter which protocol an
// upstream happens to speak.
//
// It deliberately does NOT emulate Postgres. See README.md.
package main

import (
	"context"
	"errors"
	"flag"
	"fmt"
	"log/slog"
	"net"
	"net/http"
	"os"
	"os/signal"
	"syscall"
	"time"

	"google.golang.org/grpc"
	"google.golang.org/grpc/reflection"

	pb "github.com/evanpthompson/integration-hub/synthetic/workerv1"
)

func main() {
	var (
		httpAddr   = flag.String("http", envOr("SYNTH_HTTP_ADDR", ":8080"), "HTTP listen address")
		grpcAddr   = flag.String("grpc-addr", envOr("SYNTH_GRPC_ADDR", ":50052"), "gRPC listen address")
		seed       = flag.Uint64("seed", 42, "PRNG seed — same seed, same data, every run")
		orderCount = flag.Int("orders", 250, "how many synthetic orders to generate")
		enableREST = flag.Bool("rest", true, "serve the REST adapter")
		enableGQL  = flag.Bool("graphql", true, "serve the GraphQL adapter")
		enableGRPC = flag.Bool("grpc", true, "serve the Worker gRPC stand-in")
	)
	flag.Parse()

	log := slog.New(slog.NewJSONHandler(os.Stdout, nil))

	data := NewDataset(*seed, *orderCount)
	faults := NewFaults()
	log.Info("dataset generated", "seed", *seed, "orders", len(data.Orders),
		"stations", len(data.Snapshots))

	root := http.NewServeMux()
	root.Handle("/_synth/", faults.AdminHandler())
	root.HandleFunc("GET /healthz", func(w http.ResponseWriter, _ *http.Request) {
		writeJSON(w, http.StatusOK, map[string]any{"status": "ok"})
	})

	if *enableREST {
		// Faults wrap only the data paths — /healthz and /_synth must stay reachable
		// while a fault is armed, or you cannot turn one off again.
		root.Handle("/v1/", faults.Middleware(RESTHandler(data)))
		log.Info("REST adapter enabled", "prefix", "/v1")
	}

	if *enableGQL {
		schema, err := buildSchema(data)
		if err != nil {
			log.Error("graphql schema failed to build", "err", err)
			os.Exit(1)
		}
		root.Handle("/graphql", GraphQLHandler(data, faults, schema))
		log.Info("GraphQL adapter enabled", "path", "/graphql", "introspection", true)
	}

	ctx, stop := signal.NotifyContext(context.Background(), syscall.SIGINT, syscall.SIGTERM)
	defer stop()

	httpSrv := &http.Server{
		Addr:              *httpAddr,
		Handler:           root,
		ReadHeaderTimeout: 5 * time.Second,
	}

	go func() {
		log.Info("http listening", "addr", *httpAddr)
		if err := httpSrv.ListenAndServe(); err != nil && !errors.Is(err, http.ErrServerClosed) {
			log.Error("http server failed", "err", err)
			stop()
		}
	}()

	var grpcSrv *grpc.Server
	if *enableGRPC {
		listener, err := net.Listen("tcp", *grpcAddr)
		if err != nil {
			log.Error("grpc listen failed", "addr", *grpcAddr, "err", err)
			os.Exit(1)
		}
		grpcSrv = grpc.NewServer()
		pb.RegisterWorkerServer(grpcSrv, NewWorkerStandIn(data, faults))
		// Reflection so grpcurl works without hunting for the .proto.
		reflection.Register(grpcSrv)

		go func() {
			log.Info("grpc listening", "addr", *grpcAddr)
			if err := grpcSrv.Serve(listener); err != nil {
				log.Error("grpc server failed", "err", err)
				stop()
			}
		}()
	}

	<-ctx.Done()
	log.Info("draining")

	shutdownCtx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	if err := httpSrv.Shutdown(shutdownCtx); err != nil {
		log.Warn("http shutdown", "err", err)
	}
	if grpcSrv != nil {
		grpcSrv.GracefulStop()
	}
	fmt.Fprintln(os.Stderr, "synthetic: stopped")
}

func envOr(key, fallback string) string {
	if v := os.Getenv(key); v != "" {
		return v
	}
	return fallback
}
