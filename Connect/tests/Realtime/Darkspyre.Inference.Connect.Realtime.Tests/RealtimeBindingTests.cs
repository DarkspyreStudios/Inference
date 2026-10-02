using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Realtime;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Connect.LLM;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.Realtime.Tests;

public sealed class RealtimeBindingTests
{
    private static RealtimeConnection Connection => new()
    { Transport = RealtimeTransport.XaiRealtime, Endpoint = new("wss://api.x.ai/v1"), Xai = new("selected") };
    private static InferenceStreamLimits Limits => new(2, 3, 4);
    private static SetupConfigurationIdentity Identity => new(SetupChangeIntent.Create, "entry", null);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task RegistrationDoesNotAdvertiseAnUnimplementedAdapter()
    {
        var services = new ServiceCollection(); services.AddRealtimeConnect();
        using ServiceProvider provider = services.BuildServiceProvider();
        RealtimeProviderService facade = provider.GetRequiredService<RealtimeProviderService>();
        Assert.Empty(facade.SetupDefinitions); Assert.Empty(provider.GetServices<IRealtimeProvider>());
        InferenceException error = await Assert.ThrowsAsync<InferenceException>(() => facade.OpenSessionAsync(Connection, new(), Limits));
        Assert.Equal(InferenceFailureCode.UnsupportedFeature, error.Code);
        Assert.Throws<ArgumentOutOfRangeException>(() => facade.GetRequiredProvider((RealtimeTransport)99));
        Assert.Throws<ArgumentException>(() => new RealtimeProviderService([new ControlledRealtimeProvider(), new ControlledRealtimeProvider()]));
    }

