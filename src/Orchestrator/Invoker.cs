using System.Diagnostics;
using System.Text.Json;
using Polly;
using IntegrationHub.Worker.V1;

// The generated service type is `IntegrationHub.Worker.V1.Worker`, and from inside
// IntegrationHub.Orchestrator the bare name `Worker` binds to the namespace instead.
using WorkerClient = IntegrationHub.Worker.V1.Worker.WorkerClient;

namespace IntegrationHub.Orchestrator;

public sealed record InvocationResult(bool Ok, object Payload, int StatusCode);

/// <summary>
/// Turns a manifest resource plus caller-supplied params into one worker call.
/// </summary>
public sealed class Invoker(WorkerClient worker, ResiliencePipelines pipelines, ILogger<Invoker> logger)
{
    public async Task<InvocationResult> InvokeAsync(
        IntegrationManifest manifest,
        ResourceSpec resource,
        Dictionary<string, string> supplied,
        CancellationToken ct)
    {
        // UUIDv7 is time-ordered like the ULID the spec calls for, and it is in the
        // standard library — no dependency for an id nobody parses.
        var runId = Guid.CreateVersion7().ToString();

        if (!TryBindParams(resource, supplied, out var pathValues, out var query, out var problem))
        {
            return new InvocationResult(false, new { runId, error = "INVALID_PARAMS", message = problem }, 400);
        }

        var request = new InvokeRequest
        {
            RunId = runId,
            IntegrationId = manifest.Metadata.Id,
            Resource = resource.Name,
            Protocol = Protocol.Rest,
            BaseUrl = manifest.Spec.BaseUrl,
            Method = string.IsNullOrWhiteSpace(resource.Method) ? "GET" : resource.Method,
            Path = SubstitutePath(resource.Path, pathValues),
            Transform = resource.Transform,
            Emit = resource.Emit == "list" ? Emit.List : Emit.Single,
            TimeoutMs = manifest.Spec.Defaults.TimeoutMs,
            Attempt = 1,
        };

        request.Query.Add(query);
        request.Headers.Add(manifest.Spec.Defaults.Headers);
        // auth.type is always "none" in MVP-0 — credential resolution is task 1.4.

        // One key for the whole logical operation, reused across every attempt — that
        // is what lets the upstream collapse duplicates. A per-attempt key would defeat
        // the entire point.
        if (!string.IsNullOrWhiteSpace(resource.IdempotencyKey))
        {
            request.Headers[resource.IdempotencyKey] = runId;
        }

        var started = Stopwatch.GetTimestamp();
        var attempts = 0;
        InvokeResponse response;

        var context = ResilienceContextPool.Shared.Get(ct);
        context.Properties.Set(ResilienceKeys.RetrySafe, IsRetrySafe(manifest, resource));
        try
        {
            response = await pipelines.For(manifest).ExecuteAsync(async ctx =>
            {
                attempts++;
                request.Attempt = attempts;
                return await worker.InvokeAsync(request, cancellationToken: ctx.CancellationToken);
            }, context);
        }
        catch (Polly.CircuitBreaker.BrokenCircuitException)
        {
            // Failing fast while the breaker is open is the feature, not an error to
            // paper over — the upstream gets a chance to recover instead of being
            // hammered. 503 with Retry-After is the honest way to say so.
            logger.LogWarning("run {RunId} rejected: circuit open for {Integration}", runId, manifest.Metadata.Id);
            return new InvocationResult(false, new
            {
                runId,
                integrationId = manifest.Metadata.Id,
                resource = resource.Name,
                error = "CIRCUIT_OPEN",
                message = "the circuit breaker for this integration is open; not attempting the call",
                retryable = true,
                attempts = 0,
            }, 503);
        }
        catch (Grpc.Core.RpcException ex)
        {
            logger.LogError(
                "run {RunId} could not reach the worker after {Attempts} attempt(s): {Status}",
                runId, attempts, ex.StatusCode);
            return new InvocationResult(false, new
            {
                runId,
                error = "WORKER_UNAVAILABLE",
                message = $"worker RPC failed: {ex.Status.Detail}",
                retryable = true,
                attempts,
            }, 503);
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }

        var durationMs = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var outcome = response.Ok
            ? (attempts > 1 ? Outcome.RetriedSuccess : Outcome.Success)
            : Outcome.Failed;

        if (!response.Ok)
        {
            logger.LogWarning(
                "run {RunId} {Integration}.{Resource} failed after {Attempts} attempt(s): {Code} (retryable={Retryable})",
                runId, manifest.Metadata.Id, resource.Name, attempts, response.ErrorCode, response.Retryable);

            return new InvocationResult(false, new
            {
                runId,
                integrationId = manifest.Metadata.Id,
                resource = resource.Name,
                error = response.ErrorCode,
                message = response.ErrorMessage,
                retryable = response.Retryable,
                upstreamStatus = response.UpstreamStatus,
                durationMs,
                attempts,
                outcome = Outcome.Failed.ToString(),
            }, 502);
        }

        using var records = JsonDocument.Parse(response.RecordsJson.Memory);

        logger.LogInformation(
            "run {RunId} {Integration}.{Resource} {Outcome} count={Count} attempts={Attempts} ms={Duration}",
            runId, manifest.Metadata.Id, resource.Name, outcome, response.Count, attempts, durationMs);

        return new InvocationResult(true, new
        {
            runId,
            integrationId = manifest.Metadata.Id,
            resource = resource.Name,
            fetchedAt = DateTimeOffset.UtcNow,
            count = response.Count,
            durationMs,
            attempts,
            outcome = outcome.ToString(),
            records = records.RootElement.Clone(),
        }, 200);
    }

