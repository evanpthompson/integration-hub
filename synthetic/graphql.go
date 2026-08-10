package main

import (
	"encoding/json"
	"net/http"

	"github.com/graphql-go/graphql"
)

// A real GraphQL schema rather than a canned-response fake, for one specific
// reason: introspection. The MCP agent's probe_api tool introspects GraphQL
// endpoints to infer shapes (Phase 2), and you cannot test that against a mock
// that answers every query with the same blob.
//
// It also lets us produce the case that is genuinely hard to trigger against a
// real API: HTTP 200 with a non-empty errors[]. That single behaviour is why
// GraphQL upstreams need their own classification path.

func buildSchema(d *Dataset) (graphql.Schema, error) {
	money := graphql.NewObject(graphql.ObjectConfig{
		Name: "Money",
		Fields: graphql.Fields{
			"cents":    &graphql.Field{Type: graphql.Int},
			"currency": &graphql.Field{Type: graphql.String},
		},
	})

	customer := graphql.NewObject(graphql.ObjectConfig{
		Name: "Customer",
		Fields: graphql.Fields{
			"id":          &graphql.Field{Type: graphql.NewNonNull(graphql.ID)},
			"displayName": &graphql.Field{Type: graphql.String, Resolve: field("DisplayName")},
			"email":       &graphql.Field{Type: graphql.String, Resolve: field("Email")},
			"tier":        &graphql.Field{Type: graphql.String, Resolve: field("Tier")},
		},
	})

	lineItem := graphql.NewObject(graphql.ObjectConfig{
		Name: "LineItem",
		Fields: graphql.Fields{
			"sku":            &graphql.Field{Type: graphql.String, Resolve: field("SKU")},
			"description":    &graphql.Field{Type: graphql.String, Resolve: field("Description")},
			"quantity":       &graphql.Field{Type: graphql.Int, Resolve: field("Quantity")},
			"unitPriceCents": &graphql.Field{Type: graphql.Int, Resolve: field("UnitPriceCents")},
		},
	})

	order := graphql.NewObject(graphql.ObjectConfig{
		Name: "Order",
		Fields: graphql.Fields{
			"id":        &graphql.Field{Type: graphql.NewNonNull(graphql.ID)},
			"reference": &graphql.Field{Type: graphql.String, Resolve: field("Reference")},
			"status":    &graphql.Field{Type: graphql.String, Resolve: field("Status")},
			"placedAt":  &graphql.Field{Type: graphql.String, Resolve: field("PlacedAt")},
			"total":     &graphql.Field{Type: money, Resolve: field("Total")},
			"customer":  &graphql.Field{Type: customer, Resolve: field("Customer")},
			"lineItems": &graphql.Field{Type: graphql.NewList(lineItem), Resolve: field("LineItems")},
		},
	})

	orderPage := graphql.NewObject(graphql.ObjectConfig{
		Name: "OrderPage",
		Fields: graphql.Fields{
			"nodes":      &graphql.Field{Type: graphql.NewList(order)},
			"totalCount": &graphql.Field{Type: graphql.Int},
			"nextOffset": &graphql.Field{Type: graphql.Int},
		},
	})

	location := graphql.NewObject(graphql.ObjectConfig{
		Name: "Location",
		Fields: graphql.Fields{
			"latitude":  &graphql.Field{Type: graphql.Float},
			"longitude": &graphql.Field{Type: graphql.Float},
			"elevationM": &graphql.Field{Type: graphql.Float},
		},
	})

	reading := graphql.NewObject(graphql.ObjectConfig{
		Name: "Reading",
		Fields: graphql.Fields{
			"observedAt":   &graphql.Field{Type: graphql.String},
			"temperatureC": &graphql.Field{Type: graphql.Float},
			"humidityPct":  &graphql.Field{Type: graphql.Int},
			"windSpeedKph": &graphql.Field{Type: graphql.Float},
		},
	})

	snapshot := graphql.NewObject(graphql.ObjectConfig{
		Name: "Snapshot",
		Fields: graphql.Fields{
			"station":  &graphql.Field{Type: graphql.String},
			"location": &graphql.Field{Type: location},
			"current":  &graphql.Field{Type: reading},
		},
	})

	query := graphql.NewObject(graphql.ObjectConfig{
		Name: "Query",
		Fields: graphql.Fields{
			"orders": &graphql.Field{
				Type: graphql.NewNonNull(orderPage),
				Args: graphql.FieldConfigArgument{
					"limit":  &graphql.ArgumentConfig{Type: graphql.Int, DefaultValue: 20},
					"offset": &graphql.ArgumentConfig{Type: graphql.Int, DefaultValue: 0},
				},
				Resolve: func(p graphql.ResolveParams) (any, error) {
					page, next := d.Page(argInt(p, "limit", 20), argInt(p, "offset", 0))
					return map[string]any{
						"nodes": toAnySlice(page), "totalCount": len(d.Orders), "nextOffset": next,
					}, nil
				},
			},
			"order": &graphql.Field{
				Type: order,
				Args: graphql.FieldConfigArgument{
					"id": &graphql.ArgumentConfig{Type: graphql.NewNonNull(graphql.ID)},
				},
				Resolve: func(p graphql.ResolveParams) (any, error) {
					id, _ := p.Args["id"].(string)
					if o, ok := d.Order(id); ok {
						return *o, nil
					}
					return nil, nil
				},
			},
			"snapshot": &graphql.Field{
				Type: snapshot,
				Args: graphql.FieldConfigArgument{
					"station": &graphql.ArgumentConfig{Type: graphql.String},
				},
				Resolve: func(p graphql.ResolveParams) (any, error) {
					code, _ := p.Args["station"].(string)
					if code == "" {
						code = d.StationCodes()[0]
					}
					s, ok := d.Snapshot(code)
					if !ok {
						return nil, nil
					}
					return map[string]any{
						"station": s.Station,
						"location": map[string]any{
							"latitude": s.Location.Latitude, "longitude": s.Location.Longitude,
							"elevationM": s.Location.Elevation,
						},
						"current": map[string]any{
							"observedAt": s.Current.ObservedAt, "temperatureC": s.Current.TemperatureC,
							"humidityPct": s.Current.HumidityPct, "windSpeedKph": s.Current.WindSpeedKph,
						},
					}, nil
				},
			},
		},
	})

	return graphql.NewSchema(graphql.SchemaConfig{Query: query})
}

