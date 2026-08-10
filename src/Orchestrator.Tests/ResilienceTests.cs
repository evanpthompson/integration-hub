using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using IntegrationHub.Worker.V1;
using Microsoft.Extensions.Logging.Abstractions;
using WorkerClient = IntegrationHub.Worker.V1.Worker.WorkerClient;

namespace IntegrationHub.Orchestrator.Tests;

/// <summary>
/// A worker that returns a scripted sequence, so retry behaviour can be asserted
/// without a network, a real worker, or a sleep-and-hope test.
/// </summary>
internal sealed class ScriptedWorker(params InvokeResponse[] script) : WorkerClient
{
    private readonly Queue<InvokeResponse> queue = new(script);

    public int Calls { get; private set; }

    public override AsyncUnaryCall<InvokeResponse> InvokeAsync(InvokeRequest request, CallOptions options)
    {
        Calls++;
        if (queue.Count == 0)
        {
            throw new InvalidOperationException(
                $"the worker was called {Calls} times but only {script.Length} responses were scripted");
        }

        var next = queue.Dequeue();
        if (next is null)
        {
            // A null slot in the script means "the RPC itself fails" — a worker that
            // is restarting, which is the kubectl-scale-to-zero case.
            throw new RpcException(new Status(StatusCode.Unavailable, "worker is down"));
        }

        return new AsyncUnaryCall<InvokeResponse>(
            Task.FromResult(next),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => [],
            () => { });
    }

    public static InvokeResponse Ok(int count = 1) => new()
    {
        Ok = true,
        UpstreamStatus = 200,
        Count = count,
        RecordsJson = ByteString.CopyFromUtf8("""[{"id":"1"}]"""),
    };

    public static InvokeResponse Retryable(string code = "UPSTREAM_5XX") => new()
    {
        Ok = false, UpstreamStatus = 503, ErrorCode = code,
        ErrorMessage = "upstream returned 503", Retryable = true,
    };

    public static InvokeResponse Permanent(string code = "UPSTREAM_4XX") => new()
    {
        Ok = false, UpstreamStatus = 404, ErrorCode = code,
        ErrorMessage = "upstream returned 404", Retryable = false,
    };
}

public class ResilienceTests
{
    private static IntegrationManifest Manifest(int maxAttempts = 3, CircuitBreakerSpec? breaker = null) => new()
    {
        ApiVersion = ManifestLoader.SupportedApiVersion,
        Kind = "Integration",
        Metadata = new ManifestMetadata { Id = $"t{Guid.NewGuid():N}", DisplayName = "Test" },
        Spec = new IntegrationSpec
        {
            Protocol = "rest",
            BaseUrl = "https://api.example.test",
            // Delays are 1ms so the suite stays fast; the strategy under test is the
            // decision to retry, not the wall-clock backoff curve.
            Resiliency = new ResiliencySpec
            {
                Retry = new RetrySpec { MaxAttempts = maxAttempts, BaseDelayMs = 1, Jitter = false },
                CircuitBreaker = breaker,
            },
            Resources = [Resource()],
        },
    };

    private static ResourceSpec Resource() => new()
    {
        Name = "thing", Method = "GET", Path = "/thing",
        Emit = "single", Transform = "{ id: to_string(id) }",
    };

    private static Invoker NewInvoker(ScriptedWorker worker) => new(
        worker,
        new ResiliencePipelines(NullLogger<ResiliencePipelines>.Instance),
        NullLogger<Invoker>.Instance);

    private static JsonElement Payload(InvocationResult r) => JsonSerializer.SerializeToElement(r.Payload);

