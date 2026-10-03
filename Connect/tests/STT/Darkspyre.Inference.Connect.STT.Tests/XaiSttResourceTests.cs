using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Common.STT;
using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.STT.Tests;

public sealed class XaiSttResourceTests
{
    private static readonly InferenceResourceReference Reference = new("workspace", "actual");

    [Fact]
    public async Task OnlyNamedAudioIsReadWithoutSelectionAndItsHandleOwnsContent()
    {
        var resources = new XaiSttResourceFixture(); using var access = new LlmConnectionAccess(new XaiSttTests.Source(), []);
        using var http = Http(); await using var client = XaiSttTests.Provider(http).Bind(XaiSttTests.Connection, new(new("selected"), access), resources);
        Assert.Equal("actual", (await client.TranscribeAsync(new ResourceSpeechInput(Reference), new())).Text);
        Assert.Equal(1, resources.Gets); Assert.Equal(1, resources.Reads); Assert.Equal(0, resources.Selections);
        Assert.Equal(1, resources.HandleDisposals); Assert.Equal(1, resources.ContentDisposals); Assert.Equal(0, resources.BorrowedDisposals);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("invalid mime")]
    public async Task MissingOrInvalidNamedTypeRefusesBeforeOpeningContent(string? contentType)
    {
        var resources = new XaiSttResourceFixture { ContentType = contentType }; using var access = new LlmConnectionAccess(new XaiSttTests.Source(), []);
        var handler = new XaiSttTests.Handler(_ => throw new InvalidOperationException("No HTTP.")); using var http = new HttpClient(handler);
        await using var client = XaiSttTests.Provider(http).Bind(XaiSttTests.Connection, new(new("selected"), access), resources);
        var error = Assert.IsType<InferenceResourceOperationException>(await Record.ExceptionAsync(() => client.TranscribeAsync(new ResourceSpeechInput(Reference), new())));
        Assert.Equal(InferenceResourceOperationPhase.OpenRead, error.Failure.Phase); Assert.Equal(0, resources.Reads); Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ContradictoryImmutableFactsAndKnownHandleSizeCannotSucceed(bool contradictory)
    {
        var resources = new XaiSttResourceFixture { GetBytes = contradictory ? 3 : null, HandleBytes = 5 };
        using var access = new LlmConnectionAccess(new XaiSttTests.Source(), []); using var http = Http();
        await using var client = XaiSttTests.Provider(http).Bind(XaiSttTests.Connection, new(new("selected"), access), resources);
        var failure = await Record.ExceptionAsync(() => client.TranscribeAsync(new ResourceSpeechInput(Reference), new())); Assert.NotNull(failure);
        Assert.True(ContainsResource(failure!)); Assert.Equal(1, resources.HandleDisposals); Assert.Equal(0, resources.BorrowedDisposals);
    }

    [Fact]
    public async Task ReentrantDescriptorRetirementDoesNotReadHandleContent()
    {
        var resources = new XaiSttResourceFixture(); using var access = new LlmConnectionAccess(new XaiSttTests.Source(), []); using var http = Http();
        var client = XaiSttTests.Provider(http).Bind(XaiSttTests.Connection, new(new("selected"), access), resources); Task? retirement = null;
        resources.OnHandleResource = () => retirement = client.DisposeAsync().AsTask();
        try
        {
            var error = Assert.IsType<InferenceResourceOperationCanceledException>(await Record.ExceptionAsync(() => client.TranscribeAsync(new ResourceSpeechInput(Reference), new())));
            Assert.Equal(InferenceResourceOperationPhase.OpenRead, error.Failure.Phase);
            await retirement!.WaitAsync(XaiSttStreamingTests.Deadline); Assert.Equal(0, resources.ContentGets); Assert.Equal(1, resources.HandleDisposals);
        }
        finally { await client.DisposeAsync(); }
    }

    [Fact]
    public async Task RefusedReadAndCleanupRemainIndependentAndRepeatedRetirementCachesCleanupOnly()
    {
        var original = new InvalidOperationException("actual read refusal"); var cleanup = new InvalidOperationException("actual handle refusal");
        var resources = new XaiSttResourceFixture { ReadFailure = original, CleanupFailure = cleanup };
        using var access = new LlmConnectionAccess(new XaiSttTests.Source(), []); using var http = Http();
        var client = XaiSttTests.Provider(http).Bind(XaiSttTests.Connection, new(new("selected"), access), resources);
        try
        {
            var aggregate = Assert.IsType<AggregateException>(await Record.ExceptionAsync(() => client.TranscribeAsync(new ResourceSpeechInput(Reference), new())));
            Assert.True(Contains(aggregate.InnerExceptions[0], original));
            var refused = Assert.IsType<InferenceResourceOperationException>(aggregate.InnerExceptions[1]);
            Assert.Equal(InferenceResourceOperationPhase.Cleanup, refused.Failure.Phase); Assert.Same(cleanup, refused.InnerException);
            Assert.Same(refused, await Record.ExceptionAsync(() => client.DisposeAsync().AsTask()));
            Assert.Same(refused, await Record.ExceptionAsync(() => client.DisposeAsync().AsTask()));
            Assert.Equal(1, resources.HandleDisposals); Assert.Equal(0, resources.ContentDisposals); Assert.Equal(0, resources.BorrowedDisposals);
        }
        finally { _ = await Record.ExceptionAsync(() => client.DisposeAsync().AsTask()); }
    }

    [Fact]
    public void RetainedRetiredDefaultClientReleasesActualSeparatelyForeignResourceCollaborator()
    {
        Evidence evidence = ExerciseForeign();
        try
        {
            for (int attempt = 0; evidence.Roots.Any(root => root.IsAlive) && attempt < 30; attempt++)
            { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); }
            foreach (WeakReference root in evidence.Roots) Assert.False(root.IsAlive);
        }
        finally { GC.KeepAlive(evidence.Client); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Evidence ExerciseForeign()
    {
        var context = new Foreign(); Assembly assembly = context.LoadFromAssemblyPath(typeof(XaiSttResourceFixture).Assembly.Location);
        Type type = assembly.GetType(typeof(XaiSttResourceFixture).FullName!, true)!;
        Assert.Equal(typeof(XaiSttResourceFixture).Assembly.ManifestModule.ModuleVersionId, assembly.ManifestModule.ModuleVersionId);
        Assert.Same(context, AssemblyLoadContext.GetLoadContext(type.Assembly));
        object instance = Activator.CreateInstance(type)!; var resources = Assert.IsAssignableFrom<IInferenceResourceAccess>(instance);
        using var access = new LlmConnectionAccess(new XaiSttTests.Source(), []); using var http = Http();
        var client = XaiSttTests.Provider(http).Bind(XaiSttTests.Connection, new(new("selected"), access), resources);
        try
        {
            Assert.Equal("actual", client.TranscribeAsync(new ResourceSpeechInput(Reference), new()).GetAwaiter().GetResult().Text);
            Assert.Equal(1, type.GetField(nameof(XaiSttResourceFixture.Gets))!.GetValue(instance));
            Assert.Equal(1, type.GetField(nameof(XaiSttResourceFixture.Reads))!.GetValue(instance));
            Assert.Equal(0, type.GetField(nameof(XaiSttResourceFixture.BorrowedDisposals))!.GetValue(instance));
            Assert.Same(assembly, type.GetMethod(nameof(IInferenceResourceAccess.OpenReadAsync))!.Module.Assembly);
            client.Dispose(); return new(client, [new(context), new(assembly), new(type), new(instance)]);
        }
        finally { context.Unload(); }
    }
    private sealed record Evidence(ISpeechTranscriptionClient Client, WeakReference[] Roots);
    private sealed class Foreign() : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name) => name.Name == typeof(XaiSttResourceFixture).Assembly.GetName().Name ? null
            : Default.Assemblies.FirstOrDefault(assembly => assembly.GetName().Name == name.Name);
    }
    private static HttpClient Http() => new(new XaiSttTests.Handler(async request =>
    { _ = await request.Content!.ReadAsByteArrayAsync(); return XaiSttTests.Json("{\"text\":\"actual\"}"); }));
    private static bool ContainsResource(Exception error) => error is InferenceResourceOperationException or InferenceResourceOperationCanceledException
        || error is AggregateException aggregate && aggregate.InnerExceptions.Any(ContainsResource) || error.InnerException is { } inner && ContainsResource(inner);
    private static bool Contains(Exception error, Exception original) => ReferenceEquals(error, original)
        || error is AggregateException aggregate && aggregate.InnerExceptions.Any(value => Contains(value, original)) || error.InnerException is { } inner && Contains(inner, original);
}

