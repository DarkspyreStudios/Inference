using System.Runtime.CompilerServices;
using Amazon.BedrockAgentCore.Model;
using Amazon.Runtime.EventStreams;

namespace Darkspyre.Inference.Connect.LLM.Bedrock;

internal interface IBedrockAgentCoreInvoker
{
    IAsyncEnumerable<IEventStreamEvent> InvokeAsync(
        LlmAwsCredentials aws,
        InvokeHarnessRequest request,
        CancellationToken cancellationToken);
}

internal sealed class BedrockAgentCoreInvoker(BedrockClientFactory factory)
    : IBedrockAgentCoreInvoker
{
    public async IAsyncEnumerable<IEventStreamEvent> InvokeAsync(
        LlmAwsCredentials aws,
        InvokeHarnessRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using InvokeHarnessResponse response = await factory.AgentCore(aws).InvokeHarnessAsync(
            request,
            cancellationToken);
        await foreach (IEventStreamEvent streamEvent in response.Stream.WithCancellation(
                           cancellationToken))
        {
            yield return streamEvent;
        }
    }
}