    [Fact]
    public async Task Two_retryable_failures_then_success_is_RETRIED_SUCCESS()
    {
        // The headline assertion for task 1.6, and the behaviour synthetic-flaky
        // produces against a real worker.
        var worker = new ScriptedWorker(
            ScriptedWorker.Retryable(), ScriptedWorker.Retryable(), ScriptedWorker.Ok());

        var result = await NewInvoker(worker).InvokeAsync(Manifest(), Resource(), [], default);
        var payload = Payload(result);

        Assert.True(result.Ok);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal(3, worker.Calls);
        Assert.Equal(3, payload.GetProperty("attempts").GetInt32());
        Assert.Equal("RetriedSuccess", payload.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task First_time_success_is_plain_SUCCESS_not_RETRIED_SUCCESS()
    {
        var worker = new ScriptedWorker(ScriptedWorker.Ok());

        var payload = Payload(await NewInvoker(worker).InvokeAsync(Manifest(), Resource(), [], default));

        Assert.Equal(1, worker.Calls);
        Assert.Equal("Success", payload.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task A_non_retryable_failure_is_attempted_exactly_once()
    {
        // Retrying a 404 burns rate limit to get the same answer.
        var worker = new ScriptedWorker(ScriptedWorker.Permanent());

        var result = await NewInvoker(worker).InvokeAsync(Manifest(), Resource(), [], default);
        var payload = Payload(result);

        Assert.False(result.Ok);
        Assert.Equal(502, result.StatusCode);
        Assert.Equal(1, worker.Calls);
        Assert.Equal(1, payload.GetProperty("attempts").GetInt32());
        Assert.Equal("Failed", payload.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Retries_stop_at_maxAttempts_rather_than_looping()
    {
        var worker = new ScriptedWorker(
            ScriptedWorker.Retryable(), ScriptedWorker.Retryable(), ScriptedWorker.Retryable());

        var result = await NewInvoker(worker).InvokeAsync(Manifest(maxAttempts: 3), Resource(), [], default);

        Assert.False(result.Ok);
        Assert.Equal(3, worker.Calls);
        Assert.Equal(3, Payload(result).GetProperty("attempts").GetInt32());
    }

    [Fact]
    public async Task MaxAttempts_of_one_disables_retrying_entirely()
    {
        var worker = new ScriptedWorker(ScriptedWorker.Retryable());

        var result = await NewInvoker(worker).InvokeAsync(Manifest(maxAttempts: 1), Resource(), [], default);

        Assert.Equal(1, worker.Calls);
        Assert.False(result.Ok);
    }

    [Fact]
    public async Task A_worker_that_is_down_is_retried_and_can_recover()
    {
        // null = the RPC itself fails. This is the "scale the worker to zero mid-run"
        // path from the chaos demo: transient, therefore retryable.
        var worker = new ScriptedWorker(null!, null!, ScriptedWorker.Ok());

        var result = await NewInvoker(worker).InvokeAsync(Manifest(), Resource(), [], default);
        var payload = Payload(result);

        Assert.True(result.Ok);
        Assert.Equal(3, worker.Calls);
        Assert.Equal("RetriedSuccess", payload.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task A_worker_that_stays_down_reports_503_not_a_stack_trace()
    {
        var worker = new ScriptedWorker(null!, null!, null!);

        var result = await NewInvoker(worker).InvokeAsync(Manifest(), Resource(), [], default);
        var payload = Payload(result);

        Assert.False(result.Ok);
        Assert.Equal(503, result.StatusCode);
        Assert.Equal("WORKER_UNAVAILABLE", payload.GetProperty("error").GetString());
        Assert.Equal(3, payload.GetProperty("attempts").GetInt32());
    }

    [Fact]
    public async Task An_open_circuit_fails_fast_without_calling_the_worker()
    {
        var breaker = new CircuitBreakerSpec
        {
            FailureRatio = 0.5, SamplingSeconds = 30, BreakSeconds = 30, MinThroughput = 2,
        };
        // maxAttempts 1 so each call is exactly one attempt and the arithmetic is obvious.
        var manifest = Manifest(maxAttempts: 1, breaker: breaker);

        var worker = new ScriptedWorker(
            ScriptedWorker.Retryable(), ScriptedWorker.Retryable(), ScriptedWorker.Ok());
        var invoker = NewInvoker(worker);

        await invoker.InvokeAsync(manifest, Resource(), [], default);
        await invoker.InvokeAsync(manifest, Resource(), [], default);

        // Two failures at a 0.5 ratio over the minimum throughput: the circuit opens,
        // and the third call must not reach the worker at all.
        var third = await invoker.InvokeAsync(manifest, Resource(), [], default);
        var payload = Payload(third);

        Assert.False(third.Ok);
        Assert.Equal(503, third.StatusCode);
        Assert.Equal("CIRCUIT_OPEN", payload.GetProperty("error").GetString());
        Assert.Equal(2, worker.Calls);   // the point: still 2, not 3
    }
}