public sealed class XaiSttResourceFixture : IInferenceResourceAccess, IDisposable
{
    public string WorkspaceId => "workspace"; public string SessionId => "session";
    public int Gets, Reads, Selections, HandleDisposals, ContentDisposals, ContentGets, BorrowedDisposals;
    public string? ContentType { get; init; } = "audio/wav";
    public long? GetBytes { get; init; } = 3;
    public long? HandleBytes { get; init; } = 3;
    public Exception? ReadFailure { get; init; }
    public Exception? CleanupFailure { get; init; }
    public Action? OnHandleResource { get; set; }
    public Task<InferenceResourceDescriptor?> GetAsync(InferenceResourceReference reference, CancellationToken cancellationToken = default)
    { Gets++; return Task.FromResult<InferenceResourceDescriptor?>(new(reference, SessionId, null, ContentType, GetBytes)); }
    public Task<IInferenceResourceReadHandle> OpenReadAsync(InferenceResourceReference reference, CancellationToken cancellationToken = default)
    { Reads++; return Task.FromResult<IInferenceResourceReadHandle>(new Handle(this, reference)); }
    public Task<IInferenceResourceWriteHandle> OpenWriteAsync(InferenceResourceCreate resource, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    public Task<InferenceResourceSelection> GetSelectionAsync(CancellationToken cancellationToken = default) { Selections++; throw new InvalidOperationException("Named audio does not read selection."); }
    public Task<InferenceResourceSelection> SelectAsync(IReadOnlyList<InferenceResourceReference> references, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    public Task<InferenceResourceSelection> ReplaceSelectionAsync(IReadOnlyList<InferenceResourceReference> references, long expectedRevision, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    public void Dispose() => BorrowedDisposals++;
    private sealed class Handle(XaiSttResourceFixture owner, InferenceResourceReference reference) : IInferenceResourceReadHandle
    {
        private readonly ContentStream _content = new(owner);
        public InferenceResourceDescriptor Resource { get { owner.OnHandleResource?.Invoke(); return new(reference, owner.SessionId, null, owner.ContentType, owner.HandleBytes); } }
        public Stream Content { get { owner.ContentGets++; return _content; } }
        public ValueTask DisposeAsync()
        { owner.HandleDisposals++; if (owner.CleanupFailure is { } failure) return ValueTask.FromException(failure); _content.Dispose(); return ValueTask.CompletedTask; }
    }
    private sealed class ContentStream(XaiSttResourceFixture owner) : MemoryStream(new byte[] { 1, 2, 3 }, writable: false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => owner.ReadFailure is { } failure ? ValueTask.FromException<int>(failure) : base.ReadAsync(buffer, cancellationToken);
        protected override void Dispose(bool disposing) { if (disposing) owner.ContentDisposals++; base.Dispose(disposing); }
    }
}
