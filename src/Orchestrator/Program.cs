using Grpc.Net.Client;
using IntegrationHub.Orchestrator;
using Scalar.AspNetCore;
using WorkerClient = IntegrationHub.Worker.V1.Worker.WorkerClient;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddSingleton<IntegrationRegistry>();
builder.Services.AddSingleton(_ => GrpcChannel.ForAddress(
    builder.Configuration["Worker:Address"] ?? "http://localhost:50051"));
builder.Services.AddSingleton(sp => new WorkerClient(sp.GetRequiredService<GrpcChannel>()));
builder.Services.AddSingleton<Invoker>();

var app = builder.Build();

// Reconcile the declarative record on the way up. A bad manifest fails startup —
// SPEC §3.1. The API hot-load path is task 1.3's other half.
var registry = app.Services.GetRequiredService<IntegrationRegistry>();
var manifestDir = builder.Configuration["Integrations:Directory"]
                  ?? Path.Combine(AppContext.BaseDirectory, "../../../../../integrations");
registry.LoadDirectory(Path.GetFullPath(manifestDir), app.Logger);

// Docs are the API's UI — see docs/SPEC.md §4.2. Nothing hand-rolled.
app.MapOpenApi();
app.MapScalarApiReference();

// ponytail: no UseHttpsRedirection — TLS terminates at the ingress (docs/SPEC.md §2),
// and redirecting inside the pod breaks health probes.

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }))
   .WithName("Liveness")
   .WithSummary("Liveness probe. No dependencies checked.");

app.MapGet("/readyz", () => Results.Ok(new { status = "ok", checks = Array.Empty<string>() }))
   .WithName("Readiness")
   .WithSummary("Readiness probe. Will include a Postgres ping once the registry is backed by one.");

app.MapGet("/integrations", (IntegrationRegistry reg) => Results.Ok(
        reg.All.Select(m => new
        {
            id = m.Metadata.Id,
            displayName = m.Metadata.DisplayName,
            protocol = m.Spec.Protocol,
            baseUrl = m.Spec.BaseUrl,
            resources = m.Spec.Resources.Select(r => new { r.Name, r.Method, r.Emit }),
        })))
   .WithName("ListIntegrations")
   .WithSummary("Every integration currently in the registry.");

app.MapPost("/integrations/{id}/resources/{resource}/invoke", async (
        string id,
        string resource,
        Dictionary<string, string>? body,
        IntegrationRegistry reg,
        Invoker invoker,
        CancellationToken ct) =>
    {
        var manifest = reg.Find(id);
        if (manifest is null)
        {
            return Results.NotFound(new { error = "UNKNOWN_INTEGRATION", message = $"no integration '{id}'" });
        }

        var spec = manifest.Spec.Resources.FirstOrDefault(r => r.Name == resource);
        if (spec is null)
        {
            return Results.NotFound(new
            {
                error = "UNKNOWN_RESOURCE",
                message = $"integration '{id}' has no resource '{resource}'",
                available = manifest.Spec.Resources.Select(r => r.Name),
            });
        }

        var result = await invoker.InvokeAsync(manifest, spec, body ?? [], ct);
        return Results.Json(result.Payload, statusCode: result.StatusCode);
    })
   .WithName("InvokeResource")
   .WithSummary("Call an upstream through its manifest and return canonical records.");

app.Run();

// Exposed so the test project can drive the app through WebApplicationFactory later.
public partial class Program;
