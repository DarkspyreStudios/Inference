using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Tensor.LLM.Qwen.Tests;

public sealed class QwenModelResolverTests
{
    private const string Commit = "1111111111111111111111111111111111111111";

    [Fact]
    public async Task ActualHeaderPolicyResolvesPinnedPackWithoutDownloadingWeights()
    {
        var source = new Source();
        TensorModelResolution result = await new QwenModelResolver(source).ResolveAsync(new(new TensorModelLocation.RepositoryArtifact(source.Id, new("owner/model", "main", "model.gguf")), TensorBackendKind.Cpu));
        Assert.Equal(TensorCompatibilityState.Supported, result.Evaluation.State);
        Assert.NotNull(result.Definition);
        Assert.StartsWith(Commit + ":", result.Definition.Key.Revision, StringComparison.Ordinal);
        TensorModelArtifactDefinition artifact = Assert.Single(result.Definition.Artifacts);
        Assert.Equal("model", artifact.Role);
        Assert.Equal(Commit, artifact.Provenance!.Revision);
        Assert.Equal(1L << 30, artifact.Length);
        Assert.Contains(result.Evaluation.Evidence, item => item.Check == "context-length" && item.Detail == "2048");
        Assert.All(source.Reads, read => Assert.Equal(TensorMetadataReadKind.HeaderRange, read.Kind));
        Assert.Equal(65536, Assert.Single(source.Reads).Length);
    }

    [Theory]
    [InlineData("architecture")]
    [InlineData("template")]
    [InlineData("context")]
    [InlineData("tensor")]
    [InlineData("shape")]
    [InlineData("quantization")]
    [InlineData("shards")]
    public async Task RepositoryTagsDoNotOverrideIncompatibleCheckpointEvidence(string change)
    {
        var source = new Source(change);
        var resolver = new QwenModelResolver(source);
        TensorModelResolution result = await resolver.ResolveAsync(new(new TensorModelLocation.RepositoryArtifact(source.Id, new("owner/model", Commit, "model.gguf"))));
        Assert.Equal(TensorCompatibilityState.Unsupported, result.Evaluation.State);
        Assert.Null(result.Definition);
        Assert.NotEmpty(result.Evaluation.Issues);
    }

    [Fact]
    public async Task MissingIntegrityLicenseAndProtectedHeadersRemainUnverified()
    {
        var source = new Source();
        var resolver = new QwenModelResolver(source);
        foreach (TensorModelCandidate candidate in new[]
        {
            source.Candidate with { License = null },
            source.Candidate with { Artifacts = [source.Candidate.Artifacts[0] with { Sha256 = null }] },
            source.Candidate with { Artifacts = [source.Candidate.Artifacts[0] with { Length = null }] }
        })
        { Assert.Equal(TensorCompatibilityState.Unverified, (await resolver.EvaluateAsync(candidate, TensorBackendKind.Cpu)).State); }
        Assert.Empty(source.Reads);
        source.ReadState = TensorSourceState.Gated;
        TensorModelEvaluation result = await resolver.EvaluateAsync(source.Candidate, TensorBackendKind.Cpu);
        Assert.Equal(TensorCompatibilityState.Unverified, result.State);
        Assert.Equal(TensorSourceState.Gated, result.SourceState);
    }

    [Fact]
    public async Task CompleteSplitSetUsesOneCanonicalSelectionAndRejectsMissingShards()
    {
        var source = new Source(split: true);
        var resolver = new QwenModelResolver(source);
        TensorModelResolution first = await resolver.ResolveAsync(new(new TensorModelLocation.RepositoryArtifact(source.Id, new("owner/model", Commit, source.Candidate.Artifacts[0].Path))));
        TensorModelResolution second = await resolver.ResolveAsync(new(new TensorModelLocation.RepositoryArtifact(source.Id, new("owner/model", Commit, source.Candidate.Artifacts[1].Path))));
        Assert.Equal(TensorCompatibilityState.Supported, first.Evaluation.State);
        Assert.Equal(first.Definition!.Key, second.Definition!.Key);
        Assert.Equal(2, first.Definition.Artifacts.Count);
        Assert.Contains(first.Definition.Artifacts, artifact => artifact.Role == "model-shard-2");
        TensorModelCandidate missing = source.Candidate with { Artifacts = [source.Candidate.Artifacts[0]] };
        Assert.Equal(TensorCompatibilityState.Unverified, (await resolver.EvaluateAsync(missing, TensorBackendKind.Cpu)).State);
        TensorModelCandidate ambiguous = source.Candidate with { Artifacts = [.. source.Candidate.Artifacts, source.Candidate.Artifacts[1]] };
        Assert.Equal(TensorCompatibilityState.Unverified, (await resolver.EvaluateAsync(ambiguous, TensorBackendKind.Cpu)).State);
    }

