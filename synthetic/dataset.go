package main

import (
	"fmt"
	"math/rand/v2"
	"time"
)

// The dataset is generated from a fixed seed and a fixed base time, so the same
// --seed always produces byte-identical payloads. Tests can assert on values, not
// just shapes, and a failure is reproducible rather than "worked yesterday".
//
// Field names are snake_case on purpose: real upstreams rarely hand you the shape
// you want, and the whole point of the transform is renaming into canonical form.
// A mock that emits already-canonical fields would test nothing.

var baseTime = time.Date(2026, 1, 1, 0, 0, 0, 0, time.UTC)

type Money struct {
	Cents    int64  `json:"cents"`
	Currency string `json:"currency"`
}

type Customer struct {
	ID          string `json:"id"`
	DisplayName string `json:"display_name"`
	Email       string `json:"email_address"`
	Tier        string `json:"account_tier"`
}

type LineItem struct {
	SKU            string `json:"sku"`
	Description    string `json:"description"`
	Quantity       int    `json:"quantity"`
	UnitPriceCents int64  `json:"unit_price_cents"`
}

type Order struct {
	ID        string     `json:"id"`
	Reference string     `json:"order_reference"`
	Status    string     `json:"fulfillment_status"`
	PlacedAt  string     `json:"placed_at"`
	Total     Money      `json:"order_total"`
	Customer  Customer   `json:"customer"`
	LineItems []LineItem `json:"line_items"`
}

// Snapshot mirrors the nested single-object shape of a real telemetry or weather
// API, so `emit: single` transforms get exercised against something realistic.
type Snapshot struct {
	Station  string `json:"station_id"`
	Location struct {
		Latitude  float64 `json:"latitude"`
		Longitude float64 `json:"longitude"`
		Elevation float64 `json:"elevation_m"`
	} `json:"location"`
	Current struct {
		ObservedAt    string  `json:"observed_at"`
		TemperatureC  float64 `json:"temperature_c"`
		HumidityPct   int     `json:"humidity_pct"`
		WindSpeedKph  float64 `json:"wind_speed_kph"`
	} `json:"current"`
}

type Dataset struct {
	Orders    []Order
	byID      map[string]*Order
	Snapshots map[string]*Snapshot
}

var (
	statuses = []string{"pending", "picking", "shipped", "delivered", "cancelled"}
	tiers    = []string{"free", "standard", "enterprise"}
	products = []string{
		"Widget", "Sprocket", "Flange", "Bearing", "Gasket",
		"Coupling", "Bracket", "Manifold", "Regulator", "Actuator",
	}
	surnames = []string{
		"Okafor", "Nakamura", "Delacroix", "Vasquez", "Lindqvist",
		"Haddad", "Oyelaran", "Petrov", "Silva", "Novak",
	}
	givenNames = []string{
		"Ada", "Rin", "Milo", "Sasha", "Ines", "Kofi", "Nadia", "Theo", "Ravi", "Юля",
	}
	stations = []string{"KOJC", "KMCI", "KIXD"}
)

func NewDataset(seed uint64, orderCount int) *Dataset {
	r := rand.New(rand.NewPCG(seed, seed^0x9e3779b97f4a7c15))

	d := &Dataset{
		byID:      make(map[string]*Order, orderCount),
		Snapshots: make(map[string]*Snapshot, len(stations)),
	}

	for i := range orderCount {
		id := fmt.Sprintf("ord_%05d", i+1)
		lineCount := 1 + r.IntN(4)

		lines := make([]LineItem, lineCount)
		var totalCents int64
		for j := range lines {
			qty := 1 + r.IntN(9)
			unit := int64(250 + r.IntN(48_000))
			lines[j] = LineItem{
				SKU:            fmt.Sprintf("SKU-%04d", r.IntN(9999)),
				Description:    products[r.IntN(len(products))],
				Quantity:       qty,
				UnitPriceCents: unit,
			}
			totalCents += int64(qty) * unit
		}

		given := givenNames[r.IntN(len(givenNames))]
		surname := surnames[r.IntN(len(surnames))]

		order := Order{
			ID:        id,
			Reference: fmt.Sprintf("REF-%d-%04d", 2026, i+1),
			Status:    statuses[r.IntN(len(statuses))],
			PlacedAt:  baseTime.Add(time.Duration(i) * 37 * time.Minute).Format(time.RFC3339),
			Total:     Money{Cents: totalCents, Currency: "USD"},
			Customer: Customer{
				ID:          fmt.Sprintf("cus_%04d", 1+r.IntN(250)),
				DisplayName: given + " " + surname,
				Email:       fmt.Sprintf("%s.%d@example.test", surname, i+1),
				Tier:        tiers[r.IntN(len(tiers))],
			},
			LineItems: lines,
		}
		d.Orders = append(d.Orders, order)
	}

	for i := range d.Orders {
		d.byID[d.Orders[i].ID] = &d.Orders[i]
	}

	for i, code := range stations {
		s := &Snapshot{Station: code}
		s.Location.Latitude = 38.8 + float64(i)*0.17
		s.Location.Longitude = -94.9 + float64(i)*0.21
		s.Location.Elevation = 280 + float64(r.IntN(120))
		s.Current.ObservedAt = baseTime.Add(time.Duration(i) * time.Hour).Format(time.RFC3339)
		s.Current.TemperatureC = float64(r.IntN(350)) / 10
		s.Current.HumidityPct = 30 + r.IntN(60)
		s.Current.WindSpeedKph = float64(r.IntN(400)) / 10
		d.Snapshots[code] = s
	}

	return d
}

func (d *Dataset) Order(id string) (*Order, bool) {
	o, ok := d.byID[id]
	return o, ok
}

// Page returns a window plus the offset of the next one, or -1 when exhausted.
func (d *Dataset) Page(limit, offset int) ([]Order, int) {
	if limit <= 0 {
		limit = 20
	}
	if limit > 200 {
		limit = 200
	}
	if offset < 0 || offset >= len(d.Orders) {
		return []Order{}, -1
	}
	end := min(offset+limit, len(d.Orders))
	next := -1
	if end < len(d.Orders) {
		next = end
	}
	return d.Orders[offset:end], next
}

func (d *Dataset) Snapshot(station string) (*Snapshot, bool) {
	s, ok := d.Snapshots[station]
	return s, ok
}

func (d *Dataset) StationCodes() []string { return stations }
