using System.Runtime.CompilerServices;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime;
using Amazon.Runtime.EventStreams;

namespace Darkspyre.Inference.Connect.LLM.Bedrock.Tests;

/// <summary>
/// Records the Converse and ConverseStream requests it receives and answers with a scripted response or event
/// stream. It can throw a scripted exception after a given number of stream events.
/// </summary>
internal sealed class StubConverseInvoker : IBedrockConverseInvoker
{
    public ConverseResponse? Response { get; init; }

    public IReadOnlyList<IEventStreamEvent> Events { get; init; } = [];

    public string? RequestId { get; init; } = "stream-request-1";

    public Exception? Failure { get; init; }

    public int FailAfter { get; init; }

    public List<ConverseRequest> ConverseRequests { get; } = [];

    public List<ConverseStreamRequest> StreamRequests { get; } = [];

    public bool Disposed { get; private set; }

    public Task<ConverseResponse> ConverseAsync(
        LlmAwsCredentials aws,
        ConverseRequest request,
        CancellationToken cancellationToken)
    {
        ConverseRequests.Add(request);
        return Task.FromResult(Response ?? throw new InvalidOperationException("No Converse response is scripted."));
    }

    public Task<IBedrockEventStream> ConverseStreamAsync(
        LlmAwsCredentials aws,
        ConverseStreamRequest request,
        CancellationToken cancellationToken)
    {
        StreamRequests.Add(request);
        return Task.FromResult<IBedrockEventStream>(new Stream(this));
    }

    public static ConverseResponse Converse(
        IEnumerable<ContentBlock> content,
        string stopReason,
        TokenUsage? usage = null,
        string requestId = "converse-request-1") => new()
        {
            Output = new ConverseOutput
            {
                Message = new Message { Role = ConversationRole.Assistant, Content = [.. content] }
            },
            StopReason = new StopReason(stopReason),
            Usage = usage,
            ResponseMetadata = new ResponseMetadata { RequestId = requestId }
        };

    private sealed class Stream(StubConverseInvoker owner) : IBedrockEventStream
    {
        public string? RequestId => owner.RequestId;

        public async IAsyncEnumerable<IEventStreamEvent> ReadAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            for (int index = 0; index < owner.Events.Count; index++)
            {
                if (owner.Failure is not null && index == owner.FailAfter)
                {
                    throw owner.Failure;
                }
                cancellationToken.ThrowIfCancellationRequested();
                yield return owner.Events[index];
                await Task.Yield();
            }
            if (owner.Failure is not null)
            {
                throw owner.Failure;
            }
        }

        public ValueTask DisposeAsync()
        {
            owner.Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