    [Fact]
    public async Task OpenFreezesSchemasAndHostedOptionsBeforeFirstAwait()
    {
        using var schema = JsonDocument.Parse("{\"type\":\"object\",\"required\":[\"actual\"]}");
        var handles = new List<string> { "original" };
        var entered = new TaskCompletionSource<RealtimeSessionConfig>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new ControlledRealtimeSession();
        InferenceStreamLimits suppliedLimits = Limits;
        var provider = new ControlledRealtimeProvider
        {
            Open = async (connection, config, limits, xai, resources, token) =>
            {
                Assert.Same(suppliedLimits, limits); Assert.Null(xai); Assert.Null(resources);
                entered.SetResult(config); await release.Task.ConfigureAwait(false);
                Assert.Equal("actual", config.Tools![0].InputSchema.GetProperty("required")[0].GetString());
                Assert.Equal("original", Assert.Single(Assert.IsType<XaiXSearchTool>(Assert.Single(config.HostedTools!)).AllowedHandles!));
                return session;
            }
        };
        var facade = new RealtimeProviderService([provider]);
        Task<IRealtimeVoiceSession> open = facade.OpenSessionAsync(Connection, new()
        { Tools = [new LlmToolDefinition { Name = "tool", InputSchema = schema.RootElement }], HostedTools = [new XaiXSearchTool { AllowedHandles = handles }] }, suppliedLimits);
        try
        {
            await entered.Task.WaitAsync(Deadline); schema.Dispose(); handles.Clear(); release.SetResult();
            Assert.Same(session, await open.WaitAsync(Deadline)); Assert.Equal(0, session.DisposeCalls);
        }
        finally
        {
            release.TrySetResult();
            await Record.ExceptionAsync(() => open.WaitAsync(Deadline));
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task CapabilityLookupRefusesLateCancellationWithoutInventingFacts()
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<RealtimeCapabilities>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ControlledRealtimeProvider { Capabilities = (connection, model, token) => { entered.SetResult(); return release.Task; } };
        var facade = new RealtimeProviderService([provider]);
        Task<RealtimeCapabilities> lookup = facade.GetCapabilitiesAsync(Connection, "manual", cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(Deadline); await cancellation.CancelAsync(); release.SetResult(new());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup.WaitAsync(Deadline));
        }
        finally { release.TrySetResult(new()); await Record.ExceptionAsync(() => lookup.WaitAsync(Deadline)); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateCancelledOpenRetiresUnhandedSessionAndPreservesCleanupFailure(bool cleanupFails)
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<IRealtimeVoiceSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new InvalidOperationException("controlled cleanup failure");
        var session = new ControlledRealtimeSession { CleanupError = cleanupFails ? cleanup : null };
        var provider = new ControlledRealtimeProvider { Open = (connection, config, limits, xai, resources, token) => { entered.SetResult(); return release.Task; } };
        Task<IRealtimeVoiceSession> open = new RealtimeProviderService([provider]).OpenSessionAsync(Connection, new(), Limits, cancellationToken: cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(Deadline); await cancellation.CancelAsync(); release.SetResult(session);
            Exception? error = await Record.ExceptionAsync(() => open.WaitAsync(Deadline));
            if (cleanupFails)
            {
                var aggregate = Assert.IsType<AggregateException>(error);
                Assert.Contains(aggregate.InnerExceptions, item => item is OperationCanceledException);
                Assert.Contains(cleanup, aggregate.InnerExceptions);
            }
            else Assert.IsAssignableFrom<OperationCanceledException>(error);
            Assert.Equal(1, session.DisposeCalls);
        }
        finally { await cancellation.CancelAsync(); release.TrySetResult(session); await Record.ExceptionAsync(() => open.WaitAsync(Deadline)); }
    }

    [Fact]
    public async Task SettingsPreserveFullDefaultsWithoutInferringReadiness()
    {
        var provider = new ControlledRealtimeProvider();
        var target = new ControlledCommitTarget();
        await using RealtimeSettingsManager manager = new RealtimeProviderService([provider]).CreateSettingsManager(target);
        using var schema = JsonDocument.Parse("{\"type\":\"object\"}");
        var config = new RealtimeSessionConfig
        {
            ModelId = "manual",
            Voice = "actual",
            Instructions = "",
            ReasoningEffort = LlmReasoningLevel.Low,
            Tools = [new LlmToolDefinition { Name = "tool", InputSchema = schema.RootElement }],
            HostedTools = [new XaiXSearchTool()],
            TurnDetection = new RealtimeManualTurnDetection(),
            InputAudio = new(RealtimeAudioEncoding.Opus, Transport: RealtimeAudioTransport.Binary),
            OutputAudio = new(RealtimeAudioEncoding.Mulaw),
            TranscriptionLanguage = "en",
            TranscriptionKeyTerms = ["actual"],
            OutputSpeed = 1.25,
            Resumption = new(false),
            ProviderOptions = new XaiRealtimeOptions(new Dictionary<string, string> { ["spoken"] = "replacement" })
        };
        var existing = new RealtimeSetupConfiguration(Connection, config, Limits);
        schema.Dispose();
        RealtimeSettings settings = await manager.OpenAsync(RealtimeTransport.XaiRealtime, new(SetupChangeIntent.Edit, "entry", "version"), existing);
        Assert.False(settings.HasUnsavedChanges); Assert.True(settings.CanSave);
        Assert.Equal(SetupActionState.Completed, (await manager.RunAsync(settings, "capabilities")).State);
        Assert.Null(settings.Status.Verified.Value); Assert.Null(settings.Status.Ready.Value); Assert.Null(settings.Status.Authenticated.Value);
        Assert.Equal(SetupCommitState.Committed, (await manager.SaveAsync(settings)).State);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(config with { Tools = existing.Session.Tools }, ConnectRealtimeJson.RealtimeSessionConfig),
            JsonSerializer.SerializeToElement(target.Changes!.Configuration.Session, ConnectRealtimeJson.RealtimeSessionConfig)));
        Assert.Equal(Limits, target.Changes.Configuration.StreamLimits);
        Assert.Null(target.Changes.Configuration.Connection.Authorization);
        Assert.Empty(target.Changes.Configuration.Connection.AdditionalHeaders);
        Assert.Equal(0, provider.OpenCalls);
    }

    [Fact]
    public async Task HeaderEditsRemainOneWayAndCommitAsAtomicPatch()
    {
        var target = new ControlledCommitTarget();
        await using RealtimeSettingsManager manager = new RealtimeProviderService([new ControlledRealtimeProvider()]).CreateSettingsManager(target);
        RealtimeSettings settings = await manager.OpenAsync(RealtimeTransport.XaiRealtime, new(SetupChangeIntent.Edit, "entry", "v"),
            new(Connection with { AdditionalHeaders = new Dictionary<string, string> { ["x-old"] = "original" } }, new()));
        var group = Assert.IsType<SetupRepeatableGroupField>(Field(settings, "headers"));
        SetupFieldRow row = Assert.Single(group.Rows);
        Assert.True(Assert.IsType<SetupSecretField>(row.Fields[1]).HasValue);
        Assert.Equal(SetupEditState.Applied, Assert.IsType<SetupTextField>(row.Fields[0]).SetValue("x-new").State);
        Assert.Equal(SetupEditState.Applied, Assert.IsType<SetupSecretField>(row.Fields[1]).SetValue(SetupSecretEdit.Replace("replacement")).State);
        Assert.Equal(SetupCommitState.Committed, (await manager.SaveAsync(settings)).State);
        Assert.Equal("replacement", target.Changes!.Headers.Set["X-NEW"]); Assert.Equal("x-old", Assert.Single(target.Changes.Headers.Remove));
        Assert.Empty(target.Changes.Configuration.Connection.AdditionalHeaders);
        Assert.DoesNotContain("replacement", target.Changes.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task BufferingBudgetsAreExplicitCompleteAndPositive(int changed)
    {
        await using RealtimeSettingsManager manager = new RealtimeProviderService([new ControlledRealtimeProvider()]).CreateSettingsManager(new ControlledCommitTarget());
        RealtimeSettings settings = await manager.OpenAsync(RealtimeTransport.XaiRealtime, Identity);
        Assert.Equal(SetupEditState.Applied, Assert.IsType<SetupTextField>(Field(settings, "xai-connection")).SetValue("selected").State);
        Assert.True(settings.CanSave);
        string[] ids = ["buffered-events", "buffered-bytes", "message-bytes"];
        Assert.IsType<SetupIntegerField>(Field(settings, ids[changed])).SetValue(1);
        Assert.False(settings.CanSave);
        foreach (string id in ids) Assert.IsType<SetupIntegerField>(Field(settings, id)).SetValue(1);
        Assert.True(settings.CanSave);
        Assert.IsType<SetupIntegerField>(Field(settings, ids[changed])).SetValue(0);
        Assert.False(settings.CanSave);
    }

    [Fact]
    public async Task FractionalTimeoutCannotBecomeZeroTicks()
    {
        await using RealtimeSettingsManager manager = new RealtimeProviderService([new ControlledRealtimeProvider()]).CreateSettingsManager(new ControlledCommitTarget());
        RealtimeSettings settings = await manager.OpenAsync(RealtimeTransport.XaiRealtime, Identity);
        Assert.IsType<SetupTextField>(Field(settings, "xai-connection")).SetValue("selected");
        Assert.IsType<SetupDecimalField>(Field(settings, "timeout")).SetValue(0.00000001m);
        Assert.False(settings.CanSave);
    }

    [Fact]
    public async Task EditedDraftCannotPublishStaleCapabilityCompletion()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<RealtimeCapabilities>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ControlledRealtimeProvider { Capabilities = (connection, model, token) => { Assert.Equal("original", model); entered.SetResult(); return release.Task; } };
        await using RealtimeSettingsManager manager = new RealtimeProviderService([provider]).CreateSettingsManager(new ControlledCommitTarget());
        RealtimeSettings settings = await manager.OpenAsync(RealtimeTransport.XaiRealtime, new(SetupChangeIntent.Edit, "entry", "v"), new(Connection, new() { ModelId = "original" }));
        Task<SetupActionResult> run = manager.RunAsync(settings, "capabilities");
        try
        {
            await entered.Task.WaitAsync(Deadline); Assert.IsType<SetupTextField>(Field(settings, "model")).SetValue("new"); release.SetResult(new());
            Assert.Equal(SetupActionState.Cancelled, (await run.WaitAsync(Deadline)).State); Assert.Null(settings.Status.Ready.Value);
        }
        finally { release.TrySetResult(new()); await run.WaitAsync(Deadline); }
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1859", Justification = "The regression exercises the public settings-manager interface dispatch.")]
    public async Task RetiringManagerDrainsIgnoredCancellationAndDetachesBorrowedTarget()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<RealtimeCapabilities>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ControlledRealtimeProvider { Capabilities = (connection, model, token) => { entered.SetResult(); return release.Task; } };
        var target = new ControlledCommitTarget();
        var manager = new RealtimeProviderService([provider]).CreateSettingsManager(target);
        RealtimeSettings settings = await manager.OpenAsync(RealtimeTransport.XaiRealtime, new(SetupChangeIntent.Edit, "entry", "v"), new(Connection, new()));
        IInferenceSettingsManager caller = manager;
        Task<SetupActionResult>? run = null;
        try
        {
            run = caller.RunAsync(settings, "capabilities"); await entered.Task.WaitAsync(Deadline);
            Task retirement = manager.DisposeAsync().AsTask(); Task second = manager.DisposeAsync().AsTask();
            Assert.False(retirement.IsCompleted);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => caller.SaveAsync(settings));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => caller.QueryChoicesAsync(settings, "model", new()));
            release.SetResult(new()); Assert.Equal(SetupActionState.Cancelled, (await run.WaitAsync(Deadline)).State);
            await Task.WhenAll(retirement, second).WaitAsync(Deadline);
            Assert.Equal(0, target.Calls); Assert.Equal(1, provider.Calls);
            Assert.Null(typeof(RealtimeSettingsManager).BaseType!.GetField("_target", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(manager));
        }
        finally
        {
            Task retirement = manager.DisposeAsync().AsTask(); release.TrySetResult(new());
            if (run is not null) await run.WaitAsync(Deadline);
            await retirement.WaitAsync(Deadline);
        }
    }

    [Fact]
    public async Task SynchronousOwnerFenceWinsBeforeLinkedCancellationCallbacksFinish()
    {
        CancellationToken enteredToken = default;
        int calls = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<RealtimeCapabilities>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var callbackRelease = new ManualResetEventSlim();
        var provider = new ControlledRealtimeProvider
        {
            Capabilities = (connection, model, token) =>
            {
                if (++calls == 1) return Task.FromResult(new RealtimeCapabilities());
                enteredToken = token; entered.SetResult(); return release.Task;
            }
        };
        var manager = new RealtimeProviderService([provider]).CreateSettingsManager(new ControlledCommitTarget());
        // A completed provider operation's linked token no longer represents the manager owner.
        object lifetime = typeof(RealtimeSettingsManager).GetField("_lifetime", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(manager)!;
        CancellationToken stop = ((CancellationTokenSource)lifetime.GetType().GetField("_stop", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(lifetime)!).Token;
        Task? retirement = null;
        CancellationTokenRegistration callback = default;
        try
        {
            RealtimeSettings settings = await manager.OpenAsync(RealtimeTransport.XaiRealtime, new(SetupChangeIntent.Edit, "entry", "v"), new(Connection, new()));
            Assert.Equal(SetupActionState.Completed, (await manager.RunAsync(settings, "capabilities")).State);
            using var callerCancellation = new CancellationTokenSource();
            Task<SetupActionResult> pending = manager.RunAsync(settings, "capabilities", cancellationToken: callerCancellation.Token);
            await entered.Task.WaitAsync(Deadline);
            callback = stop.Register(() => { callbackEntered.SetResult(); callbackRelease.Wait(); });
            retirement = manager.DisposeAsync().AsTask(); await callbackEntered.Task.WaitAsync(Deadline);
            Assert.False(enteredToken.IsCancellationRequested); release.SetResult(new());
            Assert.Equal(SetupActionState.Cancelled, (await pending.WaitAsync(Deadline)).State);
            Assert.Null(settings.Status.Ready.Value);
        }
        finally
        {
            release.TrySetResult(new()); callbackRelease.Set();
            callback.Dispose();
            if (retirement is not null) await retirement.WaitAsync(Deadline);
            await manager.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(SetupCommitState.Committed)]
    [InlineData(SetupCommitState.Uncertain)]
    public async Task RetirementPreservesActualAdmittedCommitOutcome(SetupCommitState outcome)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<SetupCommitResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var target = new ControlledCommitTarget { Commit = (changes, token) => { entered.SetResult(); return release.Task; } };
        var manager = new RealtimeProviderService([new ControlledRealtimeProvider()]).CreateSettingsManager(target);
        RealtimeSettings settings = await manager.OpenAsync(RealtimeTransport.XaiRealtime, new(SetupChangeIntent.Edit, "entry", "v"), new(Connection, new()));
        InferenceSettingsManager<RealtimeSettings, RealtimeSetupChanges> caller = manager;
        Task<SetupCommitResult>? save = null;
        try
        {
            save = caller.SaveAsync(settings); await entered.Task.WaitAsync(Deadline);
            Task retirement = manager.DisposeAsync().AsTask(); Assert.False(retirement.IsCompleted);
            release.SetResult(new(outcome, "observed"));
            Assert.Equal(outcome, (await save.WaitAsync(Deadline)).State); await retirement.WaitAsync(Deadline);
            Assert.True(settings.IsSaved); Assert.Equal(outcome == SetupCommitState.Uncertain, settings.SaveOutcomeUncertain); Assert.Equal(1, target.Calls);
        }
        finally
        {
            Task retirement = manager.DisposeAsync().AsTask(); release.TrySetResult(new(outcome, "observed"));
            if (save is not null) await save.WaitAsync(Deadline);
            await retirement.WaitAsync(Deadline);
        }
    }

    private static SetupField Field(RealtimeSettings settings, string id) => settings.Steps.SelectMany(step => step.Sections)
        .SelectMany(section => section.Fields).Single(field => field.Description.Id == id);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task NullProviderResultsAreProtocolFailureUnlessCallerCancelled(bool open, bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var provider = new ControlledRealtimeProvider
        {
            Capabilities = (connection, model, token) => { if (cancel) cancellation.Cancel(); return Task.FromResult<RealtimeCapabilities>(null!); },
            Open = (connection, config, limits, xai, resources, token) => { if (cancel) cancellation.Cancel(); return Task.FromResult<IRealtimeVoiceSession>(null!); }
        };
        var facade = new RealtimeProviderService([provider]);
        Exception? error = await Record.ExceptionAsync(async () =>
        {
            if (open) await facade.OpenSessionAsync(Connection, new(), Limits, cancellationToken: cancellation.Token);
            else await facade.GetCapabilitiesAsync(Connection, cancellationToken: cancellation.Token);
        });
        if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.Equal(InferenceFailureCode.ProtocolFailure, Assert.IsType<InferenceException>(error).Code);
    }

    [Fact]
    public async Task SettingsOpenRefusesLateBorrowedDefinitionAfterRetirement()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var provider = new ControlledRealtimeProvider { ReadDefinition = () => { entered.SetResult(); release.Wait(); } };
        var manager = new RealtimeProviderService([provider]).CreateSettingsManager(new ControlledCommitTarget());
        Task<RealtimeSettings>? opening = null;
        Task? retirement = null;
        try
        {
            opening = Task.Run(() => manager.OpenAsync(RealtimeTransport.XaiRealtime, Identity));
            await entered.Task.WaitAsync(Deadline);
            retirement = manager.DisposeAsync().AsTask(); Assert.False(retirement.IsCompleted);
            release.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(Deadline));
        }
        finally
        {
            release.Set();
            if (opening is not null) await Record.ExceptionAsync(() => opening.WaitAsync(Deadline));
            if (retirement is not null) await retirement.WaitAsync(Deadline);
            await manager.DisposeAsync();
        }
    }
}

