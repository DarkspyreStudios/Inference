namespace Darkspyre.Inference.Tensor.Common.Tests;

public sealed class TensorMetadataReaderTests
{
    private static readonly TensorSourceArtifact Artifact = new("weight.bin", 1L << 30, new('a', 64),
        new(new("https://example.test/"), "owner/model", new('1', 40), "weight.bin", "test"), new("https://example.test/weight.bin"));

    [Fact]
    public async Task SeekableInspectionCachesBoundedPagesWithoutReadingTheWholeArtifact()
    {
        var source = new Source();
        var reader = new TensorMetadataReader(source);
        int result = await reader.ReadAsync(Artifact, stream =>
        {
            Assert.Null(SynchronizationContext.Current);
            Assert.Equal(1L << 30, stream.Length);
            Assert.Equal(0, stream.ReadByte());
            stream.Seek(-1, SeekOrigin.End);
            Assert.Equal(255, stream.ReadByte());
            stream.Position = 1;
            return stream.ReadByte();
        });
        Assert.Equal(1, result);
        Assert.Equal(2, source.Reads.Count);
        Assert.All(source.Reads, read => Assert.Equal(65536, read.Length));
        Assert.All(source.Reads, read => Assert.Equal(TensorMetadataReadKind.HeaderRange, read.Kind));
    }

    [Fact]
    public async Task ResolutionBudgetIsSharedAcrossArtifactsAndPreventsAnotherRequest()
    {
        var source = new Source();
        var reader = new TensorMetadataReader(source, 65536);
        await reader.ReadAsync(Artifact, stream => stream.ReadByte());
        TensorSourceException error = await Assert.ThrowsAsync<TensorSourceException>(() => reader.ReadAsync(Artifact, stream => stream.ReadByte()));
        Assert.Equal("metadata-budget", error.Code);
        Assert.Single(source.Reads);
    }

    [Fact]
    public async Task SourceFailuresKeepTheirTypedAccessStateAndCancellationPreventsReads()
    {
        var source = new Source { Result = new(TensorSourceState.Gated, Issues: [new("source-gated", "License acceptance required.")]) };
        var reader = new TensorMetadataReader(source);
        TensorSourceException error = await Assert.ThrowsAsync<TensorSourceException>(() => reader.ReadAsync(Artifact, stream => stream.ReadByte()));
        Assert.Equal(TensorSourceState.Gated, error.State);
        Assert.Equal("source-gated", error.Code);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(Artifact, stream => stream.ReadByte(), new(true)));
        Assert.Single(source.Reads);
    }

    [Fact]
    public async Task InconsistentRangeAndUnknownSizeCannotBeParsedAsVerifiedMetadata()
    {
        var source = new Source { Result = new(TensorSourceState.Available, new(new byte[1], 0, Artifact.Length!.Value)) };
        var reader = new TensorMetadataReader(source);
        Assert.Equal("metadata-range", (await Assert.ThrowsAsync<TensorSourceException>(() => reader.ReadAsync(Artifact, stream => stream.ReadByte()))).Code);
        Assert.Equal("metadata-size", (await Assert.ThrowsAsync<TensorSourceException>(() => reader.ReadAsync(Artifact with { Length = null }, stream => stream.ReadByte()))).Code);
        Assert.Single(source.Reads);
    }

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

    [Fact]
    public async Task SequentialReadsDoubleTheDownloadWindow()
    {
        var source = new Source();
        var reader = new TensorMetadataReader(source);
        await reader.ReadAsync(Artifact, stream =>
        {
            stream.ReadExactly(new byte[4 * 65536]);
            return 0;
        });
        Assert.Equal([65536, 131072, 262144], source.Reads.Select(read => read.Length));
        Assert.Equal([0L, 65536L, 196608L], source.Reads.Select(read => read.Offset));
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
