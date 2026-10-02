using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using Darkspyre.Inference.Common;
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
        await using AsyncServiceScope other = provider.CreateAsyncScope();
        Assert.NotSame(facade, other.ServiceProvider.GetRequiredService<ILlmFileClient>());
        Assert.Equal(0, source.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
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
            Assert.False(source.Disposed);
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
        Assert.Equal(new LlmFileCapabilities(), defaults.Files);
        Assert.Equal(new XaiSkillCapabilities(), defaults.Skills);
        var supplied = new LlmProviderCapabilities
        {
            Files = new() { Upload = true, PublicUrls = true },
            Skills = new() { ResourceOperations = true }
        };
        LlmProviderCapabilities applied = supplied.Apply(new() { SupportsStreaming = true });
        LlmFileCapabilities files = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(applied.Files, ConnectLlmJson.LlmFileCapabilities), ConnectLlmJson.LlmFileCapabilities)!;
        XaiSkillCapabilities skills = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(applied.Skills, ConnectLlmJson.XaiSkillCapabilities), ConnectLlmJson.XaiSkillCapabilities)!;
        Assert.True(files.Upload);
        Assert.True(files.PublicUrls);
        Assert.True(skills.ResourceOperations);
        Assert.False(skills.GenerationAttachment);
        Assert.False(applied.SupportsFileReferences);
    }

    [Fact]
    public void ActualForeignFacadeCallsAndOwnedCapabilitiesRetire()
    {
        WeakReference generation = ExerciseForeign();
        for (int attempt = 0; generation.IsAlive && attempt < 20; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); }
        Assert.False(generation.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ExerciseForeign()
    {
        var generation = new Generation(Path.GetDirectoryName(typeof(FileResourceFacadeTests).Assembly.Location)!);
        var weak = new WeakReference(generation);
        try
        {
            foreach (Assembly original in new[] { typeof(ConnectLlmJson).Assembly, typeof(InferenceJson).Assembly, typeof(FileResourceFacadeTests).Assembly })
            {
                Assembly loaded = generation.LoadFromAssemblyPath(original.Location);
                Assert.NotSame(original, loaded);
                Assert.Equal(original.ManifestModule.ModuleVersionId, loaded.ManifestModule.ModuleVersionId);
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(loaded));
            }
            Type caller = generation.Assemblies.Single(assembly => assembly.GetName().Name == typeof(FileResourceFacadeTests).Assembly.GetName().Name)
                .GetType(typeof(FileResourceFacadeTests).FullName!, true)!;
            var operation = (Task)caller.GetMethod(nameof(ForeignCallerAsync), BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
            operation.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        }
        finally { generation.Unload(); }
        return weak;
    }

    private static async Task ForeignCallerAsync()
    {
        AssemblyLoadContext? generation = AssemblyLoadContext.GetLoadContext(typeof(FileResourceFacadeTests).Assembly);
        Assert.True(generation!.IsCollectible);
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(typeof(LlmProviderService).Assembly));
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(typeof(InferenceJson).Assembly));
        Assert.Same(typeof(LlmProviderService).Assembly, ConnectLlmJson.LlmFileCapabilities.Type.Assembly);
        Assert.Same(typeof(LlmProviderService).Assembly, ConnectLlmJson.XaiSkillCapabilities.Type.Assembly);
        var fixture = new FileResourceFacadeTests();
        for (int operation = 0; operation < 12; operation++)
            await fixture.UnimplementedOperationsRejectBeforeSourceOrBorrowedStreamEffects(operation);
        fixture.CapabilityNestingIsInertAndApplyPreservesResourceFacts();
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

    private sealed class UnusedSource : ILlmConnectionSource, IDisposable
    {
        internal int Calls { get; private set; }
        internal bool Disposed { get; private set; }
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("Resource refusal must precede source resolution."); }
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion,
            CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("Resource refusal must precede credential changes."); }
        public void Dispose() => Disposed = true;
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

    private sealed class Generation(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (!name.Name!.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal)) return null;
            string path = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }
}
