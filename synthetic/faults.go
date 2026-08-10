package main

import (
	"encoding/json"
	"net/http"
	"strconv"
	"sync"
	"time"
)

// Fault injection is the reason this service exists. Hitting a real upstream proves
// the happy path; nothing proves the retry pipeline, the circuit breaker, or the
// error-classification table except an upstream you can make fail on demand.
//
// Two ways in, deliberately:
//
//   Headers  — stateless per request, so parallel tests never interfere. The
//              orchestrator forwards a manifest's spec.defaults.headers upstream,
//              so a test can arm a fault purely by writing YAML.
//   Rule     — one global override set via POST /_synth/faults, for demos and for
//              callers that cannot control headers.
//
// ponytail: one global rule, not a rule engine. A list of path-matched rules with
// priorities is a config language nobody asked for. Add it when a single rule
// genuinely cannot express a test.

const (
	hdrStatus    = "X-Synth-Status"
	hdrDelayMs   = "X-Synth-Delay-Ms"
	hdrFailTimes = "X-Synth-Fail-Times"
	hdrKey       = "X-Synth-Key"
	hdrBody      = "X-Synth-Body"
	hdrGraphQL   = "X-Synth-Graphql-Error"
)

type Directive struct {
	Status       int    `json:"status"`
	DelayMs      int    `json:"delayMs"`
	FailTimes    int    `json:"failTimes"`
	Key          string `json:"key"`
	Body         string `json:"body"`         // "notjson" | "empty" | ""
	GraphQLError string `json:"graphqlError"` // e.g. RATE_LIMITED
}

func (d Directive) empty() bool { return d == Directive{} }

func directiveFromHeaders(h http.Header) Directive {
	atoi := func(name string) int {
		n, err := strconv.Atoi(h.Get(name))
		if err != nil {
			return 0
		}
		return n
	}
	return Directive{
		Status:       atoi(hdrStatus),
		DelayMs:      atoi(hdrDelayMs),
		FailTimes:    atoi(hdrFailTimes),
		Key:          h.Get(hdrKey),
		Body:         h.Get(hdrBody),
		GraphQLError: h.Get(hdrGraphQL),
	}
}

type Faults struct {
	mu       sync.Mutex
	counters map[string]int
	rule     Directive
}

func NewFaults() *Faults { return &Faults{counters: map[string]int{}} }

func (f *Faults) SetRule(d Directive) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.rule = d
}

func (f *Faults) Reset() {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.counters = map[string]int{}
	f.rule = Directive{}
}

// resolve merges the header directive over the standing rule.
func (f *Faults) resolve(h http.Header) Directive {
	d := directiveFromHeaders(h)
	if !d.empty() {
		return d
	}
	f.mu.Lock()
	defer f.mu.Unlock()
	return f.rule
}

// shouldFail answers whether THIS request fails, and consumes a fail-times budget.
//
// The counter is keyed and server-side because the client cannot vary its headers
// between retry attempts — the orchestrator sends the same manifest headers every
// time. "Fail twice then succeed" is the single most useful behaviour here: it is
// exactly the assertion behind RETRIED_SUCCESS.
func (f *Faults) shouldFail(d Directive, fallbackKey string) (bool, int) {
	status := d.Status
	if status == 0 {
		status = http.StatusServiceUnavailable
	}

	if d.FailTimes > 0 {
		key := d.Key
		if key == "" {
			key = fallbackKey
		}
		f.mu.Lock()
		f.counters[key]++
		n := f.counters[key]
		f.mu.Unlock()
		return n <= d.FailTimes, status
	}

	return d.Status >= 400, status
}

// Middleware applies delay, status and body faults to any HTTP handler.
// GraphQL error injection is not handled here — it has to happen inside a 200
// response body, so the GraphQL handler consults the directive itself.
func (f *Faults) Middleware(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		d := f.resolve(r.Header)

		if d.DelayMs > 0 {
			select {
			case <-time.After(time.Duration(d.DelayMs) * time.Millisecond):
			case <-r.Context().Done():
				// The caller already gave up — this is the timeout path under test.
				return
			}
		}

		if fail, status := f.shouldFail(d, r.URL.Path); fail {
			w.Header().Set("Content-Type", "application/json")
			if status == http.StatusTooManyRequests {
				w.Header().Set("Retry-After", "1")
			}
			w.WriteHeader(status)
			_ = json.NewEncoder(w).Encode(map[string]any{
				"error":   http.StatusText(status),
				"message": "synthetic fault injected",
				"path":    r.URL.Path,
			})
			return
		}

		switch d.Body {
		case "notjson":
			// A 200 that is not JSON — the "upstream lied about its content type"
			// path, which must be classified as non-retryable.
			w.Header().Set("Content-Type", "application/json")
			w.WriteHeader(http.StatusOK)
			_, _ = w.Write([]byte("<html><body>definitely not json</body></html>"))
			return
		case "empty":
			w.Header().Set("Content-Type", "application/json")
			w.WriteHeader(http.StatusOK)
			return
		}

		next.ServeHTTP(w, r)
	})
}

func (f *Faults) AdminHandler() http.Handler {
	mux := http.NewServeMux()

	mux.HandleFunc("POST /_synth/faults", func(w http.ResponseWriter, r *http.Request) {
		var d Directive
		if err := json.NewDecoder(r.Body).Decode(&d); err != nil {
			http.Error(w, `{"error":"invalid directive"}`, http.StatusBadRequest)
			return
		}
		f.SetRule(d)
		writeJSON(w, http.StatusOK, map[string]any{"rule": d})
	})

	mux.HandleFunc("DELETE /_synth/faults", func(w http.ResponseWriter, _ *http.Request) {
		f.SetRule(Directive{})
		writeJSON(w, http.StatusOK, map[string]any{"rule": nil})
	})

	mux.HandleFunc("POST /_synth/reset", func(w http.ResponseWriter, _ *http.Request) {
		f.Reset()
		writeJSON(w, http.StatusOK, map[string]any{"reset": true})
	})

	return mux
}

func writeJSON(w http.ResponseWriter, status int, v any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(v)
}