    /// <summary>
    /// Whether repeating this call is safe. Retrying a POST after a timeout can create
    /// the same record twice — the upstream may have committed before the response was
    /// lost — so unsafe methods are only retried when the manifest supplies an
    /// idempotency key for the upstream to deduplicate on.
    /// </summary>
    internal static bool IsRetrySafe(IntegrationManifest manifest, ResourceSpec resource)
    {
        if (!string.IsNullOrWhiteSpace(resource.IdempotencyKey))
        {
            return true;
        }

        // GraphQL is always an HTTP POST, but only queries are supported — mutations
        // are not implemented. Revisit the moment they are.
        if (manifest.Spec.Protocol == "graphql")
        {
            return true;
        }

        // RFC 9110 §9.2.2. PUT and DELETE are idempotent by definition; POST and PATCH
        // are not.
        return (resource.Method ?? "GET").ToUpperInvariant()
            is "GET" or "HEAD" or "OPTIONS" or "TRACE" or "PUT" or "DELETE";
    }

    internal static bool TryBindParams(
        ResourceSpec resource,
        Dictionary<string, string> supplied,
        out Dictionary<string, string> pathValues,
        out Dictionary<string, string> query,
        out string problem)
    {
        pathValues = new Dictionary<string, string>(StringComparer.Ordinal);
        query = new Dictionary<string, string>(StringComparer.Ordinal);
        problem = "";

        var declared = resource.Params.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var unknown = supplied.Keys.Where(k => !declared.Contains(k)).ToList();
        if (unknown.Count > 0)
        {
            // Silently dropping a param the caller believed mattered produces a
            // confusing wrong answer rather than an obvious failure.
            problem = $"unknown param(s): {string.Join(", ", unknown.Order(StringComparer.Ordinal))}";
            return false;
        }

        foreach (var p in resource.Params)
        {
            var value = supplied.TryGetValue(p.Name, out var v) && !string.IsNullOrEmpty(v)
                ? v
                : p.Default;

            if (string.IsNullOrEmpty(value))
            {
                if (p.Required)
                {
                    problem = $"missing required param '{p.Name}'";
                    return false;
                }
                continue;
            }

            switch (p.In)
            {
                case "path":
                    pathValues[p.Name] = value;
                    break;
                case "query":
                    query[p.Name] = value;
                    break;
            }
        }

        return true;
    }

    internal static string SubstitutePath(string path, Dictionary<string, string> values)
    {
        foreach (var (name, value) in values)
        {
            path = path.Replace($"{{{name}}}", Uri.EscapeDataString(value), StringComparison.Ordinal);
        }
        return path;
    }
}
