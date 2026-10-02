using System.Text.Json.Serialization.Metadata;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class RuntimeAuthenticationContractTests
{
    public static TheoryData<string> DataRoots => new()
    {
        "LlmAgentRuntimeProfile", "LlmAgentRuntimeStateConfiguration", "LlmAgentIsolatedRuntimeState", "LlmAgentAdoptedRuntimeState",
        "LlmAcpConfiguration", "LlmAcpToolRelayConfiguration", "LlmCopilotEndpointMode", "LlmCopilotConfiguration",
        "LlmAcpAuthenticationConfiguration", "LlmAcpExternalAuthentication", "LlmAcpAgentAuthentication", "LlmAcpTerminalAuthentication",
        "LlmAcpAuthenticationFlow", "LlmAcpAuthenticationMethod", "LlmCopilotAuthenticationConfiguration",
        "LlmCopilotExplicitGitHubToken", "LlmCopilotRuntimeAccount", "LlmAgentRuntimeLoginScope",
        "LlmAgentRuntimeAuthenticationSnapshot", "LlmAgentRuntimeLoginOptions", "LlmAgentStagedLoginState",
        "LlmAgentRuntimeLoginResult", "LlmAgentStagedStateDisposition", "LlmAgentStagedStateSnapshot", "LlmAgentStagedStateCommitResult",
        "LlmCopilotCredentials", "LlmCopilotRuntimeConnectionCredential"
    };

    [Theory, MemberData(nameof(DataRoots))]
    public void EveryExactDataRootHasItsOwnedNamedMetadata(string name)
    {
        Type? type = typeof(ConnectLlmJson).Assembly.GetType("Darkspyre.Inference.Connect.LLM." + name);
        Assert.NotNull(type);
        var getter = typeof(ConnectLlmJson).GetProperty(name);
        Assert.NotNull(getter);
        JsonTypeInfo metadata = Assert.IsAssignableFrom<JsonTypeInfo>(getter.GetValue(null));
        Assert.Equal(type, metadata.Type);
        Assert.Same(typeof(ConnectLlmJson).Assembly, metadata.Type.Assembly);
    }

    [Fact]
    public void NewTransportIdentitiesAppendWithoutReclaimingFive()
    {
        Assert.True(Enum.TryParse("GitHubCopilot", out LlmTransport copilot));
        Assert.True(Enum.TryParse("AgentClientProtocol", out LlmTransport acp));
        Assert.Equal(16, (int)copilot); Assert.Equal(17, (int)acp);
        Assert.False(Enum.IsDefined((LlmTransport)5)); Assert.Equal(15, (int)LlmTransport.AzureOpenAiChat);
    }
}
