using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Darkspyre.Inference.Common.Resources;

namespace Darkspyre.Inference.Connect.TTS.Tests;

public sealed class PocketResourceLifetimeTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    internal static byte[] Audio => Convert.FromHexString("524946462600000057415645666D74201000000001000100803E0000007D00000200100064617461020000000000");
    private static TtsConnection Connection => new() { Endpoint = new("https://example.invalid/") };
    private static HttpResponseMessage Wave() => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Audio) };

    [Fact]
    public async Task ActualWavCommitsAndSelectsBeforeReceiptAndSurvivesClientRetirement()
    {
        var access = new VoiceResourceFixture();
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Wave())));
        ITtsClient client = new PocketTtsHttpProvider(http).Bind(Connection, resources: access);
        TtsResult result = await client.SpeakAsync(new() { Text = "actual" });
        Assert.Equal(Audio, result.Audio.ToArray()); Assert.NotNull(result.Resource);
        Assert.Null(result.Resource.FileName); Assert.Equal("audio/wav", result.Resource.ContentType);
        Assert.Equal(Audio, access.Committed); Assert.True(access.Selected);
        Assert.Equal(1, access.CommitCalls); Assert.Equal(1, access.HandleDisposals);
        await client.DisposeAsync(); await client.DisposeAsync();
        Assert.Equal(0, access.BorrowedDisposals); Assert.Equal(Audio, access.Committed);
        Assert.Throws<ObjectDisposedException>(() => client.Descriptor);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.SpeakAsync(new() { Text = "late" }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetirementDrainsIgnoringHttpAndCachesCallbackFailure(bool callbackFails)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackFailure = new InvalidOperationException("controlled callback");
        CancellationTokenRegistration registration = default;
        using var http = new HttpClient(new Handler(async (_, token) =>
        {
            if (callbackFails) registration = token.Register(() => throw callbackFailure);
            entered.TrySetResult(); await release.Task; return Wave();
        }));
        ITtsClient client = new PocketTtsHttpProvider(http).Bind(Connection);
        Task<TtsResult>? pending = null; Task? retirement = null;
        try
        {
            pending = client.SpeakAsync(new() { Text = "held" });
            await entered.Task.WaitAsync(Bound);
            retirement = client.DisposeAsync().AsTask();
            Assert.Same(retirement, client.DisposeAsync().AsTask()); Assert.False(retirement.IsCompleted);
            release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            if (callbackFails)
            {
                Exception error = (await Record.ExceptionAsync(() => retirement))!;
                Assert.NotNull(error); Assert.Contains(callbackFailure, ((AggregateException)error).Flatten().InnerExceptions);
                Assert.Same(error, await Record.ExceptionAsync(() => client.DisposeAsync().AsTask()));
            }
            else await retirement.WaitAsync(Bound);
        }
        finally
        {
            retirement ??= client.DisposeAsync().AsTask(); release.TrySetResult();
            try { if (pending is not null) await Observe(pending); }
            finally { try { await Observe(retirement); } finally { registration.Dispose(); } }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnedHandleCleanupRetainsActualEffectsAndCachedOriginalFailure(bool commitFails)
    {
        var original = new InvalidOperationException("actual commit failure");
        var cleanup = new InvalidOperationException("actual release failure");
        var access = new VoiceResourceFixture { CommitFailure = commitFails ? original : null, CleanupFailure = cleanup };
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Wave())));
        ITtsClient client = new PocketTtsHttpProvider(http).Bind(Connection, resources: access);
        Exception error = (await Record.ExceptionAsync(() => client.SpeakAsync(new() { Text = "actual" })))!;
        InferenceResourceOperationException refused;
        if (commitFails)
        {
            var aggregate = Assert.IsType<AggregateException>(error);
            Assert.Same(original, Assert.IsType<InferenceResourceOperationException>(aggregate.InnerExceptions[0]).InnerException);
            refused = Assert.IsType<InferenceResourceOperationException>(aggregate.InnerExceptions[1]);
            Assert.Equal(InferenceResourceCommitOutcome.Unknown, refused.Failure.CommitOutcome);
        }
        else
        {
            refused = Assert.IsType<InferenceResourceOperationException>(error);
            Assert.Equal(InferenceResourceCommitOutcome.Committed, refused.Failure.CommitOutcome);
            Assert.Equal(InferenceResourceCommitOutcome.Committed, refused.Failure.SelectionOutcome);
            Assert.Equal(1, refused.Failure.SelectionVersion!.Revision);
        }
        Assert.Equal(InferenceResourceOperationPhase.Cleanup, refused.Failure.Phase);
        Assert.Same(cleanup, refused.InnerException); Assert.Equal(Audio.Length, refused.Failure.BytesTransferred);
        Assert.Same(refused, await Record.ExceptionAsync(() => client.DisposeAsync().AsTask()));
        Assert.Same(refused, await Record.ExceptionAsync(() => client.DisposeAsync().AsTask()));
        Assert.Equal(1, access.HandleDisposals); Assert.Equal(0, access.BorrowedDisposals);
        Assert.True(access.LastWriter!.CanWrite, "Refused handle cleanup does not trigger dependent stream disposal.");
    }

    [Fact]
    public void RetainedDefaultClientDropsSeparatelyCollectibleBorrowedResourceGraph()
    {
        (ITtsClient client, WeakReference[] roots) = ExerciseForeign();
        for (int index = 0; roots.Any(root => root.IsAlive) && index < 30; index++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); }
        Assert.All(roots, root => Assert.False(root.IsAlive)); GC.KeepAlive(client);
    }

    [Fact]
    public async Task RetirementAfterCommitPreservesActualDurableEffectsWithoutReceiptPublication()
    {
        var access = new VoiceResourceFixture();
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Wave())));
        ITtsClient client = new PocketTtsHttpProvider(http).Bind(Connection, resources: access);
        Task? retirement = null;
        access.AfterCommit = () => retirement = client.DisposeAsync().AsTask();
        var failure = await Assert.ThrowsAsync<InferenceResourceOperationCanceledException>(() => client.SpeakAsync(new() { Text = "actual" }));
        Assert.Equal(InferenceResourceCommitOutcome.Committed, failure.Failure.CommitOutcome);
        Assert.Equal(InferenceResourceCommitOutcome.Committed, failure.Failure.SelectionOutcome);
        Assert.Equal(1, failure.Failure.SelectionVersion!.Revision);
        Assert.Equal(Audio, access.Committed); Assert.Equal(1, access.HandleDisposals);
        await retirement!.WaitAsync(Bound); await client.DisposeAsync();
    }

    [Fact]
    public async Task IgnoringResourceWriteDrainsBeforeRetirementAndNeverCommitsLateBytes()
    {
        var access = new VoiceResourceFixture { HoldWrite = true };
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Wave())));
        ITtsClient client = new PocketTtsHttpProvider(http).Bind(Connection, resources: access);
        Task<TtsResult>? pending = null; Task? retirement = null;
        try
        {
            pending = client.SpeakAsync(new() { Text = "held" }); await access.WriteEntered.Task.WaitAsync(Bound);
            retirement = client.DisposeAsync().AsTask(); Assert.False(retirement.IsCompleted);
            access.WriteRelease.TrySetResult();
            var failure = await Assert.ThrowsAsync<InferenceResourceOperationCanceledException>(() => pending);
            Assert.Equal(InferenceResourceOperationPhase.Write, failure.Failure.Phase);
            Assert.Equal(Audio.Length, failure.Failure.BytesTransferred);
            Assert.Equal(InferenceResourceCommitOutcome.NotAttempted, failure.Failure.CommitOutcome);
            await retirement.WaitAsync(Bound); Assert.Equal(0, access.CommitCalls); Assert.Equal(1, access.HandleDisposals);
        }
        finally
        {
            retirement ??= client.DisposeAsync().AsTask(); access.WriteRelease.TrySetResult();
            try { if (pending is not null) await Observe(pending); } finally { await Observe(retirement); }
        }
    }

    [Fact]
    public void FailedConcreteHandleIsRootedOnlyByActualRetainedClientFailure()
    {
        WeakReference[] roots = ExerciseFailedOwner();
        for (int index = 0; roots.Any(root => root.IsAlive) && index < 30; index++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); }
        Assert.All(roots, root => Assert.False(root.IsAlive));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] ExerciseFailedOwner()
    {
        var generation = new FixtureGeneration();
        Assembly assembly = generation.LoadFromAssemblyPath(typeof(VoiceResourceFixture).Assembly.Location);
        Type type = assembly.GetType(typeof(VoiceResourceFixture).FullName!, true)!;
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(assembly));
        Assert.Equal(typeof(VoiceResourceFixture).Module.ModuleVersionId, type.Module.ModuleVersionId);
        var access = (IInferenceResourceAccess)Activator.CreateInstance(type)!;
        type.GetProperty(nameof(VoiceResourceFixture.CleanupFailure))!.SetValue(access, new InvalidOperationException("refused handle"));
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Wave())));
        ITtsClient client = new PocketTtsHttpProvider(http).Bind(Connection, resources: access);
        Exception? failure = Record.Exception(() => client.SpeakAsync(new() { Text = "actual" }).GetAwaiter().GetResult());
        Assert.NotNull(failure); Assert.Same(failure, Record.Exception(client.Dispose));
        object handle = type.GetProperty(nameof(VoiceResourceFixture.LastHandle))!.GetValue(access)!;
        object writer = type.GetProperty(nameof(VoiceResourceFixture.LastWriter))!.GetValue(access)!;
        Assert.Same(assembly, handle.GetType().Assembly); Assert.Same(assembly, writer.GetType().Assembly);
        WeakReference[] roots = [new(generation), new(assembly), new(type), new(access), new(handle), new(writer)];
        access = null!;
        generation.Unload();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.All(roots, root => Assert.True(root.IsAlive));
        GC.KeepAlive(failure); GC.KeepAlive(client); return roots;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (ITtsClient, WeakReference[]) ExerciseForeign()
    {
        var generation = new FixtureGeneration();
        Assembly assembly = generation.LoadFromAssemblyPath(typeof(VoiceResourceFixture).Assembly.Location);
        Type type = assembly.GetType(typeof(VoiceResourceFixture).FullName!, true)!;
        Assert.NotSame(type.Assembly, typeof(VoiceResourceFixture).Assembly);
        Assert.Equal(typeof(VoiceResourceFixture).Module.ModuleVersionId, type.Module.ModuleVersionId);
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(assembly));
        var calls = new List<(string, Guid)>();
        var access = (IInferenceResourceAccess)Activator.CreateInstance(type, new Action<string, Guid>((method, mvid) => calls.Add((method, mvid))))!;
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Wave())));
        ITtsClient client = new PocketTtsHttpProvider(http).Bind(Connection, resources: access);
        TtsResult result = client.SpeakAsync(new() { Text = "actual" }).GetAwaiter().GetResult();
        Assert.NotNull(result.Resource);
        client.Dispose();
        Assert.Contains(calls, call => call.Item1 == "OpenWriteAsync" && call.Item2 == assembly.ManifestModule.ModuleVersionId);
        Assert.Contains(calls, call => call.Item1 == "CommitAsync");
        Assert.DoesNotContain(calls, call => call.Item1 == "Dispose");
        MethodInfo method = type.GetMethod(nameof(VoiceResourceFixture.OpenWriteAsync))!;
        WeakReference[] roots = [new(generation), new(assembly), new(type), new(access), new(method)];
        generation.Unload(); return (client, roots);
    }

    private sealed class FixtureGeneration() : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name) => Default.Assemblies.FirstOrDefault(assembly => AssemblyName.ReferenceMatchesDefinition(name, assembly.GetName()));
    }
    private static async Task Observe(Task task) { try { await task.WaitAsync(Bound); } catch (Exception error) when (error is not TimeoutException) { } }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
}

