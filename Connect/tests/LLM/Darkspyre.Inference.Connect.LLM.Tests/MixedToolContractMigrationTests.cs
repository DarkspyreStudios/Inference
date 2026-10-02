using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ManagedLaneDoesNotInferCallerMappingsFromPortableOrAdvertisedAgentShellFacts(bool portableShell)
    {
        var advertised = ManagedAgentPolicy.Capabilities with
        {
            SupportsShellTools = true,
            SupportedCallerServices = new HashSet<LlmAgentCallerServiceKind> { LlmAgentCallerServiceKind.ShellExecution, LlmAgentCallerServiceKind.Permission }
        };
        var portable = new LlmProviderCapabilities { SupportsShellTools = portableShell, Agent = advertised };
        var managed = ClientTools.ManagedCapabilities(portable);
        Assert.False(managed.SupportsShellTools); Assert.Empty(managed.SupportedCallerServices);
        Assert.True(advertised.SupportsShellTools); Assert.Equal(2, advertised.SupportedCallerServices.Count);
        Assert.Throws<NotSupportedException>(() => ClientTools.ValidateManagedOpening(new()
        { ModelId = "model", Tools = [new LlmAgentShellTool(new([]))] }, portable));
    }

    [Fact]
    public void ShellHistoryLedgerRetainsCallOwnedLimitsAndOrderedActualOutputs()
    {
        var call = new LlmShellCall("shell", ["first", "second"], 0, 0);
        var result = new LlmShellCallResult(call.Id,
            [new(null, "observed error", new LlmShellExitedOutcome(7)), new("", null, new LlmShellTimedOutOutcome())]);
        var request = new LlmChatRequest
        {
            ModelId = "model",
            Messages = [new("assistant", "") { ToolCalls = [call] }, new("user", "") { ToolResults = [result] }]
        };
        ClientTools.Validate(request, supportsShellTools: true);
        Assert.Equal(0, call.MaxOutputLength); Assert.Equal(0, call.TimeoutMs);
        Assert.Null(result.Output[0].Stdout); Assert.Equal("observed error", result.Output[0].Stderr);
        Assert.Equal(7, Assert.IsType<LlmShellExitedOutcome>(result.Output[0].Outcome).ExitCode);
        Assert.IsType<LlmShellTimedOutOutcome>(result.Output[1].Outcome);
        Assert.Null(typeof(LlmShellCallResult).GetProperty("MaxOutputLength"));
        Assert.Throws<NotSupportedException>(() => ClientTools.Validate(request, supportsShellTools: false));
        Assert.Throws<ArgumentException>(() => ClientTools.Validate(request with
        { Messages = [request.Messages[0], new("user", "") { ToolResults = [new LlmToolResult(call.Id, new())] }] }, true));
        Assert.Throws<ArgumentException>(() => ClientTools.Validate(request with
        { Messages = [request.Messages[0], new("user", "") { ToolResults = [result, result] }] }, true));
        Assert.Throws<ArgumentException>(() => ClientTools.Validate(request with
        { Messages = [new("user", "") { ToolResults = [result] }] }, true));
    }
}
