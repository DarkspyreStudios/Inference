using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Tensor.ConsoleTester;
using Runner = Darkspyre.Inference.Tensor.ConsoleTester.Program;

namespace Darkspyre.Inference.Tensor.Common.Tests;

public sealed class ConsoleRuntimeLifetimeTests
{
    [Fact]
    public async Task SelfHostToolRoundTripUsesExplicitSchemaWithoutReflectionDefaults()
    {
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        var engine = new SelfHostEngine();

        Assert.True(await SelfHostSuite.RunAsync(engine, "model", CancellationToken.None));
        Assert.Equal(2, engine.Requests.Count);
        LlmToolDefinition tool = Assert.IsType<LlmToolDefinition>(Assert.Single(engine.Requests[0].Tools));
        Assert.Equal("get_time", tool.Name);
        Assert.Equal("object", tool.InputSchema.GetProperty("type").GetString());
        Assert.Empty(tool.InputSchema.GetProperty("properties").EnumerateObject());
        Assert.False(tool.InputSchema.GetProperty("additionalProperties").GetBoolean());
        LlmToolResult result = Assert.IsType<LlmToolResult>(Assert.Single(
            engine.Requests[1].Messages.SelectMany(message => message.ToolResults)));
        Assert.Equal("time-1", result.ToolCallId);
    }

    [Fact]
    public async Task OperationAndIndependentCleanupFailuresRemainTogetherWithoutRetirementRetry()
    {
        var operation = new InvalidOperationException("operation failed");
        var engineError = new IOException("engine release failed");
        var runtimeError = new InvalidOperationException("supplier retirement failed");
        var phases = new List<string>();
        var engine = new Engine(phases, engineError);
        var runtime = new Runtime(phases, runtimeError);
        using var output = new StringWriter();

        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(() => Runner.RunOwnedEngineAsync(
            () => { phases.Add("run"); return Task.FromException<int>(operation); }, engine, runtime, output));

        Assert.Equal([operation, engineError, runtimeError], failure.InnerExceptions);
        Assert.Equal(["run", "engine", "runtime"], phases);
        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public async Task RetiredRuntimeObservationIsReportedAfterEngineAndRuntimeDisposal()
    {
        var phases = new List<string>();
        var engine = new Engine(phases);
        var runtime = new Runtime(phases);
        using var output = new StringWriter();

        int result = await Runner.RunOwnedEngineAsync(() => { phases.Add("run"); return Task.FromResult(0); },
            engine, runtime, output);

        Assert.Equal(0, result);
        Assert.Equal(["run", "engine", "runtime", "shutdown"], phases);
        string line = output.ToString().TrimEnd();
        Assert.StartsWith("runtime : ", line);
        TensorRuntimeShutdownResult observation = JsonSerializer.Deserialize(line["runtime : ".Length..],
            TensorJson.TensorRuntimeShutdownResult)!;
        Assert.Equal(runtime.Observation.OperationId, observation.OperationId);
        Assert.Equal(runtime.Observation.ObservedAt, observation.ObservedAt);
        Assert.Empty(observation.Diagnostics);
        Assert.Equal(TensorRuntimeShutdownState.AlreadyStopped, observation.State);
        Assert.Equal(0, observation.OutstandingLeases);
        Assert.False(observation.NativeStateMayRemain);
    }

    private sealed class SelfHostEngine : ILlmInference
    {
        public List<LlmInferenceRequest> Requests { get; } = [];
        public InferenceImplementationDescriptor Descriptor { get; } = new("test-engine", "Test engine",
            InferenceImplementationKind.Tensor, InferenceTask.TextGeneration);
        public Task<LlmCapabilities> GetCapabilitiesAsync(string? modelId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LlmCapabilities { SupportsToolUse = true });
        public Task<IReadOnlyList<LlmModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LlmModelInfo>>([new("model", "Model")]);
        public Task<IReadOnlyList<LlmModelInfo>> RefreshModelsAsync(CancellationToken cancellationToken = default) =>
            ListModelsAsync(cancellationToken);
        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmInferenceRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return LlmInferenceExtensions.CompleteAsStreamAsync(new()
            {
                Text = Requests.Count == 1 ? "" : "The tool supplied the time.",
                Status = Requests.Count == 1 ? LlmCompletionStatus.RequiresAction : LlmCompletionStatus.Completed,
                ToolCalls = Requests.Count == 1 ? [new LlmToolCall("time-1", "get_time", "{}")] : [],
                Provenance = new() { ImplementationId = Descriptor.Id, ModelId = "model" }
            }, cancellationToken);
        }
    }

    private sealed class Engine(List<string> phases, Exception? failure = null) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            phases.Add("engine");
            return failure is null ? ValueTask.CompletedTask : ValueTask.FromException(failure);
        }
    }

    private sealed class Runtime(List<string> phases, Exception? failure = null) : ITensorRuntimeOwner
    {
        public TensorRuntimeShutdownResult Observation { get; } = new(TensorRuntimeShutdownState.AlreadyStopped,
            Guid.NewGuid(), DateTimeOffset.UtcNow, 0, false, []);
        public TensorRuntimePlan Plan => throw new NotSupportedException();
        public TensorRuntimeState State => TensorRuntimeState.Stopped;
        public TensorRuntimeBackendIdentity? Current => null;

        public Task<TensorBackendAvailability> CheckAsync(TensorBackendProbeRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<TensorRuntimeShutdownResult> ShutdownAsync(CancellationToken cancellationToken = default)
        {
            phases.Add("shutdown");
            return Task.FromResult(Observation);
        }

        public ValueTask DisposeAsync()
        {
            phases.Add("runtime");
            return failure is null ? ValueTask.CompletedTask : ValueTask.FromException(failure);
        }
    }
}
