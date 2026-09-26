using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Tensor.STT.Whisper.Tests;

public sealed class WhisperMetadataTests
{
    [Fact]
    public async Task KnownContentCanBeInspectedAtAnIndependentUrlWithoutARepositoryAdapter()
    {
        var reader = new Source();
        TensorModelArtifactDefinition artifact = reader.Pack.Artifacts[0];
        var resolver = new WhisperModelResolver(reader: reader);
        var selection = new TensorModelSelection(new TensorModelLocation.RemoteFile(new("https://models.test/whisper.bin")),
            License: "MIT", ExpectedLength: artifact.Length, ExpectedSha256: artifact.Sha256);
        TensorModelResolution result = await resolver.ResolveAsync(selection);
        Assert.Equal(TensorCompatibilityState.Supported, result.Evaluation.State);
        Assert.Null(result.Definition!.Artifacts[0].Provenance!.Repository);
        Assert.Equal("models.test", result.Definition.Artifacts[0].Sources[0].Host);
        Assert.Null((await resolver.ResolveAsync(selection with { ExpectedSha256 = new('b', 64) })).Definition);
    }
    [Fact]
    public void AllTwelveVariantsDeclareImmutableIntegrityAndHonestCapabilities()
    {
        Assert.Equal(12, TensorSpeechModels.Whisper.Count);
        Assert.Equal(12, TensorSpeechModels.Whisper.Select(pack => pack.Key).Distinct().Count());
        foreach (TensorModelPackDefinition pack in TensorSpeechModels.Whisper)
        {
            WhisperCompatibility.Validate(pack);
            Assert.Equal(40, pack.Key.Revision.Length);
            Assert.Equal(TensorSpeechModels.Revision, pack.Artifacts.Single().Provenance!.Revision);
            Assert.Equal(64, pack.Artifacts[0].Sha256.Length);
            Assert.True(pack.Artifacts[0].Length > 77_000_000);
            Assert.True(pack.SupportedBackends.SetEquals([TensorBackendKind.Cpu]));
            SpeechModelInfo info = TensorSpeechModels.Info(pack);
            Assert.Equal(pack.Key.ModelId.EndsWith(".en", StringComparison.Ordinal), info.EnglishOnly);
            Assert.Equal(info.EnglishOnly == false && !pack.Key.ModelId.EndsWith("-turbo", StringComparison.Ordinal), info.SupportsTranslationToEnglish);
        }
        Assert.Contains(InferenceTask.SpeechTranscription, WhisperSetup.Definition.Implementation.Tasks);
    }

    [Fact]
    public async Task SetupSelectionUsesExistingGenericContractAndSavingDoesNotInstall()
    {
        using var directory = new WhisperFixture.PackDirectory();
        using var packs = new TensorModelPackManager(new(TensorSpeechModels.Whisper), directory.Path);
        var target = new Target();
        TensorSettingsManager manager = WhisperSetup.CreateSettingsManager(target, packs);
        TensorSettings settings = await manager.OpenAsync(new(SetupChangeIntent.Create, "speech", null));
        Assert.False(settings.Validate().IsValid);
        SetupChoiceField model = Field(settings, "model");
        Assert.Equal(12, (await manager.QueryChoicesAsync(settings, "model", new())).Choices.Count);
        TensorModelPackDefinition selected = TensorSpeechModels.Whisper.Single(pack => pack.Key.ModelId == "whisper/large-v3-turbo");
        Assert.Equal(SetupEditState.Applied, model.SetValue(selected.Key.ToString()).State);
        Assert.Equal(SetupEditState.Applied, Field(settings, "backend").SetValue("Cpu").State);
        Assert.Equal(SetupCommitState.Committed, (await manager.SaveAsync(settings)).State);
        Assert.Equal(selected.Key, target.Changes!.Configuration.Model);
        var editTarget = new Target();
        TensorSettingsManager editManager = WhisperSetup.CreateSettingsManager(editTarget, packs);
        TensorSettings edit = await editManager.OpenAsync(new(SetupChangeIntent.Edit, "speech", "saved-v1"), target.Changes.Configuration);
        Assert.Equal(selected.Key.ToString(), Field(edit, "model").Value);
        TensorModelPackDefinition replacement = TensorSpeechModels.Whisper.Single(pack => pack.Key.ModelId == "whisper/tiny.en");
        Assert.Equal(SetupEditState.Applied, Field(edit, "model").SetValue(replacement.Key.ToString()).State);
        Assert.Equal(SetupCommitState.Committed, (await editManager.SaveAsync(edit)).State);
        Assert.Equal("saved-v1", editTarget.Changes!.Identity.ExpectedVersion);
        Assert.Equal(replacement.Key, editTarget.Changes.Configuration.Model);
        Assert.True(editTarget.Changes.RequiresUnload);
        Assert.Equal(TensorModelPackState.NotInstalled, (await packs.GetStatusAsync(selected.Key)).State);
        Assert.Equal(TensorModelPackState.NotInstalled, (await packs.GetStatusAsync(replacement.Key)).State);
        Assert.Null(settings.Status.Loaded.Value);
    }

