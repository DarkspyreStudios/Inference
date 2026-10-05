using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
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
    public IAsyncEnumerable<IEventStreamEvent> InvokeAsync(
        LlmAwsCredentials aws,
        InvokeHarnessRequest request,
        CancellationToken cancellationToken) =>
        factory.StreamAsync(token => InvokeOwnedAsync(aws, request, token), cancellationToken);

    [SuppressMessage("Design", "CA1031", Justification = "The adapter preserves the operation and each independent owned cleanup failure.")]
    [SuppressMessage("Usage", "CA2219", Justification = "Iterator retirement must surface recorded operation and cleanup failures on early reader disposal.")]
    private async IAsyncEnumerable<IEventStreamEvent> InvokeOwnedAsync(
        LlmAwsCredentials aws,
        InvokeHarnessRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using BedrockClientFactory.Lease<AmazonBedrockAgentCoreClient> lease = factory.AgentCore(aws, cancellationToken);
        cancellationToken = lease.Token;
        var failures = new List<Exception>();
        IDisposable? ownedResponse = null;
        IAsyncEnumerator<IEventStreamEvent>? events = null;
        try
        {
            try
            {
                lease.ThrowIfCanceled();
                InvokeHarnessResponse response = await lease.Client.InvokeHarnessAsync(request, cancellationToken);
                ownedResponse = lease.OwnResponse(response);
                lease.ThrowIfCanceled();
                events = response.Stream.GetAsyncEnumerator(cancellationToken);
            }
            catch (Exception error) { failures.Add(error); }
            while (events is not null)
            {
                bool moved;
                IEventStreamEvent current;
                try
                {
                    lease.ThrowIfCanceled();
                    moved = await events.MoveNextAsync();
                    lease.ThrowIfCanceled();
                    if (!moved) break;
                    current = events.Current;
                }
                catch (BedrockAgentCoreEventStreamException) when (cancellationToken.IsCancellationRequested)
                {
                    failures.Add(new OperationCanceledException(cancellationToken));
                    break;
                }
                catch (Exception error) { failures.Add(error); break; }
                yield return current;
            }
        }
        finally
        {
            if (events is not null)
                try { await events.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) { failures.Add(BedrockLlmClient.CleanupFailure(error, events)); }
            if (ownedResponse is not null)
                try { ownedResponse.Dispose(); }
                catch (Exception error) { failures.Add(error); }
            if (failures.Count == 1) ExceptionDispatchInfo.Throw(failures[0]);
            if (failures.Count > 1) throw new AggregateException("Bedrock AgentCore and owned cleanup failed.", failures);
        }
    }
}
