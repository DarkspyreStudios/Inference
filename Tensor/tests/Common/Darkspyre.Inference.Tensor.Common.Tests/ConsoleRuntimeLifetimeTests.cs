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
        LlmToolDefinition tool = Assert.Single(engine.Requests[0].Tools.OfType<LlmToolDefinition>(), tool => tool.Name == "get_time");
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

        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(() => Runner.RunOwnedEngineAsync(
            () => { phases.Add("run"); return Task.FromException<int>(operation); }, engine, runtime));

        Assert.Equal([operation, engineError, runtimeError], failure.InnerExceptions);
        Assert.Equal(["run", "engine", "runtime"], phases);
    }

    private sealed class SelfHostEngine : ILlmInference
    {
        public List<LlmInferenceRequest> Requests { get; } = [];
        public InferenceImplementationDescriptor Descriptor { get; } = new("test-engine", "Test engine",
            InferenceImplementationKind.Tensor, InferenceTask.TextGeneration);
        public Task<LlmCapabilities> GetCapabilitiesAsync(string? modelId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LlmCapabilities { SupportsToolUse = InferenceSupport.Supported });
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
        public TensorRuntimePlan Plan => throw new NotSupportedException();
        public TensorRuntimeState State => TensorRuntimeState.Unavailable;
        public TensorRuntimeBackendIdentity? Current => null;

        public Task<TensorBackendAvailability> CheckAsync(TensorBackendProbeRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            phases.Add("runtime");
            return failure is null ? ValueTask.CompletedTask : ValueTask.FromException(failure);
        }
    }
}
