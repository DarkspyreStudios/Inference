using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Decision;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Tensor.Decision.Laya.Tests;

public sealed class LayaPackSelectionTests : IDisposable
{
    private static readonly string[] ArtifactRoles = ["model", "tokenizer", "agent-config"];
    private const string AgentConfiguration = """
        {"max_len":384,"head_max_len":128,"temperature":[2,3,4],"temperature_by_options":{"noul:2":1.006}}
        """;
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"darkspyre-laya-selection-{Guid.NewGuid():N}");

    [Fact]
    public async Task LoadedPackControlsTheSequenceWindowAndHeadBudget()
    {
        TensorModelPackDefinition pack = Pack("host/custom", "1");
        using var http = new HttpClient(new ArtifactHandler());
        using var packs = Manager(http, pack);
        await packs.InstallAsync(pack.Key);
        var loader = new Loader();
        await using var engine = new LayaDecisionInference(packs, loader, TensorBackendKind.Cpu);
        await engine.LoadAsync(pack.Key);
        await engine.DecideAsync(new DecisionInferenceRequest
        {
            ModelId = pack.Key.ModelId,
            State = JsonSerializer.SerializeToElement(new string('x', 2000)),
            Questions = [new BooleanDecisionQuestion { Id = "ready", Instructions = new string('y', 500) }]
        });
        LayaSequence sequence = Assert.Single(Assert.Single(loader.Models).Sequences);
        Assert.Equal(384, sequence.TokenIds.Length);
        Assert.Equal(130, Array.LastIndexOf(sequence.TokenIds, LayaTokenizer.SeparatorTokenId, sequence.TokenIds.Length - 2));
    }

    [Fact]
    public async Task MissingAgentConfigurationFailsCompatibilityBeforeLoading()
    {
        TensorModelPackDefinition pack = Pack("host/custom", "1");
        pack = pack with { Artifacts = pack.Artifacts.Where(artifact => artifact.Role != "agent-config").ToArray() };
        using var http = new HttpClient(new ArtifactHandler());
        using var packs = Manager(http, pack);
        var loader = new Loader();
        await using var engine = new LayaDecisionInference(packs, loader, TensorBackendKind.Cpu);
        InferenceException error = await Assert.ThrowsAsync<InferenceException>(() => engine.LoadAsync(pack.Key));
        Assert.Equal(InferenceFailureCode.UnsupportedFeature, error.Code);
        Assert.Empty(loader.Loaded);
    }

    [Fact]
    public async Task InvalidAgentConfigurationFailsLoadWithoutNativeWorkOrLeakingThePackLease()
    {
        TensorModelPackDefinition pack = Pack("host/custom", "1", "{}");
        using var http = new HttpClient(new ArtifactHandler("{}"));
        using var packs = Manager(http, pack);
        await packs.InstallAsync(pack.Key);
        var loader = new Loader();
        await using var engine = new LayaDecisionInference(packs, loader, TensorBackendKind.Cpu);
        InferenceException error = await Assert.ThrowsAsync<InferenceException>(() => engine.LoadAsync(pack.Key));
        Assert.Equal(InferenceFailureCode.ModelCorrupt, error.Code);
        Assert.Empty(loader.Loaded);
        Assert.Null(engine.Observation.Model);
        Assert.Null((await engine.GetCapabilitiesAsync()).MaximumInputTokens);
        await packs.RemoveAsync(pack.Key);
    }

    [Fact]
    public async Task OneInstanceLoadsRunsUnloadsAndSwitchesBetweenHostPacks()
    {
        TensorModelPackDefinition first = Pack("host/first", "revision-a");
        TensorModelPackDefinition second = Pack("host/second", "revision-b");
        using var http = new HttpClient(new ArtifactHandler());
        using var packs = Manager(http, first, second);
        await packs.InstallAsync(first.Key);
        await packs.InstallAsync(second.Key);
        var loader = new Loader();
        await using var engine = new LayaDecisionInference(packs, loader, TensorBackendKind.Cpu);
        await engine.StartAsync();
        Assert.Null(engine.Observation.Model);
        Assert.Null((await engine.GetCapabilitiesAsync()).MaximumInputTokens);
        foreach (TensorModelPackDefinition selected in new[] { first, second })
        {
            await engine.LoadAsync(selected.Key);
            Assert.Equal(selected.Key, engine.Observation.Model);
            Assert.Equal(selected.Key, loader.Loaded[^1]);
            Assert.Equal(3, (await engine.GetCapabilitiesAsync(selected.Key.ModelId)).SupportedQuestionKinds.Count);
            Assert.Equal(384, (await engine.GetCapabilitiesAsync()).MaximumInputTokens);
            InferenceException wrongModel = await Assert.ThrowsAsync<InferenceException>(() => engine.DecideAsync(Request("not-loaded")));
            Assert.Equal(InferenceFailureCode.ModelUnavailable, wrongModel.Code);
            DecisionInferenceResult result = await engine.DecideAsync(Request(selected.Key.ModelId));
            Assert.Equal(selected.Key.ModelId, result.Provenance.ModelId);
            Assert.Equal(selected.Key.Revision, result.Provenance.ModelRevision);
            Assert.True(Assert.IsType<BooleanDecisionAnswer>(result.Answers["ready"]).Value);
            Assert.Equal(Math.Round(1 / (1 + Math.Exp(-2 / 1.006)), 4),
                Assert.IsType<BooleanDecisionAnswer>(result.Answers["ready"]).TrueProbability);
            await Assert.ThrowsAsync<InvalidOperationException>(() => engine.LoadAsync(selected.Key));
            await Assert.ThrowsAsync<TensorModelPackException>(() => packs.RemoveAsync(selected.Key));
            await engine.UnloadAsync();
            Assert.True(loader.Models[^1].Disposed);
            Assert.Null(engine.Observation.Model);
            Assert.Null((await engine.GetCapabilitiesAsync()).MaximumInputTokens);
            await packs.RemoveAsync(selected.Key); // Unload releases the storage lease too.
        }
        Assert.Equal(2, loader.Loaded.Count);
        await engine.StopAsync();
        Assert.True(engine.Observation.IsStopped);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.LoadAsync(first.Key));
    }

    [Theory]
    [InlineData(TensorCapability.Image, "laya-modernbert", "Image")]
    [InlineData(TensorCapability.Decision, "different-family", "different-family")]
    public async Task WrongFamilyFailsAtLoadBeforeNativeWork(TensorCapability capability, string architecture, string expected)
    {
        TensorModelPackDefinition invalid = Pack("host/wrong", "1") with { Capability = capability, RuntimeArchitecture = architecture };
        using var http = new HttpClient(new ArtifactHandler());
        using var packs = Manager(http, invalid);
        var loader = new Loader();
        await using var engine = new LayaDecisionInference(packs, loader, TensorBackendKind.Cpu);
        InferenceException error = await Assert.ThrowsAsync<InferenceException>(() => engine.LoadAsync(invalid.Key));
        Assert.Equal(InferenceFailureCode.UnsupportedFeature, error.Code);
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
        Assert.Empty(loader.Loaded);
        Assert.Null(engine.Observation.Model);
    }

    [Fact]
    public async Task UnknownPackAndFailedNativeLoadDoNotLeaveResidentStateOrStorageLeases()
    {
        TensorModelPackDefinition pack = Pack("host/custom", "1");
        using var http = new HttpClient(new ArtifactHandler());
        using var packs = Manager(http, pack);
        var loader = new Loader { Failure = new InferenceException(InferenceFailureCode.ModelCorrupt, "fixture load failure") };
        await using var engine = new LayaDecisionInference(packs, loader, TensorBackendKind.Cpu);
        InferenceException missing = await Assert.ThrowsAsync<InferenceException>(() => engine.LoadAsync(new("absent", "1")));
        Assert.Equal(InferenceFailureCode.ModelUnavailable, missing.Code);
        await packs.InstallAsync(pack.Key);
        InferenceException corrupt = await Assert.ThrowsAsync<InferenceException>(() => engine.LoadAsync(pack.Key));
        Assert.Equal(InferenceFailureCode.ModelCorrupt, corrupt.Code);
        Assert.Null(engine.Observation.Model);
        await packs.RemoveAsync(pack.Key);
        loader.Failure = null;
        await packs.InstallAsync(pack.Key);
        await engine.LoadAsync(pack.Key);
        Assert.Equal(pack.Key, engine.Observation.Model);
    }

    [Fact]
    public async Task CancelledLoadDisposesNewResourcesAndReleasesLease()
    {
        TensorModelPackDefinition pack = Pack("host/custom", "1");
        using var http = new HttpClient(new ArtifactHandler());
        using var packs = Manager(http, pack);
        await packs.InstallAsync(pack.Key);
        using var cancellation = new CancellationTokenSource();
        var loader = new Loader { AfterLoad = cancellation.Cancel };
        await using var engine = new LayaDecisionInference(packs, loader, TensorBackendKind.Cpu);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.LoadAsync(pack.Key, cancellation.Token));
        Assert.True(Assert.Single(loader.Models).Disposed);
        Assert.Null(engine.Observation.Model);
        await packs.RemoveAsync(pack.Key);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnloadProtectsNativeWorkAndUrgencyDoesNotFreeAnInUseModel(bool urgent)
    {
        TensorModelPackDefinition pack = Pack("host/custom", "1");
        using var http = new HttpClient(new ArtifactHandler());
        using var packs = Manager(http, pack);
        await packs.InstallAsync(pack.Key);
        using var nativeRelease = new ManualResetEventSlim();
        var nativeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loader = new Loader { Forward = () => { nativeEntered.TrySetResult(); nativeRelease.Wait(); } };
        await using var engine = new LayaDecisionInference(packs, loader, TensorBackendKind.Cpu);
        await engine.LoadAsync(pack.Key);
        Task<DecisionInferenceResult> request = Task.Run(() => engine.DecideAsync(Request(pack.Key.ModelId)));
        Task? unload = null;
        try
        {
            await nativeEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            unload = engine.UnloadAsync(new CancellationToken(urgent));
            Assert.False(unload.IsCompleted);
            Assert.False(Assert.Single(loader.Models).Disposed);
        }
        finally { nativeRelease.Set(); }
        if (urgent) { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request); }
        else { Assert.Equal(pack.Key.ModelId, (await request).Provenance.ModelId); }
        await unload!;
        Assert.True(Assert.Single(loader.Models).Disposed);
        Assert.Null(engine.Observation.Model);
    }

    [Fact]
    public async Task SettingsOfferHostPacksSaveTheSelectedKeyAndReuseTheLiveInstance()
    {
        TensorModelPackDefinition first = Pack("host/first", "a"), second = Pack("host/second", "b");
        using var http = new HttpClient(new ArtifactHandler());
        using var packs = Manager(http, first, second);
        var loader = new Loader();
        await using var engine = new LayaDecisionInference(packs, loader, TensorBackendKind.Cpu);
        await packs.InstallAsync(first.Key);
        await packs.InstallAsync(second.Key);
        await engine.LoadAsync(first.Key);
        var target = new Target();
        await using TensorSetupSession setup = LayaSetup.CreateSession(new(SetupChangeIntent.Edit, "laya", "v1"), target,
            packs, [first, second], new(LayaSetup.Definition.Implementation.Id, first.Key, TensorBackendKind.Cpu), engine);
        SetupChoiceField choice = Assert.IsType<SetupChoiceField>(setup.Snapshot.Steps[0].Sections[0].Fields[0]);
        SetupChoicePage page = await choice.GetChoicesAsync(new(setup.Snapshot.Revision, PageSize: 1));
        Assert.Equal(first.Key.ToString(), Assert.Single(page.Choices).Value);
        Assert.NotNull(page.Continuation);
        SetupChoicePage next = await choice.GetChoicesAsync(new(setup.Snapshot.Revision, Continuation: page.Continuation, PageSize: 1));
        Assert.Equal(second.Key.ToString(), Assert.Single(next.Choices).Value);
        Assert.Equal(SetupEditState.Applied, choice.SetValue(second.Key.ToString()).State);
        Assert.False(Action(setup, SetupActionPurpose.Load).Descriptor.IsEnabled);
        Assert.False(setup.Snapshot.Status.Ready.Value);
        Assert.Equal(SetupActionState.Completed, (await Action(setup, SetupActionPurpose.Unload).ExecuteAsync()).State);
        Assert.Equal(SetupActionState.Completed, (await Action(setup, SetupActionPurpose.Load).ExecuteAsync()).State);
        Assert.True(setup.Snapshot.Status.Ready.Value);
        Assert.Equal(second.Key, engine.Observation.Model);
        Assert.Equal(SetupCommitState.Committed, (await setup.CommitAsync()).State);
        Assert.Equal(second.Key, target.Changes!.Configuration.Model);
        Assert.True(target.Changes.RequiresUnload);
        await setup.DisposeAsync();
        Assert.Equal(second.Key, engine.Observation.Model); // View/session disposal never owns the engine.
        Assert.Equal(InferenceCapability.Decision, Assert.Single(setup.Definition.Implementation.Tasks).GetCapability());
    }

    [Fact]
    public async Task SetupOffersOnlyHostPacksAndSaveHasNoLoadSideEffects()
    {
        TensorModelPackDefinition offered = Pack("host/custom", "1");
        using var http = new HttpClient(new ArtifactHandler());
        using var packs = Manager(http, offered);
        var target = new Target();
        await using TensorSetupSession setup = LayaSetup.CreateSession(new(SetupChangeIntent.Create, "laya", null), target, packs, [offered]);
        SetupChoiceField choice = Assert.IsType<SetupChoiceField>(setup.Snapshot.Steps[0].Sections[0].Fields[0]);
        SetupChoice pack = Assert.Single((await choice.GetChoicesAsync(new(setup.Snapshot.Revision))).Choices);
        Assert.Equal(offered.Key.ToString(), pack.Value);
        choice.SetValue(pack.Value);
        Assert.Equal(SetupCommitState.Committed, (await setup.CommitAsync()).State);
        Assert.Equal(offered.Key, target.Changes!.Configuration.Model);
        Assert.Equal(TensorModelPackState.NotInstalled, (await packs.GetStatusAsync(offered.Key)).State);
    }

    [Fact]
    public void SetupRejectsMalformedAndIncompatibleOfferedDefinitions()
    {
        TensorModelPackDefinition pack = Pack("host/custom", "1");
        using var http = new HttpClient(new ArtifactHandler());
        using var packs = Manager(http, pack);
        foreach (TensorModelPackDefinition invalid in new[]
        {
            pack with { Capability = TensorCapability.Llm },
            pack with { RuntimeArchitecture = "other" },
            pack with { Artifacts = [] },
            pack with { Artifacts = pack.Artifacts.Where(artifact => artifact.Role != "tokenizer").ToArray() },
            pack with { Artifacts = pack.Artifacts.Where(artifact => artifact.Role != "agent-config").ToArray() },
            pack with { SupportedBackends = new HashSet<TensorBackendKind> { TensorBackendKind.Cpu, TensorBackendKind.Cuda } },
            pack with { SupportedBackends = new HashSet<TensorBackendKind> { TensorBackendKind.GgmlMetal } },
            pack with { SupportedBackends = new HashSet<TensorBackendKind>() }
        })
        {
            Assert.Throws<ArgumentException>(() => LayaSetup.CreateSession(new(SetupChangeIntent.Create, "laya", null), new Target(), packs, [invalid]));
        }
    }

    [Fact]
    public async Task MeasureUsesTheLoadedPackWithoutAForwardPassAndMatchesWhatDecideKeeps()
    {
        TensorModelPackDefinition pack = Pack("host/custom", "1");
        using var http = new HttpClient(new ArtifactHandler());
        using var packs = Manager(http, pack);
        await packs.InstallAsync(pack.Key);
        var loader = new Loader();
        var engine = new LayaDecisionInference(packs, loader, TensorBackendKind.Cpu);
        var question = new BooleanDecisionQuestion { Id = "ready", Instructions = new string('y', 100) };
        var request = new DecisionInferenceRequest
        {
            ModelId = pack.Key.ModelId,
            State = JsonSerializer.SerializeToElement(new string('x', 2000)),
            Questions = [question]
        };

        InferenceException invalid = await Assert.ThrowsAsync<InferenceException>(() =>
            engine.MeasureAsync(request with { Questions = [] }));
        Assert.Equal(InferenceFailureCode.InvalidRequest, invalid.Code);
        InferenceException notLoaded = await Assert.ThrowsAsync<InferenceException>(() => engine.MeasureAsync(request));
        Assert.Equal(InferenceFailureCode.ModelUnavailable, notLoaded.Code);

        await engine.LoadAsync(pack.Key);
        DecisionInputMeasurement measurement = await engine.MeasureAsync(request);
        LayaFrame frame = LayaSequenceBuilder.Frame(new Tokenizer(), question, 384, 128);
        Assert.Equal(2000, measurement.StateTokens);
        Assert.Equal(frame.StateBudget, measurement.StateTokenBudgets["ready"]);
        Assert.Empty(Assert.Single(loader.Models).Sequences);

        await engine.DecideAsync(request);
        LayaSequence sequence = Assert.Single(loader.Models[0].Sequences);
        Assert.Equal(
            Math.Min(measurement.StateTokens, measurement.StateTokenBudgets["ready"]),
            sequence.TokenIds.Length - frame.Prefix.Length - 1);

        InferenceException wrongModel = await Assert.ThrowsAsync<InferenceException>(() =>
            engine.MeasureAsync(request with { ModelId = "not-loaded" }));
        Assert.Equal(InferenceFailureCode.ModelUnavailable, wrongModel.Code);

        await engine.UnloadAsync();
        InferenceException unloaded = await Assert.ThrowsAsync<InferenceException>(() => engine.MeasureAsync(request));
        Assert.Equal(InferenceFailureCode.ModelUnavailable, unloaded.Code);

        await engine.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.MeasureAsync(request));
        await packs.RemoveAsync(pack.Key);
    }

    private TensorModelPackManager Manager(HttpClient http, params TensorModelPackDefinition[] definitions) =>
        new(new TensorModelCatalog(definitions), new TensorDirectFilePersistStore(_root), http);
    private static ISetupAction Action(TensorSetupSession setup, SetupActionPurpose purpose) =>
        Assert.Single(setup.Snapshot.Actions, action => action.Descriptor.Purpose == purpose);
    private static DecisionInferenceRequest Request(string modelId) => new()
    {
        ModelId = modelId,
        State = JsonElement.Parse("{}"),
        Questions = [new BooleanDecisionQuestion { Id = "ready", Instructions = "Is the system ready?" }]
    };
    private static TensorModelPackDefinition Pack(string id, string revision, string configuration = AgentConfiguration) => new()
    {
        Key = new(id, revision),
        Capability = TensorCapability.Decision,
        RuntimeArchitecture = TensorDecisionModels.LayaRuntimeArchitecture,
        License = "test-only",
        SupportedBackends = new HashSet<TensorBackendKind> { TensorBackendKind.Cpu },
        Artifacts = ArtifactRoles.Select(role =>
        {
            var uri = new Uri($"https://example.test/{id}/{revision}/{role}");
            byte[] content = ArtifactContent(uri, configuration);
            return new TensorModelArtifactDefinition
            {
                Role = role,
                RelativePath = role + ".bin",
                Length = content.Length,
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(content)),
                Sources = [uri]
            };
        }).ToArray()
    };
    public void Dispose()
    {
        if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }
    }
    private static byte[] ArtifactContent(Uri uri, string configuration) => Encoding.UTF8.GetBytes(
        uri.AbsolutePath.EndsWith("/agent-config", StringComparison.Ordinal) ? configuration : uri.AbsolutePath);
    private sealed class ArtifactHandler(string configuration = AgentConfiguration) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ArtifactContent(request.RequestUri!, configuration)) });
    }
    private sealed class Loader : ILayaModelLoader
    {
        public List<TensorModelKey> Loaded { get; } = [];
        public List<Model> Models { get; } = [];
        public Exception? Failure { get; set; }
        public Action? AfterLoad { get; init; }
        public Action? Forward { get; init; }
        public async Task<LayaModelResources> LoadAsync(TensorModelPackLease lease, TensorBackendKind backend,
            LayaAgentConfiguration configuration, CancellationToken cancellationToken)
        {
            Loaded.Add(lease.Key);
            Assert.Equal(TensorBackendKind.Cpu, backend);
            var artifact = lease.GetRequiredArtifact("model");
            await using Stream? stream = await artifact.Store.OpenReadAsync(artifact.StoreName, artifact.AssetName, cancellationToken);
            Assert.NotNull(stream);
            using var reader = new StreamReader(stream);
            Assert.Equal($"/{lease.Key.ModelId}/{lease.Key.Revision}/model", await reader.ReadToEndAsync(cancellationToken));
            if (Failure is not null) { throw Failure; }
            var model = new Model(Forward);
            Models.Add(model);
            AfterLoad?.Invoke();
            return new(new Tokenizer(), model);
        }
    }
    private sealed class Model(Action? forward) : ILayaDecisionModel
    {
        public bool Disposed { get; private set; }
        public List<LayaSequence> Sequences { get; } = [];
        public int MaximumOptions => 32;
        public LayaModelOutput Forward(LayaSequence sequence, CancellationToken cancellationToken)
        { Assert.False(Disposed); Sequences.Add(sequence); forward?.Invoke(); Assert.False(Disposed); return new([0, 2], .75f); }
        public void Dispose() => Disposed = true;
    }
    private sealed class Tokenizer : ILayaTokenizer
    {
        public IReadOnlyList<int> Encode(string text) => Enumerable.Repeat(1, text.Length).ToArray();
    }
    private sealed class Target : ISetupCommitTarget<TensorSetupChanges>
    {
        public TensorSetupChanges? Changes { get; private set; }
        public Task<SetupCommitResult> CommitAsync(TensorSetupChanges changes, CancellationToken cancellationToken = default)
        { Changes = changes; return Task.FromResult(new SetupCommitResult(SetupCommitState.Committed, "v2")); }
    }
}
