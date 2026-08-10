using System.Collections.Concurrent;
using Grpc.Core;
using IntegrationHub.Worker.V1;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace IntegrationHub.Orchestrator;

public enum Outcome
{
    Success,
    RetriedSuccess,
    Failed,
}

public static class ResilienceKeys
{
    /// <summary>
    /// Set per invocation, because retry safety is a property of the resource
    /// (its HTTP method) while the pipeline is cached per integration.
    /// </summary>
    public static readonly ResiliencePropertyKey<bool> RetrySafe = new("ih.retry-safe");
}

/// <summary>
/// Builds one resilience pipeline per integration from its manifest.
/// </summary>
/// <remarks>
/// The retry decision is driven by the worker's <c>Retryable</c> flag, not by
/// exceptions: the worker classifies, the orchestrator decides (SPEC §4.1). That
/// keeps one retry policy, in one language, observable in one place.
///
/// Polly v8 applies strategies outermost-first, so retry wraps the breaker. The
/// breaker therefore counts individual attempts rather than whole retry sequences,
/// which is what makes it able to trip at all.
///
/// ponytail: keyed by integration id alone. Editing a manifest's resiliency block
/// needs a restart to take effect — fine while manifests only load at startup, but
/// this cache must key on manifest version once hot-load lands (task 1.3).
/// </remarks>
public sealed class ResiliencePipelines(ILogger<ResiliencePipelines> logger)
{
    private readonly ConcurrentDictionary<string, ResiliencePipeline<InvokeResponse>> cache = new(StringComparer.Ordinal);

    public ResiliencePipeline<InvokeResponse> For(IntegrationManifest manifest) =>
        cache.GetOrAdd(manifest.Metadata.Id, _ => Build(manifest));

    private ResiliencePipeline<InvokeResponse> Build(IntegrationManifest manifest)
    {
        var id = manifest.Metadata.Id;
        var builder = new ResiliencePipelineBuilder<InvokeResponse>();

        // A worker that is restarting or past its deadline is a transient condition,
        // same as an upstream 503 — this is what makes the "scale the worker to zero
        // mid-run" demo recover rather than just fail.
        static bool IsTransient(Outcome<InvokeResponse> outcome) =>
            outcome.Result is { Ok: false, Retryable: true }
            || (outcome.Exception is RpcException ex
                && ex.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded);

        // The breaker counts every transient failure, including ones that were not
        // retried — a failing POST is still evidence the upstream is unhealthy.
        var breakerPredicate = new Func<CircuitBreakerPredicateArguments<InvokeResponse>, ValueTask<bool>>(
            args => ValueTask.FromResult(IsTransient(args.Outcome)));

        // Retry additionally requires the call to be safe to repeat. Replaying a POST
        // after a timeout can create the same order twice — the upstream may well have
        // committed before the response was lost.
        var retryPredicate = new Func<RetryPredicateArguments<InvokeResponse>, ValueTask<bool>>(args =>
        {
            if (!args.Context.Properties.TryGetValue(ResilienceKeys.RetrySafe, out var safe) || !safe)
            {
                return ValueTask.FromResult(false);
            }
            return ValueTask.FromResult(IsTransient(args.Outcome));
        });

        var retry = manifest.Spec.Resiliency?.Retry;
        if (retry is { MaxAttempts: > 1 })
        {
            builder.AddRetry(new RetryStrategyOptions<InvokeResponse>
            {
                ShouldHandle = retryPredicate,
                // MaxRetryAttempts counts retries; the manifest counts total attempts.
                MaxRetryAttempts = retry.MaxAttempts - 1,
                BackoffType = retry.Backoff == "constant"
                    ? DelayBackoffType.Constant
                    : DelayBackoffType.Exponential,
                Delay = TimeSpan.FromMilliseconds(retry.BaseDelayMs),
                UseJitter = retry.Jitter,
                OnRetry = args =>
                {
                    logger.LogWarning(
                        "{Integration}: retrying after {Code} (attempt {Attempt}, waited {Delay}ms)",
                        id,
                        args.Outcome.Result?.ErrorCode ?? args.Outcome.Exception?.GetType().Name,
                        args.AttemptNumber + 1,
                        (int)args.RetryDelay.TotalMilliseconds);
                    return default;
                },
            });
        }

        var breaker = manifest.Spec.Resiliency?.CircuitBreaker;
        if (breaker is { FailureRatio: > 0 })
        {
            builder.AddCircuitBreaker(new CircuitBreakerStrategyOptions<InvokeResponse>
            {
                ShouldHandle = breakerPredicate,
                FailureRatio = breaker.FailureRatio,
                // Polly rejects a sampling window under half a second, and a breaker
                // needs a few calls before a ratio means anything.
                SamplingDuration = TimeSpan.FromSeconds(Math.Max(1, breaker.SamplingSeconds)),
                MinimumThroughput = Math.Max(2, breaker.MinThroughput),
                BreakDuration = TimeSpan.FromSeconds(Math.Max(1, breaker.BreakSeconds)),
                OnOpened = args =>
                {
                    logger.LogError("{Integration}: circuit opened for {Break}s", id, args.BreakDuration.TotalSeconds);
                    return default;
                },
                OnClosed = _ =>
                {
                    logger.LogInformation("{Integration}: circuit closed", id);
                    return default;
                },
            });
        }

        logger.LogInformation(
            "{Integration}: resilience pipeline built (maxAttempts={Attempts}, breaker={Breaker})",
            id, retry?.MaxAttempts ?? 1, breaker is { FailureRatio: > 0 });

        return builder.Build();
    }
}
