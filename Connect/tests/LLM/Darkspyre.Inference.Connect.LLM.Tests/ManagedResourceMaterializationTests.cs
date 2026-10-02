using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class ManagedResourceMaterializationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ToolResourceAuthorizationScopeOrRetirementFailureNeverFabricatesFunctionText(bool retire)
    {
        var resources = new ControlledSessionResources();
        using var destination = new LlmResourceMaterializationTests.ResourceDestination();
        destination.Responses.Enqueue("{\"id\":\"tool-response\",\"status\":\"completed\",\"output\":[{\"type\":\"function_call\",\"id\":\"fc-one\",\"call_id\":\"call-one\",\"name\":\"generate\",\"arguments\":\"{}\"}]}");
        using var http = new HttpClient(destination);
        var client = new XaiResponsesLlmClient(http);
        ManagedAgentSession? session = null;
        Task? retirement = null;
        var events = new List<LlmAgentEvent>();
        session = await ManagedAgentSession.CreateAsync(LlmResourceMaterializationTests.Connection(), new()
        {
            ModelId = "grok-4", CallerServices = new() { Resources = resources },
            Tools = [new LlmAgentToolDefinition { Name = "generate", InputSchema = JsonElement.Parse("{\"type\":\"object\",\"properties\":{}}") }],
            ToolHandler = async (_, _) =>
            {
                InferenceResourceReference generated = await resources.RegisterAsync("generated.json", "application/json", "{}");
                if (retire) retirement = session!.DisposeAsync().AsTask();
                else resources.CurrentSessionId = "replacement-session";
                return new() { Parts = [new LlmToolOutputSessionResource(generated)] };
            }
        }, [], client, (request, token) => client.StreamAsync(LlmResourceMaterializationTests.Connection(), request, token),
            model => ManagedCapabilities(client, model));
        try
        {
            Exception failure = await Assert.ThrowsAnyAsync<Exception>(async () =>
            { await foreach (var item in session.RunTurnAsync(new() { Input = [new LlmAgentTextContent("generate")] })) events.Add(item); });
            Assert.True(LlmResourcePreparation.IsResourceFailure(failure));
            Assert.DoesNotContain(events.OfType<LlmAgentToolEvent>(), item => item.Status is "completed" or "failed");
            Assert.Empty(destination.Uploads);
            Assert.Single(destination.Prompts);
        }
        finally { retirement ??= session.DisposeAsync().AsTask(); await Drain(retirement); }
    }

    [Fact]
    public async Task RequestFailureAfterSuccessfulUploadNeverAutomaticallyUploadsOrSendsAgain()
    {
        var resources = new ControlledSessionResources();
        await resources.RegisterAsync("source.txt", "text/plain", "source");
        using var destination = new LlmResourceMaterializationTests.ResourceDestination();
        destination.ResponseFailure = new HttpRequestException("controlled post-upload failure");
        using var http = new HttpClient(destination);
        var client = new XaiResponsesLlmClient(http);
        await using var session = await ManagedAgentSession.CreateAsync(LlmResourceMaterializationTests.Connection(),
            new() { ModelId = "grok-4", CallerServices = new() { Resources = resources } }, [], client,
            (request, token) => client.StreamAsync(LlmResourceMaterializationTests.Connection(), request, token), model => ManagedCapabilities(client, model));
        var failure = await Assert.ThrowsAsync<InferenceResourceOperationException>(() => Read(session.RunTurnAsync(new() { Input = [new LlmAgentTextContent("inspect")] })));
        Assert.Same(destination.ResponseFailure, failure.InnerException);
        Assert.Single(destination.Uploads);
        Assert.Single(destination.Prompts);
        Assert.Equal(1, resources.ReadCleanupAttempts);
    }

    [Fact]
    public async Task RetiringDuringActualResourceReadPreservesTypedCancellationForLiveCallerReader()
    {
        var resources = new ControlledSessionResources();
        await resources.RegisterAsync("source.txt", "text/plain", "source");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        resources.BeforeRead = async token =>
        {
            using var registration = token.Register(() => canceled.TrySetResult());
            entered.TrySetResult();
            await release.Task;
        };
        using var http = new HttpClient(new LlmResourceMaterializationTests.ResourceDestination());
        var client = new XaiResponsesLlmClient(http);
        var session = await ManagedAgentSession.CreateAsync(LlmResourceMaterializationTests.Connection(),
            new() { ModelId = "grok-4", CallerServices = new() { Resources = resources } }, [], client,
            (request, token) => client.StreamAsync(LlmResourceMaterializationTests.Connection(), request, token), model => ManagedCapabilities(client, model));
        Task<List<LlmAgentEvent>> turn = Read(session.RunTurnAsync(new() { Input = [new LlmAgentTextContent("inspect")] }));
        Task? retirement = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            retirement = session.DisposeAsync().AsTask();
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(retirement.IsCompleted);
            release.TrySetResult();
            var actual = await Assert.ThrowsAsync<InferenceResourceOperationCanceledException>(() => turn);
            Assert.Equal(InferenceResourceOperationPhase.Materialize, actual.Failure.Phase);
            Assert.Equal(6, actual.Failure.BytesTransferred);
            await retirement;
            Assert.Equal(resources.OpenedReads, resources.DisposedReads);
        }
        finally
        {
            retirement ??= session.DisposeAsync().AsTask();
            release.TrySetResult();
            await Drain(turn);
            await Drain(retirement);
        }
    }

    [Fact]
    public async Task ExplicitUnboundImportedContextNeverAcquiresCurrentSessionProvenance()
    {
        var resources = new ControlledSessionResources();
        await resources.RegisterAsync("existing.txt", "text/plain", "existing-session-content");
        using var http = new HttpClient(new LlmResourceMaterializationTests.ResourceDestination());
        var client = new XaiResponsesLlmClient(http);
        await Assert.ThrowsAsync<ArgumentException>(() => ManagedAgentSession.CreateAsync(LlmResourceMaterializationTests.Connection(),
            new() { ModelId = "grok-4", Context = new(), CallerServices = new() { Resources = resources } }, [], client,
            (request, token) => client.StreamAsync(LlmResourceMaterializationTests.Connection(), request, token), model => ManagedCapabilities(client, model)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaptureRetirementDrainsIgnoringSelectionAndSharesActualCallbackFailure(bool failCallback)
    {
        var resources = new ControlledSessionResources();
        await resources.RegisterAsync("original.txt", "text/plain", "original");
        using var http = new HttpClient(new LlmResourceMaterializationTests.ResourceDestination());
        var client = new XaiResponsesLlmClient(http);
        var session = await ManagedAgentSession.CreateAsync(LlmResourceMaterializationTests.Connection(),
            new() { ModelId = "grok-4", CallerServices = new() { Resources = resources } }, [], client,
            (request, token) => client.StreamAsync(LlmResourceMaterializationTests.Connection(), request, token), model => ManagedCapabilities(client, model));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new InvalidOperationException("controlled host callback failure");
        resources.BeforeSelection = async token =>
        {
            using var registration = token.Register(() => { canceled.TrySetResult(); if (failCallback) throw failure; });
            entered.TrySetResult();
            await release.Task;
        };
        Task<LlmAgentContext> capture = session.CaptureContextAsync();
        Task? first = null;
        Task? second = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            first = session.DisposeAsync().AsTask();
            second = session.DisposeAsync().AsTask();
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture);
            if (failCallback)
            {
                AggregateException actual = await Assert.ThrowsAsync<AggregateException>(() => first);
                Assert.Same(failure, Assert.Single(actual.Flatten().InnerExceptions));
                Assert.Same(actual, await Assert.ThrowsAsync<AggregateException>(() => second));
            }
            else { await first; await second; }
            Assert.Equal(0, resources.DisposeCalls);
        }
        finally
        {
            first ??= session.DisposeAsync().AsTask();
            release.TrySetResult();
            await Drain(capture);
            await Drain(first);
            if (second is not null) await Drain(second);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HeldCaptureOrReplacementCannotOverwriteInterveningTurn(bool replace)
    {
        var resources = new ControlledSessionResources();
        using var http = new HttpClient(new LlmResourceMaterializationTests.ResourceDestination());
        var client = new XaiResponsesLlmClient(http);
        await using var session = await ManagedAgentSession.CreateAsync(LlmResourceMaterializationTests.Connection(),
            new() { ModelId = "grok-4", CallerServices = new() { Resources = resources } }, [], client,
            (request, token) => client.StreamAsync(LlmResourceMaterializationTests.Connection(), request, token), model => ManagedCapabilities(client, model));
        LlmAgentContext original = await session.CaptureContextAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        resources.BeforeSelection = async _ => { entered.TrySetResult(); await release.Task; };
        Task operation = replace ? session.ReplaceContextAsync(original, original.Revision) : session.CaptureContextAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            resources.BeforeSelection = null;
            await Read(session.RunTurnAsync(new() { Input = [new LlmAgentTextContent("intervening turn")] }));
            release.TrySetResult();
            await Assert.ThrowsAsync<InvalidOperationException>(() => operation);
            LlmAgentContext current = await session.CaptureContextAsync();
            Assert.True(current.Revision > original.Revision);
            Assert.Contains(current.Entries, entry => entry.Message.Content == "intervening turn");
        }
        finally { release.TrySetResult(); await Drain(operation); }
    }

    [Fact]
    public async Task ImportedSelectionRequiresExactScopeVersionAndOrderedMembershipWithoutWrites()
    {
        var resources = new ControlledSessionResources();
        InferenceResourceReference reference = await resources.RegisterAsync("source.txt", "text/plain", "bytes");
        using var http = new HttpClient(new LlmResourceMaterializationTests.ResourceDestination());
        var client = new XaiResponsesLlmClient(http);
        var options = new LlmAgentSessionOptions { ModelId = "grok-4", CallerServices = new() { Resources = resources } };
        await using var first = await ManagedAgentSession.CreateAsync(LlmResourceMaterializationTests.Connection(), options, [], client,
            (request, token) => client.StreamAsync(LlmResourceMaterializationTests.Connection(), request, token), model => ManagedCapabilities(client, model));
        LlmAgentContext context = await first.CaptureContextAsync();
        foreach (LlmAgentContext invalid in new[]
        {
            context with { ResourceSelectionVersion = context.ResourceSelectionVersion! with { SessionId = "another-session" } },
            context with { ResourceSelectionVersion = context.ResourceSelectionVersion! with { Revision = 99 } },
            context with { SelectedResources = [] }
        })
            await Assert.ThrowsAsync<ArgumentException>(() => ManagedAgentSession.CreateAsync(LlmResourceMaterializationTests.Connection(), options with { Context = invalid }, [], client,
                (request, token) => client.StreamAsync(LlmResourceMaterializationTests.Connection(), request, token), model => ManagedCapabilities(client, model)));
        Assert.Equal(new[] { reference }, (await resources.GetSelectionAsync()).SelectedResources);
        Assert.NotNull(await resources.GetAsync(reference));
    }

    [Fact]
    public async Task ActualToolCommittedArtifactAndOriginalSurviveProviderOwnerSwitch()
    {
        var resources = new ControlledSessionResources();
        InferenceResourceReference original = await resources.RegisterAsync("original.txt", "text/plain", "original bytes");
        InferenceResourceReference? generated = null;
        using var handler = new LlmResourceMaterializationTests.ResourceDestination();
        handler.Responses.Enqueue("{\"id\":\"tool-response\",\"status\":\"completed\",\"output\":[{\"type\":\"function_call\",\"id\":\"fc-one\",\"call_id\":\"call-one\",\"name\":\"generate\",\"arguments\":\"{}\"}]}");
        using var http = new HttpClient(handler);
        var client = new XaiResponsesLlmClient(http);
        var service = new LlmProviderService(new LlmResourceMaterializationTests.Source(LlmResourceMaterializationTests.Connection()), [client]);
        LlmAgentContext captured;
        var options = new LlmAgentSessionOptions
        {
            ModelId = "grok-4",
            CallerServices = new() { Resources = resources },
            Tools = [new LlmAgentToolDefinition { Name = "generate", InputSchema = JsonElement.Parse("{\"type\":\"object\",\"properties\":{}}") }],
            ToolHandler = async (_, _) =>
            {
                generated = await resources.RegisterAsync("generated.json", "application/json", "{\"committed\":true}");
                return new() { Parts = [new LlmToolOutputSessionResource(generated)] };
            }
        };
        try
        {
            await using var session = await ManagedAgentSession.CreateAsync(LlmResourceMaterializationTests.Connection(), options, [], client,
                (request, token) => service.StreamAsync("provider-a", request, token), model => ManagedCapabilities(client, model));
            var events = await Read(session.RunTurnAsync(new() { Input = [new LlmAgentTextContent("generate the file")] }));
            Assert.Equal(LlmAgentCompletionReason.Completed, Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>()).Reason);
            captured = await session.CaptureContextAsync();
            Assert.Equal(new[] { original, generated! }, captured.SelectedResources);
            Assert.Equal("logical-session", captured.ResourceSelectionVersion!.SessionId);
            Assert.Single(captured.Entries.SelectMany(entry => entry.Message.ToolResults).OfType<LlmToolResult>()
                .SelectMany(result => result.Output.Parts).OfType<LlmToolOutputSessionResource>());
        }
        finally { await service.DisposeAsync(); }

        using var secondHandler = new LlmResourceMaterializationTests.ResourceDestination();
        using var secondHttp = new HttpClient(secondHandler);
        var secondClient = new XaiResponsesLlmClient(secondHttp);
        await using var second = new LlmProviderService(new LlmResourceMaterializationTests.Source(LlmResourceMaterializationTests.Connection()), [secondClient]);
        await using var resumed = await ManagedAgentSession.CreateAsync(LlmResourceMaterializationTests.Connection(), options with
        { Context = captured, UnboundContinuationState = LlmUnboundContinuationState.Omit }, [], secondClient,
            (request, token) => second.StreamAsync("provider-b", request, token), model => ManagedCapabilities(secondClient, model));
        await Read(resumed.RunTurnAsync(new() { Input = [new LlmAgentTextContent("inspect both actual files")] }));
        // Explicit generated content in retained tool history precedes the absent selected original appended to the new prompt.
        Assert.Equal<string>(["{\"committed\":true}", "original bytes"], secondHandler.Uploads.Select(upload => System.Text.Encoding.UTF8.GetString(upload.Bytes)));
        Assert.Equal(0, resources.DisposeCalls);
    }

    internal static LlmProviderCapabilities ManagedCapabilities(XaiResponsesLlmClient client, string model) =>
        client.GetCapabilities(LlmResourceMaterializationTests.Connection(), model) with
        { SupportsAgentSessions = true, SupportsAgentToolUse = true, SupportsAgentTurnInstructions = true, Agent = ManagedAgentPolicy.Capabilities };

    internal static async Task<List<LlmAgentEvent>> Read(IAsyncEnumerable<LlmAgentEvent> source)
    { var events = new List<LlmAgentEvent>(); await foreach (LlmAgentEvent item in source) events.Add(item); return events; }

    private static async Task Drain(Task task)
    { try { await task.WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) when (task.IsCompleted) { } }
}
