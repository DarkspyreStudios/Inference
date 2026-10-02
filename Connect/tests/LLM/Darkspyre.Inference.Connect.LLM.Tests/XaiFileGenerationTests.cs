using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class XaiFileGenerationTests
{
    private const string FileJson = "{\"id\":\"file-a\",\"filename\":\"a\",\"bytes\":3,\"created_at\":0,\"object\":\"file\"}";

    [Fact]
    public async Task ActualForeignAdapterAndOwnedCommonConnectMetadataCollectOutsideCallingFrame()
    {
        (WeakReference[] roots, string json) = await ExerciseForeign();
        await Collect(roots);
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal("file-a", document.RootElement.GetProperty("Id").GetString());
        Assert.Equal(3, document.RootElement.GetProperty("Bytes").GetInt64());
        Assert.Equal("actual-workspace", document.RootElement.GetProperty("Resource").GetProperty("WorkspaceId").GetString());
    }

    [Fact]
    public async Task RetainedDisposedDefaultAdapterDropsSeparatelyForeignBorrowedHttpWithoutDisposingIt()
    {
        (WeakReference[] roots, object retainedAdapter) = await ExerciseBorrowed();
        await Collect(roots);
        GC.KeepAlive(retainedAdapter);
    }

    public static async Task<object[]> ExecuteForeignAsync()
    {
        AssemblyLoadContext generation = AssemblyLoadContext.GetLoadContext(typeof(XaiFileGenerationTests).Assembly)!;
        Assert.True(generation.IsCollectible);
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(typeof(XaiFileClient).Assembly));
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(typeof(InferenceJson).Assembly));
        using var handler = new ForeignHandler();
        using var http = new HttpClient(handler);
        var adapter = new XaiFileClient(http);
        LlmProviderFile actual;
        try { actual = await adapter.GetAsync(Connection(), "file-a", () => { }, CancellationToken.None); }
        finally { await adapter.DisposeAsync(); }
        Assert.Equal(1, handler.Calls);
        Assert.Equal(0, handler.Disposals);
        Assert.Null(typeof(XaiFileClient).GetField("_http", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(adapter));
        string resourceJson = JsonSerializer.Serialize(new InferenceResourceReference("actual-workspace", "actual-resource"), InferenceJson.InferenceResourceReference);
        InferenceResourceReference reference = JsonSerializer.Deserialize(resourceJson, InferenceJson.InferenceResourceReference)!;
        actual = actual with { Resource = reference };
        string json = JsonSerializer.Serialize(actual, ConnectLlmJson.LlmProviderFile);
        actual = JsonSerializer.Deserialize(json, ConnectLlmJson.LlmProviderFile)!;
        JsonTypeInfo metadata = ConnectLlmJson.LlmProviderFile;
        JsonTypeInfo commonMetadata = InferenceJson.InferenceResourceReference;
        Assert.Same(typeof(XaiFileClient).Assembly, metadata.Type.Assembly);
        Assert.Same(typeof(InferenceJson).Assembly, commonMetadata.Type.Assembly);
        return [JsonSerializer.Serialize(actual, ConnectLlmJson.LlmProviderFile),
            new WeakReference(adapter), new WeakReference(handler), new WeakReference(metadata),
            new WeakReference(metadata.Options.TypeInfoResolver!), new WeakReference(commonMetadata), new WeakReference(commonMetadata.Options.TypeInfoResolver!)];
    }

    public static HttpClient CreateBorrowedClient() => new(new ForeignHandler());

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference[], string)> ExerciseForeign()
    {
        var generation = new Generation(false);
        try
        {
            Assembly fixture = generation.LoadFromAssemblyPath(typeof(XaiFileGenerationTests).Assembly.Location);
            Assembly connect = generation.LoadFromAssemblyPath(typeof(XaiFileClient).Assembly.Location);
            Assembly common = generation.LoadFromAssemblyPath(typeof(InferenceJson).Assembly.Location);
            foreach (Assembly assembly in new[] { fixture, connect, common }) Assert.Same(generation, AssemblyLoadContext.GetLoadContext(assembly));
            Assert.Equal(typeof(XaiFileClient).Module.ModuleVersionId, connect.ManifestModule.ModuleVersionId);
            Assert.Equal(typeof(InferenceJson).Module.ModuleVersionId, common.ManifestModule.ModuleVersionId);
            Assert.Equal(typeof(XaiFileGenerationTests).Module.ModuleVersionId, fixture.ManifestModule.ModuleVersionId);
            Type caller = fixture.GetType(typeof(XaiFileGenerationTests).FullName!, true)!;
            MethodInfo method = caller.GetMethod(nameof(ExecuteForeignAsync))!;
            object[] result = await (Task<object[]>)method.Invoke(null, null)!;
            Type adapter = connect.GetType(typeof(XaiFileClient).FullName!, true)!;
            var roots = new List<WeakReference> { new(generation), new(fixture), new(connect), new(common), new(caller), new(method), new(adapter) };
            roots.AddRange(result.Skip(1).Cast<WeakReference>());
            return (roots.ToArray(), (string)result[0]);
        }
        finally { generation.Unload(); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference[], object)> ExerciseBorrowed()
    {
        var generation = new Generation(true);
        XaiFileClient? adapter = null;
        HttpClient? http = null;
        try
        {
            Assembly fixture = generation.LoadFromAssemblyPath(typeof(XaiFileGenerationTests).Assembly.Location);
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(fixture));
            Assert.Equal(typeof(XaiFileGenerationTests).Module.ModuleVersionId, fixture.ManifestModule.ModuleVersionId);
            Type caller = fixture.GetType(typeof(XaiFileGenerationTests).FullName!, true)!;
            MethodInfo method = caller.GetMethod(nameof(CreateBorrowedClient))!;
            http = (HttpClient)method.Invoke(null, null)!;
            adapter = new XaiFileClient(http);
            LlmProviderFile result = await adapter.GetAsync(Connection(), "file-a", () => { }, CancellationToken.None);
            Assert.Equal("file-a", result.Id);
            await adapter.DisposeAsync();
            Assert.Null(typeof(XaiFileClient).GetField("_http", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(adapter));
            // The explicit caller-owned client remains usable after adapter retirement.
            using HttpResponseMessage again = await http.GetAsync("https://api.x.ai/v1/files/file-a");
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            Type handlerType = caller.GetNestedType(nameof(ForeignHandler), BindingFlags.NonPublic)!;
            Assert.Equal(2, handlerType.GetField(nameof(ForeignHandler.TotalCalls))!.GetValue(null));
            Assert.Equal(0, handlerType.GetField(nameof(ForeignHandler.TotalDisposals))!.GetValue(null));
            return ([new(generation), new(fixture), new(caller), new(method), new(handlerType)], adapter);
        }
        finally
        {
            if (adapter is not null) await adapter.DisposeAsync();
            http?.Dispose();
            generation.Unload();
        }
    }

    private static async Task Collect(WeakReference[] roots)
    {
        for (int attempt = 0; attempt < 30 && roots.Any(root => root.IsAlive); attempt++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); await Task.Delay(10);
        }
        Assert.All(roots, root => Assert.False(root.IsAlive));
    }

    private static LlmConnection Connection() => new()
    {
        Transport = LlmTransport.XaiResponses,
        Endpoint = "https://api.x.ai/v1",
        Version = "v1",
        Credential = new LlmCredential { OAuth = new() { AccessToken = "oauth-token" } }.Serialize()
    };

    private sealed class ForeignHandler : HttpMessageHandler
    {
        public static int TotalCalls;
        public static int TotalDisposals;
        public int Calls { get; private set; }
        public int Disposals { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; TotalCalls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(FileJson) });
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { Disposals++; TotalDisposals++; }
            base.Dispose(disposing);
        }
    }

    private sealed class Generation(bool shareContracts) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (shareContracts && name.Name is "Darkspyre.Inference.Connect.LLM" or "Darkspyre.Inference.Common")
                return AssemblyLoadContext.Default.Assemblies.Single(assembly => assembly.GetName().Name == name.Name);
            if (name.Name?.StartsWith("Darkspyre.Inference", StringComparison.Ordinal) == true)
            {
                string path = Path.Combine(Path.GetDirectoryName(typeof(XaiFileGenerationTests).Assembly.Location)!, name.Name + ".dll");
                if (System.IO.File.Exists(path)) return LoadFromAssemblyPath(path);
            }
            return null;
        }
    }
}
