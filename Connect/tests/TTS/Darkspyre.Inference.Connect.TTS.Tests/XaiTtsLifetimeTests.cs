using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.TTS.Tests;

public sealed class XaiTtsLifetimeTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task RetirementDrainsIgnoringResponseAndNeverPublishesLateAudio()
    {
        using var access = new LlmConnectionAccess(new XaiTtsTests.Source(), []);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new XaiTtsTests.Handler(async _ => { entered.TrySetResult(); await release.Task; return Audio(); }));
        ITtsClient client = XaiTtsTests.Provider(http).Bind(XaiTtsTests.Connection, new(new("selected"), access));
        Task<TtsResult> request = client.SpeakAsync(XaiTtsTests.Request); Task? retirement = null;
        try
        {
            await entered.Task.WaitAsync(Bound); retirement = client.DisposeAsync().AsTask();
            Assert.False(retirement.IsCompleted); Assert.Same(retirement, client.DisposeAsync().AsTask());
            release.TrySetResult(); Assert.IsAssignableFrom<OperationCanceledException>(await Record.ExceptionAsync(() => request));
            await retirement.WaitAsync(Bound); await Assert.ThrowsAsync<ObjectDisposedException>(() => client.SpeakAsync(XaiTtsTests.Request));
        }
        finally
        {
            retirement ??= client.DisposeAsync().AsTask(); release.TrySetResult();
            try { await Observe(request); } finally { await Observe(retirement); }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CatalogDrainsUnderBothBorrowedFacadeAndClientFences(bool retireFacade)
    {
        var source = new XaiTtsTests.Source(); await using var access = new LlmConnectionAccess(source, []);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new XaiTtsTests.Handler(async _ => { entered.TrySetResult(); await release.Task; return XaiTtsTests.Json("{\"voices\":[]}"); }));
        var facade = XaiTtsTests.Facade(source, access, http);
        ITtsClient client = XaiTtsTests.Provider(http).Bind(XaiTtsTests.Connection, new(new("selected"), access, facade));
        Task<IReadOnlyList<TtsVoice>> request = client.ListVoicesAsync(); Task? retirement = null;
        try
        {
            await entered.Task.WaitAsync(Bound);
            retirement = retireFacade ? facade.DisposeAsync().AsTask() : client.DisposeAsync().AsTask();
            Assert.False(retirement.IsCompleted); release.TrySetResult();
            Assert.IsAssignableFrom<OperationCanceledException>(await Record.ExceptionAsync(() => request));
            await retirement.WaitAsync(Bound); Assert.Equal(1, source.Calls);
        }
        finally
        {
            retirement ??= retireFacade ? facade.DisposeAsync().AsTask() : client.DisposeAsync().AsTask();
            release.TrySetResult();
            try { await Observe(request); } finally
            {
                try { await Observe(retirement); } finally
                {
                    try { await Observe(client.DisposeAsync().AsTask()); } finally { await Observe(facade.DisposeAsync().AsTask()); }
                }
            }
        }
    }

    [Fact]
    public async Task ReadAndOwnedResponseCleanupFailuresRemainIndependentAndCached()
    {
        using var access = new LlmConnectionAccess(new XaiTtsTests.Source(), []);
        var cleanup = new InvalidOperationException("actual response cleanup"); var content = new RefusedContent(cleanup);
        using var http = new HttpClient(new XaiTtsTests.Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content })));
        ITtsClient client = XaiTtsTests.Provider(http).Bind(XaiTtsTests.Connection, new(new("selected"), access));
        var error = Assert.IsType<AggregateException>(await Record.ExceptionAsync(() => client.SpeakAsync(XaiTtsTests.Request with { IncludeTimestamps = true })));
        Assert.IsType<Darkspyre.Inference.Common.InferenceException>(error.InnerExceptions[0]);
        var refused = Assert.IsType<InferenceResourceOperationException>(error.InnerExceptions[1]);
        Assert.Equal(InferenceResourceOperationPhase.Cleanup, refused.Failure.Phase); Assert.Same(cleanup, refused.InnerException);
        Assert.Same(refused, await Record.ExceptionAsync(() => client.DisposeAsync().AsTask()));
        Assert.Same(refused, await Record.ExceptionAsync(() => client.DisposeAsync().AsTask())); Assert.Equal(1, content.Disposals);
    }

    [Fact]
    public void DisposedDefaultClientReleasesExecutedCollectibleBorrowedResourceGraph()
    {
        (ITtsClient retained, WeakReference[] roots) = ExerciseForeign();
        for (int i = 0; roots.Any(root => root.IsAlive) && i < 30; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); }
        Assert.All(roots, root => Assert.False(root.IsAlive)); GC.KeepAlive(retained);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (ITtsClient, WeakReference[]) ExerciseForeign()
    {
        var generation = new FixtureGeneration(); Assembly assembly = generation.LoadFromAssemblyPath(typeof(VoiceResourceFixture).Assembly.Location);
        Type type = assembly.GetType(typeof(VoiceResourceFixture).FullName!, true)!;
        Assert.NotSame(type, typeof(VoiceResourceFixture)); Assert.Equal(typeof(VoiceResourceFixture).Module.ModuleVersionId, type.Module.ModuleVersionId);
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(assembly));
        var executed = new List<(string Method, Guid Mvid)>();
        var resource = (IInferenceResourceAccess)Activator.CreateInstance(type, new Action<string, Guid>((name, mvid) => executed.Add((name, mvid))))!;
        using var access = new LlmConnectionAccess(new XaiTtsTests.Source(), []);
        using var http = new HttpClient(new XaiTtsTests.Handler(_ => Task.FromResult(Audio())));
        ITtsClient client = XaiTtsTests.Provider(http).Bind(XaiTtsTests.Connection, new(new("selected"), access), resource);
        TtsResult result = client.SpeakAsync(XaiTtsTests.Request).GetAwaiter().GetResult(); Assert.NotNull(result.Resource);
        client.Dispose();
        Assert.Contains(executed, call => call.Method == "OpenWriteAsync" && call.Mvid == assembly.ManifestModule.ModuleVersionId);
        Assert.Contains(executed, call => call.Method == "CommitAsync"); Assert.DoesNotContain(executed, call => call.Method == "Dispose");
        MethodInfo method = type.GetMethod(nameof(VoiceResourceFixture.OpenWriteAsync))!;
        WeakReference[] roots = [new(generation), new(assembly), new(type), new(resource), new(method)];
        generation.Unload(); return (client, roots);
    }

    private sealed class FixtureGeneration() : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name) => Default.Assemblies.FirstOrDefault(assembly => AssemblyName.ReferenceMatchesDefinition(name, assembly.GetName()));
    }
    private static async Task Observe(Task task) { try { await task.WaitAsync(Bound); } catch (Exception error) when (error is not TimeoutException) { } }
    private static HttpResponseMessage Audio()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) };
        response.Content.Headers.ContentType = new("audio/mpeg"); return response;
    }
    private sealed class RefusedContent(Exception cleanup) : HttpContent
    {
        internal int Disposals;
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync("malformed-json"u8.ToArray()).AsTask();
        protected override bool TryComputeLength(out long length) { length = 14; return true; }
        protected override void Dispose(bool disposing) { if (disposing) { Disposals++; throw cleanup; } base.Dispose(disposing); }
    }
}
