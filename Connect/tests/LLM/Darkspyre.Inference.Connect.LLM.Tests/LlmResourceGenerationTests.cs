using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class LlmResourceGenerationTests
{
    [Fact]
    public async Task RetainedDefaultSessionReleasesSeparatelyCollectibleBorrowedResourceAccess()
    {
        (ManagedAgentSession retained, WeakReference[] roots) = await ExerciseBorrowedAsync();
        Collect(roots);
        GC.KeepAlive(retained);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(ManagedAgentSession, WeakReference[])> ExerciseBorrowedAsync()
    {
        var generation = new Generation();
        var roots = new List<WeakReference> { new(generation) };
        try
        {
            Assembly fixture = generation.LoadFromAssemblyPath(typeof(ControlledSessionResources).Assembly.Location);
            Assert.NotSame(typeof(ControlledSessionResources).Assembly, fixture);
            Assert.Equal(typeof(ControlledSessionResources).Assembly.ManifestModule.ModuleVersionId, fixture.ManifestModule.ModuleVersionId);
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(fixture));
            Type type = fixture.GetType(typeof(ControlledSessionResources).FullName!, true)!;
            var access = (IInferenceResourceAccess)Activator.CreateInstance(type, nonPublic: true)!;
            Assert.Same(typeof(IInferenceResourceAccess).Assembly, type.GetInterface(typeof(IInferenceResourceAccess).FullName!)!.Assembly);
            MethodInfo register = type.GetMethod("RegisterAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            InferenceResourceReference reference = await (Task<InferenceResourceReference>)register.Invoke(access, ["source.txt", "text/plain", "foreign source"])!;
            Assert.Equal("resource-1", reference.Id);
            using var destination = new LlmResourceMaterializationTests.ResourceDestination();
            using var http = new HttpClient(destination);
            var client = new XaiResponsesLlmClient(http);
            var session = await ManagedAgentSession.CreateAsync(LlmResourceMaterializationTests.Connection(),
                new() { ModelId = "grok-4", CallerServices = new() { Resources = access } }, [], client,
                (request, token) => client.StreamAsync(LlmResourceMaterializationTests.Connection(), request, token),
                model => ManagedResourceMaterializationTests.ManagedCapabilities(client, model));
            await ManagedResourceMaterializationTests.Read(session.RunTurnAsync(new() { Input = [new LlmAgentTextContent("inspect")] }));
            LlmAgentContext captured = await session.CaptureContextAsync();
            Assert.Equal(new[] { reference }, captured.SelectedResources);
            Assert.Single(destination.Uploads);
            Assert.Equal("foreign source", System.Text.Encoding.UTF8.GetString(destination.Uploads[0].Bytes));
            await session.DisposeAsync();
            Assert.Equal(0, type.GetProperty("DisposeCalls", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(access));
            Assert.Equal(1, type.GetProperty("OpenedReads", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(access));
            Assert.Equal(1, type.GetProperty("DisposedReads", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(access));
            MethodInfo executed = type.GetMethod(nameof(IInferenceResourceAccess.OpenReadAsync))!;
            Assert.Same(fixture, executed.Module.Assembly);
            roots.AddRange([new(fixture), new(type), new(access), new(register), new(executed)]);
            return (session, roots.ToArray());
        }
        finally { generation.Unload(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualForeignCommonConnectMetadataAndMaterializationCallersExecuteAndRetire(bool executeRoute)
    {
        WeakReference[] roots = ExerciseOwnedGraph(executeRoute);
        Collect(roots);
    }

    public static async Task<int> ExerciseOwnedGraphAsyncCaller()
    {
        foreach (bool facade in new[] { false, true })
        {
            var resources = new ControlledSessionResources();
            await resources.RegisterAsync("original.txt", "text/plain", "foreign original");
            await resources.RegisterAsync("generated.json", "application/json", "{\"foreign\":true}");
            using var destination = new LlmResourceMaterializationTests.ResourceDestination();
            using var http = new HttpClient(destination);
            var client = new XaiResponsesLlmClient(http);
            await using var service = new LlmProviderService(new LlmResourceMaterializationTests.Source(LlmResourceMaterializationTests.Connection()), [client]);
            int completions = 0;
            await foreach (var item in facade ? service.StreamAsync("selected", LlmResourceMaterializationTests.Request(resources))
                : client.StreamAsync(LlmResourceMaterializationTests.Connection(), LlmResourceMaterializationTests.Request(resources)))
                if (item is LlmChatCompletionEvent) completions++;
            Assert.Equal(1, completions);
            Assert.Equal(2, destination.Uploads.Count);
            Assert.Equal("foreign original", System.Text.Encoding.UTF8.GetString(destination.Uploads[0].Bytes));
            Assert.Equal("{\"foreign\":true}", System.Text.Encoding.UTF8.GetString(destination.Uploads[1].Bytes));
            Assert.Equal(2, resources.DisposedReads);
            Assert.Equal(0, resources.DisposeCalls);
        }
        return 2;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] ExerciseOwnedGraph(bool executeRoute)
    {
        var generation = new Generation();
        var roots = new List<WeakReference> { new(generation) };
        try
        {
            Assembly common = generation.LoadFromAssemblyPath(typeof(InferenceJson).Assembly.Location);
            Assembly connect = generation.LoadFromAssemblyPath(typeof(ConnectLlmJson).Assembly.Location);
            Assembly fixture = generation.LoadFromAssemblyPath(typeof(LlmResourceGenerationTests).Assembly.Location);
            foreach ((Assembly source, Assembly actual) in new[] { (typeof(InferenceJson).Assembly, common), (typeof(ConnectLlmJson).Assembly, connect), (typeof(LlmResourceGenerationTests).Assembly, fixture) })
            {
                Assert.NotSame(source, actual);
                Assert.Equal(source.ManifestModule.ModuleVersionId, actual.ManifestModule.ModuleVersionId);
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(actual));
                roots.Add(new(actual));
            }
            foreach ((Assembly owner, Type sourceFacade, string name, string json) in new[]
            {
                (common, typeof(InferenceJson), nameof(InferenceJson.InferenceResourceOperationFailure), "{\"Phase\":9,\"Reference\":null,\"CommitOutcome\":0,\"BytesTransferred\":null,\"SelectionOutcome\":0,\"SelectionVersion\":null}"),
                (connect, typeof(ConnectLlmJson), nameof(ConnectLlmJson.AgentContext), "{\"FormatVersion\":3,\"Revision\":0,\"Entries\":[],\"SelectedResources\":[],\"ResourceSelectionVersion\":null}")
            })
            {
                Type facade = owner.GetType(sourceFacade.FullName!, true)!;
                PropertyInfo getter = facade.GetProperty(name)!;
                Assert.Same(owner, getter.GetMethod!.Module.Assembly);
                var metadata = (JsonTypeInfo)getter.GetValue(null)!;
                object value = JsonSerializer.Deserialize(json, metadata)!;
                Assert.Same(owner, metadata.Type.Assembly);
                Assert.Same(owner, metadata.Options.TypeInfoResolver!.GetType().Assembly);
                using var parsed = JsonDocument.Parse(JsonSerializer.Serialize(value, metadata));
                Assert.Equal(name == nameof(InferenceJson.InferenceResourceOperationFailure) ? 9 : 3,
                    parsed.RootElement.GetProperty(name == nameof(InferenceJson.InferenceResourceOperationFailure) ? "Phase" : "FormatVersion").GetInt32());
                roots.AddRange([new(facade), new(getter), new(metadata), new(metadata.Type), new(metadata.Options.TypeInfoResolver)]);
            }
            Type proof = fixture.GetType(typeof(LlmResourceGenerationTests).FullName!, true)!;
            MethodInfo caller = proof.GetMethod(nameof(ExerciseOwnedGraphAsyncCaller))!;
            Assert.Same(fixture, caller.Module.Assembly);
            if (executeRoute) Assert.Equal(2, ((Task<int>)caller.Invoke(null, null)!).GetAwaiter().GetResult());
            roots.AddRange([new(proof), new(caller)]);
        }
        finally { generation.Unload(); }
        return roots.ToArray();
    }

    private static void Collect(WeakReference[] roots)
    {
        for (int attempt = 0; roots.Any(root => root.IsAlive) && attempt < 30; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); }
        for (int index = 0; index < roots.Length; index++) Assert.False(roots[index].IsAlive, $"Root {index} remains alive.");
    }

    private sealed class Generation() : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name) => Assemblies.FirstOrDefault(assembly => AssemblyName.ReferenceMatchesDefinition(name, assembly.GetName()))
            ?? Default.Assemblies.FirstOrDefault(assembly => AssemblyName.ReferenceMatchesDefinition(name, assembly.GetName()));
    }
}
