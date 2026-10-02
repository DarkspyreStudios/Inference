using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Common.STT;
using Darkspyre.Inference.Connect.LLM;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.STT.Tests;

public sealed class SttBindingTests
{
    private static SttConnection Connection => new() { Transport = SttTransport.XaiStt, Endpoint = new("https://api.x.ai/v1"), Xai = new("selected") };
    private static readonly SetupConfigurationIdentity Create = new(SetupChangeIntent.Create, "stt", null);
    private static readonly string[] FlagChoices = ["", "True", "False"];
    private static readonly string[] TermChoices = ["Omitted", "List"];
    private const string ConnectionJson = "{\"Transport\":0,\"Endpoint\":\"https://api.x.ai/v1\",\"RequestTimeout\":\"00:05:00\",\"Xai\":{\"ConnectionId\":\"selected\"},\"Authorization\":null,\"AdditionalHeaders\":{\"X-Test\":\"sensitive\"}}";
    public static TheoryData<string, string> Shapes => new()
    {
        { "SttTransport", "0" }, { "SttConnection", ConnectionJson },
        { "SttSetupConfiguration", "{\"Connection\":" + ConnectionJson + ",\"Transcription\":{\"ModelId\":\"manual\",\"Language\":\"en\",\"RawAudioFormat\":0,\"SampleRate\":16000,\"Diarization\":false,\"Multichannel\":true,\"Channels\":2,\"KeyTerms\":[\"one\",\"one\"],\"IncludeFillerWords\":false,\"VadThreshold\":0,\"TextNormalization\":false},\"Streaming\":{\"Encoding\":3,\"InterimResults\":false,\"Endpointing\":\"00:00:00\",\"SmartTurnThreshold\":0,\"SmartTurnTimeout\":\"00:00:00\"},\"StreamLimits\":{\"MaximumBufferedEvents\":2,\"MaximumBufferedPayloadBytes\":3,\"MaximumMessageBytes\":4}}" },
        { "SttSetupHeaderChanges", "{\"Set\":{\"X-Test\":\"secret\"},\"Remove\":[\"X-Old\",\"X-Old\"]}" },
        { "SttSetupChanges", "{\"Identity\":{\"Intent\":0,\"Id\":\"stt\",\"ExpectedVersion\":null},\"Configuration\":{\"Connection\":" + ConnectionJson + "},\"Headers\":{\"Set\":{},\"Remove\":[]}}" }
    };
    [Theory, MemberData(nameof(Shapes))]
    public void ExactOwnedGetterRoundTripsPopulatedData(string name, string json) => RoundTrip(json, Metadata(typeof(ConnectSttJson), name));
    [Theory]
    [InlineData("SttConnection", "Transport")]
    [InlineData("SttConnection", "Endpoint")]
    [InlineData("SttSetupConfiguration", "Connection")]
    [InlineData("SttSetupHeaderChanges", "Set")]
    [InlineData("SttSetupHeaderChanges", "Remove")]
    [InlineData("SttSetupChanges", "Identity")]
    [InlineData("SttSetupChanges", "Configuration")]
    [InlineData("SttSetupChanges", "Headers")]
    public void RequiredFactsCannotDisappear(string name, string property)
    {
        string json = Shapes.Single(row => (string)row[0] == name)[1].ToString()!;
        using JsonDocument original = JsonDocument.Parse(json);
        Dictionary<string, JsonElement> fields = original.RootElement.EnumerateObject().ToDictionary(item => item.Name, item => item.Value);
        fields.Remove(property);
        Assert.ThrowsAny<Exception>(() => JsonSerializer.Deserialize(JsonSerializer.Serialize(fields), Metadata(typeof(ConnectSttJson), name)));
        fields[property] = JsonSerializer.SerializeToElement<object?>(null);
        Assert.ThrowsAny<Exception>(() => JsonSerializer.Deserialize(JsonSerializer.Serialize(fields), Metadata(typeof(ConnectSttJson), name)));
    }
    [Fact]
    public void UndefinedTransportAndInvalidNestedDataRefuse()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("1", ConnectSttJson.SttTransport));
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize((SttTransport)1, ConnectSttJson.SttTransport));
        Assert.Throws<ArgumentOutOfRangeException>(() => Connection with { Transport = (SttTransport)1 });
        Assert.ThrowsAny<Exception>(() => JsonSerializer.Deserialize("{\"Connection\":" + ConnectionJson + ",\"Streaming\":{\"Encoding\":99}}", ConnectSttJson.SttSetupConfiguration));
        Assert.ThrowsAny<Exception>(() => JsonSerializer.Deserialize("{\"Connection\":" + ConnectionJson + ",\"StreamLimits\":{\"MaximumBufferedEvents\":0,\"MaximumBufferedPayloadBytes\":1,\"MaximumMessageBytes\":1}}", ConnectSttJson.SttSetupConfiguration));
    }
    [Fact]
    public void DictionariesAndRemovalsAreImmutableCopiedData()
    {
        var headers = new Dictionary<string, string> { ["X-Test"] = "original" }; string[] removed = ["X-Old"];
        SttConnection connection = Connection with { AdditionalHeaders = headers };
        var changes = new SttSetupHeaderChanges(headers, removed);
        headers["X-Test"] = "changed"; removed[0] = "changed";
        Assert.Equal("original", connection.AdditionalHeaders["x-test"]); Assert.Equal("original", changes.Set["x-test"]); Assert.Equal("X-Old", changes.Remove[0]);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string>)connection.AdditionalHeaders).Add("x", "y"));
        Assert.Throws<ArgumentException>(() => Connection with { AdditionalHeaders = new Dictionary<string, string> { ["X"] = "one", ["x"] = "two" } });
        var replacement = new Dictionary<string, string> { ["New"] = "old" }; var updated = changes with { Set = replacement }; replacement["New"] = "new"; Assert.Equal("old", updated.Set["new"]);
        var named = new SttSetupConfiguration(Connection: connection); named.Deconstruct(out SttConnection selected);
        Assert.Same(connection, selected);
        Assert.DoesNotContain("original", connection.ToString(), StringComparison.Ordinal); Assert.DoesNotContain("original", changes.ToString(), StringComparison.Ordinal);
    }
    [Fact]
    public void FacadeOnlyRegistrationAdvertisesNoProviderAndRefusesBeforeEffects()
    {
        using ServiceProvider services = new ServiceCollection().AddSttConnect().BuildServiceProvider();
        SttProviderService service = services.GetRequiredService<SttProviderService>();
        Assert.Empty(service.SetupDefinitions); Assert.Empty(services.GetServices<ISttProvider>());
        using var access = new LlmConnectionAccess(new UntouchableSource(), []);
        var binding = new XaiFamilyBinding(new("selected"), access);
        Assert.Equal(InferenceFailureCode.UnsupportedFeature, Assert.Throws<InferenceException>(() => service.Bind(Connection, binding)).Code);
    }
    [Fact]
    public void SelectedProviderReceivesExactBorrowedAuthoritiesWithoutAcquisition()
    {
        var provider = new ControlledProvider(); var service = new SttProviderService([provider]);
        using var access = new LlmConnectionAccess(new UntouchableSource(), []); var binding = new XaiFamilyBinding(new("selected"), access);
        var resources = new UntouchableResources();
        SttConnection connection = Connection;
        ISpeechTranscriptionClient client = service.Bind(connection, binding, resources);
        Assert.Same(provider.LastClient, client); Assert.Same(binding, provider.Binding); Assert.Same(resources, provider.Resources); Assert.Same(connection, provider.Connection);
        Assert.Throws<ArgumentException>(() => service.Bind(Connection, new(new("other"), access), resources));
        Assert.Throws<ArgumentException>(() => service.Bind(Connection with { Authorization = new UntouchableAuthorization() }, binding, resources));
        Assert.Throws<InvalidOperationException>(() => service.Bind(Connection with { AdditionalHeaders = new Dictionary<string, string> { ["aUtHoRiZaTiOn"] = "secret" } }, binding, resources));
        Assert.Throws<ArgumentException>(() => service.Bind(Connection with { Endpoint = new("https://outside.invalid/v1") }, binding, resources));
        Assert.Throws<ArgumentException>(() => service.Bind(Connection with { Endpoint = new("https://api.x.ai/v1?token=secret") }, binding, resources));
        Assert.Throws<ArgumentException>(() => service.Bind(Connection with { RequestTimeout = TimeSpan.Zero }, binding, resources));
        Assert.Equal(1, provider.Binds);
        Assert.Throws<ArgumentException>(() => new SttProviderService([provider, provider]));
    }
    public static TheoryData<string, string> InvalidHeaders => new()
    {
        { "aUtHoRiZaTiOn", "secret" }, { "Accept", "application/json" }, { "Content-Type", "audio/wav" },
        { "Host", "outside.invalid" }, { "bad name", "value" }, { "X-Bad\rName", "value" },
        { "X-Value", "value\r\ninjected: true" }, { "X-Value", "value\ninjected: true" }
    };
    [Theory, MemberData(nameof(InvalidHeaders))]
    public void SharedHeaderPolicyRefusesBeforeProviderBinding(string name, string value)
    {
        var provider = new ControlledProvider(); var service = new SttProviderService([provider]);
        using var access = new LlmConnectionAccess(new UntouchableSource(), []); var binding = new XaiFamilyBinding(new("selected"), access);
        Assert.ThrowsAny<Exception>(() => service.Bind(Connection with
        { AdditionalHeaders = new Dictionary<string, string> { [name] = value } }, binding));
        Assert.Equal(0, provider.Binds);
    }
    [Theory, MemberData(nameof(InvalidHeaders))]
    public async Task InvalidDraftHeadersRefuseBeforeFactoryProviderOrCommit(string name, string value)
    {
        var provider = new ControlledProvider(); var target = new Target(); int factories = 0;
        using var access = new LlmConnectionAccess(new UntouchableSource(), []); var binding = new XaiFamilyBinding(new("selected"), access);
        await using SttSettingsManager manager = new SttProviderService([provider]).CreateSettingsManager(target, _ => { factories++; return binding; });
        SttSettings settings = await manager.OpenAsync(SttTransport.XaiStt, new(SetupChangeIntent.Edit, "stt", "version"), Populated());
        SetupFieldRow header = Assert.Single(Edit<SetupRepeatableGroupField>(settings, "headers").Rows);
        ((SetupTextField)header.Fields[0]).SetValue(name); ((SetupSecretField)header.Fields[1]).SetValue(SetupSecretEdit.Replace(value));
        Assert.False(settings.CanSave);
        Assert.NotEqual(SetupActionState.Completed, (await manager.RunAsync(settings, "check")).State);
        Assert.NotEqual(SetupCommitState.Committed, (await manager.SaveAsync(settings)).State);
        Assert.Equal(0, factories); Assert.Equal(0, provider.Binds); Assert.Null(target.Changes);
    }
    [Fact]
    public async Task BorrowedAuthorityWorksForCheckButNeverEntersPortableChanges()
    {
        var provider = new ControlledProvider(); var target = new Target();
        using var access = new LlmConnectionAccess(new UntouchableSource(), []); var binding = new XaiFamilyBinding(new("selected"), access);
        await using SttSettingsManager manager = new SttProviderService([provider]).CreateSettingsManager(target, _ => binding);
        SttSetupConfiguration existing = Populated() with { Connection = Connection with { Authorization = binding.Authorization } };
        SttSettings settings = await manager.OpenAsync(SttTransport.XaiStt, new(SetupChangeIntent.Edit, "stt", "version"), existing);
        Assert.False(settings.HasUnsavedChanges); Assert.True(settings.CanSave);
        Assert.Equal(SetupActionState.Completed, (await manager.RunAsync(settings, "check")).State);
        Assert.Same(binding.Authorization, provider.Connection!.Authorization); Assert.Same(binding, provider.Binding);
        Assert.Equal(SetupCommitState.Committed, (await manager.SaveAsync(settings)).State);
        Assert.Null(target.Changes!.Configuration.Connection.Authorization);
        Assert.Equal("selected", target.Changes.Configuration.Connection.Xai!.ConnectionId);
        string json = JsonSerializer.Serialize(target.Changes, ConnectSttJson.SttSetupChanges);
        Assert.Null(JsonSerializer.Deserialize(json, ConnectSttJson.SttSetupChanges)!.Configuration.Connection.Authorization);
        Assert.False(provider.Disposed);
    }
    [Fact]
    public void LiveAuthoritiesAndRuntimeServicesRefuseWithoutCallbacksOrDiagnostics()
    {
        Assert.Equal(0, ExerciseRuntimeRefusal());
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize(ConnectionJson.Replace("\"Authorization\":null", "\"Authorization\":{}", StringComparison.Ordinal), ConnectSttJson.SttConnection));
    }
    public static int ExerciseRuntimeRefusal()
    {
        var authorization = new UntouchableAuthorization();
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(Connection with { Authorization = authorization }, ConnectSttJson.SttConnection));
        var provider = new ControlledProvider(); var service = new SttProviderService([provider]);
        using var access = new LlmConnectionAccess(new UntouchableSource(), []); var binding = new XaiFamilyBinding(new("selected"), access);
        using SttSettingsManager manager = service.CreateSettingsManager(new Target());
        JsonSerializerOptions options = ConnectSttJson.SttConnection.Options;
        foreach ((object value, Type type) in new[] { ((object)binding, typeof(XaiFamilyBinding)), (provider, typeof(ISttProvider)), (service, typeof(SttProviderService)), (manager, typeof(SttSettingsManager)), (new UntouchableResources(), typeof(IInferenceResourceAccess)), ((object)(Func<XaiConnectionSelection, XaiFamilyBinding>)(_ => throw new InvalidOperationException()), typeof(Func<XaiConnectionSelection, XaiFamilyBinding>)) })
        {
            Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(value, type, options));
            Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize("{}", type, options));
            Assert.Equal("null", JsonSerializer.Serialize((object?)null, type, options)); Assert.Null(JsonSerializer.Deserialize("null", type, options));
        }
        return authorization.Effects;
    }
    [Fact]
    public async Task SetupPreservesAllDefaultsAndAppliesOnlyExplicitHeaderEdits()
    {
        var target = new Target(); var service = new SttProviderService([new ControlledProvider()]);
        await using SttSettingsManager manager = service.CreateSettingsManager(target);
        SttSetupConfiguration original = Populated();
        SttSettings settings = await manager.OpenAsync(SttTransport.XaiStt, new(SetupChangeIntent.Edit, "stt", "version"), original);
        Assert.True(settings.CanSave); Assert.False(settings.HasUnsavedChanges);
        Assert.Equal(SetupCommitState.Committed, (await manager.SaveAsync(settings)).State);
        Assert.Equal(original.Transcription, target.Changes!.Configuration.Transcription); Assert.Equal(original.Streaming, target.Changes.Configuration.Streaming);
        Assert.Equal(original.StreamLimits, target.Changes.Configuration.StreamLimits); Assert.Empty(target.Changes.Configuration.Connection.AdditionalHeaders); Assert.Empty(target.Changes.Headers.Set); Assert.Empty(target.Changes.Headers.Remove);
        Assert.Null(target.Changes.Configuration.Connection.Authorization);
    }
    [Fact]
    public async Task SetupEditFieldsKeepFalseZeroEmptyAndExplicitLimits()
    {
        var target = new Target(); await using SttSettingsManager manager = new SttProviderService([new ControlledProvider()]).CreateSettingsManager(target);
        SttSettings settings = await manager.OpenAsync(SttTransport.XaiStt, Create);
        Edit<SetupTextField>(settings, "xai-connection").SetValue("selected");
        Edit<SetupChoiceField>(settings, "diarization").SetValue("False"); Edit<SetupIntegerField>(settings, "sample-rate").SetValue(0);
        Edit<SetupTextField>(settings, "vad").SetValue("0"); Edit<SetupIntegerField>(settings, "endpointing").SetValue(0);
        Edit<SetupChoiceField>(settings, "key-terms-mode").SetValue("List"); Edit<SetupChoiceField>(settings, "stream-key-terms-mode").SetValue("Omitted");
        Edit<SetupIntegerField>(settings, "maximum-buffered-events").SetValue(2);
        Assert.False(settings.CanSave);
        Edit<SetupIntegerField>(settings, "maximum-buffered-payload-bytes").SetValue(3); Edit<SetupIntegerField>(settings, "maximum-message-bytes").SetValue(4);
        Assert.Equal(SetupCommitState.Committed, (await manager.SaveAsync(settings)).State);
        Assert.False(target.Changes!.Configuration.Transcription.Diarization); Assert.Equal(0, target.Changes.Configuration.Transcription.SampleRate); Assert.Equal(0d, target.Changes.Configuration.Transcription.VadThreshold);
        Assert.Equal(TimeSpan.Zero, target.Changes.Configuration.Streaming.Endpointing); Assert.Equal(new InferenceStreamLimits(2, 3, 4), target.Changes.Configuration.StreamLimits);
        Assert.Empty(target.Changes.Configuration.Transcription.KeyTerms!); Assert.Null(target.Changes.Configuration.Streaming.KeyTerms);
    }
    [Fact]
    public async Task RetiringManagerDrainsIgnoringCheckAndNeverDisposesBorrowedDependencies()
    {
        var provider = new ControlledProvider { Hold = true }; var source = new UntouchableSource();
        using var access = new LlmConnectionAccess(source, []); var binding = new XaiFamilyBinding(new("selected"), access);
        var target = new Target(); var resources = new UntouchableResources();
        SttSettingsManager manager = new SttProviderService([provider]).CreateSettingsManager(target, _ => binding, resources);
        Task<SetupActionResult>? pending = null; Task? retirement = null;
        try
        {
            SttSettings settings = await manager.OpenAsync(SttTransport.XaiStt, new(SetupChangeIntent.Edit, "stt", "version"), Populated());
            pending = manager.RunAsync(settings, "check"); await provider.Entered.Task;
            retirement = manager.DisposeAsync().AsTask(); Assert.False(retirement.IsCompleted);
            InferenceSettingsManager<SttSettings, SttSetupChanges> baseManager = manager;
            await Assert.ThrowsAsync<ObjectDisposedException>(() => baseManager.QueryChoicesAsync(settings, "raw-format", new()));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => baseManager.RunAsync(settings, "missing"));
            provider.Release.TrySetResult(); Assert.Equal(SetupActionState.Cancelled, (await pending).State); await retirement;
            Assert.Null(settings.Status.Ready.Value); Assert.False(provider.Disposed); Assert.False(resources.Disposed); Assert.False(target.Disposed);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => manager.OpenAsync(SttTransport.XaiStt, Create));
            await manager.DisposeAsync();
        }
        finally { retirement ??= manager.DisposeAsync().AsTask(); provider.Release.TrySetResult(); if (pending is not null) await pending; await retirement; }
    }
    [Fact]
    public async Task DelayedOwnerCancellationRejectsLateCheckWithStructuredCancellation() => await ExerciseDelayedRetirementAsync();

