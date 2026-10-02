using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.TTS.Tests;

public sealed class TtsBindingTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    private static TtsConnection Connection => new() { Transport = TtsTransport.XaiTts, Endpoint = new("https://api.x.ai/v1"), Xai = new("selected") };
    private static TtsSetupConfiguration Configuration => new(Connection) { Language = "en", Format = TtsAudioFormat.Mp3 };
    private static SetupConfigurationIdentity Identity => new(SetupChangeIntent.Edit, "tts", "version");

    [Fact]
    public async Task OrdinaryTemporaryClientFailureDoesNotPoisonRetirement()
    {
        var fixture = new TtsBorrowedFixture { ClientOperationFailure = new InvalidOperationException("ordinary action") };
        using var access = new LlmConnectionAccess(new UntouchableSource(), []);
        await using var facade = new TtsProviderService([fixture]);
        var manager = facade.CreateSettingsManager(fixture, _ => new(new("selected"), access));
        try
        {
            var settings = await manager.OpenAsync(TtsTransport.XaiTts, Identity, Configuration);
            Assert.Equal(SetupActionState.Failed, (await manager.RunAsync(settings, "test")).State);
            await manager.DisposeAsync(); Assert.Equal(1, fixture.ClientDisposals); Assert.Equal(0, fixture.DisposeCalls);
        }
        finally { await manager.DisposeAsync(); }
    }

    [Fact]
    public async Task ConcurrentTemporaryCleanupFailuresAllSurviveSharedRetirement()
    {
        Exception first = new InvalidOperationException("first cleanup"), second = new InvalidOperationException("second cleanup");
        var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>([first, second]);
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); int entered = 0;
        var fixture = new TtsBorrowedFixture { HoldOperation = true, ClientCleanup = () => failures.TryDequeue(out var failure) ? failure : null };
        fixture.OnOperation = _ => { if (Interlocked.Increment(ref entered) == 2) both.TrySetResult(); };
        using var access = new LlmConnectionAccess(new UntouchableSource(), []);
        await using var facade = new TtsProviderService([fixture]);
        var manager = facade.CreateSettingsManager(fixture, _ => new(new("selected"), access));
        Task<SetupActionResult>? action = null; Task<SetupChoicePage>? query = null; Task? retirement = null;
        try
        {
            var settings = await manager.OpenAsync(TtsTransport.XaiTts, Identity, Configuration);
            action = manager.RunAsync(settings, "test"); query = manager.QueryChoicesAsync(settings, "voice", new());
            await both.Task.WaitAsync(Bound); retirement = manager.DisposeAsync().AsTask(); Assert.False(retirement.IsCompleted);
            fixture.Release.TrySetResult(); await Task.WhenAll(action, query).WaitAsync(Bound);
            var failure = Assert.IsType<AggregateException>(await Record.ExceptionAsync(() => retirement.WaitAsync(Bound)));
            Assert.Contains(first, failure.Flatten().InnerExceptions); Assert.Contains(second, failure.Flatten().InnerExceptions);
            Assert.Same(failure, await Record.ExceptionAsync(() => manager.DisposeAsync().AsTask()));
            Assert.Equal(2, fixture.ClientDisposals); Assert.Equal(0, fixture.DisposeCalls);
        }
        finally
        {
            retirement ??= manager.DisposeAsync().AsTask(); fixture.Release.TrySetResult();
            try { await Task.WhenAll(new Task?[] { action, query }.OfType<Task>()).WaitAsync(Bound); }
            finally { _ = await Record.ExceptionAsync(() => retirement.WaitAsync(Bound)); }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TemporaryCleanupFailureSurvivesStructuredActionAndSharedRetirement(bool operationFails)
    {
        var operation = new InvalidOperationException("controlled operation");
        var cleanup = new InvalidOperationException("controlled cleanup");
        var fixture = new TtsBorrowedFixture { ClientCleanupFailure = cleanup, ClientOperationFailure = operationFails ? operation : null };
        using var access = new LlmConnectionAccess(new UntouchableSource(), []);
        var facade = new TtsProviderService([fixture]);
        var manager = facade.CreateSettingsManager(fixture, _ => new(new("selected"), access), fixture);
        try
        {
            var settings = await manager.OpenAsync(TtsTransport.XaiTts, Identity, Configuration);
            Assert.Equal(SetupActionState.Failed, (await manager.RunAsync(settings, "test")).State);
            Exception? failure = await Record.ExceptionAsync(() => manager.DisposeAsync().AsTask());
            Assert.NotNull(failure);
            if (operationFails)
            {
                var aggregate = Assert.IsType<AggregateException>(failure);
                Assert.Same(operation, aggregate.InnerExceptions[0]); Assert.Same(cleanup, aggregate.InnerExceptions[1]);
            }
            else Assert.Same(cleanup, failure);
            Assert.Same(failure, await Record.ExceptionAsync(() => manager.DisposeAsync().AsTask()));
            Assert.Equal(1, fixture.ClientDisposals); Assert.Equal(0, fixture.DisposeCalls); Assert.Null(fixture.Resources);
        }
        finally { _ = await Record.ExceptionAsync(() => manager.DisposeAsync().AsTask()); await facade.DisposeAsync(); }
    }

    [Fact]
    public async Task LateBindRetiresUnhandedClientAndPreservesCleanupFailure()
    {
        var cleanup = new InvalidOperationException("controlled cleanup");
        var fixture = new TtsBorrowedFixture { ClientCleanupFailure = cleanup };
        using var access = new LlmConnectionAccess(new UntouchableSource(), []);
        await using var facade = new TtsProviderService([fixture]);
        var manager = facade.CreateSettingsManager(fixture, _ => new(new("selected"), access));
        Task? retirement = null;
        try
        {
            var settings = await manager.OpenAsync(TtsTransport.XaiTts, Identity, Configuration);
            fixture.OnBind = () => retirement = manager.DisposeAsync().AsTask();
            Assert.Equal(SetupActionState.Cancelled, (await manager.RunAsync(settings, "test")).State);
            var failure = Assert.IsType<AggregateException>(await Record.ExceptionAsync(() => retirement!));
            Assert.IsAssignableFrom<OperationCanceledException>(failure.InnerExceptions[0]); Assert.Same(cleanup, failure.InnerExceptions[1]);
            Assert.Equal(1, fixture.ClientDisposals); Assert.Equal(0, fixture.DisposeCalls);
        }
        finally { _ = await Record.ExceptionAsync(() => manager.DisposeAsync().AsTask()); }
    }

    [Fact]
    public void BindingForwardsFrozenConnectionAndSameBorrowedOwnersWithoutAcquisition()
    {
        var source = new UntouchableSource(); using var access = new LlmConnectionAccess(source, []);
        var binding = new XaiFamilyBinding(new("selected"), access); var fixture = new TtsBorrowedFixture();
        var headers = new Dictionary<string, string> { ["X-Test"] = "actual" };
        TtsConnection connection = Connection with { Headers = headers, Authorization = binding.Authorization };
        headers["X-Test"] = "changed";
        var service = new TtsProviderService([fixture]);
        using ITtsClient client = service.Bind(connection, binding, fixture);
        Assert.Same(fixture.LastClient, client);
        Assert.Same(connection, fixture.Connection); Assert.Same(binding, fixture.Binding); Assert.Same(fixture, fixture.Resources);
        Assert.Equal("actual", fixture.Connection!.Headers["x-test"]); Assert.Equal(0, source.Calls);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string>)connection.Headers).Add("x", "y"));
        Assert.Throws<ArgumentNullException>(() => connection with { Headers = null! });
        Assert.Throws<ArgumentException>(() => service.Bind(connection, new(new("other"), access), fixture));
        Assert.Throws<ArgumentException>(() => service.Bind(connection with { Authorization = new Authority() }, binding, fixture));
        Assert.Equal(1, fixture.Binds); Assert.Equal(0, source.Calls);
    }

    [Theory]
    [InlineData("Authorization", "secret")]
    [InlineData("Host", "invalid")]
    [InlineData("Accept", "invalid")]
    [InlineData("Content-Type", "invalid")]
    [InlineData("api-key", "secret")]
    [InlineData("bad name", "value")]
    [InlineData("X-Test", "value\r\nHost: invalid")]
    public async Task InvalidHeadersRefuseBeforeBindingFactoryProviderOrCommit(string name, string value)
    {
        var source = new UntouchableSource(); using var access = new LlmConnectionAccess(source, []);
        var binding = new XaiFamilyBinding(new("selected"), access); var fixture = new TtsBorrowedFixture();
        var service = new TtsProviderService([fixture]); int factories = 0;
        TtsConnection connection = Connection with { Headers = new Dictionary<string, string> { [name] = value } };
        Assert.ThrowsAny<Exception>(() => service.Bind(connection, binding, fixture));
        await using TtsSettingsManager manager = service.CreateSettingsManager(fixture, _ => { factories++; return binding; }, fixture);
        TtsSettings settings = await manager.OpenAsync(TtsTransport.XaiTts, Identity, Configuration with { Connection = connection });
        Assert.False(settings.Validate().IsValid);
        Assert.Equal(SetupActionState.Unavailable, (await manager.RunAsync(settings, "check")).State);
        Assert.Equal(SetupCommitState.Invalid, (await manager.SaveAsync(settings)).State);
        Assert.Equal(0, factories); Assert.Equal(0, fixture.Binds); Assert.Equal(0, fixture.Commits); Assert.Equal(0, source.Calls);
    }

    [Theory]
    [InlineData("binding")]
    [InlineData("authorization")]
    [InlineData("selection")]
    public void PocketRefusesNewAuthorityBeforeAnyHttpEffect(string kind)
    {
        using var access = new LlmConnectionAccess(new UntouchableSource(), []); using var http = new HttpClient();
        var binding = new XaiFamilyBinding(new("selected"), access);
        TtsConnection connection = new() { Endpoint = new("http://127.0.0.1:8020") };
        if (kind == "authorization") connection = connection with { Authorization = binding.Authorization };
        if (kind == "selection") connection = connection with { Xai = binding.Selection };
        InferenceException failure = Assert.Throws<InferenceException>(() => new PocketTtsHttpProvider(http).Bind(connection, kind == "binding" ? binding : null));
        Assert.Equal(InferenceFailureCode.UnsupportedFeature, failure.Code);
    }

    [Fact]
    public async Task SetupPreservesAllControlsAndStripsRuntimeAuthorityFromPortableCommit()
    {
        var fixture = new TtsBorrowedFixture(); using var access = new LlmConnectionAccess(new UntouchableSource(), []);
        var binding = new XaiFamilyBinding(new("selected"), access); int factories = 0;
        await using TtsSettingsManager manager = new TtsProviderService([fixture]).CreateSettingsManager(fixture, _ => { factories++; return binding; }, fixture);
        var original = Configuration with
        {
            Connection = Connection with
            {
                Authorization = binding.Authorization,
                Headers = new Dictionary<string, string> { ["X-Keep"] = "protected" },
                XaiDefaults = new(0, false, new Dictionary<string, string> { ["actual"] = "" })
            },
            Speed = 1.25,
            VoiceId = "manual",
            SampleRate = 0,
            BitRate = 0,
            IncludeTimestamps = false
        };
        TtsSettings settings = await manager.OpenAsync(TtsTransport.XaiTts, Identity, original);
        Assert.False(settings.HasUnsavedChanges); Assert.True(settings.CanSave);
        Assert.Equal(SetupActionState.Completed, (await manager.RunAsync(settings, "test")).State);
        Assert.Equal(JsonSerializer.Serialize(original.RequestForTest(), ConnectTtsJson.TtsRequest), JsonSerializer.Serialize(fixture.Request!, ConnectTtsJson.TtsRequest));
        Assert.Same(binding.Authorization, fixture.Connection!.Authorization); Assert.Same(binding, fixture.Binding); Assert.Null(fixture.Resources);
        Assert.Equal(SetupCommitState.Committed, (await manager.SaveAsync(settings)).State);
        Assert.Null(fixture.Changes!.Configuration.Connection.Authorization); Assert.Empty(fixture.Changes.Configuration.Connection.Headers);
        Assert.Empty(fixture.Changes.Headers.Set); Assert.Empty(fixture.Changes.Headers.Remove); Assert.Equal(1, factories);
        string json = JsonSerializer.Serialize(fixture.Changes, ConnectTtsJson.TtsSetupChanges);
        Assert.NotNull(JsonSerializer.Deserialize(json, ConnectTtsJson.TtsSetupChanges));
        Assert.Equal(0, fixture.DisposeCalls);
    }

    [Fact]
    public async Task SetupEditsKeepExplicitFalseZeroEmptyAndReplacementEntries()
    {
        var fixture = new TtsBorrowedFixture(); await using TtsSettingsManager manager = new TtsProviderService([fixture]).CreateSettingsManager(fixture);
        TtsSettings settings = await manager.OpenAsync(TtsTransport.XaiTts, new(SetupChangeIntent.Create, "tts", null));
        Field<SetupTextField>(settings, "xai-connection").SetValue("selected"); Field<SetupTextField>(settings, "language").SetValue("en");
        Field<SetupChoiceField>(settings, "format").SetValue("Pcm"); Field<SetupIntegerField>(settings, "sample-rate").SetValue(0);
        Field<SetupIntegerField>(settings, "bit-rate").SetValue(0); Field<SetupIntegerField>(settings, "latency").SetValue(0);
        Field<SetupChoiceField>(settings, "timestamps").SetValue("False"); Field<SetupChoiceField>(settings, "normalization").SetValue("False");
        var replacements = Field<SetupRepeatableGroupField>(settings, "replacements"); Assert.Equal(SetupEditState.Applied, replacements.Add().State);
        SetupFieldRow row = Assert.Single(Field<SetupRepeatableGroupField>(settings, "replacements").Rows);
        Assert.Equal(SetupEditState.Applied, ((SetupTextField)row.Fields[0]).SetValue("original").State);
        Assert.Equal(SetupEditState.Applied, ((SetupTextField)row.Fields[1]).SetValue("").State);
        Assert.Equal(SetupCommitState.Committed, (await manager.SaveAsync(settings)).State);
        var saved = fixture.Changes!.Configuration;
        Assert.Equal(TtsAudioFormat.Pcm, saved.Format); Assert.Equal(0, saved.SampleRate); Assert.Equal(0, saved.BitRate);
        Assert.False(saved.IncludeTimestamps); Assert.Equal(0, saved.Connection.XaiDefaults!.OptimizeStreamingLatency); Assert.False(saved.Connection.XaiDefaults.TextNormalization);
        Assert.Equal("", saved.Connection.XaiDefaults.Replace!["original"]); Assert.Equal(0, fixture.Binds);
    }

    [Theory]
    [InlineData("check")]
    [InlineData("test")]
    [InlineData("query")]
    public async Task RetirementDrainsIgnoringCallsAndNeverPublishesLateSetupSuccess(string kind)
    {
        var fixture = new TtsBorrowedFixture { HoldOperation = true }; using var access = new LlmConnectionAccess(new UntouchableSource(), []);
        var binding = new XaiFamilyBinding(new("selected"), access);
        TtsSettingsManager manager = new TtsProviderService([fixture]).CreateSettingsManager(fixture, _ => binding, fixture);
        Task? pending = null, retirement = null;
        try
        {
            TtsSettings settings = await manager.OpenAsync(TtsTransport.XaiTts, Identity, Configuration);
            InferenceSettingsManager<TtsSettings, TtsSetupChanges> caller = manager;
            pending = kind == "query" ? caller.QueryChoicesAsync(settings, "voice", new()) : caller.RunAsync(settings, kind);
            await fixture.Entered.Task.WaitAsync(Bound); retirement = manager.DisposeAsync().AsTask(); Assert.False(retirement.IsCompleted);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => caller.SaveAsync(settings));
            fixture.Release.TrySetResult(); await pending.WaitAsync(Bound);
            if (pending is Task<SetupActionResult> action) Assert.Equal(SetupActionState.Cancelled, (await action).State);
            if (pending is Task<SetupChoicePage> choices) Assert.Equal(SetupChoiceState.Cancelled, (await choices).State);
            Assert.Null(settings.Status.Ready.Value); Assert.NotEqual(true, settings.Status.Verified.Value);
        }
        finally { retirement ??= manager.DisposeAsync().AsTask(); fixture.Release.TrySetResult(); try { if (pending is not null) await pending; } finally { await retirement.WaitAsync(Bound); } }
        Assert.Equal(0, fixture.DisposeCalls);
    }

    [Theory]
    [InlineData(SetupCommitState.Committed)]
    [InlineData(SetupCommitState.Uncertain)]
    public async Task RetirementPreservesObservedConditionalCommit(SetupCommitState outcome)
    {
        var fixture = new TtsBorrowedFixture { HoldCommit = true, Outcome = outcome };
        TtsSettingsManager manager = new TtsProviderService([fixture]).CreateSettingsManager(fixture);
        Task<SetupCommitResult>? pending = null; Task? retirement = null;
        try
        {
            TtsSettings settings = await manager.OpenAsync(TtsTransport.XaiTts, Identity, Configuration);
            pending = manager.SaveAsync(settings); await fixture.Entered.Task.WaitAsync(Bound);
            retirement = manager.DisposeAsync().AsTask(); Assert.Same(retirement, manager.DisposeAsync().AsTask()); Assert.False(retirement.IsCompleted);
            fixture.Release.TrySetResult(); Assert.Equal(outcome, (await pending.WaitAsync(Bound)).State);
            Assert.True(settings.IsSaved); Assert.Equal(outcome == SetupCommitState.Uncertain, settings.SaveOutcomeUncertain);
        }
        finally { retirement ??= manager.DisposeAsync().AsTask(); fixture.Release.TrySetResult(); try { if (pending is not null) await pending; } finally { await retirement.WaitAsync(Bound); } }
        Assert.Equal(1, fixture.Commits); Assert.Equal(0, fixture.DisposeCalls);
    }

    [Fact]
    public async Task OpenRefusesLateResultFromRetiringBorrowedDefinitionGetter()
    {
        var fixture = new TtsBorrowedFixture();
        TtsSettingsManager manager = new TtsProviderService([fixture]).CreateSettingsManager(fixture);
        Task? retirement = null;
        int readsAtFence = -1;
        try
        {
            fixture.OnDefinition = () => { readsAtFence = fixture.TransportReads; retirement = manager.DisposeAsync().AsTask(); Assert.False(retirement.IsCompleted); };
            await Assert.ThrowsAsync<OperationCanceledException>(() => manager.OpenAsync(TtsTransport.XaiTts, Identity, Configuration));
            Assert.Equal(readsAtFence, fixture.TransportReads);
        }
        finally { retirement ??= manager.DisposeAsync().AsTask(); await retirement.WaitAsync(Bound); }
        Assert.Equal(0, fixture.DisposeCalls);
    }

    [Fact]
    public async Task FactoryDraftEditRefusesProviderOperationAndHeldHealthCannotVerifyEditedDraft()
    {
        var fixture = new TtsBorrowedFixture { HoldOperation = true }; using var access = new LlmConnectionAccess(new UntouchableSource(), []);
        var binding = new XaiFamilyBinding(new("selected"), access); TtsSettings? settings = null; bool edit = true;
        await using TtsSettingsManager manager = new TtsProviderService([fixture]).CreateSettingsManager(fixture, _ =>
        { if (edit) Field<SetupTextField>(settings!, "language").SetValue("fr"); return binding; });
        settings = await manager.OpenAsync(TtsTransport.XaiTts, Identity, Configuration);
        Assert.Equal(SetupActionState.Cancelled, (await manager.RunAsync(settings, "check")).State);
        Assert.False(fixture.Entered.Task.IsCompleted); Assert.Null(settings.Status.Verified.Value);
        edit = false; Task<SetupActionResult>? pending = null;
        try
        {
            pending = manager.RunAsync(settings, "check"); await fixture.Entered.Task.WaitAsync(Bound);
            Field<SetupTextField>(settings, "language").SetValue("de"); fixture.Release.TrySetResult();
            Assert.Equal(SetupActionState.Cancelled, (await pending.WaitAsync(Bound)).State);
            Assert.Null(settings.Status.Verified.Value);
        }
        finally { fixture.Release.TrySetResult(); if (pending is not null) await pending.WaitAsync(Bound); }
    }

    [Fact]
    public async Task SynchronousRetirementFencePrecedesDelayedLinkedCancellation()
    {
        var fixture = new TtsBorrowedFixture(); using var access = new LlmConnectionAccess(new UntouchableSource(), []);
        var binding = new XaiFamilyBinding(new("selected"), access);
        TtsSettingsManager manager = new TtsProviderService([fixture]).CreateSettingsManager(fixture, _ => binding);
        using var caller = new CancellationTokenSource(); using var callbackRelease = new ManualResetEventSlim();
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken stop = default, operation = default; CancellationTokenRegistration registration = default;
        Task<SetupActionResult>? pending = null; Task? retirement = null;
        try
        {
            TtsSettings settings = await manager.OpenAsync(TtsTransport.XaiTts, Identity, Configuration);
            fixture.OnOperation = token => stop = token;
            Assert.Equal(SetupActionState.Completed, (await manager.RunAsync(settings, "check")).State);
            fixture.HoldOperation = true; fixture.OnOperation = token => { operation = token; operationEntered.TrySetResult(); };
            pending = manager.RunAsync(settings, "test", cancellationToken: caller.Token);
            await operationEntered.Task.WaitAsync(Bound);
            registration = stop.Register(() => { callbackEntered.TrySetResult(); callbackRelease.Wait(Bound); });
            retirement = manager.DisposeAsync().AsTask(); await callbackEntered.Task.WaitAsync(Bound);
            Assert.True(stop.IsCancellationRequested); Assert.False(operation.IsCancellationRequested);
            fixture.Release.TrySetResult(); Assert.Equal(SetupActionState.Cancelled, (await pending.WaitAsync(Bound)).State);
            Assert.Null(settings.Status.Ready.Value); Assert.False(retirement.IsCompleted);
        }
        finally
        {
            retirement ??= manager.DisposeAsync().AsTask(); callbackRelease.Set(); fixture.Release.TrySetResult();
            try { if (pending is not null) await pending.WaitAsync(Bound); }
            finally { try { await retirement.WaitAsync(Bound); } finally { registration.Dispose(); } }
        }
        Assert.Equal(0, fixture.DisposeCalls);
    }

    [Fact]
    public void RuntimeEnvelopeUsesActualOwnedAuthorityConvertersWithoutEffects()
    {
        var authority = new Authority(); using var access = new LlmConnectionAccess(new UntouchableSource(), []);
        var binding = new XaiFamilyBinding(new("selected"), access); var fixture = new TtsBorrowedFixture();
        var service = new TtsProviderService([fixture]); using TtsSettingsManager manager = service.CreateSettingsManager(fixture);
        var options = new JsonSerializerOptions(ConnectTtsJson.TtsConnection.Options) { TypeInfoResolver = TtsRuntimeContext.Default }; options.MakeReadOnly();
        JsonTypeInfo<TtsRuntimeEnvelope> metadata = options.GetTypeInfo<TtsRuntimeEnvelope>();
        foreach (TtsRuntimeEnvelope value in new TtsRuntimeEnvelope[] { new() { Authorization = authority }, new() { Binding = binding }, new() { Resources = fixture }, new() { Factory = _ => binding }, new() { Service = service }, new() { Manager = manager } })
            Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(value, metadata));
        foreach (string property in new[] { "Authorization", "Binding", "Resources", "Factory", "Service", "Manager" })
        {
            Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize("{\"" + property + "\":{}}", metadata));
            Assert.NotNull(JsonSerializer.Deserialize("{\"" + property + "\":null}", metadata));
            Type type = typeof(TtsRuntimeEnvelope).GetProperty(property)!.PropertyType;
            Assert.Equal("null", JsonSerializer.Serialize((object?)null, type, ConnectTtsJson.TtsConnection.Options));
        }
        Assert.Same(typeof(InferenceJson).Assembly, options.GetConverter(typeof(IProviderAuthorization)).GetType().Assembly);
        Assert.Same(typeof(ConnectLlmJson).Assembly, options.GetConverter(typeof(XaiFamilyBinding)).GetType().Assembly);
        Assert.Equal(0, authority.Calls); Assert.Equal(0, fixture.Binds);
    }

    [Fact]
    public void RetiredDefaultManagerReleasesActualForeignBorrowedCollaborators()
    {
        (TtsSettingsManager retained, WeakReference[] roots) = RetireForeignBorrowers();
        for (int attempt = 0; roots.Any(root => root.IsAlive) && attempt < 30; attempt++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); }
        Assert.All(roots, root => Assert.False(root.IsAlive)); GC.KeepAlive(retained);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (TtsSettingsManager, WeakReference[]) RetireForeignBorrowers()
    {
        var generation = new FixtureGeneration(); var weak = new WeakReference(generation);
        Assembly assembly = generation.LoadFromAssemblyPath(typeof(TtsBindingTests).Assembly.Location);
        Assert.NotSame(typeof(TtsBindingTests).Assembly, assembly); Assert.Same(generation, AssemblyLoadContext.GetLoadContext(assembly));
        Assert.Equal(typeof(TtsBindingTests).Assembly.ManifestModule.ModuleVersionId, assembly.ManifestModule.ModuleVersionId);
        Type type = assembly.GetType(typeof(TtsBorrowedFixture).FullName!, true)!; object foreign = Activator.CreateInstance(type)!;
        Assert.Same(assembly, foreign.GetType().Assembly);
        var provider = (ITtsProvider)foreign; var target = (ISetupCommitTarget<TtsSetupChanges>)foreign;
        var factory = type.GetMethod(nameof(TtsBorrowedFixture.CreateBinding))!.CreateDelegate<Func<XaiConnectionSelection, XaiFamilyBinding>>(foreign);
        TtsSettingsManager manager = new TtsProviderService([provider]).CreateSettingsManager(target, factory, (IInferenceResourceAccess)foreign);
        TtsSettings settings = manager.OpenAsync(TtsTransport.XaiTts, Identity, Configuration).GetAwaiter().GetResult();
        Assert.Equal(SetupActionState.Completed, manager.RunAsync(settings, "check").GetAwaiter().GetResult().State);
        Assert.Equal(SetupCommitState.Committed, manager.SaveAsync(settings).GetAwaiter().GetResult().State);
        manager.Dispose(); Assert.Equal(0, (int)type.GetProperty(nameof(TtsBorrowedFixture.DisposeCalls))!.GetValue(foreign)!);
        Assert.Equal(1, (int)type.GetProperty(nameof(TtsBorrowedFixture.ClientDisposals))!.GetValue(foreign)!);
        Assert.Equal(1, (int)type.GetProperty(nameof(TtsBorrowedFixture.FactoryCalls))!.GetValue(foreign)!);
        Assert.Equal(1, (int)type.GetProperty(nameof(TtsBorrowedFixture.Commits))!.GetValue(foreign)!);
        WeakReference[] roots = [weak, new(assembly), new(type), new(foreign), new(factory), new(factory.Method)];
        generation.Unload(); return (manager, roots);
    }

    private static T Field<T>(TtsSettings settings, string id) where T : SetupField => (T)settings.Steps.SelectMany(step => step.Sections).SelectMany(section => section.Fields).Single(field => field.Description.Id == id);
    private sealed class FixtureGeneration() : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name) => name.Name?.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal) == true && name.Name != typeof(TtsBindingTests).Assembly.GetName().Name
            ? Default.Assemblies.SingleOrDefault(assembly => assembly.GetName().Name == name.Name) : null;
    }
    private sealed class Authority : IProviderAuthorization
    {
        internal int Calls { get; private set; }
        public ValueTask<ProviderAuthorizationHeader> GetAsync(CancellationToken cancellationToken = default) { Calls++; throw new InvalidOperationException(); }
        public override string ToString() { Calls++; throw new InvalidOperationException(); }
    }
    private sealed class UntouchableSource : ILlmConnectionSource
    {
        internal int Calls { get; private set; }
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) { Calls++; throw new InvalidOperationException("No acquisition is permitted."); }
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default) { Calls++; throw new InvalidOperationException(); }
    }
}