internal sealed class ControlledRealtimeProvider : IRealtimeProvider
{
    public RealtimeTransport Transport => RealtimeTransport.XaiRealtime;
    private readonly InferenceSetupDefinition _definition = new(new("controlled-realtime", "Controlled", InferenceImplementationKind.Connect,
        [InferenceTask.RealtimeVoice]), "Controlled test boundary");
    internal Action? ReadDefinition { get; init; }
    public InferenceSetupDefinition SetupDefinition { get { ReadDefinition?.Invoke(); return _definition; } }
    internal int Calls { get; private set; }
    internal int OpenCalls { get; private set; }
    internal Func<RealtimeConnection, string?, CancellationToken, Task<RealtimeCapabilities>>? Capabilities { get; init; }
    internal Func<RealtimeConnection, RealtimeSessionConfig, InferenceStreamLimits, XaiFamilyBinding?, IInferenceResourceAccess?, CancellationToken, Task<IRealtimeVoiceSession>>? Open { get; init; }
    public Task<RealtimeCapabilities> GetCapabilitiesAsync(RealtimeConnection connection, string? modelId = null, CancellationToken cancellationToken = default)
    { Calls++; return Capabilities?.Invoke(connection, modelId, cancellationToken) ?? Task.FromResult(new RealtimeCapabilities()); }
    public Task<IRealtimeVoiceSession> OpenSessionAsync(RealtimeConnection connection, RealtimeSessionConfig config, InferenceStreamLimits limits,
        XaiFamilyBinding? xai = null, IInferenceResourceAccess? resources = null, CancellationToken cancellationToken = default)
    { Calls++; OpenCalls++; return Open?.Invoke(connection, config, limits, xai, resources, cancellationToken) ?? Task.FromResult<IRealtimeVoiceSession>(new ControlledRealtimeSession()); }
}

