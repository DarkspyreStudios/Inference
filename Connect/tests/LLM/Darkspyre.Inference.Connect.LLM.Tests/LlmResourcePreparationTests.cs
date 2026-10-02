using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class LlmResourcePreparationTests
{
    [Fact]
    public async Task RetainedPreparationDropsBorrowedAuthorityAndPreservesFailedHandleWithoutMetadataRetry()
    {
        var store = new ControlledSessionResources();
        InferenceResourceReference reference = await store.RegisterAsync("file.txt", "text/plain", "abc");
        bool retiring = false;
        var access = new ObservedAccess(store) { OnHandleMetadata = () => { if (retiring) throw new InvalidOperationException("Unexpected cleanup metadata callback."); } };
        using var handler = new LlmResourceMaterializationTests.ResourceDestination();
        using var http = new HttpClient(handler);
        await using var files = new XaiFileClient(http);
        LlmChatRequest original = LlmResourceMaterializationTests.Request(access);
        LlmResourcePreparation retained = await LlmResourcePreparation.PrepareAsync(
            LlmResourceMaterializationTests.Connection(), original, files, static () => { }, CancellationToken.None);
        var cleanup = new InvalidOperationException("controlled handle cleanup refusal");
        store.ReadCleanupFailure = cleanup;
        retiring = true;
        var failure = await Assert.ThrowsAsync<InferenceResourceOperationException>(() => retained.DisposeAsync().AsTask());
        Assert.Equal(InferenceResourceOperationPhase.Cleanup, failure.Failure.Phase);
        Assert.Equal(reference, failure.Failure.Reference);
        Assert.Same(cleanup, failure.InnerException);
        Assert.Same(failure, await Assert.ThrowsAsync<InferenceResourceOperationException>(() => retained.DisposeAsync().AsTask()));
        Assert.Null(retained.Request.Resources);
        Assert.Same(access, original.Resources);
        Assert.Equal(1, store.ReadCleanupAttempts);
        Assert.Equal(0, store.DisposedReads);
        Assert.Equal(0, store.DisposeCalls);
        GC.KeepAlive(retained);
    }

    [Fact]
    public async Task HandleMetadataCallbackCannotCrossRetirementIntoContentGetter()
    {
        var store = new ControlledSessionResources();
        await store.RegisterAsync("file.txt", "text/plain", "abc");
        bool stopped = false;
        var access = new ObservedAccess(store) { OnHandleMetadata = () => stopped = true };
        using var handler = new LlmResourceMaterializationTests.ResourceDestination();
        using var http = new HttpClient(handler);
        await using var files = new XaiFileClient(http);
        await Assert.ThrowsAsync<InferenceResourceOperationCanceledException>(() => LlmResourcePreparation.PrepareAsync(
            LlmResourceMaterializationTests.Connection(), LlmResourceMaterializationTests.Request(access), files,
            () => { if (stopped) throw new OperationCanceledException(); }, CancellationToken.None));
        Assert.Equal(0, access.ContentReads);
        Assert.Equal(1, store.DisposedReads);
        Assert.Empty(handler.Uploads);
    }

    [Theory]
    [InlineData("bytes")]
    [InlineData("name")]
    [InlineData("type")]
    [InlineData("ownership")]
    public async Task ContradictoryImmutableHandleMetadataRefusesBeforeUpload(string fact)
    {
        var store = new ControlledSessionResources();
        await store.RegisterAsync("file.txt", "text/plain", "abc");
        var access = new ObservedAccess(store)
        {
            Lookup = descriptor => fact == "bytes" ? descriptor with { Bytes = null } : descriptor,
            HandleMetadata = descriptor => fact switch
            {
                "bytes" => descriptor with { Bytes = 5 },
                "name" => descriptor with { FileName = "other.txt" },
                "type" => descriptor with { ContentType = "application/json" },
                _ => descriptor with { SessionId = null }
            }
        };
        using var handler = new LlmResourceMaterializationTests.ResourceDestination();
        using var http = new HttpClient(handler);
        await using var files = new XaiFileClient(http);
        await Assert.ThrowsAsync<InferenceResourceOperationException>(() => LlmResourcePreparation.PrepareAsync(
            LlmResourceMaterializationTests.Connection(), LlmResourceMaterializationTests.Request(access), files,
            static () => { }, CancellationToken.None));
        Assert.Null(handler.Prompt);
        if (fact != "bytes") Assert.Empty(handler.Uploads);
        Assert.Equal(1, store.DisposedReads);
    }

    [Fact]
    public async Task SelectionCannotRebindAdmittedAuthorityToAnotherLogicalSession()
    {
        var store = new ControlledSessionResources();
        await store.RegisterAsync("file.txt", "text/plain", "abc");
        var access = new ObservedAccess(store);
        access.AfterSelection = () => access.Session = "replacement-session";
        access.Lookup = descriptor => descriptor with { SessionId = access.Session };
        access.HandleMetadata = descriptor => descriptor with { SessionId = access.Session };
        using var handler = new LlmResourceMaterializationTests.ResourceDestination();
        using var http = new HttpClient(handler);
        await using var files = new XaiFileClient(http);
        await Assert.ThrowsAsync<InferenceResourceOperationException>(() => LlmResourcePreparation.PrepareAsync(
            LlmResourceMaterializationTests.Connection(), LlmResourceMaterializationTests.Request(access), files,
            static () => { }, CancellationToken.None));
        Assert.Empty(handler.Uploads);
        Assert.Equal(0, store.OpenedReads);
    }

    private sealed class ObservedAccess(ControlledSessionResources store) : IInferenceResourceAccess
    {
        internal string Session { get; set; } = store.SessionId;
        internal Action? AfterSelection { get; set; }
        internal Action? OnHandleMetadata { get; init; }
        internal Func<InferenceResourceDescriptor, InferenceResourceDescriptor>? Lookup { get; set; }
        internal Func<InferenceResourceDescriptor, InferenceResourceDescriptor>? HandleMetadata { get; set; }
        internal int ContentReads { get; private set; }
        public string WorkspaceId => store.WorkspaceId;
        public string SessionId => Session;
        public async Task<InferenceResourceDescriptor?> GetAsync(InferenceResourceReference reference, CancellationToken cancellationToken = default)
        {
            InferenceResourceDescriptor? descriptor = await store.GetAsync(reference, cancellationToken);
            return descriptor is null ? null : Lookup?.Invoke(descriptor) ?? descriptor;
        }
        public async Task<IInferenceResourceReadHandle> OpenReadAsync(InferenceResourceReference reference, CancellationToken cancellationToken = default) =>
            new ObservedHandle(await store.OpenReadAsync(reference, cancellationToken), this);
        public Task<IInferenceResourceWriteHandle> OpenWriteAsync(InferenceResourceCreate resource, CancellationToken cancellationToken = default) =>
            store.OpenWriteAsync(resource, cancellationToken);
        public async Task<InferenceResourceSelection> GetSelectionAsync(CancellationToken cancellationToken = default)
        {
            InferenceResourceSelection selection = await store.GetSelectionAsync(cancellationToken);
            AfterSelection?.Invoke();
            return selection;
        }
        public Task<InferenceResourceSelection> SelectAsync(IReadOnlyList<InferenceResourceReference> references, CancellationToken cancellationToken = default) =>
            store.SelectAsync(references, cancellationToken);
        public Task<InferenceResourceSelection> ReplaceSelectionAsync(IReadOnlyList<InferenceResourceReference> references, long expectedRevision, CancellationToken cancellationToken = default) =>
            store.ReplaceSelectionAsync(references, expectedRevision, cancellationToken);
        private sealed class ObservedHandle(IInferenceResourceReadHandle handle, ObservedAccess owner) : IInferenceResourceReadHandle
        {
            public InferenceResourceDescriptor Resource
            {
                get
                {
                    owner.OnHandleMetadata?.Invoke();
                    InferenceResourceDescriptor descriptor = handle.Resource;
                    return owner.HandleMetadata?.Invoke(descriptor) ?? descriptor;
                }
            }
            public Stream Content { get { owner.ContentReads++; return handle.Content; } }
            public ValueTask DisposeAsync() => handle.DisposeAsync();
        }
    }
}