    [Fact]
    public async Task RemoteResolutionBindsSetupAndRetainsBuiltInIdentityWithoutRegistration()
    {
        var source = new Source();
        using var directory = new WhisperFixture.PackDirectory();
        using var packs = new TensorModelPackManager(new([]), directory.Path);
        var target = new Target();
        TensorSettingsManager manager = WhisperSetup.CreateSettingsManager(target, packs, source: source);
        TensorSettings settings = await manager.OpenAsync(new(SetupChangeIntent.Create, "speech", null));
        foreach ((string field, string value) in new[] { ("repository", TensorSpeechModels.Repository), ("source-revision", "main"), ("variant", "ggml-tiny.bin") })
            Assert.Equal(SetupEditState.Applied, Field(settings, field).SetValue(value).State);
        Assert.Equal(SetupActionState.Completed, (await manager.RunAsync(settings, "resolve")).State);
        Assert.Equal(SetupCommitState.Committed, (await manager.SaveAsync(settings)).State);
        Assert.Equal(source.Pack.Key, target.Changes!.Definition.Key);
        Assert.Empty((await packs.GetDefinitionsAsync()));
        Assert.Single(source.Reads);
        Assert.Equal(TensorMetadataReadKind.HeaderRange, source.Reads[0].Kind);
        Assert.Equal(65536, source.Reads[0].Length);
    }

    [Fact]
    public async Task UnknownIntegrityGatedAccessWrongHeaderAndBackendNeverBecomeSupported()
    {
        var source = new Source();
        var resolver = new WhisperModelResolver(source);
        Assert.Equal(TensorCompatibilityState.Unsupported, (await resolver.EvaluateAsync(source.Candidate, TensorBackendKind.Cuda)).State);
        Assert.Equal(TensorCompatibilityState.Unverified, (await resolver.EvaluateAsync(source.Candidate with
        { Artifacts = [source.Candidate.Artifacts[0] with { Sha256 = new('a', 64) }] }, TensorBackendKind.Cpu)).State);
        Assert.Empty(source.Reads);
        source.State = TensorSourceState.Gated;
        Assert.Equal(TensorSourceState.Gated, (await resolver.ResolveAsync(new(new TensorModelLocation.RepositoryArtifact(source.Id, source.Candidate.Selection)))).Evaluation.SourceState);
        source.State = TensorSourceState.Available;
        source.Header = WhisperFixture.Header(english: true);
        Assert.Equal(TensorCompatibilityState.Unsupported, (await resolver.ResolveAsync(new(new TensorModelLocation.RepositoryArtifact(source.Id, source.Candidate.Selection)))).Evaluation.State);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.ResolveAsync(new(new TensorModelLocation.RepositoryArtifact(source.Id, source.Candidate.Selection)), new(true)));
    }

    [Fact]
    public async Task RuntimeChecksAreExplicitAndBackendSpecific()
    {
        int calls = 0;
        var probe = new WhisperBackendProbe(() => { calls++; return "runtime is callable"; });
        Assert.Equal(0, calls);
        Assert.True((await probe.CheckAsync(TensorBackendKind.Cpu)).IsAvailable);
        Assert.True((await probe.CheckAsync(TensorBackendKind.Auto)).IsAvailable);
        Assert.False((await probe.CheckAsync(TensorBackendKind.Cuda)).IsAvailable);
        Assert.Equal(2, calls);
        Assert.False((await new WhisperBackendProbe(() => throw new DllNotFoundException()).CheckAsync(TensorBackendKind.Cpu)).IsAvailable);
        Assert.Null((await new WhisperBackendProbe(() => null).CheckAsync(TensorBackendKind.Cpu)).IsAvailable);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.CheckAsync(TensorBackendKind.Cpu, new(true)));
    }

    private static SetupChoiceField Field(TensorSettings settings, string id) => Assert.IsType<SetupChoiceField>(
        settings.Steps.SelectMany(step => step.Sections).SelectMany(section => section.Fields).Single(field => field.Description.Id == id));
    private sealed class Target : ISetupCommitTarget<TensorSetupChanges>
    {
        public TensorSetupChanges? Changes { get; private set; }
        public Task<SetupCommitResult> CommitAsync(TensorSetupChanges changes, CancellationToken cancellationToken = default)
        { Changes = changes; return Task.FromResult(new SetupCommitResult(SetupCommitState.Committed, "v1")); }
    }

    private sealed class Source : ITensorModelSource
    {
        public string Id => "hugging-face";
        public TensorModelPackDefinition Pack { get; } = TensorSpeechModels.Whisper.Single(pack => pack.Key.ModelId == "whisper/tiny");
        public TensorModelCandidate Candidate { get; }
        public TensorSourceState State { get; set; } = TensorSourceState.Available;
        public byte[] Header { get; set; } = WhisperFixture.Header();
        public List<TensorMetadataRead> Reads { get; } = [];
        public Source()
        {
            TensorModelArtifactDefinition artifact = Pack.Artifacts[0];
            Candidate = new(Id, new(TensorSpeechModels.Repository, TensorSpeechModels.Revision, artifact.RelativePath),
                new("whisper", null, null), "MIT", false, false,
                [new(artifact.RelativePath, artifact.Length, artifact.Sha256, artifact.Provenance!, artifact.Sources[0])]);
        }
        public Task<TensorSourceResult<TensorModelCandidate>> GetCandidateAsync(TensorCandidateSelection selection, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(new TensorSourceResult<TensorModelCandidate>(State, State == TensorSourceState.Available ? Candidate : null)); }
        public Task<TensorSourceResult<TensorMetadataContent>> ReadMetadataAsync(TensorSourceArtifact artifact, TensorMetadataRead read, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Reads.Add(read);
            byte[] bytes = new byte[read.Length]; Header.CopyTo(bytes, 0);
            return Task.FromResult(new TensorSourceResult<TensorMetadataContent>(State, new(bytes, read.Offset, artifact.Length!.Value)));
        }
        public Task<TensorSourceResult<TensorSourcePage<TensorSourceModel>>> SearchAsync(TensorSourceQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TensorSourceResult<TensorSourcePage<TensorSourceRevision>>> ListRevisionsAsync(TensorRepositoryQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TensorSourceResult<TensorSourcePage<TensorSourceArtifact>>> ListVariantsAsync(TensorVariantQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
