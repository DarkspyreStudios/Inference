namespace Darkspyre.Inference.Tensor.Common.Tests;

public sealed class TensorMetadataReaderTests
{
    private static readonly TensorSourceArtifact Artifact = new("weight.bin", 1L << 30, new('a', 64),
        new(new("https://example.test/"), "owner/model", new('1', 40), "weight.bin", "test"), new("https://example.test/weight.bin"));

    [Fact]
    public async Task AParserThatCatchesTheMissingRangeStillRunsAgainOverDownloadedBytes()
    {
        var source = new Source();
        var reader = new TensorMetadataReader(source);
        int result = await reader.ReadAsync(Artifact, stream =>
        {
            try { return stream.ReadByte(); }
            catch (IOException) { return -1; }
        });
        Assert.Equal(0, result);
        Assert.Single(source.Reads);
    }

    private sealed class Source : ITensorModelSource
    {
        public string Id => "test";
        public List<TensorMetadataRead> Reads { get; } = [];
        public TensorSourceResult<TensorMetadataContent>? Result { get; init; }
        public Task<TensorSourceResult<TensorMetadataContent>> ReadMetadataAsync(TensorSourceArtifact artifact, TensorMetadataRead read, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads.Add(read);
            return Task.FromResult(Result ?? new(TensorSourceState.Available,
                new(Enumerable.Range(0, read.Length).Select(index => (byte)((read.Offset + index) % 256)).ToArray(), read.Offset, artifact.Length!.Value)));
        }
        public Task<TensorSourceResult<TensorSourcePage<TensorSourceModel>>> SearchAsync(TensorSourceQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TensorSourceResult<TensorSourcePage<TensorSourceRevision>>> ListRevisionsAsync(TensorRepositoryQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TensorSourceResult<TensorSourcePage<TensorSourceArtifact>>> ListVariantsAsync(TensorVariantQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TensorSourceResult<TensorModelCandidate>> GetCandidateAsync(TensorCandidateSelection selection, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
