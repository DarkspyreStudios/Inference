using System.Runtime.CompilerServices;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime.EventStreams;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Bedrock;

/// <summary>The Bedrock runtime calls Converse makes. Tests replace it with a double.</summary>
internal interface IBedrockConverseInvoker
{
    Task<ConverseResponse> ConverseAsync(LlmAwsCredentials aws, ConverseRequest request, CancellationToken cancellationToken);

    /// <summary>Opens a ConverseStream response. The caller disposes it.</summary>
    Task<IBedrockEventStream> ConverseStreamAsync(
        LlmAwsCredentials aws, ConverseStreamRequest request, CancellationToken cancellationToken);
}

/// <summary>An open ConverseStream response.</summary>
internal interface IBedrockEventStream : IAsyncDisposable
{
    string? RequestId { get; }

    /// <summary>
    /// The stream's events. A stream exception is thrown as its typed AmazonBedrockRuntimeException, and a cancelled
    /// token as OperationCanceledException.
    /// </summary>
    IAsyncEnumerable<IEventStreamEvent> ReadAsync(CancellationToken cancellationToken);
}

internal sealed class BedrockConverseInvoker(BedrockClientFactory factory) : IBedrockConverseInvoker
{
    public async Task<ConverseResponse> ConverseAsync(
        LlmAwsCredentials aws,
        ConverseRequest request,
        CancellationToken cancellationToken)
    {
        using BedrockClientFactory.Lease<AmazonBedrockRuntimeClient> lease = factory.Runtime(aws, cancellationToken);
        ConverseResponse response = await lease.Client.ConverseAsync(request, lease.Token);
        lease.Token.ThrowIfCancellationRequested();
        return response;
    }

    public async Task<IBedrockEventStream> ConverseStreamAsync(
        LlmAwsCredentials aws,
        ConverseStreamRequest request,
        CancellationToken cancellationToken)
    {
        BedrockClientFactory.Lease<AmazonBedrockRuntimeClient> lease = factory.Runtime(aws, cancellationToken);
        ConverseStreamResponse? response = null;
        bool transferred = false;
        try
        {
            response = await lease.Client.ConverseStreamAsync(request, lease.Token);
            lease.Token.ThrowIfCancellationRequested();
            var stream = new EventStream(response, lease);
            transferred = true;
            return stream;
        }
        finally
        {
            if (!transferred)
            {
                try { if (response is not null) lease.DisposeResponse(response); }
                finally { lease.Dispose(); }
            }
        }
    }

    /// <summary>
    /// The exception a stream failure surfaces as: cancellation when the token is cancelled, otherwise the typed
    /// exception the SDK wrapped, so a stream error has the same type as the error Converse throws.
    /// </summary>
    internal static Exception Unwrap(BedrockRuntimeEventStreamException exception, CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested
            ? new OperationCanceledException(cancellationToken)
            : exception.InnerException as AmazonBedrockRuntimeException ?? (Exception)exception;

    private sealed class EventStream(ConverseStreamResponse response,
        BedrockClientFactory.Lease<AmazonBedrockRuntimeClient> lease) : IBedrockEventStream
    {
        private readonly LlmOperationLifetime _readers = new();
        public string? RequestId => response.ResponseMetadata?.RequestId;

        public async IAsyncEnumerable<IEventStreamEvent> ReadAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            using LlmOperationLifetime.Lease reader = _readers.Enter(cancellationToken);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(reader.Token, lease.Token);
            cancellationToken = linked.Token;
            // C# forbids yield inside try/catch, so each MoveNextAsync is guarded on its own.
            IAsyncEnumerator<IEventStreamEvent> events = response.Stream.GetAsyncEnumerator(cancellationToken);
            await using (events)
            {
                while (true)
                {
                    bool moved;
                    try
                    {
                        moved = await events.MoveNextAsync();
                    }
                    catch (BedrockRuntimeEventStreamException exception)
                    {
                        throw Unwrap(exception, cancellationToken);
                    }

                    if (!moved)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        yield break;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    yield return events.Current;
                }
            }
        }

        public ValueTask DisposeAsync() => new(_readers.RetireAsync(ReleaseAsync));

        private Task ReleaseAsync()
        {
            try { lease.DisposeResponse(response); }
            finally { lease.Dispose(); }
            return Task.CompletedTask;
        }
    }
}
