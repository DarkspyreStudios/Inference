using System.Runtime.CompilerServices;
using Amazon.BedrockAgentCore;
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
        using BedrockClientFactory.Lease<AmazonBedrockAgentCoreClient> lease = factory.AgentCore(aws, cancellationToken);
        cancellationToken = lease.Token;
        InvokeHarnessResponse response = await lease.Client.InvokeHarnessAsync(
            request,
            cancellationToken);
        using IDisposable ownedResponse = lease.OwnResponse(response);
        cancellationToken.ThrowIfCancellationRequested();
        await using IAsyncEnumerator<IEventStreamEvent> events = response.Stream.GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            bool moved;
            try { moved = await events.MoveNextAsync(); }
            catch (BedrockAgentCoreEventStreamException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!moved) break;
            yield return events.Current;
        }
        cancellationToken.ThrowIfCancellationRequested();
    }
}
