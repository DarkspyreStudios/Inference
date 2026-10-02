using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class ProviderControlContractTests
{
    [Theory]
    [InlineData(typeof(LlmInferenceRequest))]
    [InlineData(typeof(LlmChatRequest))]
    [InlineData(typeof(LlmAgentTurnRequest))]
    public void RequestsExposeOneOptionalProviderControl(Type requestType)
    {
        Type controls = RequiredType(typeof(LlmInferenceRequest).Assembly, "Darkspyre.Inference.Common.LLM.LlmProviderControls");
        PropertyInfo property = Assert.IsType<PropertyInfo>(requestType.GetProperty("ProviderControls"), exactMatch: false);
        Assert.Equal(controls, property.PropertyType);
        Assert.Null(property.GetValue(Activator.CreateInstance(requestType)));
    }

    [Fact]
    public void SavedDefaultsUseTheFixedConnectControl()
    {
        Type controls = RequiredType(typeof(LlmChatRequest).Assembly, "Darkspyre.Inference.Connect.LLM.XaiMultiAgentControls");
        PropertyInfo property = Assert.IsType<PropertyInfo>(typeof(XaiConnectionOptions).GetProperty("MultiAgent"), exactMatch: false);
        Assert.Equal(controls, property.PropertyType);
        Assert.Null(property.GetValue(new XaiConnectionOptions()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(4)]
    [InlineData(16)]
    public void ConnectMetadataPreservesTheFixedDiscriminatorAndNullCount(int? count)
    {
        Type controls = RequiredType(typeof(LlmChatRequest).Assembly, "Darkspyre.Inference.Connect.LLM.XaiMultiAgentControls");
        object value = Activator.CreateInstance(controls, [count])!;
        var request = new LlmChatRequest { ModelId = "grok-4.20-multi-agent" };
        typeof(LlmChatRequest).GetProperty("ProviderControls")!.SetValue(request, value);
        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(request, ConnectLlmJson.ChatRequest));
        JsonElement control = json.RootElement.GetProperty("ProviderControls");
        Assert.Equal("xai-multi-agent", control.GetProperty("type").GetString());
        if (count is null) Assert.Equal(JsonValueKind.Null, control.GetProperty("AgentCount").ValueKind);
        else Assert.Equal(count, control.GetProperty("AgentCount").GetInt32());
        var restored = JsonSerializer.Deserialize(json.RootElement.GetRawText(), ConnectLlmJson.ChatRequest)!;
        object? restoredControl = typeof(LlmChatRequest).GetProperty("ProviderControls")!.GetValue(restored);
        Assert.Equal(value, restoredControl);
        PropertyInfo getter = Assert.IsType<PropertyInfo>(typeof(ConnectLlmJson).GetProperty("XaiMultiAgentControls"), exactMatch: false);
        Assert.Equal(controls, Assert.IsAssignableFrom<JsonTypeInfo>(getter.GetValue(null)).Type);
    }

    [Fact]
    public void CommonMetadataRefusesTheConnectSubtypeAndExportsItsOwnBase()
    {
        Type controls = RequiredType(typeof(LlmChatRequest).Assembly, "Darkspyre.Inference.Connect.LLM.XaiMultiAgentControls");
        var request = new LlmInferenceRequest { ModelId = "grok-4.20-multi-agent" };
        typeof(LlmInferenceRequest).GetProperty("ProviderControls")!.SetValue(request, Activator.CreateInstance(controls, [(int?)4]));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(request, InferenceJson.LlmRequest));
        PropertyInfo getter = Assert.IsType<PropertyInfo>(typeof(InferenceJson).GetProperty("LlmProviderControls"), exactMatch: false);
        Assert.Equal(controls.BaseType, Assert.IsAssignableFrom<JsonTypeInfo>(getter.GetValue(null)).Type);
    }

    private static Type RequiredType(Assembly assembly, string name) => Assert.IsAssignableFrom<Type>(assembly.GetType(name));
}