#pragma warning disable xUnit1013 // Public executed entry point for the actual foreign assembly fixture.
    public static async Task ExerciseDelayedRetirementAsync()
    {
        var provider = new ControlledProvider { Hold = true }; using var caller = new CancellationTokenSource();
        using var unblock = new ManualResetEventSlim(); var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var access = new LlmConnectionAccess(new UntouchableSource(), []); var binding = new XaiFamilyBinding(new("selected"), access);
        var target = new Target(); var manager = new SttProviderService([provider]).CreateSettingsManager(target, _ => binding);
        Task<SetupActionResult>? anchor = null, pending = null; Task? retiring = null; CancellationTokenRegistration blocker = default;
        try
        {
            SttSettings settings = await manager.OpenAsync(SttTransport.XaiStt, new(SetupChangeIntent.Edit, "stt", "version"), Populated());
            anchor = manager.RunAsync(settings, "check"); await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            CancellationToken stop = provider.LastToken;
            pending = manager.RunAsync(settings, "check", cancellationToken: caller.Token); CancellationToken linked = provider.LastToken;
            blocker = stop.Register(() => { callbackEntered.TrySetResult(); if (!unblock.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("The controlled cancellation callback was not released."); });
            retiring = manager.DisposeAsync().AsTask(); await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(stop.IsCancellationRequested); Assert.False(linked.IsCancellationRequested); Assert.False(retiring.IsCompleted);
            provider.Release.TrySetResult();
            Assert.Equal(SetupActionState.Cancelled, (await pending.WaitAsync(TimeSpan.FromSeconds(5))).State);
            Assert.False(linked.IsCancellationRequested); Assert.Null(settings.Status.Ready.Value); Assert.False(retiring.IsCompleted);
        }
        finally
        {
            retiring ??= manager.DisposeAsync().AsTask(); unblock.Set(); provider.Release.TrySetResult();
            try { await Task.WhenAll(new[] { anchor, pending }.OfType<Task>()); }
            finally { try { await retiring.WaitAsync(TimeSpan.FromSeconds(5)); } finally { blocker.Dispose(); } }
        }
    }
#pragma warning restore xUnit1013

    [Theory]
    [InlineData(SetupCommitState.Committed)]
    [InlineData(SetupCommitState.Uncertain)]
    public async Task RetirementDrainsAdmittedSaveAndPreservesActualCommit(SetupCommitState state)
    {
        var target = new Target { Hold = true, Outcome = state };
        var manager = new SttProviderService([new ControlledProvider()]).CreateSettingsManager(target);
        Task<SetupCommitResult>? pending = null; Task? retirement = null;
        try
        {
            SttSettings settings = await manager.OpenAsync(SttTransport.XaiStt, new(SetupChangeIntent.Edit, "stt", "version"), Populated());
            pending = manager.SaveAsync(settings); await target.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            retirement = manager.DisposeAsync().AsTask(); Assert.False(retirement.IsCompleted); Assert.Same(retirement, manager.DisposeAsync().AsTask());
            await Assert.ThrowsAsync<ObjectDisposedException>(() => manager.SaveAsync(settings)); target.Release.TrySetResult();
            Assert.Equal(state, (await pending.WaitAsync(TimeSpan.FromSeconds(5))).State); await retirement.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(settings.IsSaved); Assert.Equal(state == SetupCommitState.Uncertain, settings.SaveOutcomeUncertain); Assert.False(target.Disposed);
        }
        finally { retirement ??= manager.DisposeAsync().AsTask(); target.Release.TrySetResult(); try { if (pending is not null) await pending; } finally { await retirement; } }
    }

    [Fact]
    public async Task EarlierCapabilityResultCannotVerifyEditedDraft()
    {
        var provider = new ControlledProvider { Hold = true }; using var access = new LlmConnectionAccess(new UntouchableSource(), []); var binding = new XaiFamilyBinding(new("selected"), access);
        await using SttSettingsManager manager = new SttProviderService([provider]).CreateSettingsManager(new Target(), _ => binding);
        Task<SetupActionResult>? pending = null;
        try
        {
            SttSettings settings = await manager.OpenAsync(SttTransport.XaiStt, new(SetupChangeIntent.Edit, "stt", "version"), Populated()); pending = manager.RunAsync(settings, "check");
            await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Edit<SetupTextField>(settings, "model").SetValue("different"); provider.Release.TrySetResult();
            Assert.Equal(SetupActionState.Cancelled, (await pending).State); Assert.Null(settings.Status.Ready.Value);
        }
        finally { provider.Release.TrySetResult(); if (pending is not null) await pending; }
    }

    [Fact]
    public async Task LocalChoiceQueriesDoNotAcquireCredentialsOrLoseOmittedValues()
    {
        using var access = new LlmConnectionAccess(new UntouchableSource(), []); var binding = new XaiFamilyBinding(new("selected"), access); int factories = 0;
        await using SttSettingsManager manager = new SttProviderService([new ControlledProvider()]).CreateSettingsManager(new Target(), _ => { factories++; return binding; });
        SttSettings settings = await manager.OpenAsync(SttTransport.XaiStt, new(SetupChangeIntent.Edit, "stt", "version"), Populated());
        Assert.Equal(5, (await manager.QueryChoicesAsync(settings, "raw-format", new())).Choices.Count);
        Assert.Equal(FlagChoices, (await manager.QueryChoicesAsync(settings, "interim-results", new())).Choices.Select(choice => choice.Value));
        Assert.Equal(TermChoices, (await manager.QueryChoicesAsync(settings, "key-terms-mode", new())).Choices.Select(choice => choice.Value));
        Assert.Equal(0, factories);
    }

    [Fact]
    public async Task ExplicitHeaderMutationPreservesUnnamedHeaders()
    {
        var target = new Target(); await using SttSettingsManager manager = new SttProviderService([new ControlledProvider()]).CreateSettingsManager(target);
        SttSettings settings = await manager.OpenAsync(SttTransport.XaiStt, new(SetupChangeIntent.Edit, "stt", "version"), Populated() with
        { Connection = Connection with { AdditionalHeaders = new Dictionary<string, string> { ["X-One"] = "one", ["X-Two"] = "two" } } });
        SetupRepeatableGroupField headers = Edit<SetupRepeatableGroupField>(settings, "headers");
        SetupFieldRow one = headers.Rows.Single(row => ((SetupTextField)row.Fields[0]).Value == "X-One");
        ((SetupSecretField)one.Fields[1]).SetValue(SetupSecretEdit.Replace("replacement"));
        Assert.Equal(SetupCommitState.Committed, (await manager.SaveAsync(settings)).State);
        Assert.Single(target.Changes!.Headers.Set); Assert.Equal("replacement", target.Changes.Headers.Set["X-One"]); Assert.Empty(target.Changes.Headers.Remove);
    }
    [Fact]
    public async Task CollaboratorDisposalFailureIsNotTranslatedIntoOwnerRetirement()
    {
        var provider = new ControlledProvider { CapabilityFailure = new ObjectDisposedException("borrowed-provider") };
        using var access = new LlmConnectionAccess(new UntouchableSource(), []); var binding = new XaiFamilyBinding(new("selected"), access);
        await using SttSettingsManager manager = new SttProviderService([provider]).CreateSettingsManager(new Target(), _ => binding);
        SttSettings settings = await manager.OpenAsync(SttTransport.XaiStt, new(SetupChangeIntent.Edit, "stt", "version"), Populated());
        Assert.Equal(SetupActionState.Failed, (await manager.RunAsync(settings, "check")).State);
        Assert.Equal(5, (await manager.QueryChoicesAsync(settings, "raw-format", new())).Choices.Count);
        Assert.Null(settings.Status.Ready.Value);
    }
    [Theory, MemberData(nameof(Shapes))]
    public void ActualForeignPublicMetadataAndCallerRetire(string name, string json)
    {
        WeakReference weak = Foreign(name, json);
        for (int attempt = 0; weak.IsAlive && attempt < 20; attempt++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); }
        Assert.False(weak.IsAlive);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Foreign(string name, string json)
    {
        var context = new Generation(Path.GetDirectoryName(typeof(ConnectSttJson).Assembly.Location)!); var weak = new WeakReference(context);
        try
        {
            var loaded = new Dictionary<string, Assembly>();
            foreach (Assembly original in new[] { typeof(InferenceJson).Assembly, typeof(XaiFamilyBinding).Assembly, typeof(ConnectSttJson).Assembly, typeof(SttBindingTests).Assembly })
            {
                Assembly actual = context.LoadFromAssemblyPath(original.Location); loaded.Add(original.GetName().Name!, actual);
                Assert.NotSame(original, actual); Assert.Same(context, AssemblyLoadContext.GetLoadContext(actual)); Assert.Equal(original.ManifestModule.ModuleVersionId, actual.ManifestModule.ModuleVersionId);
            }
            Assembly stt = loaded[typeof(ConnectSttJson).Assembly.GetName().Name!]; JsonTypeInfo metadata = Metadata(stt.GetType(typeof(ConnectSttJson).FullName!, true)!, name);
            object value = RoundTrip(json, metadata); Assert.Same(stt, metadata.Type.Assembly); Assert.Same(stt, value.GetType().Assembly);
            if (value.GetType().GetProperty("Transcription")?.GetValue(value) is { } options) Assert.Same(loaded[typeof(InferenceJson).Assembly.GetName().Name!], options.GetType().Assembly);
            object? connection = name == "SttConnection" ? value : value.GetType().GetProperty("Connection")?.GetValue(value);
            if (connection?.GetType().GetProperty("Xai")?.GetValue(connection) is { } selection) Assert.Same(loaded[typeof(XaiFamilyBinding).Assembly.GetName().Name!], selection.GetType().Assembly);
            MethodInfo entry = loaded[typeof(SttBindingTests).Assembly.GetName().Name!].GetType(typeof(SttBindingTests).FullName!, true)!.GetMethod(nameof(ExerciseRuntimeRefusal))!;
            Assert.Same(loaded[typeof(SttBindingTests).Assembly.GetName().Name!], entry.Module.Assembly); Assert.Equal(0, entry.Invoke(null, null));
            MethodInfo callerEntry = entry.DeclaringType!.GetMethod(nameof(ExerciseDelayedRetirementAsync))!;
            Assert.Same(entry.Module.Assembly, callerEntry.Module.Assembly); ((Task)callerEntry.Invoke(null, null)!).GetAwaiter().GetResult();
            MethodInfo saveEntry = entry.DeclaringType.GetMethod(nameof(BorrowedAuthorityWorksForCheckButNeverEntersPortableChanges))!;
            Assert.Same(entry.Module.Assembly, saveEntry.Module.Assembly);
            ((Task)saveEntry.Invoke(Activator.CreateInstance(entry.DeclaringType), null)!).GetAwaiter().GetResult();
            Type auth = loaded[typeof(InferenceJson).Assembly.GetName().Name!].GetType(typeof(IProviderAuthorization).FullName!, true)!;
            Assert.Same(loaded[typeof(InferenceJson).Assembly.GetName().Name!], metadata.Options.GetConverter(auth).GetType().Assembly);
        }
        finally { context.Unload(); }
        return weak;
    }
    private static T Edit<T>(SttSettings settings, string id) where T : SetupField => (T)settings.Steps.SelectMany(step => step.Sections).SelectMany(section => section.Fields).Single(field => field.Description.Id == id);
    private static SttSetupConfiguration Populated() => new(Connection with { AdditionalHeaders = new Dictionary<string, string> { ["X-Original"] = "secret" } })
    {
        Transcription = new("manual", "en", SpeechAudioEncoding.Pcm16LittleEndian, 0, false, true, 2, ["one", "one"], false, 0, false),
        Streaming = new("manual-stream", "en", SpeechAudioEncoding.Opus, 0, false, true, 2, [], false, 0, false, false, TimeSpan.Zero, 0, TimeSpan.Zero),
        StreamLimits = new(2, 3, 4)
    };
    private static JsonTypeInfo Metadata(Type owner, string name) => (JsonTypeInfo)owner.GetProperty(name)!.GetValue(null)!;
    private static object RoundTrip(string json, JsonTypeInfo metadata)
    {
        object value = JsonSerializer.Deserialize(json, metadata)!; string text = JsonSerializer.Serialize(value, metadata);
        Assert.Equal(text, JsonSerializer.Serialize(JsonSerializer.Deserialize(text, metadata), metadata));
        using JsonDocument original = JsonDocument.Parse(json); using JsonDocument actual = JsonDocument.Parse(text); Preserve(original.RootElement, actual.RootElement); return value;
    }
    private static void Preserve(JsonElement expected, JsonElement actual)
    {
        if (expected.ValueKind == JsonValueKind.Object) foreach (JsonProperty property in expected.EnumerateObject()) { Assert.True(actual.TryGetProperty(property.Name, out var item)); Preserve(property.Value, item); }
        else if (expected.ValueKind == JsonValueKind.Array) { Assert.Equal(expected.GetArrayLength(), actual.GetArrayLength()); for (int index = 0; index < expected.GetArrayLength(); index++) Preserve(expected[index], actual[index]); }
        else Assert.True(JsonElement.DeepEquals(expected, actual));
    }
    private sealed class Generation(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name) => name.Name?.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal) == true
            ? LoadFromAssemblyPath(Path.Combine(directory, name.Name + ".dll")) : null;
    }
    private sealed class UntouchableAuthorization : IProviderAuthorization
    {
        public int Effects { get; private set; }
        public ValueTask<ProviderAuthorizationHeader> GetAsync(CancellationToken cancellationToken = default) { Effects++; throw new InvalidOperationException(); }
        public override string ToString() { Effects++; throw new InvalidOperationException(); }
    }
    private sealed class UntouchableSource : ILlmConnectionSource
    {
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No acquisition is allowed.");
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
    private sealed class Target : ISetupCommitTarget<SttSetupChanges>, IDisposable
    {
        public SttSetupChanges? Changes { get; private set; }
        public bool Disposed { get; private set; }
        public bool Hold { get; init; }
        public SetupCommitState Outcome { get; init; } = SetupCommitState.Committed;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<SetupCommitResult> CommitAsync(SttSetupChanges changes, CancellationToken cancellationToken = default) { Changes = changes; Entered.TrySetResult(); if (Hold) await Release.Task; return new(Outcome, "next"); }
        public void Dispose() => Disposed = true;
    }
    private sealed class ControlledProvider : ISttProvider, ISpeechTranscriptionClient, IDisposable
    {
        public SttTransport Transport => SttTransport.XaiStt;
        public InferenceSetupDefinition SetupDefinition => new(new("controlled", "Controlled", InferenceImplementationKind.Connect, InferenceTask.SpeechTranscription), "Controlled provider");
        public SttConnection? Connection { get; private set; }
        public XaiFamilyBinding? Binding { get; private set; }
        public IInferenceResourceAccess? Resources { get; private set; }
        public int Binds { get; private set; }
        public ISpeechTranscriptionClient? LastClient { get; private set; }
        public bool Hold { get; init; }
        public bool Disposed { get; private set; }
        public CancellationToken LastToken { get; private set; }
        public Exception? CapabilityFailure { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ISpeechTranscriptionClient Bind(SttConnection connection, XaiFamilyBinding? xai = null, IInferenceResourceAccess? resources = null) { Connection = connection; Binding = xai; Resources = resources; Binds++; return LastClient = new BoundClient(this); }
        public async Task<SpeechTranscriptionCapabilities> GetCapabilitiesAsync(string? modelId = null, CancellationToken cancellationToken = default) { LastToken = cancellationToken; Entered.TrySetResult(); if (Hold) await Release.Task; if (CapabilityFailure is not null) throw CapabilityFailure; return new() { Transcription = new() { Available = true } }; }
        public Task<SpeechTranscriptionResult> TranscribeAsync(SpeechTranscriptionInput input, SpeechTranscriptionOptions options, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<ISpeechTranscriptionSession> OpenStreamAsync(SpeechStreamOptions options, InferenceStreamLimits limits, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public IAsyncEnumerable<SpeechTranscriptEvent> TranscribeStreamAsync(IAsyncEnumerable<ReadOnlyMemory<byte>> audio, SpeechStreamOptions options, InferenceStreamLimits limits, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        private sealed class BoundClient(ControlledProvider owner) : ISpeechTranscriptionClient
        {
            public Task<SpeechTranscriptionCapabilities> GetCapabilitiesAsync(string? modelId = null, CancellationToken cancellationToken = default) => owner.GetCapabilitiesAsync(modelId, cancellationToken);
            public Task<SpeechTranscriptionResult> TranscribeAsync(SpeechTranscriptionInput input, SpeechTranscriptionOptions options, CancellationToken cancellationToken = default) => owner.TranscribeAsync(input, options, cancellationToken);
            public Task<ISpeechTranscriptionSession> OpenStreamAsync(SpeechStreamOptions options, InferenceStreamLimits limits, CancellationToken cancellationToken = default) => owner.OpenStreamAsync(options, limits, cancellationToken);
            public IAsyncEnumerable<SpeechTranscriptEvent> TranscribeStreamAsync(IAsyncEnumerable<ReadOnlyMemory<byte>> audio, SpeechStreamOptions options, InferenceStreamLimits limits, CancellationToken cancellationToken = default) => owner.TranscribeStreamAsync(audio, options, limits, cancellationToken);
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
    private sealed class UntouchableResources : IInferenceResourceAccess, IDisposable
    {
        public string WorkspaceId => throw new InvalidOperationException();
        public string SessionId => throw new InvalidOperationException();
        public bool Disposed { get; private set; }
        public Task<InferenceResourceDescriptor?> GetAsync(InferenceResourceReference reference, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<IInferenceResourceReadHandle> OpenReadAsync(InferenceResourceReference reference, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<IInferenceResourceWriteHandle> OpenWriteAsync(InferenceResourceCreate create, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<InferenceResourceSelection> GetSelectionAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<InferenceResourceSelection> SelectAsync(IReadOnlyList<InferenceResourceReference> references, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<InferenceResourceSelection> ReplaceSelectionAsync(IReadOnlyList<InferenceResourceReference> references, long expectedRevision, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public void Dispose() => Disposed = true;
    }
}