public sealed class TtsBorrowedFixture : ITtsProvider, ITtsClient, ISetupCommitTarget<TtsSetupChanges>, IInferenceResourceAccess, IDisposable
{
    public int TransportReads { get; private set; }
    public TtsTransport Transport { get { TransportReads++; return TtsTransport.XaiTts; } }
    private readonly InferenceSetupDefinition _definition = new(new("controlled-tts", "Controlled TTS", InferenceImplementationKind.Connect, InferenceTask.SpeechSynthesis), "Managed fixture.");
    public InferenceSetupDefinition SetupDefinition { get { OnDefinition?.Invoke(); return _definition; } }
    public Action? OnDefinition { get; set; }
    public Action<CancellationToken>? OnOperation { get; set; }
    public InferenceImplementationDescriptor Descriptor => SetupDefinition.Implementation;
    public TtsConnection? Connection { get; private set; }
    public XaiFamilyBinding? Binding { get; private set; }
    public IInferenceResourceAccess? Resources { get; private set; }
    public TtsRequest? Request { get; private set; }
    public ITtsClient? LastClient { get; private set; }
    public Action? OnBind { get; set; }
    private int _clientDisposals;
    public int ClientDisposals => Volatile.Read(ref _clientDisposals);
    public Exception? ClientCleanupFailure { get; init; }
    public Func<Exception?>? ClientCleanup { get; init; }
    public Exception? ClientOperationFailure { get; init; }
    public TtsSetupChanges? Changes { get; private set; }
    public int Binds { get; private set; }
    public int Commits { get; private set; }
    public int DisposeCalls { get; private set; }
    public int FactoryCalls { get; private set; }
    public bool HoldOperation { get; set; }
    public bool HoldCommit { get; init; }
    public SetupCommitState Outcome { get; init; } = SetupCommitState.Committed;
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ITtsClient Bind(TtsConnection connection, XaiFamilyBinding? xai = null, IInferenceResourceAccess? resources = null)
    { Binds++; Connection = connection; Binding = xai; Resources = resources; LastClient = new BoundClient(this); OnBind?.Invoke(); return LastClient; }
    public XaiFamilyBinding CreateBinding(XaiConnectionSelection selection) { FactoryCalls++; return new(selection, new LlmConnectionAccess(new EmptySource(), [])); }
    public Task<TtsCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default) => Task.FromResult(new TtsCapabilities(true, true, [TtsAudioFormat.Mp3]));
    public async Task<IReadOnlyList<TtsVoice>> ListVoicesAsync(CancellationToken cancellationToken = default) { OnOperation?.Invoke(cancellationToken); Entered.TrySetResult(); if (HoldOperation) await Release.Task; return [new("actual", "Actual")]; }
    public async Task<TtsResult> SpeakAsync(TtsRequest request, CancellationToken cancellationToken = default) { Request = request; OnOperation?.Invoke(cancellationToken); Entered.TrySetResult(); if (HoldOperation) await Release.Task; return new("audio/mpeg", new byte[] { 1 }, new() { ImplementationId = "controlled-tts", ModelId = "controlled" }); }
    public async Task<SetupCommitResult> CommitAsync(TtsSetupChanges changes, CancellationToken cancellationToken = default) { Changes = changes; Commits++; Entered.TrySetResult(); if (HoldCommit) await Release.Task; return new(Outcome, "actual-version"); }
    public void Dispose() => DisposeCalls++;
    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    private sealed class BoundClient(TtsBorrowedFixture owner) : ITtsClient
    {
        public InferenceImplementationDescriptor Descriptor => owner.Descriptor;
        public Task<TtsCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default) => owner.GetCapabilitiesAsync(cancellationToken);
        public Task<IReadOnlyList<TtsVoice>> ListVoicesAsync(CancellationToken cancellationToken = default) => owner.ListVoicesAsync(cancellationToken);
        public Task<TtsResult> SpeakAsync(TtsRequest request, CancellationToken cancellationToken = default) => owner.ClientOperationFailure is { } error
            ? Task.FromException<TtsResult>(error) : owner.SpeakAsync(request, cancellationToken);
        public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref owner._clientDisposals);
            return (owner.ClientCleanup?.Invoke() ?? owner.ClientCleanupFailure) is { } error ? ValueTask.FromException(error) : ValueTask.CompletedTask;
        }
    }
    public string WorkspaceId => "workspace";
    public string SessionId => "session";
    public Task<InferenceResourceDescriptor?> GetAsync(InferenceResourceReference reference, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    public Task<IInferenceResourceReadHandle> OpenReadAsync(InferenceResourceReference reference, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    public Task<IInferenceResourceWriteHandle> OpenWriteAsync(InferenceResourceCreate resource, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    public Task<InferenceResourceSelection> GetSelectionAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    public Task<InferenceResourceSelection> SelectAsync(IReadOnlyList<InferenceResourceReference> references, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    public Task<InferenceResourceSelection> ReplaceSelectionAsync(IReadOnlyList<InferenceResourceReference> references, long expectedRevision, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    private sealed class EmptySource : ILlmConnectionSource
    {
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
}

public sealed record TtsRuntimeEnvelope
{
    public IProviderAuthorization? Authorization { get; init; }
    public XaiFamilyBinding? Binding { get; init; }
    public IInferenceResourceAccess? Resources { get; init; }
    public Func<XaiConnectionSelection, XaiFamilyBinding>? Factory { get; init; }
    public TtsProviderService? Service { get; init; }
    public TtsSettingsManager? Manager { get; init; }
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(TtsRuntimeEnvelope))]
internal sealed partial class TtsRuntimeContext : JsonSerializerContext;

internal static class SetupFixtureRequests
{
    internal static TtsRequest RequestForTest(this TtsSetupConfiguration configuration) => new()
    {
        Text = "This is a speech test.",
        VoiceId = configuration.VoiceId,
        Speed = configuration.Speed,
        Format = configuration.Format,
        Language = configuration.Language,
        SampleRate = configuration.SampleRate,
        BitRate = configuration.BitRate,
        IncludeTimestamps = configuration.IncludeTimestamps,
        Xai = configuration.Connection.XaiDefaults
    };
}