    [Fact]
    public async Task UnsupportedBackendAndCancellationDoNotReadHeaders()
    {
        var source = new Source();
        var resolver = new QwenModelResolver(source);
        Assert.Equal(TensorCompatibilityState.Unsupported, (await resolver.EvaluateAsync(source.Candidate, (TensorBackendKind)999)).State);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.EvaluateAsync(source.Candidate, TensorBackendKind.Cpu, new(true)));
        Assert.Empty(source.Reads);
    }

    [Fact]
    public async Task RealQwenSetupBindsResolverAndCommitsACompleteManifestWithoutInstall()
    {
        var source = new Source();
        var target = new Target();
        await using TensorSetupSession session = QwenSetup.CreateSession(new(SetupChangeIntent.Create, "test", null), target, new NoInstallPacks(), source: source);
        Select(session, "repository", "owner/model"); Select(session, "source-revision", "main"); Select(session, "variant", "model.gguf");
        ISetupAction resolve = session.Snapshot.Actions.Single(action => action.Descriptor.Id == "resolve");
        Assert.Equal(SetupActionState.Completed, (await resolve.ExecuteAsync()).State);
        Assert.Equal(SetupCommitState.Committed, (await session.CommitAsync()).State);
        Assert.Equal(TensorLlmModels.QwenRuntimeArchitecture, target.Changes!.Definition.RuntimeArchitecture);
        Assert.Equal(target.Changes.Definition.Key, target.Changes.Configuration.Model);
    }

    private static void Select(TensorSetupSession session, string field, string value)
    {
        SetupChoiceField choice = Assert.IsType<SetupChoiceField>(session.Snapshot.Steps.SelectMany(step => step.Sections)
            .SelectMany(section => section.Fields).Single(item => item.Description.Id == field));
        Assert.Equal(SetupEditState.Applied, choice.SetValue(value).State);
    }
    private sealed class Target : ISetupCommitTarget<TensorSetupChanges>
    {
        public TensorSetupChanges? Changes { get; private set; }
        public Task<SetupCommitResult> CommitAsync(TensorSetupChanges changes, CancellationToken cancellationToken = default)
        { Changes = changes; return Task.FromResult(new SetupCommitResult(SetupCommitState.Committed, "v1")); }
    }
    private sealed class NoInstallPacks : ITensorModelPackManager
    {
        public Task<TensorModelImportResult> ImportAsync(TensorModelImportRequest request, IProgress<TensorModelInstallProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TensorModelPackDefinition>> GetDefinitionsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TensorModelPackDefinition>>([]);
        public Task<TensorModelPackDefinition> RegisterAsync(TensorModelPackDefinition definition, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Must not register during Save.");
        public Task<TensorModelPackDefinition> GetRequiredDefinitionAsync(TensorModelKey key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TensorModelPackStatus> GetStatusAsync(TensorModelKey key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task InstallAsync(TensorModelKey key, IProgress<TensorModelInstallProgress>? progress = null, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Must not install during Save.");
        public Task<TensorModelPackStatus> VerifyAsync(TensorModelKey key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TensorModelPackLease> AcquireAsync(TensorModelKey key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RemoveAsync(TensorModelKey key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class Source : ITensorModelSource
    {
        private readonly Dictionary<string, byte[]> _headers;
        public string Id => "test";
        public TensorModelCandidate Candidate { get; }
        public List<TensorMetadataRead> Reads { get; } = [];
        public TensorSourceState ReadState { get; set; } = TensorSourceState.Available;
        public Source(string change = "valid", bool split = false)
        {
            _headers = split ? new()
            {
                ["quant/model-00001-of-00002.gguf"] = QwenCompatibilityTests.Fixture(change, 0),
                ["quant/model-00002-of-00002.gguf"] = QwenCompatibilityTests.Fixture(change, 1)
            }
                : new() { ["model.gguf"] = QwenCompatibilityTests.Fixture(change) };
            TensorSourceArtifact[] artifacts = _headers.Keys.Select(path => new TensorSourceArtifact(path, 1L << 30, new('a', 64),
                new(new("https://example.test/"), "owner/model", Commit, path, "test-license"), new("https://example.test/" + path))).ToArray();
            Candidate = new(Id, new("owner/model", Commit, artifacts[0].Path), new("qwen3", 40960, true), "test-license", false, false, artifacts);
        }
        public Task<TensorSourceResult<TensorModelCandidate>> GetCandidateAsync(TensorCandidateSelection selection, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(new TensorSourceResult<TensorModelCandidate>(TensorSourceState.Available, Candidate with { Selection = Candidate.Selection with { Variant = selection.Variant } })); }
        public Task<TensorSourceResult<TensorMetadataContent>> ReadMetadataAsync(TensorSourceArtifact artifact, TensorMetadataRead read, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Reads.Add(read);
            if (ReadState != TensorSourceState.Available) return Task.FromResult(new TensorSourceResult<TensorMetadataContent>(ReadState));
            byte[] bytes = new byte[read.Length];
            byte[] fixture = _headers[artifact.Path];
            if (read.Offset < fixture.Length) fixture.AsSpan((int)read.Offset, Math.Min(read.Length, fixture.Length - (int)read.Offset)).CopyTo(bytes);
            return Task.FromResult(new TensorSourceResult<TensorMetadataContent>(TensorSourceState.Available, new(bytes, read.Offset, artifact.Length!.Value)));
        }
        public Task<TensorSourceResult<TensorSourcePage<TensorSourceModel>>> SearchAsync(TensorSourceQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TensorSourceResult<TensorSourcePage<TensorSourceRevision>>> ListRevisionsAsync(TensorRepositoryQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TensorSourceResult<TensorSourcePage<TensorSourceArtifact>>> ListVariantsAsync(TensorVariantQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
