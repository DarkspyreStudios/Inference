using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Connect.LLM.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class RuntimeAuthenticationBindingTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    [Fact]
    public void ExactSavedAndDraftBindingMethodsAreCallableSharedSurfaces()
    {
        var saved = typeof(LlmAgentProviderService).GetMethod("BindRuntimeAuthentication", [typeof(string)]);
        var draft = typeof(LlmAgentProviderService).GetMethod("BindRuntimeAuthentication", [typeof(LlmConnection), typeof(SetupConfigurationIdentity)]);
        Assert.NotNull(saved); Assert.NotNull(draft);
        Assert.Equal(typeof(ILlmAgentRuntimeAuthentication), saved.ReturnType);
        Assert.Equal(typeof(ILlmAgentRuntimeAuthentication), draft.ReturnType);
    }

    [Fact]
    public void SettingsCompositionAcceptsOptionalBorrowedAgentFacade()
    {
        var method = typeof(LlmProviderService).GetMethod("CreateSettingsManager", [typeof(ISetupCommitTarget<LlmSetupChanges>), typeof(LlmAgentProviderService)]);
        Assert.NotNull(method);
        Assert.True(method.GetParameters()[1].HasDefaultValue); Assert.Null(method.GetParameters()[1].DefaultValue);
    }

    [Fact]
    public void SettingsRetirementReleasesBorrowedComposition()
    {
        Assert.Contains(typeof(IDisposable), typeof(LlmSettingsManager).GetInterfaces());
        Assert.Contains(typeof(IAsyncDisposable), typeof(LlmSettingsManager).GetInterfaces());
    }

    [Fact]
    public async Task UnavailableBindingValidatesAdmissionWithoutAnySourceEffects()
    {
        var source = new Source();
        var agents = new LlmAgentProviderService(source, []);
        try
        {
            Assert.Throws<ArgumentException>(() => agents.BindRuntimeAuthentication(""));
            Assert.Throws<NotSupportedException>(() => agents.BindRuntimeAuthentication("connection"));
            var draft = new LlmConnection { Transport = LlmTransport.AgentClientProtocol, Version = "draft", Endpoint = "/selected/executable" };
            Assert.Throws<NotSupportedException>(() => agents.BindRuntimeAuthentication(draft, new(SetupChangeIntent.Create, "new", null)));
            Assert.Throws<ArgumentException>(() => agents.BindRuntimeAuthentication(draft, new(SetupChangeIntent.Edit, "connection", null)));
            Assert.Throws<ArgumentException>(() => agents.BindRuntimeAuthentication(draft with { Endpoint = "relative" }, new(SetupChangeIntent.Edit, "connection", "version")));
            Assert.Throws<ArgumentOutOfRangeException>(() => agents.BindRuntimeAuthentication(draft with { Transport = LlmTransport.OpenAiResponses }, new(SetupChangeIntent.Create, "new", null)));
            Assert.Empty(agents.SupportedTransports); Assert.Equal(0, source.Effects);
            await agents.DisposeAsync();
            Assert.Throws<ObjectDisposedException>(() => agents.BindRuntimeAuthentication("connection"));
            Assert.Throws<ObjectDisposedException>(() => agents.BindRuntimeAuthentication(draft, new(SetupChangeIntent.Create, "new", null)));
            Assert.Equal(0, source.Effects); Assert.Equal(0, source.Disposals);
        }
        finally { await agents.DisposeAsync(); }
    }

    [Fact]
    public async Task ContractDeclarationsDoNotRegisterNewRuntimeAdapters()
    {
        var source = new Source();
        var services = new ServiceCollection();
        services.AddSingleton<ILlmConnectionSource>(source);
        services.AddLlmConnect();
        await using ServiceProvider container = services.BuildServiceProvider();
        await using AsyncServiceScope scope = container.CreateAsyncScope();
        var agents = scope.ServiceProvider.GetRequiredService<LlmAgentProviderService>();
        var llms = scope.ServiceProvider.GetRequiredService<LlmProviderService>();
        foreach (LlmTransport transport in new[] { LlmTransport.GitHubCopilot, LlmTransport.AgentClientProtocol })
        {
            Assert.DoesNotContain(transport, agents.SupportedTransports);
            Assert.DoesNotContain(transport, llms.SupportedTransports);
        }
        Assert.Throws<NotSupportedException>(() => agents.BindRuntimeAuthentication("connection"));
        Assert.Equal(0, source.Effects);
    }

    [Fact]
    public async Task OptionalAgentCompositionRequiresSameSourceAndRemainsBorrowed()
    {
        var source = new Source();
        await using var provider = new LlmProviderService(source, [new Client()]);
        await using var agents = new LlmAgentProviderService(source, []);
        await using var other = new LlmAgentProviderService(new Source(), []);
        Assert.Throws<ArgumentException>(() => provider.CreateSettingsManager(new Target(), other));
        var manager = provider.CreateSettingsManager(new Target(), agents);
        await manager.DisposeAsync();
        Assert.Throws<NotSupportedException>(() => agents.BindRuntimeAuthentication("connection"));
        Assert.Equal(0, source.Effects); Assert.Equal(0, source.Disposals);
        await other.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => provider.CreateSettingsManager(new Target(), other));
    }

    [Theory]
    [InlineData("direct", SetupCommitState.Committed)]
    [InlineData("base", SetupCommitState.Committed)]
    [InlineData("interface", SetupCommitState.Committed)]
    [InlineData("interface", SetupCommitState.Uncertain)]
    [InlineData("interface", SetupCommitState.Conflict)]
    public async Task RetirementDrainsIgnoringSaveAndPreservesActualCommitFacts(string dispatch, SetupCommitState state)
    {
        var target = new Target { Held = true, Result = new(state, "actual-version") };
        var source = new Source();
        var client = new Client();
        await using var provider = new LlmProviderService(source, [client]);
        await using var agents = new LlmAgentProviderService(source, []);
        var manager = provider.CreateSettingsManager(target, agents);
        Task<SetupCommitResult>? save = null;
        Task? retirement = null;
        try
        {
            LlmSettings settings = await Open(manager);
            save = dispatch switch
            {
                "base" => ((InferenceSettingsManager<LlmSettings, LlmSetupChanges>)manager).SaveAsync(settings),
                "interface" => ((IInferenceSettingsManager)manager).SaveAsync(settings),
                _ => manager.SaveAsync(settings)
            };
            await target.Entered.Task.WaitAsync(Bound);
            retirement = manager.DisposeAsync().AsTask();
            Assert.False(retirement.IsCompleted); Assert.Same(retirement, manager.DisposeAsync().AsTask());
            await Assert.ThrowsAsync<ObjectDisposedException>(() => manager.OpenAsync(client.Transport, new(SetupChangeIntent.Create, "other", null)));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => ((IInferenceSettingsManager)manager).RunAsync(settings, "check"));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => ((IInferenceSettingsManager)manager).QueryChoicesAsync(settings, "model", new()));
            Assert.Equal(0, target.Disposals); Assert.Equal(0, source.Disposals);
            target.Release.TrySetResult();
            SetupCommitResult result = await save.WaitAsync(Bound);
            Assert.Same(target.Result, result); Assert.Equal("actual-version", result.Version);
            Assert.Equal(state is SetupCommitState.Committed or SetupCommitState.Uncertain, settings.IsSaved);
            Assert.Equal(state == SetupCommitState.Uncertain, settings.SaveOutcomeUncertain);
            await retirement.WaitAsync(Bound);
            Assert.Equal(0, target.Disposals); Assert.Equal(0, client.Disposals);
            Assert.Throws<NotSupportedException>(() => agents.BindRuntimeAuthentication("connection"));
        }
        finally
        {
            retirement ??= manager.DisposeAsync().AsTask();
            target.Release.TrySetResult();
            try { if (save is not null) await save.WaitAsync(Bound); }
            finally { await retirement.WaitAsync(Bound); }
        }
    }

    [Theory]
    [InlineData("query")]
    [InlineData("check")]
    public async Task RetirementDrainsIgnoringProviderAndRefusesLatePublication(string lane)
    {
        var client = new Client { Held = true };
        await using var provider = new LlmProviderService(new Source(), [client]);
        var manager = provider.CreateSettingsManager(new Target());
        Task<SetupChoicePage>? query = null;
        Task<SetupActionResult>? action = null;
        Task? retirement = null;
        try
        {
            LlmSettings settings = await Open(manager);
            if (lane == "query") query = ((IInferenceSettingsManager)manager).QueryChoicesAsync(settings, "model", new());
            else action = ((IInferenceSettingsManager)manager).RunAsync(settings, "check");
            await client.Entered.Task.WaitAsync(Bound);
            retirement = manager.DisposeAsync().AsTask();
            Assert.False(retirement.IsCompleted);
            client.Release.TrySetResult();
            if (query is not null)
            {
                SetupChoicePage result = await query.WaitAsync(Bound);
                Assert.Equal(SetupChoiceState.Cancelled, result.State); Assert.Empty(result.Choices);
            }
            if (action is not null)
            {
                Assert.Equal(SetupActionState.Cancelled, (await action.WaitAsync(Bound)).State);
                Assert.Null(settings.Status.Verified.Value); Assert.Null(settings.Status.Authenticated.Value);
            }
            await retirement.WaitAsync(Bound);
            Assert.Equal(0, client.Disposals);
        }
        finally
        {
            retirement ??= manager.DisposeAsync().AsTask();
            client.Release.TrySetResult();
            try { if (query is not null) await query.WaitAsync(Bound); if (action is not null) await action.WaitAsync(Bound); }
            finally { await retirement.WaitAsync(Bound); }
        }
    }

    [Fact]
    public async Task ReentrantDefinitionRetirementFencesTheNextBorrowedGetter()
    {
        var client = new Client();
        await using var provider = new LlmProviderService(new Source(), [client]);
        var manager = provider.CreateSettingsManager(new Target());
        Task? retirement = null;
        try
        {
            int reads = client.TransportReads;
            client.OnDefinition = () => retirement = manager.DisposeAsync().AsTask();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.OpenAsync(LlmTransport.OpenAiCompatibleChat,
                new(SetupChangeIntent.Create, "new", null)));
            Assert.Equal(reads, client.TransportReads);
            await retirement!.WaitAsync(Bound); Assert.Equal(0, client.Disposals);
        }
        finally { client.OnDefinition = null; await (retirement ?? manager.DisposeAsync().AsTask()).WaitAsync(Bound); }
    }

    [Fact]
    public async Task ReentrantTemporaryServiceConstructionDoesNotReachLaterMetadataGetters()
    {
        var client = new Client();
        await using var provider = new LlmProviderService(new Source(), [client]);
        var manager = provider.CreateSettingsManager(new Target());
        Task? retirement = null;
        try
        {
            LlmSettings settings = await Open(manager);
            int definitions = client.DefinitionReads;
            client.OnTransport = () => { client.OnTransport = null; retirement = manager.DisposeAsync().AsTask(); };
            Assert.Equal(SetupActionState.Cancelled, (await manager.RunAsync(settings, "check")).State);
            Assert.Equal(definitions, client.DefinitionReads);
            await retirement!.WaitAsync(Bound);
        }
        finally
        {
            client.OnTransport = null;
            await (retirement ?? manager.DisposeAsync().AsTask()).WaitAsync(Bound);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NestedCredentialConstructionSharesAdmissionAndPreservesHealthyGetterCounts(bool retire)
    {
        var client = new Client();
        var first = new Authenticator(LlmTransport.OpenAiCompatibleChat);
        var second = new Authenticator(LlmTransport.OpenAiCodex);
        await using var provider = new LlmProviderService(new Source(), [client], [first, second]);
        var manager = provider.CreateSettingsManager(new Target());
        Task? retirement = null;
        try
        {
            LlmSettings settings = await Open(manager);
            int firstReads = first.Reads, secondReads = second.Reads;
            int transportReads = client.TransportReads, definitionReads = client.DefinitionReads;
            if (retire)
                first.OnRead = () => { if (first.Reads == firstReads + 2) retirement = manager.DisposeAsync().AsTask(); };
            SetupActionResult result = await manager.RunAsync(settings, "check");
            Assert.Equal(retire ? SetupActionState.Cancelled : SetupActionState.Completed, result.State);
            Assert.Equal(firstReads + 2, first.Reads);
            Assert.Equal(secondReads + (retire ? 1 : 2), second.Reads);
            if (!retire)
            {
                Assert.Equal(transportReads + 3, client.TransportReads);
                Assert.Equal(definitionReads + 3, client.DefinitionReads);
            }
            await (retirement ?? manager.DisposeAsync().AsTask()).WaitAsync(Bound);
        }
        finally
        {
            first.OnRead = null;
            await (retirement ?? manager.DisposeAsync().AsTask()).WaitAsync(Bound);
        }
    }

    [Fact]
    public async Task SynchronousRetirementFenceRejectsLateQueryBeforeLinkedCancellationPropagates()
    {
        var client = new Client { Held = true };
        await using var provider = new LlmProviderService(new Source(), [client]);
        var manager = provider.CreateSettingsManager(new Target());
        using var caller = new CancellationTokenSource();
        using var releaseCallback = new ManualResetEventSlim();
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration? registration = null;
        Task<SetupChoicePage>? query = null;
        Task? retirement = null;
        try
        {
            LlmSettings settings = await Open(manager);
            query = manager.QueryChoicesAsync(settings, "model", new(), caller.Token);
            await client.Entered.Task.WaitAsync(Bound);
            object lifetime = typeof(LlmSettingsManager).GetField("_lifetime", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager)!;
            var stop = (CancellationTokenSource)lifetime.GetType().GetField("_stop", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(lifetime)!;
            registration = stop.Token.Register(() => { callbackEntered.TrySetResult(); Assert.True(releaseCallback.Wait(Bound)); });
            retirement = manager.DisposeAsync().AsTask();
            await callbackEntered.Task.WaitAsync(Bound);
            Assert.True(stop.IsCancellationRequested); Assert.False(client.OperationToken.IsCancellationRequested);
            client.Release.TrySetResult();
            SetupChoicePage result = await query.WaitAsync(Bound);
            Assert.Equal(SetupChoiceState.Cancelled, result.State); Assert.Empty(result.Choices);
            Assert.False(retirement.IsCompleted);
            releaseCallback.Set();
            await retirement.WaitAsync(Bound);
        }
        finally
        {
            retirement ??= manager.DisposeAsync().AsTask();
            releaseCallback.Set(); client.Release.TrySetResult();
            try { if (query is not null) await query.WaitAsync(Bound); }
            finally
            {
                try { await retirement.WaitAsync(Bound); }
                finally { registration?.Dispose(); }
            }
        }
    }

    [Fact]
    public async Task RetirementPreservesCallbackFailureAndActualCommittedSaveForEveryDisposer()
    {
        var target = new Target { Held = true, FailCancellation = true };
        await using var provider = new LlmProviderService(new Source(), [new Client()]);
        var manager = provider.CreateSettingsManager(target);
        Task<SetupCommitResult>? save = null;
        Task? retirement = null;
        try
        {
            LlmSettings settings = await Open(manager);
            save = manager.SaveAsync(settings);
            await target.Entered.Task.WaitAsync(Bound);
            retirement = manager.DisposeAsync().AsTask();
            Assert.False(retirement.IsCompleted); Assert.Same(retirement, manager.DisposeAsync().AsTask());
            target.Release.TrySetResult();
            Assert.Same(target.Result, await save.WaitAsync(Bound));
            Assert.True(settings.IsSaved); Assert.False(settings.SaveOutcomeUncertain);
            AggregateException failure = await Assert.ThrowsAsync<AggregateException>(() => retirement.WaitAsync(Bound));
            Assert.Contains(target.CancellationFailure, failure.Flatten().InnerExceptions);
            Assert.Same(failure, await Assert.ThrowsAsync<AggregateException>(() => manager.DisposeAsync().AsTask()));
            Assert.Equal(0, target.Disposals);
        }
        finally
        {
            retirement ??= manager.DisposeAsync().AsTask();
            target.Release.TrySetResult();
            try { if (save is not null) await save.WaitAsync(Bound); }
            finally
            {
                AggregateException failure = await Assert.ThrowsAsync<AggregateException>(() => retirement.WaitAsync(Bound));
                Assert.Contains(target.CancellationFailure, failure.Flatten().InnerExceptions);
            }
        }
    }

    [Fact]
    public void RetainedDefaultSettingsReleaseActualForeignCollaboratorsAndBorrowedAgent()
    {
        RetirementEvidence evidence = Foreign();
        try
        {
            for (int attempt = 0; (evidence.Generation.IsAlive || evidence.Agent.IsAlive) && attempt < 30; attempt++)
            { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); }
            Assert.False(evidence.Generation.IsAlive); Assert.False(evidence.Agent.IsAlive);
            Assert.DoesNotContain("dispose-borrowed", evidence.Executed);
        }
        finally { GC.KeepAlive(evidence.Owners); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static RetirementEvidence Foreign()
    {
        var generation = new CollaboratorGeneration();
        var weak = new WeakReference(generation);
        var executed = new List<string>();
        try
        {
            Assembly fixture = generation.LoadFromAssemblyPath(typeof(RuntimeAuthenticationBindingTests).Assembly.Location);
            Assert.NotSame(typeof(RuntimeAuthenticationBindingTests).Assembly, fixture);
            Assert.Equal(typeof(RuntimeAuthenticationBindingTests).Assembly.ManifestModule.ModuleVersionId, fixture.ManifestModule.ModuleVersionId);
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(fixture));
            Type type = fixture.GetType(typeof(ForeignRuntimeCollaborator).FullName!, true)!;
            object borrowed = Activator.CreateInstance(type, (Action<string>)executed.Add)!;
            Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(typeof(ILlmConnectionSource).Assembly));
            var provider = new LlmProviderService((ILlmConnectionSource)borrowed, [(ILlmClient)borrowed], [(ILlmAuthenticator)borrowed], [(ILlmSetupEnvironment)borrowed]);
            var agents = new LlmAgentProviderService((ILlmConnectionSource)borrowed, []);
            var agentWeak = new WeakReference(agents);
            LlmSettingsManager manager = provider.CreateSettingsManager((ISetupCommitTarget<LlmSetupChanges>)borrowed, agents);
            Assert.IsType<LlmSettings>(Open(manager).GetAwaiter().GetResult());
            manager.Dispose(); provider.Dispose(); agents.Dispose();
            return new(weak, agentWeak, [manager, provider], executed);
        }
        finally { generation.Unload(); }
    }

    private sealed record RetirementEvidence(WeakReference Generation, WeakReference Agent, object[] Owners, List<string> Executed);
    private static Task<LlmSettings> Open(LlmSettingsManager manager) => manager.OpenAsync(LlmTransport.OpenAiCompatibleChat,
        new(SetupChangeIntent.Edit, "connection", "version"), new() { Transport = LlmTransport.OpenAiCompatibleChat,
            Version = "version", Endpoint = "https://provider.invalid/v1", Credential = new LlmCredential { ApiKey = "key" }.Serialize() }, "model");

    private sealed class Source : ILlmConnectionSource, IDisposable
    {
        public int Effects { get; private set; }
        public int Disposals { get; private set; }
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) { Effects++; throw new InvalidOperationException("No source reads are admitted."); }
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default) { Effects++; throw new InvalidOperationException("No credential writes are admitted."); }
        public void Dispose() => Disposals++;
    }
    private sealed class Target : ISetupCommitTarget<LlmSetupChanges>, IDisposable
    {
        public bool Held { get; init; }
        public bool FailCancellation { get; init; }
        public InvalidOperationException CancellationFailure { get; } = new("controlled cancellation cleanup failure");
        public SetupCommitResult Result { get; init; } = new(SetupCommitState.Committed, "version");
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Disposals { get; private set; }
        public async Task<SetupCommitResult> CommitAsync(LlmSetupChanges changes, CancellationToken cancellationToken = default)
        {
            using CancellationTokenRegistration registration = FailCancellation ? cancellationToken.Register(() => throw CancellationFailure) : default;
            Entered.TrySetResult(); if (Held) await Release.Task; return Result;
        }
        public void Dispose() => Disposals++;
    }
    private sealed class Client : ILlmClient, IDisposable
    {
        public bool Held { get; init; }
        public Action? OnDefinition { get; set; }
        public Action? OnTransport { get; set; }
        public int TransportReads { get; private set; }
        public int DefinitionReads { get; private set; }
        public int Disposals { get; private set; }
        public CancellationToken OperationToken { get; private set; }
        public LlmTransport Transport { get { TransportReads++; OnTransport?.Invoke(); return LlmTransport.OpenAiCompatibleChat; } }
        public LlmProviderDefinition ProviderDefinition { get { DefinitionReads++; OnDefinition?.Invoke(); return ProviderDefinitions.OpenAiCompatible; } }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LlmProviderCapabilities GetCapabilities(LlmConnection connection, string? modelId = null) => new() { SupportsModelListing = true };
        public async Task<IReadOnlyList<LlmModel>> ListModelsAsync(LlmConnection connection, CancellationToken cancellationToken = default)
        { OperationToken = cancellationToken; Entered.TrySetResult(); if (Held) await Release.Task; return [new("model", "Model")]; }
        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmConnection connection, LlmChatRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Dispose() => Disposals++;
    }
    private sealed class Authenticator(LlmTransport transport) : ILlmAuthenticator
    {
        public int Reads { get; private set; }
        public Action? OnRead { get; set; }
        public LlmTransport Transport { get { Reads++; OnRead?.Invoke(); return transport; } }
        public Task<LlmCredential> EnsureFreshAsync(LlmCredential credential, CancellationToken cancellationToken = default) => Task.FromResult(credential);
    }
    private sealed class CollaboratorGeneration() : AssemblyLoadContext(isCollectible: true);
}

