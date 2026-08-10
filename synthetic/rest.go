package main

import (
	"net/http"
	"strconv"
)

// REST adapter. Shapes are chosen to exercise the transform, not to be pretty:
// snake_case keys, nested objects, an array of line items, and offset pagination.

func RESTHandler(d *Dataset) http.Handler {
	mux := http.NewServeMux()

	// Collection with pagination — exercises `emit: list`.
	mux.HandleFunc("GET /v1/orders", func(w http.ResponseWriter, r *http.Request) {
		limit := intParam(r, "limit", 20)
		offset := intParam(r, "offset", 0)
		page, next := d.Page(limit, offset)

		writeJSON(w, http.StatusOK, map[string]any{
			"orders":      page,
			"total_count": len(d.Orders),
			"limit":       limit,
			"offset":      offset,
			"next_offset": next, // -1 when exhausted
		})
	})

	// Single resource — exercises `emit: single` and a 404 that must not be retried.
	mux.HandleFunc("GET /v1/orders/{id}", func(w http.ResponseWriter, r *http.Request) {
		order, ok := d.Order(r.PathValue("id"))
		if !ok {
			writeJSON(w, http.StatusNotFound, map[string]any{
				"error":   "not_found",
				"message": "no order with id " + r.PathValue("id"),
			})
			return
		}
		writeJSON(w, http.StatusOK, order)
	})

	// Nested single object, shaped like a telemetry/weather payload.
	mux.HandleFunc("GET /v1/snapshot", func(w http.ResponseWriter, r *http.Request) {
		station := r.URL.Query().Get("station")
		if station == "" {
			station = d.StationCodes()[0]
		}
		snapshot, ok := d.Snapshot(station)
		if !ok {
			writeJSON(w, http.StatusNotFound, map[string]any{
				"error":            "unknown_station",
				"known_stations":   d.StationCodes(),
			})
			return
		}
		writeJSON(w, http.StatusOK, snapshot)
	})

	// Machine-readable description of what this service offers. The MCP agent's
	// probe_api tool has something real to discover in Phase 2.
	mux.HandleFunc("GET /v1/_describe", func(w http.ResponseWriter, _ *http.Request) {
		writeJSON(w, http.StatusOK, map[string]any{
			"orders":         len(d.Orders),
			"stations":       d.StationCodes(),
			"rest_endpoints": []string{"/v1/orders", "/v1/orders/{id}", "/v1/snapshot"},
			"graphql":        "/graphql",
			"fault_headers": []string{
				hdrStatus, hdrDelayMs, hdrFailTimes, hdrKey, hdrBody, hdrGraphQL,
			},
		})
	})

	return mux
}

func intParam(r *http.Request, name string, fallback int) int {
	raw := r.URL.Query().Get(name)
	if raw == "" {
		return fallback
	}
	n, err := strconv.Atoi(raw)
	if err != nil {
		return fallback
	}
	return n
}
