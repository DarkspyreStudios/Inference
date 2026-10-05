using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class ProviderStreamFailureTests
{
    [Fact]
    public async Task CleanupFailureFromFinalProviderPullIsAlsoReportedByRetirement()
    {
        var providerFailure = new LlmProviderException("The provider rejected generation.", LlmTransport.OpenAiResponses);
        Exception cleanupFailure = LlmResourcePreparation.AsCleanupFailure(new InvalidOperationException("The response could not be released."));
        var stream = new FailingStream(providerFailure, cleanupFailure, cleanupDuringPull: true);
        var service = new LlmProviderService(new Source(), [new FailingClient(stream)]);

        Exception? operation = await Record.ExceptionAsync(async () =>
        {
            await foreach (LlmStreamEvent unused in service.StreamAsync("connection", new() { ModelId = "model" }))
                throw new InvalidOperationException("The failed provider must not emit an event.");
        }).WaitAsync(TimeSpan.FromSeconds(5));
        Exception? retirement = await Record.ExceptionAsync(async () => await service.DisposeAsync())
            .WaitAsync(TimeSpan.FromSeconds(5));

        Exception[] causes = Assert.IsType<AggregateException>(operation).Flatten().InnerExceptions.ToArray();
        Assert.Equal(2, causes.Length);
        Assert.Same(providerFailure, causes[0]);
        Assert.Same(cleanupFailure, causes[1]);
        Assert.Same(cleanupFailure, retirement);
        Assert.Same(retirement, await Record.ExceptionAsync(async () => await service.DisposeAsync()));
    }

    [Fact]
    public async Task ProviderFailureSurvivesFailedEnumerationCleanupAndRetirementReportsThatCleanup()
    {
        var providerFailure = new LlmProviderException("The provider rejected generation.", LlmTransport.OpenAiResponses);
        var cleanupFailure = new InvalidOperationException("The provider stream could not be released.");
        var service = new LlmProviderService(new Source(), [new FailingClient(new FailingStream(providerFailure, cleanupFailure))]);

        Exception? operation = await Record.ExceptionAsync(async () =>
        {
            await foreach (LlmStreamEvent unused in service.StreamAsync("connection", new() { ModelId = "model" }))
                throw new InvalidOperationException("The failed provider must not emit an event.");
        }).WaitAsync(TimeSpan.FromSeconds(5));
        Exception? retirement = await Record.ExceptionAsync(async () => await service.DisposeAsync())
            .WaitAsync(TimeSpan.FromSeconds(5));

        AggregateException failure = Assert.IsType<AggregateException>(operation);
        Exception[] causes = failure.Flatten().InnerExceptions.ToArray();
        Assert.Equal(2, causes.Length);
        Assert.Same(providerFailure, causes[0]);
        var cleanup = Assert.IsType<InferenceResourceOperationException>(causes[1]);
        Assert.Equal(InferenceResourceOperationPhase.Cleanup, cleanup.Failure.Phase);
        Assert.Same(cleanupFailure, cleanup.InnerException);
        Assert.Same(cleanup, retirement);
        Assert.Same(retirement, await Record.ExceptionAsync(async () => await service.DisposeAsync()));
    }

    [Fact]
    public async Task AbandoningAStreamReportsCleanupThatFailedDuringTheFinalProviderPull()
    {
        var cleanupFailure = new InvalidOperationException("The provider stream could not be released.");
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new LlmProviderService(new Source(), [new FailingClient(new AbandoningStream(cleanupFailure, reading))]);
        IAsyncEnumerator<LlmStreamEvent> events = service.StreamAsync("connection", new() { ModelId = "model" }).GetAsyncEnumerator();

        Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Exception? abandonment = await Record.ExceptionAsync(async () => await events.DisposeAsync())
            .WaitAsync(TimeSpan.FromSeconds(5));
        Exception? retirement = await Record.ExceptionAsync(async () => await service.DisposeAsync())
            .WaitAsync(TimeSpan.FromSeconds(5));

        Exception[] causes = Assert.IsType<AggregateException>(abandonment).Flatten().InnerExceptions.ToArray();
        Assert.Equal(2, causes.Length);
        Assert.IsAssignableFrom<OperationCanceledException>(causes[0]);
        var cleanup = Assert.IsType<InferenceResourceOperationException>(causes[1]);
        Assert.Equal(InferenceResourceOperationPhase.Cleanup, cleanup.Failure.Phase);
        Assert.Same(cleanupFailure, cleanup.InnerException);
        Assert.Same(cleanup, retirement);
        Assert.Same(retirement, await Record.ExceptionAsync(async () => await service.DisposeAsync()));
    }

    private sealed class Source : ILlmConnectionSource
    {
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<LlmConnection?>(new()
            {
                Version = "1",
                Transport = LlmTransport.OpenAiResponses,
                Endpoint = "https://provider.example.test/v1",
                Credential = new LlmCredential { ApiKey = "test-key" }.Serialize()
            });

        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FailingClient(IAsyncEnumerable<LlmStreamEvent> stream) : ILlmClient
    {
        public LlmTransport Transport => LlmTransport.OpenAiResponses;
        public LlmProviderDefinition ProviderDefinition => ProviderDefinitions.OpenAiApiKey;
        public LlmProviderCapabilities GetCapabilities(LlmConnection connection, string? modelId = null) => new();
        public Task<IReadOnlyList<LlmModel>> ListModelsAsync(LlmConnection connection,
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<LlmModel>>([]);
        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmConnection connection, LlmChatRequest request,
            CancellationToken cancellationToken = default) => stream;
    }

    private sealed class AbandoningStream(Exception cleanupFailure, TaskCompletionSource reading)
        : IAsyncEnumerable<LlmStreamEvent>, IAsyncEnumerator<LlmStreamEvent>
    {
        private CancellationToken _cancellationToken;
        private bool _started;
        public LlmStreamEvent Current => new LlmTextDeltaEvent("partial");
        public IAsyncEnumerator<LlmStreamEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            _cancellationToken = cancellationToken;
            return this;
        }
        public async ValueTask<bool> MoveNextAsync()
        {
            if (!_started) { _started = true; return true; }
            reading.TrySetResult();
            await Task.Delay(Timeout.Infinite, _cancellationToken);
            return false;
        }
        public ValueTask DisposeAsync() => ValueTask.FromException(cleanupFailure);
    }

    private sealed class FailingStream(Exception providerFailure, Exception cleanupFailure, bool cleanupDuringPull = false)
        : IAsyncEnumerable<LlmStreamEvent>, IAsyncEnumerator<LlmStreamEvent>
    {
        public LlmStreamEvent Current => throw new InvalidOperationException("No event is available.");
        public IAsyncEnumerator<LlmStreamEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync() => ValueTask.FromException<bool>(cleanupDuringPull
            ? new AggregateException(providerFailure, cleanupFailure) : providerFailure);
        public ValueTask DisposeAsync() => cleanupDuringPull ? ValueTask.CompletedTask : ValueTask.FromException(cleanupFailure);
    }
}
