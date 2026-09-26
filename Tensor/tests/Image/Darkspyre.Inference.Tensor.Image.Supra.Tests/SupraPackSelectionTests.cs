using System.Net;
using System.Security.Cryptography;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Image;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Tensor.Image.Supra.Tests;

public sealed class SupraPackSelectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "darkspyre-supra-selection-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Weights = "controlled checkpoint bytes"u8.ToArray();

    [Fact]
    public async Task OneInstanceSwitchesHostPacksAndReportsActualProvenance()
    {
        TensorModelPackDefinition first = Pack("host/first"), second = Pack("host/second");
        using var http = new HttpClient(new Handler());
        using var packs = Manager(http, first, second);
        await packs.InstallAsync(first.Key);
        await packs.InstallAsync(second.Key);
        var loader = new Loader();
        await using var engine = new SupraImageInference(packs, loader, TensorBackendKind.Cpu);
        await engine.StartAndWaitAsync();
        foreach (TensorModelPackDefinition pack in new[] { first, second })
        {
            await engine.LoadAndWaitAsync(pack.Key);
            Assert.Equal(pack.Key, (await engine.GetObservationAsync()).Model);
            ImageGenerationResult result = await engine.GenerateAsync(new() { ModelId = pack.Key.ModelId, Prompt = "test", Steps = 1 });
            Assert.Equal(pack.Key.ModelId, result.Provenance!.ModelId);
            Assert.Equal(pack.Key.Revision, result.Provenance.ModelRevision);
            Assert.NotEmpty(result.Images.Single().Data.ToArray());
            InferenceException wrong = await Assert.ThrowsAsync<InferenceException>(() => engine.GenerateAsync(new()
            { ModelId = "another/model", Prompt = "test" }));
            Assert.Equal(InferenceFailureCode.ModelUnavailable, wrong.Code);
            await Assert.ThrowsAsync<TensorModelPackException>(() => packs.RemoveAsync(pack.Key));
            await engine.UnloadAndWaitAsync();
            Assert.True(loader.Models[^1].Disposed);
            Assert.Null((await engine.GetObservationAsync()).Model);
            await packs.RemoveAsync(pack.Key);
        }
        Assert.Equal(new[] { first.Key, second.Key }, loader.LoadedKeys);
    }

    [Fact]
    public async Task SetupSelectsHostPackAndSaveDoesNotInstallOrLoad()
    {
        TensorModelPackDefinition pack = Pack("host/custom");
        using var http = new HttpClient(new Handler());
        using var packs = Manager(http, pack);
        var target = new Target();
        TensorSettingsManager manager = SupraSetup.CreateSettingsManager(target, packs, offeredModels: [pack]);
        TensorSettings setup = await manager.OpenAsync(new(SetupChangeIntent.Create, "custom", null));
        SetupChoiceField model = Field(setup, "model");
        Assert.Equal(SetupEditState.Applied, model.SetValue(pack.Key.ToString()).State);
        Assert.Equal(SetupEditState.Applied, Field(setup, "backend").SetValue(TensorBackendKind.Cpu.ToString()).State);
        Assert.Equal(SetupCommitState.Committed, (await manager.SaveAsync(setup)).State);
        Assert.Equal(pack.Key, target.Changes!.Configuration.Model);
        var editTarget = new Target();
        TensorSettingsManager editManager = SupraSetup.CreateSettingsManager(editTarget, packs, offeredModels: [pack]);
        TensorSettings edit = await editManager.OpenAsync(new(SetupChangeIntent.Edit, "custom", "saved-v1"), target.Changes.Configuration);
        Assert.Equal(pack.Key.ToString(), Field(edit, "model").Value);
        Assert.Equal(SetupEditState.Applied, Field(edit, "backend").SetValue(nameof(TensorBackendKind.Auto)).State);
        Assert.Equal(SetupCommitState.Committed, (await editManager.SaveAsync(edit)).State);
        Assert.Equal("saved-v1", editTarget.Changes!.Identity.ExpectedVersion);
        Assert.Equal(pack.Key, editTarget.Changes.Configuration.Model);
        Assert.Equal(TensorBackendKind.Auto, editTarget.Changes.Configuration.Backend);
        Assert.True(editTarget.Changes.RequiresUnload);
        Assert.Equal(TensorModelPackState.NotInstalled, (await packs.GetStatusAsync(pack.Key)).State);
    }

    [Fact]
    public async Task RemoteSetupCommitsCompleteManifestWithoutRegisteringOrInstalling()
    {
        var source = new SupraMetadataFixture.Source(SupraMetadataFixture.Read());
        using var http = new HttpClient(new Handler());
        using var packs = Manager(http);
        var target = new Target();
        TensorSettingsManager manager = SupraSetup.CreateSettingsManager(target, packs, source: source);
        TensorSettings setup = await manager.OpenAsync(new(SetupChangeIntent.Create, "remote", null));
        foreach ((string field, string value) in new[] { ("repository", source.Candidate.Selection.Repository!),
            ("source-revision", "main"), ("variant", source.Candidate.Selection.Variant!), ("backend", "Cpu") })
        { Assert.Equal(SetupEditState.Applied, Field(setup, field).SetValue(value).State); }
        Assert.Equal(SetupActionState.Completed, (await manager.RunAsync(setup, "resolve")).State);
        Assert.Equal(SetupCommitState.Committed, (await manager.SaveAsync(setup)).State);
        Assert.Equal(TensorImageModels.Supra2Image.Key, target.Changes!.Configuration.Model);
        Assert.Equal(4, target.Changes.Definition.Artifacts.Count);
        Assert.All(target.Changes.Definition.Artifacts, artifact => Assert.NotNull(artifact.Provenance));
        Assert.Empty((await packs.GetDefinitionsAsync()));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task MissingCompanionAndWrongFamilyFailBeforeNativeWork()
    {
        TensorModelPackDefinition pack = Pack("host/missing");
        TensorModelPackDefinition missing = pack with { Artifacts = pack.Artifacts.Take(3).ToArray() };
        TensorModelPackDefinition wrong = pack with { Key = new("host/wrong", "one"), RuntimeArchitecture = "another-family" };
        using var http = new HttpClient(new Handler());
        using var packs = Manager(http, missing, wrong);
        var loader = new Loader();
        await using var engine = new SupraImageInference(packs, loader, TensorBackendKind.Cpu);
        foreach (TensorModelPackDefinition invalid in new[] { missing, wrong })
        {
            Assert.Throws<ArgumentException>(() => SupraSetup.CreateSettingsManager(new Target(), packs, offeredModels: [invalid]));
            InferenceException failure = await Assert.ThrowsAsync<InferenceException>(() => engine.LoadAndWaitAsync(invalid.Key));
            Assert.Equal(InferenceFailureCode.UnsupportedFeature, failure.Code);
        }
        Assert.Empty(loader.LoadedKeys);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledLoadReleasesThePack(bool cancel)
    {
        TensorModelPackDefinition pack = Pack("host/failure");
        using var http = new HttpClient(new Handler());
        using var packs = Manager(http, pack);
        await packs.InstallAsync(pack.Key);
        using var cancellation = new CancellationTokenSource();
        var loader = new Loader { AfterCreate = model => { if (cancel) { cancellation.Cancel(); } else { model.Dispose(); throw new InvalidDataException(); } } };
        await using var engine = new SupraImageInference(packs, loader, TensorBackendKind.Cpu);
        if (cancel) { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.LoadAndWaitAsync(pack.Key, cancellation.Token)); }
        else { Assert.Equal(InferenceFailureCode.ModelCorrupt, (await Assert.ThrowsAsync<InferenceException>(() => engine.LoadAndWaitAsync(pack.Key))).Code); }
        Assert.Null((await engine.GetObservationAsync()).Model);
        Assert.True(loader.Models.Single().Disposed);
        await packs.RemoveAsync(pack.Key);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnloadWaitsForNativeWorkEvenWhenUrgent(bool urgent)
    {
        TensorModelPackDefinition pack = Pack("host/resident");
        using var http = new HttpClient(new Handler());
        using var packs = Manager(http, pack);
        await packs.InstallAsync(pack.Key);
        using var entered = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        var loader = new Loader
        {
            AfterCreate = model => model.GenerateAction = token =>
        {
            entered.Set();
            Assert.True(finish.Wait(TimeSpan.FromSeconds(5), CancellationToken.None));
            token.ThrowIfCancellationRequested();
        }
        };
        await using var engine = new SupraImageInference(packs, loader, TensorBackendKind.Cpu);
        await engine.LoadAndWaitAsync(pack.Key);
        Task<ImageGenerationResult> request = Task.Run(() => engine.GenerateAsync(new() { ModelId = pack.Key.ModelId, Prompt = "test" }));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        Task unloading = engine.UnloadAndWaitAsync(new CancellationToken(urgent));
        Assert.False(unloading.IsCompleted);
        Assert.False(loader.Models.Single().Disposed);
        finish.Set();
        if (urgent) { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request); }
        else { await request; }
        await unloading;
        Assert.True(loader.Models.Single().Disposed);
        Assert.Null((await engine.GetObservationAsync()).Model);
    }

    private TensorModelPackManager Manager(HttpClient http, params TensorModelPackDefinition[] definitions) =>
        new(new(definitions), _root, http);

    private static TensorModelPackDefinition Pack(string id) => TensorImageModels.Supra2Image with
    {
        Key = new(id, "custom-revision"),
        Artifacts = TensorImageModels.Supra2Image.Artifacts.Select(artifact => artifact with
        {
            Length = Weights.Length,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(Weights)),
            Sources = [new("https://weights.example.test/" + artifact.Role)]
        }).ToArray()
    };

    private static SetupChoiceField Field(TensorSettings settings, string id) =>
        Assert.IsType<SetupChoiceField>(settings.Steps.SelectMany(step => step.Sections).SelectMany(section => section.Fields).Single(field => field.Description.Id == id));

    private sealed class Target : ISetupCommitTarget<TensorSetupChanges>
    {
        public TensorSetupChanges? Changes { get; private set; }
        public Task<SetupCommitResult> CommitAsync(TensorSetupChanges changes, CancellationToken cancellationToken = default)
        { Changes = changes; return Task.FromResult(new SetupCommitResult(SetupCommitState.Committed, "version")); }
    }

    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Weights) });
    }

    private sealed class Loader : ISupraModelLoader
    {
        public List<TensorModelKey> LoadedKeys { get; } = [];
        public List<Model> Models { get; } = [];
        public Action<Model>? AfterCreate { get; init; }
        public Task<ISupraImageModel> LoadAsync(TensorModelPackLease lease, TensorBackendKind backend, CancellationToken cancellationToken)
        {
            foreach (string role in new[] { "supra-model", "text-model", "text-tokenizer", "vae-model" })
            {
                Assert.True(File.Exists(lease.GetRequiredArtifact(role)));
            }
            LoadedKeys.Add(lease.Key);
            var model = new Model();
            Models.Add(model);
            AfterCreate?.Invoke(model);
            return Task.FromResult<ISupraImageModel>(model);
        }
    }

    private sealed class Model : ISupraImageModel
    {
        public bool Disposed { get; private set; }
        public Action<CancellationToken>? GenerateAction { get; set; }
        public byte[] Generate(string prompt, long seed, int steps, float guidanceScale, Action<int, int>? stepCompleted, Action? decodingStarted, CancellationToken cancellationToken)
        { Assert.False(Disposed); GenerateAction?.Invoke(cancellationToken); return new byte[256 * 256 * 3]; }
        public void Dispose() => Disposed = true;
    }

    public void Dispose() { if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); } }
}
