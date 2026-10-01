using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class FileResourceFacadeTests
{
    [Fact]
    public async Task DependencyInjectionAliasesBorrowTheSameScopedFacade()
    {
        var source = new UnusedSource();
        var services = new ServiceCollection();
        services.AddSingleton<ILlmConnectionSource>(source);
        services.AddLlmConnect();
        await using ServiceProvider provider = services.BuildServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        var facade = scope.ServiceProvider.GetRequiredService<LlmProviderService>();
        Assert.Same(facade, scope.ServiceProvider.GetRequiredService<ILlmFileClient>());
        Assert.Same(facade, scope.ServiceProvider.GetRequiredService<IXaiFilePublicUrlClient>());
        Assert.Same(facade, scope.ServiceProvider.GetRequiredService<IXaiSkillClient>());
        Assert.Equal(0, source.Calls);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    [InlineData(8)] [InlineData(9)] [InlineData(10)] [InlineData(11)]
    public async Task UnimplementedOperationsRejectBeforeSourceOrBorrowedStreamEffects(int operation)
    {
        var source = new UnusedSource();
        var stream = new BorrowedStream();
        var facade = new LlmProviderService(source, []);
        try
        {
            await Assert.ThrowsAsync<NotSupportedException>(() => Invoke(facade, operation, stream));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => Invoke(facade, operation, stream, cancellation.Token));
            Assert.Equal(cancellation.Token, error.CancellationToken);
            await facade.DisposeAsync();
            await facade.DisposeAsync();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => Invoke(facade, operation, stream));
            Assert.Equal(0, source.Calls);
            Assert.Equal(0, stream.Calls);
            Assert.False(stream.Disposed);
        }
        finally
        {
            await facade.DisposeAsync();
            stream.Dispose();
        }
    }

    [Fact]
    public void CapabilityNestingIsInertAndApplyPreservesResourceFacts()
    {
        var defaults = new LlmProviderCapabilities();
        foreach (string name in new[] { "Files", "Skills" })
        {
            object facts = typeof(LlmProviderCapabilities).GetProperty(name)!.GetValue(defaults)!;
            foreach (var fact in facts.GetType().GetProperties()) Assert.Equal(false, fact.GetValue(facts));
        }
        var supplied = new LlmProviderCapabilities();
        typeof(LlmProviderCapabilities).GetProperty("Files")!.SetValue(supplied, new LlmFileCapabilities { Upload = true, PublicUrls = true });
        typeof(LlmProviderCapabilities).GetProperty("Skills")!.SetValue(supplied, new XaiSkillCapabilities { ResourceOperations = true });
        LlmProviderCapabilities applied = supplied.Apply(new() { SupportsStreaming = true });
        var files = (LlmFileCapabilities)typeof(LlmProviderCapabilities).GetProperty("Files")!.GetValue(applied)!;
        var skills = (XaiSkillCapabilities)typeof(LlmProviderCapabilities).GetProperty("Skills")!.GetValue(applied)!;
        Assert.True(files.Upload);
        Assert.True(files.PublicUrls);
        Assert.True(skills.ResourceOperations);
        Assert.False(skills.GenerationAttachment);
        Assert.False(applied.SupportsFileReferences);
    }

    private static Task Invoke(LlmProviderService facade, int operation, Stream stream,
        CancellationToken cancellationToken = default) => operation switch
        {
            0 => ((ILlmFileClient)(object)facade).UploadAsync("connection", stream, "file.txt", cancellationToken: cancellationToken),
            1 => ((ILlmFileClient)(object)facade).ListAsync("connection", new(), cancellationToken),
            2 => ((ILlmFileClient)(object)facade).GetAsync("connection", "file", cancellationToken),
            3 => ((ILlmFileClient)(object)facade).DownloadAsync("connection", "file", stream, cancellationToken: cancellationToken),
            4 => ((ILlmFileClient)(object)facade).DeleteAsync("connection", "file", cancellationToken),
            5 => ((IXaiFilePublicUrlClient)(object)facade).CreatePublicUrlAsync("connection", "file", cancellationToken: cancellationToken),
            6 => ((IXaiFilePublicUrlClient)(object)facade).RevokePublicUrlAsync("connection", "file", cancellationToken),
            7 => ((IXaiSkillClient)(object)facade).UploadAsync("connection", new XaiSkillZipUpload("skill.zip", stream), cancellationToken),
            8 => ((IXaiSkillClient)(object)facade).ListAsync("connection", new(), cancellationToken),
            9 => ((IXaiSkillClient)(object)facade).GetAsync("connection", "skill", cancellationToken),
            10 => ((IXaiSkillClient)(object)facade).DownloadAsync("connection", "skill", stream, cancellationToken),
            11 => ((IXaiSkillClient)(object)facade).DeleteAsync("connection", "skill", cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

    private sealed class UnusedSource : ILlmConnectionSource
    {
        internal int Calls { get; private set; }
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("Resource refusal must precede source resolution."); }
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion,
            CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("Resource refusal must precede credential changes."); }
    }

    private sealed class BorrowedStream : Stream
    {
        internal int Calls { get; private set; }
        internal bool Disposed { get; private set; }
        public override bool CanRead => Fail<bool>();
        public override bool CanSeek => Fail<bool>();
        public override bool CanWrite => Fail<bool>();
        public override long Length => Fail<long>();
        public override long Position { get => Fail<long>(); set => Fail<bool>(); }
        public override void Flush() => Fail<bool>();
        public override int Read(byte[] buffer, int offset, int count) => Fail<int>();
        public override long Seek(long offset, SeekOrigin origin) => Fail<long>();
        public override void SetLength(long value) => Fail<bool>();
        public override void Write(byte[] buffer, int offset, int count) => Fail<bool>();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        private T Fail<T>() { Calls++; throw new InvalidOperationException("Resource refusal must not inspect or use borrowed streams."); }
    }
}