internal sealed class ControlledCommitTarget : ISetupCommitTarget<RealtimeSetupChanges>
{
    internal int Calls { get; private set; }
    internal RealtimeSetupChanges? Changes { get; private set; }
    internal Func<RealtimeSetupChanges, CancellationToken, Task<SetupCommitResult>>? Commit { get; init; }
    public Task<SetupCommitResult> CommitAsync(RealtimeSetupChanges changes, CancellationToken cancellationToken = default)
    { Calls++; Changes = changes; return Commit?.Invoke(changes, cancellationToken) ?? Task.FromResult(new SetupCommitResult(SetupCommitState.Committed)); }
}

internal sealed class ControlledRealtimeSession : IRealtimeVoiceSession
{
    internal int DisposeCalls { get; private set; }
    internal Exception? CleanupError { get; init; }
    public Task UpdateSessionAsync(RealtimeSessionConfig config, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task AppendAudioAsync(ReadOnlyMemory<byte> audio, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task CommitAudioAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task ClearAudioAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task AddItemAsync(RealtimeConversationItem item, string? previousItemId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeleteItemAsync(string itemId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task TruncateItemAsync(string itemId, int contentIndex, TimeSpan audioEnd, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task CreateResponseAsync(RealtimeResponseOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task CancelResponseAsync(string? responseId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task SubmitToolResultAsync(RealtimeToolResult result, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<RealtimeEvent> Events => throw new NotSupportedException();
    public ValueTask DisposeAsync() { DisposeCalls++; return CleanupError is null ? ValueTask.CompletedTask : ValueTask.FromException(CleanupError); }
}
