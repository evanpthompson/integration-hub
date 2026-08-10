package main

import (
	"context"
	"encoding/json"
	"fmt"
	"strconv"
	"time"

	"google.golang.org/grpc/metadata"

	pb "github.com/evanpthompson/integration-hub/synthetic/workerv1"
)

// A stand-in for the real Python worker, speaking the same proto.
//
// Why this exists: it lets the orchestrator's retry pipeline, circuit breaker and
// run-history writes be exercised without Python, without a network upstream, and
// at speeds real HTTP cannot reach — which matters for the throughput numbers in
// Phase 4. It is a load and failure harness, not a second implementation.
//
// ponytail: it does NOT evaluate JMESPath. Duplicating the transform in a second
// language is exactly the kind of drift that makes a mock lie. It returns
// pre-canonical records and leaves transforms to the real worker.

type WorkerStandIn struct {
	pb.UnimplementedWorkerServer
	data   *Dataset
	faults *Faults
}

func NewWorkerStandIn(d *Dataset, f *Faults) *WorkerStandIn {
	return &WorkerStandIn{data: d, faults: f}
}

func (s *WorkerStandIn) Invoke(ctx context.Context, req *pb.InvokeRequest) (*pb.InvokeResponse, error) {
	started := time.Now()
	d := directiveFromMetadata(ctx)

	if d.DelayMs > 0 {
		select {
		case <-time.After(time.Duration(d.DelayMs) * time.Millisecond):
		case <-ctx.Done():
			return nil, ctx.Err()
		}
	}

	// Key on the logical call, not the run id — the orchestrator issues a fresh
	// run id per attempt-set but the same integration/resource, which is what
	// "fail twice then succeed" needs to count.
	key := req.GetIntegrationId() + "/" + req.GetResource()
	if fail, status := s.faults.shouldFail(d, key); fail {
		code, retryable := classifyStatus(status)
		return &pb.InvokeResponse{
			Ok:                 false,
			UpstreamStatus:     int32(status),
			UpstreamDurationMs: int32(time.Since(started).Milliseconds()),
			ErrorCode:          code,
			ErrorMessage:       fmt.Sprintf("synthetic worker fault: %d", status),
			Retryable:          retryable,
		}, nil
	}

	count := 1
	if req.GetEmit() == pb.Emit_LIST {
		count = 10
	}
	if n := intFromMetadata(ctx, "x-synth-count"); n > 0 {
		count = n
	}

	records := make([]map[string]any, count)
	page, _ := s.data.Page(count, 0)
	for i := range records {
		if i < len(page) {
			o := page[i]
			records[i] = map[string]any{
				"id":        o.ID,
				"reference": o.Reference,
				"status":    o.Status,
				"totalCents": o.Total.Cents,
				"updatedAt": o.PlacedAt,
			}
			continue
		}
		records[i] = map[string]any{"id": fmt.Sprintf("synthetic-%04d", i)}
	}

	payload, err := json.Marshal(records)
	if err != nil {
		return &pb.InvokeResponse{
			Ok: false, ErrorCode: "TRANSFORM", ErrorMessage: err.Error(), Retryable: false,
		}, nil
	}

	return &pb.InvokeResponse{
		Ok:                 true,
		UpstreamStatus:     200,
		RecordsJson:        payload,
		Count:              int32(len(records)),
		UpstreamDurationMs: int32(time.Since(started).Milliseconds()),
	}, nil
}

// classifyStatus mirrors the real worker's table (worker/core.py). Kept in sync by
// the shared e2e assertions rather than by shared code — two languages, one rule.
func classifyStatus(status int) (string, bool) {
	switch {
	case status == 429:
		return "RATE_LIMITED", true
	case status >= 500:
		return "UPSTREAM_5XX", true
	case status == 408 || status == 425:
		return "UPSTREAM_4XX", true
	default:
		return "UPSTREAM_4XX", false
	}
}

func directiveFromMetadata(ctx context.Context) Directive {
	md, ok := metadata.FromIncomingContext(ctx)
	if !ok {
		return Directive{}
	}
	get := func(k string) string {
		if v := md.Get(k); len(v) > 0 {
			return v[0]
		}
		return ""
	}
	atoi := func(k string) int {
		n, _ := strconv.Atoi(get(k))
		return n
	}
	return Directive{
		Status:    atoi("x-synth-status"),
		DelayMs:   atoi("x-synth-delay-ms"),
		FailTimes: atoi("x-synth-fail-times"),
		Key:       get("x-synth-key"),
	}
}

func intFromMetadata(ctx context.Context, key string) int {
	md, ok := metadata.FromIncomingContext(ctx)
	if !ok {
		return 0
	}
	if v := md.Get(key); len(v) > 0 {
		n, _ := strconv.Atoi(v[0])
		return n
	}
	return 0
}
