using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.Audio;
using Darkspyre.Inference.Connect.LLM.Providers;
using Darkspyre.Inference.Connect.TTS;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class CollectibleSerializationTests
{
    private static readonly Type[] CommonConnectMetadataOwners =
    [
        typeof(InferenceJson), typeof(ConnectLlmJson), typeof(ConnectTtsJson), typeof(ConnectAudioJson)
    ];
    private static readonly string[] GenerationAssemblies = CommonConnectMetadataOwners
        .Select(owner => owner.Assembly.GetName().Name!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    // Serialized by the dev code before the phase-1 build changed any contract (B18).
    private const string DevAgentContext = """
        {"FormatVersion":3,"Revision":4,"Entries":[{"Id":"e1","TurnId":"t1","Message":{"Role":"user","Content":"Show the chart.","ContinuationState":null,"OmittedAttachments":[{"Kind":0,"FileName":"chart.png","Index":0,"ToolCallId":null}],"Attachments":[],"ToolCalls":[],"ToolResults":[]}},{"Id":"e2","TurnId":"t1","Message":{"Role":"assistant","Content":"","ContinuationState":null,"OmittedAttachments":[],"Attachments":[],"ToolCalls":[{"type":"function","Id":"call-1","Name":"read_file","ArgumentsJson":"{\u0022path\u0022:\u0022a.txt\u0022}"}],"ToolResults":[]}},{"Id":"e3","TurnId":"t1","Message":{"Role":"user","Content":"","ContinuationState":null,"OmittedAttachments":[],"Attachments":[],"ToolCalls":[],"ToolResults":[{"type":"function","ToolCallId":"call-1","Output":{"Parts":[{"type":"text","Text":"contents"}],"StructuredContent":null,"IsError":false}}]}},{"Id":"e4","TurnId":"t1","Message":{"Role":"assistant","Content":"Done.","ContinuationState":null,"OmittedAttachments":[],"Attachments":[],"ToolCalls":[],"ToolResults":[]}}],"SelectedResources":[],"ResourceSelectionVersion":null}
        """;
    private const string DevChatMessage = """
        {"Role":"user","Content":"Look at these.","ContinuationState":null,"OmittedAttachments":[],"Attachments":[{"type":"binary","Kind":0,"FileName":"chart.png","MediaType":"image/png","Data":"AQID"}],"ToolCalls":[],"ToolResults":[]}
        """;
    private const string DevToolDefinition = """
        {"Name":"read_file","Description":"Reads a file.","InputSchema":{"type":"object","properties":{"path":{"type":"string"}},"required":["path"],"additionalProperties":false},"Strict":true,"DeferLoading":false,"RequiresSequentialExecution":false}
        """;
    private const string DevContextPolicy = """
        {"CompactionThresholdRatio":0.8,"SafetyMarginTokens":2048,"ReservedOutputTokens":8000,"MaxContextTokens":200000}
        """;
    private const string DevConnection = """
        {"Version":"v1","Transport":13,"Endpoint":"https://api.x.ai/v1","Credential":null,"Vertex":null,"Azure":null,"Acp":null,"Copilot":null,"RuntimeLaunchInputs":null,"ChatCompletions":null,"Xai":{"Region":1,"ServiceTier":1,"MultiAgent":{"AgentCount":4}},"AdditionalHeaders":{},"ProviderOptions":{},"CapabilityOverrides":{"SupportsStreaming":null,"SupportsStreamingUsage":null,"SupportsUsageDeltas":null,"SupportsReasoningText":null,"SupportsModelListing":null,"SupportsAccountUsage":null,"SupportsJsonSchema":"Supported","SupportsJsonObject":null,"ReasoningLevels":null,"SupportsTemperature":"Unsupported","SupportsTopP":null,"SupportsTopK":null,"SupportsSeed":null,"SupportsPresencePenalty":null,"SupportsFrequencyPenalty":null,"MaxStopSequences":null,"SupportsToolUse":null,"SupportsAgentSessions":null,"SupportsAgentToolUse":null,"SupportsAgentTurnInstructions":null,"SupportsPromptImages":null,"SupportsPromptDocuments":null,"SupportsToolResultImages":null,"SupportsToolResultDocuments":null},"ModelCapabilityOverrides":{"grok-4":{"SupportsStreaming":null,"SupportsStreamingUsage":null,"SupportsUsageDeltas":null,"SupportsReasoningText":null,"SupportsModelListing":null,"SupportsAccountUsage":null,"SupportsJsonSchema":null,"SupportsJsonObject":null,"ReasoningLevels":null,"SupportsTemperature":null,"SupportsTopP":null,"SupportsTopK":null,"SupportsSeed":"Unsupported","SupportsPresencePenalty":null,"SupportsFrequencyPenalty":null,"MaxStopSequences":null,"SupportsToolUse":null,"SupportsAgentSessions":null,"SupportsAgentToolUse":null,"SupportsAgentTurnInstructions":null,"SupportsPromptImages":null,"SupportsPromptDocuments":null,"SupportsToolResultImages":null,"SupportsToolResultDocuments":null}}}
        """;

    [Fact]
    public void StoredDevPayloadsStillRead()
    {
        LlmAgentContext context = JsonSerializer.Deserialize(DevAgentContext, ConnectLlmJson.AgentContext)!;
        Assert.Equal(4, context.Entries.Count);
        Assert.Equal("chart.png", Assert.Single(context.Entries[0].Message.OmittedAttachments).FileName);
        Assert.Equal("read_file", Assert.IsType<LlmToolCall>(Assert.Single(context.Entries[1].Message.ToolCalls)).Name);
        Assert.Equal("call-1", Assert.Single(context.Entries[2].Message.ToolResults).ToolCallId);

        LlmChatMessage message = JsonSerializer.Deserialize(DevChatMessage, ConnectLlmJson.ChatMessage)!;
        LlmAttachment attachment = Assert.IsType<LlmAttachment>(Assert.Single(message.Attachments));
        Assert.Equal(new byte[] { 1, 2, 3 }, attachment.Data.ToArray());

        LlmAgentToolDefinition tool = JsonSerializer.Deserialize(DevToolDefinition, ConnectLlmJson.LlmAgentToolDefinition)!;
        Assert.Equal("read_file", tool.Name);
        Assert.Equal("string", tool.InputSchema.GetProperty("properties").GetProperty("path").GetProperty("type").GetString());

        LlmAgentContextPolicy policy = JsonSerializer.Deserialize<LlmAgentContextPolicy>(DevContextPolicy)!;
        Assert.Equal(8000, policy.ReservedOutputTokens);
        Assert.Equal(200000, policy.MaxContextTokens);

        LlmConnection connection = JsonSerializer.Deserialize(DevConnection, ConnectLlmJson.Connection)!;
        Assert.Equal(LlmTransport.XaiResponses, connection.Transport);
        Assert.Equal(XaiRegion.UnitedStates, connection.Xai!.Region);
        Assert.Equal(4, connection.Xai.MultiAgent!.AgentCount);
        Assert.Equal(InferenceSupport.Unsupported, connection.CapabilityOverrides!.SupportsTemperature);
        Assert.Equal(InferenceSupport.Unsupported, connection.ModelCapabilityOverrides["grok-4"].SupportsSeed);
    }

    [Fact]
    public void OwnedResultMetadataDoesNotKeepItsCollectibleGenerationAlive()
    {
        string json = JsonSerializer.Serialize(new LlmChatMessage("assistant", "done")
        {
            ToolCalls = [new LlmShellCall("call-1", ["pwd"], null, null)]
        }, ConnectLlmJson.ChatMessage);
        WeakReference[] generation = RoundTripInCollectibleGeneration(json);

        AssertCollected(generation);
    }

    [Fact]
    public void RegisteredChatFacadeHttpGraphReleasesItsCollectibleGeneration()
    {
        WeakReference[] generation = RetireRegisteredChatGeneration();

        AssertCollected(generation);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] RetireRegisteredChatGeneration()
    {
        var context = new SerializationGeneration(Path.GetDirectoryName(typeof(ConnectLlmJson).Assembly.Location)!);
        ServiceProvider? container = null;
        try
        {
            Assembly assembly = context.LoadFromAssemblyPath(typeof(ConnectLlmJson).Assembly.Location);
            Assert.Same(context, AssemblyLoadContext.GetLoadContext(assembly));
            Assert.Equal(typeof(ConnectLlmJson).Assembly.ManifestModule.ModuleVersionId, assembly.ManifestModule.ModuleVersionId);
            Type registration = assembly.GetType(typeof(ServiceCollectionExtensions).FullName!, throwOnError: true)!;
            MethodInfo register = registration.GetMethods().Single(method => method.Name == nameof(ServiceCollectionExtensions.AddLlmConnect)
                && method.GetParameters().Length == 1);
            Assembly fixtures = context.LoadFromAssemblyPath(typeof(AgentFacadeRetirementTests).Assembly.Location);
            Assert.Same(context, AssemblyLoadContext.GetLoadContext(fixtures));
            Assert.Equal(typeof(AgentFacadeRetirementTests).Assembly.ManifestModule.ModuleVersionId, fixtures.ManifestModule.ModuleVersionId);
            object source = Activator.CreateInstance(fixtures.GetType(
                typeof(AgentFacadeRetirementTests).FullName + "+Source", throwOnError: true)!, nonPublic: true)!;
            Type sourceContract = assembly.GetType(typeof(ILlmConnectionSource).FullName!, throwOnError: true)!;
            Assert.IsAssignableFrom(sourceContract, source);
            IServiceCollection services = new ServiceCollection();
            services.Add(ServiceDescriptor.Singleton(sourceContract, source));
            Type hostContract = assembly.GetType(typeof(ILlmSelfHost).FullName!, throwOnError: true)!;
            object host = Activator.CreateInstance(fixtures.GetType(
                typeof(CollectibleSerializationTests).FullName + "+UnusedSelfHost", throwOnError: true)!, nonPublic: true)!;
            services.Add(ServiceDescriptor.Singleton(hostContract, host));
            register.Invoke(null, [services]);
            container = services.BuildServiceProvider();
            Type clientType = assembly.GetType(typeof(ILlmClient).FullName!, throwOnError: true)!;
            var clients = (System.Collections.IEnumerable)container.GetRequiredService(typeof(IEnumerable<>).MakeGenericType(clientType));
            object[] resolved = clients.Cast<object>().ToArray();
            Assert.Equal(4, resolved.Length);
            Assert.All(resolved, client => Assert.Same(context, AssemblyLoadContext.GetLoadContext(client.GetType().Assembly)));
            object pool = container.GetRequiredService(assembly.GetType(
                "Darkspyre.Inference.Connect.LLM.Internal.SdkHttpGenerationPool", throwOnError: true)!);
            Assert.Same(assembly, pool.GetType().Assembly);
            object chat = container.GetRequiredService(assembly.GetType(typeof(LlmProviderService).FullName!, throwOnError: true)!);
            object agents = container.GetRequiredService(assembly.GetType(typeof(LlmAgentProviderService).FullName!, throwOnError: true)!);
            object embeddings = container.GetRequiredService(assembly.GetType(typeof(LlmEmbeddingProviderService).FullName!, throwOnError: true)!);
            Assert.All(new[] { chat, agents, embeddings }, facade => Assert.Same(assembly, facade.GetType().Assembly));
            return [new(context), new(assembly), new(fixtures), new(source), new(container), new(pool),
                new(chat), new(agents), new(embeddings), .. resolved.Select(client => new WeakReference(client))];
        }
        finally
        {
            try { container?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            finally { context.Unload(); }
        }
    }

    private static void AssertCollected(WeakReference[] generation)
    {
        // The runtime's unload guidance bounds collection at ten finalizer passes.
        for (int attempt = 0; attempt < 10 && generation.Any(reference => reference.IsAlive); attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.All(generation, reference => Assert.False(reference.IsAlive));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectingAnUnknownCollectibleTypeDoesNotKeepItAlive(bool credential)
    {
        WeakReference type = AttemptUnsupportedSerialization(credential);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(type.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] RoundTripInCollectibleGeneration(string json)
    {
        var context = new SerializationGeneration(Path.GetDirectoryName(typeof(ConnectLlmJson).Assembly.Location)!);
        try
        {
            Type owner = typeof(ConnectLlmJson);
            Assembly assembly = context.LoadFromAssemblyPath(owner.Assembly.Location);
            Assert.Same(context, AssemblyLoadContext.GetLoadContext(assembly));
            Type metadataOwner = assembly.GetType(owner.FullName!, throwOnError: true)!;
            var references = new List<WeakReference> { new(context), new(assembly) };
            ExerciseMetadata(metadataOwner, context, references);
            string rootName = nameof(ConnectLlmJson.ChatMessage);
            var metadata = (JsonTypeInfo)metadataOwner.GetProperty(rootName)!.GetValue(null)!;
            Assert.Same(assembly, metadata.Type.Assembly);

            object value = JsonSerializer.Deserialize(json, metadata)!;
            Assert.Same(assembly, value.GetType().Assembly);
            Assert.Equal(json, JsonSerializer.Serialize(value, metadata));
            var tools = (System.Collections.IEnumerable)value.GetType().GetProperty(nameof(LlmChatMessage.ToolCalls))!.GetValue(value)!;
            object tool = Assert.Single(tools.Cast<object>());
            Assembly common = tool.GetType().Assembly;
            Assert.Same(context, AssemblyLoadContext.GetLoadContext(common));
            Assert.Equal("Darkspyre.Inference.Common", common.GetName().Name);

            references.Add(new(metadata));
            references.Add(new(value));
            references.Add(new(common));
            references.Add(new(tool));
            foreach (Assembly loaded in context.Assemblies) references.Add(new(loaded));
            return references.ToArray();
        }
        finally
        {
            context.Unload();
        }
    }

    private static int ExerciseMetadata(Type metadataOwner, AssemblyLoadContext context, List<WeakReference> references)
    {
        PropertyInfo[] roots = metadataOwner.GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(property => typeof(JsonTypeInfo).IsAssignableFrom(property.PropertyType)).ToArray();
        Assert.NotEmpty(roots);
        foreach (PropertyInfo root in roots)
        {
            var typeInfo = (JsonTypeInfo)root.GetValue(null)!;
            if (typeInfo.Type.Assembly.GetName().Name!.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal))
                Assert.Same(context, AssemblyLoadContext.GetLoadContext(typeInfo.Type.Assembly));
            typeInfo.MakeReadOnly();
            _ = typeInfo.Properties.Count;
            Assert.Same(typeInfo, typeInfo.Options.GetTypeInfo(typeInfo.Type));
            references.Add(new(typeInfo));
            references.Add(new(typeInfo.Options));
        }
        return roots.Length;
    }

    private sealed class UnusedSelfHost : ILlmSelfHost
    {
        public Task<IReadOnlyList<LlmSelfHostEngine>> ListEnginesAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<ILlmInference> GetInferenceAsync(string engineId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<Common.Embedding.IEmbeddingInference> GetEmbeddingInferenceAsync(string engineId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public LlmCapabilities? GetCapabilities(string engineId, string? modelId) => throw new NotSupportedException();
    }

    private sealed class SerializationGeneration(string directory) : AssemblyLoadContext("ConnectJsonGeneration", isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (!GenerationAssemblies.Contains(name.Name, StringComparer.Ordinal)) return null;
            return LoadFromAssemblyPath(Path.Combine(directory, name.Name + ".dll"));
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AttemptUnsupportedSerialization(bool credential)
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("UnknownContract"), AssemblyBuilderAccess.RunAndCollect);
        TypeBuilder builder = assembly.DefineDynamicModule("Contracts").DefineType("UnknownContract", TypeAttributes.Public);
        builder.DefineDefaultConstructor(MethodAttributes.Public);
        Type type = builder.CreateType()!;
        object value = Activator.CreateInstance(type)!;

        JsonSerializerOptions options = credential ? ConnectLlmJson.Credential.Options : ConnectLlmJson.ChatMessage.Options;
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(value, type, options));

        return new WeakReference(type);
    }
}
