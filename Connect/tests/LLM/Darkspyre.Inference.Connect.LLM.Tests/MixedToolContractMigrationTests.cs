using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class MixedToolContractMigrationTests
{
    [Theory]
    [InlineData(typeof(LlmInferenceRequest), "Tools", typeof(LlmClientToolDefinition))]
    [InlineData(typeof(LlmChatRequest), "Tools", typeof(LlmClientToolDefinition))]
    [InlineData(typeof(LlmMessage), "ToolCalls", typeof(LlmClientToolCall))]
    [InlineData(typeof(LlmChatMessage), "ToolCalls", typeof(LlmClientToolCall))]
    [InlineData(typeof(LlmInferenceResult), "ToolCalls", typeof(LlmClientToolCall))]
    [InlineData(typeof(LlmChatResult), "ToolCalls", typeof(LlmClientToolCall))]
    [InlineData(typeof(LlmMessage), "ToolResults", typeof(LlmClientToolResult))]
    [InlineData(typeof(LlmChatMessage), "ToolResults", typeof(LlmClientToolResult))]
    [InlineData(typeof(LlmAgentSessionOptions), "Tools", typeof(LlmAgentClientToolDefinition))]
    [InlineData(typeof(LlmAgentResumeOptions), "Tools", typeof(LlmAgentClientToolDefinition))]
    public void EveryCollectionUsesTheSingleOrderedUnion(Type owner, string name, Type element)
    {
        var property = owner.GetProperty(name);
        Assert.NotNull(property);
        Assert.Equal(typeof(IReadOnlyList<>).MakeGenericType(element), property.PropertyType);
    }

    [Theory]
    [InlineData(typeof(LlmCapabilities))]
    [InlineData(typeof(LlmProviderCapabilities))]
    [InlineData(typeof(LlmAgentCapabilities))]
    public void ShellSupportIsAnExplicitFalseDefault(Type owner)
    {
        var property = owner.GetProperty("SupportsShellTools");
        Assert.NotNull(property);
        Assert.Equal(typeof(bool), property.PropertyType);
        Assert.Equal(false, property.GetValue(Activator.CreateInstance(owner)));
    }
}