public sealed class ForeignRuntimeCollaborator(Action<string> executed) : ILlmConnectionSource, ILlmClient,
    ILlmAuthenticator, ILlmSetupEnvironment, ISetupCommitTarget<LlmSetupChanges>, IDisposable
{
    public LlmTransport Transport => LlmTransport.OpenAiCompatibleChat;
    public LlmProviderDefinition ProviderDefinition => ProviderDefinitions.OpenAiCompatible;
    public LlmProviderCapabilities GetCapabilities(LlmConnection connection, string? modelId = null) => new();
    public IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmConnection connection, LlmChatRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<LlmModel>> ListModelsAsync(LlmConnection connection, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<LlmModel>>([]);
    public Task<LlmCredential> EnsureFreshAsync(LlmCredential credential, CancellationToken cancellationToken = default) => Task.FromResult(credential);
    public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<SetupChoice>> GetProfilesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SetupChoice>>([]);
    public Task<IReadOnlyList<SetupChoice>> GetRegionsAsync(string? profile, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SetupChoice>>([]);
    public SetupValidation Validate(LlmConnection connection) => SetupValidation.Valid;
    public Task<SetupCommitResult> CommitAsync(LlmSetupChanges changes, CancellationToken cancellationToken = default) => Task.FromResult(new SetupCommitResult(SetupCommitState.Committed));
    public void Dispose() => executed("dispose-borrowed");
}