public sealed class VoiceResourceFixture : IInferenceResourceAccess, IDisposable
{
    private readonly Action<string, Guid>? _executed;
    public VoiceResourceFixture() { }
    public VoiceResourceFixture(Action<string, Guid> executed) => _executed = executed;
    private void Record(string name) => _executed?.Invoke(name, GetType().Module.ModuleVersionId);
    public string WorkspaceId { get { Record(nameof(WorkspaceId)); return "workspace"; } }
    public string SessionId { get { Record(nameof(SessionId)); return "session"; } }
    public byte[]? Committed { get; private set; }
    public bool Selected { get; private set; }
    public int CommitCalls { get; private set; }
    public int HandleDisposals { get; private set; }
    public int BorrowedDisposals { get; private set; }
    public MemoryStream? LastWriter { get; private set; }
    public object? LastHandle { get; private set; }
    public Action? AfterCommit { get; set; }
    public bool HoldWrite { get; init; }
    public TaskCompletionSource WriteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource WriteRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Exception? CommitFailure { get; init; }
    public Exception? CleanupFailure { get; init; }
    public Task<InferenceResourceSelection> GetSelectionAsync(CancellationToken cancellationToken = default)
    { Record(nameof(GetSelectionAsync)); return Task.FromResult(new InferenceResourceSelection(WorkspaceId, SessionId, Selected ? 1 : 0, Selected ? [new(WorkspaceId, "audio")] : [])); }
    public Task<IInferenceResourceWriteHandle> OpenWriteAsync(InferenceResourceCreate resource, CancellationToken cancellationToken = default)
    { Record(nameof(OpenWriteAsync)); LastWriter = new StagingStream(this); var writer = new Writer(this, resource); LastHandle = writer; return Task.FromResult<IInferenceResourceWriteHandle>(writer); }
    public Task<InferenceResourceDescriptor?> GetAsync(InferenceResourceReference reference, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IInferenceResourceReadHandle> OpenReadAsync(InferenceResourceReference reference, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<InferenceResourceSelection> SelectAsync(IReadOnlyList<InferenceResourceReference> references, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Commit must select atomically.");
    public Task<InferenceResourceSelection> ReplaceSelectionAsync(IReadOnlyList<InferenceResourceReference> references, long expectedRevision, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public void Dispose() { BorrowedDisposals++; Record(nameof(Dispose)); }
    private sealed class StagingStream(VoiceResourceFixture owner) : MemoryStream
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            owner.WriteEntered.TrySetResult(); if (owner.HoldWrite) await owner.WriteRelease.Task;
            await base.WriteAsync(buffer, CancellationToken.None);
        }
    }
    private sealed class Writer(VoiceResourceFixture owner, InferenceResourceCreate create) : IInferenceResourceWriteHandle
    {
        public InferenceResourceReference Reference { get; } = new(owner.WorkspaceId, "audio");
        public Stream Content => owner.LastWriter!;
        public async Task<InferenceResourceCommitResult> CommitAsync(long bytesWritten, InferenceResourceCommitOptions? options = null, CancellationToken cancellationToken = default)
        {
            owner.Record(nameof(CommitAsync)); owner.CommitCalls++;
            if (owner.CommitFailure is not null) throw owner.CommitFailure;
            if (options?.SelectForSession != true || bytesWritten != owner.LastWriter!.Length) throw new InvalidOperationException();
            owner.Committed = owner.LastWriter!.ToArray(); owner.Selected = true;
            var receipt = new InferenceResourceCommitResult(new(Reference, create.SessionId, create.FileName, create.ContentType, bytesWritten), await owner.GetSelectionAsync(cancellationToken));
            owner.AfterCommit?.Invoke(); return receipt;
        }
        public ValueTask DisposeAsync()
        {
            owner.HandleDisposals++;
            if (owner.CleanupFailure is not null) return ValueTask.FromException(owner.CleanupFailure);
            owner.LastWriter!.Dispose(); return ValueTask.CompletedTask;
        }
    }
}
