using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class XaiFileFacadeGenerationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainedDefaultFacadeReleasesSeparatelyForeignBorrowedGraphAfterDrain(bool held)
    {
        (WeakReference[] roots, object retained) = await ExerciseBorrowed(held);
        await Collect(roots);
        GC.KeepAlive(retained);
    }

    [Fact]
    public async Task ActualForeignFacadeAndOwnedMetadataExecuteAndCollect()
    {
        (WeakReference[] roots, string json) = await ExerciseWhole();
        await Collect(roots);
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal("file-a", document.RootElement.GetProperty("Id").GetString());
    }

    public static object[] CreateBorrowed(TaskCompletionSource? entered, Task? release)
    {
        var source = new ForeignSource(entered, release);
        var auth = new ForeignAuth();
        var handler = new ForeignHandler();
        return [source, auth, new HttpClient(handler), handler];
    }

    public static int[] Counters(object[] values) =>
        [((ForeignSource)values[0]).Calls, ((ForeignAuth)values[1]).Calls,
         ((ForeignSource)values[0]).Disposals, ((ForeignAuth)values[1]).Disposals, ((ForeignHandler)values[3]).Disposals];

    public static async Task<object[]> ExecuteWhole()
    {
        var generation = AssemblyLoadContext.GetLoadContext(typeof(XaiFileFacadeGenerationTests).Assembly)!;
        Assert.True(generation.IsCollectible);
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(typeof(LlmProviderService).Assembly));
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(typeof(InferenceJson).Assembly));
        using var http = new HttpClient(new ForeignHandler());
        var service = new LlmProviderService(new ForeignSource(null, null), [new XaiResponsesLlmClient(http)], [new ForeignAuth()]);
        LlmProviderFile result;
        try { result = await service.GetAsync("selected", "file-a"); }
        finally { await service.DisposeAsync(); }
        var metadata = ConnectLlmJson.LlmProviderFile;
        Assert.Same(typeof(LlmProviderService).Assembly, metadata.Type.Assembly);
        string wire = JsonSerializer.Serialize(result, metadata);
        result = JsonSerializer.Deserialize(wire, metadata)!;
        return [JsonSerializer.Serialize(result, metadata), new WeakReference(service), new WeakReference(metadata), new WeakReference(metadata.Options.TypeInfoResolver!)];
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference[], object)> ExerciseBorrowed(bool held)
    {
        var generation = new Generation(true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        LlmProviderService? service = null;
        HttpClient? http = null;
        Task<LlmProviderFile>? request = null;
        Task? retirement = null;
        try
        {
            Assembly fixture = generation.LoadFromAssemblyPath(typeof(XaiFileFacadeGenerationTests).Assembly.Location);
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(fixture));
            Assert.Equal(typeof(XaiFileFacadeGenerationTests).Module.ModuleVersionId, fixture.ManifestModule.ModuleVersionId);
            Type caller = fixture.GetType(typeof(XaiFileFacadeGenerationTests).FullName!, true)!;
            MethodInfo create = caller.GetMethod(nameof(CreateBorrowed))!;
            object[] values = (object[])create.Invoke(null, [held ? entered : null, held ? release.Task : null])!;
            http = (HttpClient)values[2];
            service = new LlmProviderService((ILlmConnectionSource)values[0], [new XaiResponsesLlmClient(http)], [(ILlmAuthenticator)values[1]]);
            request = service.GetAsync("selected", "file-a");
            if (held)
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                retirement = service.DisposeAsync().AsTask();
                Assert.False(retirement.IsCompleted);
                release.SetResult();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            }
            else Assert.Equal("file-a", (await request).Id);
            retirement ??= service.DisposeAsync().AsTask();
            await retirement.WaitAsync(TimeSpan.FromSeconds(5));
            MethodInfo counters = caller.GetMethod(nameof(Counters))!;
            Assert.Equal(new[] { 1, held ? 0 : 1, 0, 0, 0 }, (int[])counters.Invoke(null, [values])!);
            using HttpResponseMessage stillBorrowed = await http.GetAsync("https://api.x.ai/v1/files/file-a");
            Assert.Equal(HttpStatusCode.OK, stillBorrowed.StatusCode);
            Assert.Null(typeof(LlmProviderService).GetField("_xaiFiles", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service));
            var roots = new List<WeakReference> { new(generation), new(fixture), new(caller), new(create), new(counters) };
            foreach (object value in values)
            {
                roots.Add(new(value));
                if (AssemblyLoadContext.GetLoadContext(value.GetType().Assembly) == generation)
                    roots.Add(new(value.GetType()));
            }
            return (roots.ToArray(), service);
        }
        finally
        {
            if (service is not null) retirement ??= service.DisposeAsync().AsTask();
            release.TrySetResult();
            if (request is not null) { try { await request.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { } }
            if (retirement is not null) await retirement.WaitAsync(TimeSpan.FromSeconds(5));
            http?.Dispose();
            generation.Unload();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference[], string)> ExerciseWhole()
    {
        var generation = new Generation(false);
        try
        {
            Assembly fixture = generation.LoadFromAssemblyPath(typeof(XaiFileFacadeGenerationTests).Assembly.Location);
            Assembly connect = generation.LoadFromAssemblyPath(typeof(LlmProviderService).Assembly.Location);
            Assembly common = generation.LoadFromAssemblyPath(typeof(InferenceJson).Assembly.Location);
            Assert.Equal(typeof(LlmProviderService).Module.ModuleVersionId, connect.ManifestModule.ModuleVersionId);
            Assert.Equal(typeof(InferenceJson).Module.ModuleVersionId, common.ManifestModule.ModuleVersionId);
            Type caller = fixture.GetType(typeof(XaiFileFacadeGenerationTests).FullName!, true)!;
            MethodInfo method = caller.GetMethod(nameof(ExecuteWhole))!;
            object[] result = await (Task<object[]>)method.Invoke(null, null)!;
            var roots = new List<WeakReference> { new(generation), new(fixture), new(connect), new(common), new(caller), new(method) };
            roots.AddRange(result.Skip(1).Cast<WeakReference>());
            return (roots.ToArray(), (string)result[0]);
        }
        finally { generation.Unload(); }
    }

    private static async Task Collect(WeakReference[] roots)
    {
        for (int attempt = 0; attempt < 30 && roots.Any(root => root.IsAlive); attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); await Task.Delay(10); }
        Assert.All(roots, root => Assert.False(root.IsAlive));
    }

    private sealed class ForeignSource(TaskCompletionSource? entered, Task? release) : ILlmConnectionSource, IDisposable
    {
        public int Calls;
        public int Disposals;
        public async Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
        {
            Calls++;
            entered?.SetResult();
            if (release is not null) await release;
            return new()
            {
                Transport = LlmTransport.XaiResponses,
                Endpoint = "https://api.x.ai/v1",
                Version = "v1",
                Credential = new LlmCredential { OAuth = new() { AccessToken = "oauth-token" } }.Serialize()
            };
        }
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No save.");
        public void Dispose() => Disposals++;
    }

    private sealed class ForeignAuth : ILlmAuthenticator, IDisposable
    {
        public int Calls;
        public int Disposals;
        public LlmTransport Transport => LlmTransport.XaiResponses;
        public Task<LlmCredential> EnsureFreshAsync(LlmCredential credential, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(credential); }
        public void Dispose() => Disposals++;
    }

    private sealed class ForeignHandler : HttpMessageHandler
    {
        public int Disposals;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"file-a\",\"filename\":\"a\",\"bytes\":3,\"created_at\":0,\"object\":\"file\"}") });
        protected override void Dispose(bool disposing) { if (disposing) Disposals++; base.Dispose(disposing); }
    }

    private sealed class Generation(bool shareContracts) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (shareContracts && name.Name is "Darkspyre.Inference.Connect.LLM" or "Darkspyre.Inference.Common")
                return AssemblyLoadContext.Default.Assemblies.Single(assembly => assembly.GetName().Name == name.Name);
            if (name.Name?.StartsWith("Darkspyre.Inference", StringComparison.Ordinal) == true)
            {
                string path = Path.Combine(Path.GetDirectoryName(typeof(XaiFileFacadeGenerationTests).Assembly.Location)!, name.Name + ".dll");
                if (System.IO.File.Exists(path)) return LoadFromAssemblyPath(path);
            }
            return null;
        }
    }
}