type graphQLRequest struct {
	Query     string         `json:"query"`
	Variables map[string]any `json:"variables"`
	Operation string         `json:"operationName"`
}

func GraphQLHandler(d *Dataset, f *Faults, schema graphql.Schema) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodPost {
			writeJSON(w, http.StatusMethodNotAllowed, map[string]any{
				"errors": []any{map[string]any{"message": "GraphQL requires POST"}},
			})
			return
		}

		var req graphQLRequest
		if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
			writeJSON(w, http.StatusBadRequest, map[string]any{
				"errors": []any{map[string]any{"message": "malformed request body"}},
			})
			return
		}

		// The case that makes GraphQL its own classification path: a perfectly
		// successful HTTP 200 carrying a fatal error in the body.
		if code := f.resolve(r.Header).GraphQLError; code != "" {
			writeJSON(w, http.StatusOK, map[string]any{
				"data": nil,
				"errors": []any{map[string]any{
					"message":    "synthetic GraphQL error: " + code,
					"type":       code,
					"extensions": map[string]any{"type": code, "code": code},
				}},
			})
			return
		}

		result := graphql.Do(graphql.Params{
			Schema:         schema,
			RequestString:  req.Query,
			VariableValues: req.Variables,
			OperationName:  req.Operation,
		})

		// GraphQL reports its own failures with HTTP 200 — that is the spec, and
		// mirroring it is the point.
		writeJSON(w, http.StatusOK, result)
	})
}

// field reads a struct field by name so resolvers stay one-liners. The default
// resolver cannot see Go field names that differ from the GraphQL field name.
func field(name string) graphql.FieldResolveFn {
	return func(p graphql.ResolveParams) (any, error) {
		switch src := p.Source.(type) {
		case Order:
			return orderField(src, name), nil
		case Customer:
			return customerField(src, name), nil
		case LineItem:
			return lineItemField(src, name), nil
		case map[string]any:
			return src[name], nil
		}
		return nil, nil
	}
}

func orderField(o Order, name string) any {
	switch name {
	case "Reference":
		return o.Reference
	case "Status":
		return o.Status
	case "PlacedAt":
		return o.PlacedAt
	case "Total":
		return map[string]any{"cents": o.Total.Cents, "currency": o.Total.Currency}
	case "Customer":
		return o.Customer
	case "LineItems":
		return toAnySliceOf(o.LineItems)
	}
	return nil
}

func customerField(c Customer, name string) any {
	switch name {
	case "DisplayName":
		return c.DisplayName
	case "Email":
		return c.Email
	case "Tier":
		return c.Tier
	}
	return nil
}

func lineItemField(l LineItem, name string) any {
	switch name {
	case "SKU":
		return l.SKU
	case "Description":
		return l.Description
	case "Quantity":
		return l.Quantity
	case "UnitPriceCents":
		return l.UnitPriceCents
	}
	return nil
}

func argInt(p graphql.ResolveParams, name string, fallback int) int {
	if v, ok := p.Args[name].(int); ok {
		return v
	}
	return fallback
}

func toAnySlice(orders []Order) []any {
	out := make([]any, len(orders))
	for i, o := range orders {
		out[i] = o
	}
	return out
}

func toAnySliceOf(items []LineItem) []any {
	out := make([]any, len(items))
	for i, item := range items {
		out[i] = item
	}
	return out
}
