using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

var app = builder.Build();

// Docs are the API's UI — see docs/SPEC.md §4.2. Nothing hand-rolled.
app.MapOpenApi();
app.MapScalarApiReference();

// ponytail: no UseHttpsRedirection — TLS terminates at the ingress (docs/SPEC.md §2),
// and redirecting inside the pod breaks health probes.

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }))
   .WithName("Liveness")
   .WithSummary("Liveness probe. No dependencies checked.");

// Readiness gains a DB ping in Phase 1, when there is a DB.
app.MapGet("/readyz", () => Results.Ok(new { status = "ok", checks = Array.Empty<string>() }))
   .WithName("Readiness")
   .WithSummary("Readiness probe. Will include a Postgres ping once the registry lands.");

app.Run();
