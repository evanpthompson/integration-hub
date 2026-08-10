using System.Diagnostics;
using System.Text.Json;
using IntegrationHub.Worker.V1;

// The generated service type is `IntegrationHub.Worker.V1.Worker`, and from inside
// IntegrationHub.Orchestrator the bare name `Worker` binds to the namespace instead.
using WorkerClient = IntegrationHub.Worker.V1.Worker.WorkerClient;

namespace IntegrationHub.Orchestrator;

public sealed record InvocationResult(bool Ok, object Payload, int StatusCode);

/// <summary>
/// Turns a manifest resource plus caller-supplied params into one worker call.
/// </summary>
/// <remarks>
/// ponytail: no retry pipeline here yet — MVP-0 makes exactly one attempt and reports
/// what happened. Task 1.6 wraps this in Microsoft.Extensions.Http.Resilience, which is
/// why <c>attempts</c> is already in the envelope rather than being added later.
/// </remarks>
public sealed class Invoker(WorkerClient worker, ILogger<Invoker> logger)
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

        var started = Stopwatch.GetTimestamp();
        InvokeResponse response;
        try
        {
            response = await worker.InvokeAsync(request, cancellationToken: ct);
        }
        catch (Grpc.Core.RpcException ex)
        {
            logger.LogError("run {RunId} could not reach the worker: {Status}", runId, ex.StatusCode);
            return new InvocationResult(false, new
            {
                runId,
                error = "WORKER_UNAVAILABLE",
                message = $"worker RPC failed: {ex.Status.Detail}",
            }, 503);
        }

        var durationMs = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        if (!response.Ok)
        {
            logger.LogWarning(
                "run {RunId} {Integration}.{Resource} failed: {Code} (retryable={Retryable})",
                runId, manifest.Metadata.Id, resource.Name, response.ErrorCode, response.Retryable);

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
                attempts = 1,
            }, 502);
        }

        using var records = JsonDocument.Parse(response.RecordsJson.Memory);

        logger.LogInformation(
            "run {RunId} {Integration}.{Resource} ok count={Count} ms={Duration}",
            runId, manifest.Metadata.Id, resource.Name, response.Count, durationMs);

        return new InvocationResult(true, new
        {
            runId,
            integrationId = manifest.Metadata.Id,
            resource = resource.Name,
            fetchedAt = DateTimeOffset.UtcNow,
            count = response.Count,
            durationMs,
            attempts = 1,
            records = records.RootElement.Clone(),
        }, 200);
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
