using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Diagnostics.CodeAnalysis;
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
    /// The stream's events. A recognized input overflow is a provider failure; other stream failures retain their
    /// typed AmazonBedrockRuntimeException. A cancelled token is OperationCanceledException.
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
        lease.ThrowIfCanceled();
        ConverseResponse response;
        try { response = await lease.Client.ConverseAsync(request, lease.Token); }
        catch (ValidationException error) when (LlmContextOverflow.BedrockValidation(error.Message))
        {
            throw ContextOverflow(error);
        }
        lease.ThrowIfCanceled();
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
            lease.ThrowIfCanceled();
            response = await lease.Client.ConverseStreamAsync(request, lease.Token);
            lease.ThrowIfCanceled();
            var stream = new EventStream(response, lease);
            transferred = true;
            return stream;
        }
        catch (Exception operationFailure)
        {
            Exception failure = Classify(operationFailure);
            try { if (response is not null) lease.DisposeResponse(response); }
            catch (Exception cleanup)
            {
                throw new AggregateException("Bedrock stream opening and response cleanup failed.", failure, cleanup);
            }
            ExceptionDispatchInfo.Throw(failure);
            throw;
        }
        finally
        {
            if (!transferred)
            {
                lease.Dispose();
            }
        }
    }

    /// <summary>
    /// Classifies the SDK's actual input overflow after cancellation, and unwraps recognized service,
    /// client and HTTP failures for the adapter's diagnostic boundary.
    /// </summary>
    internal static Exception Unwrap(BedrockRuntimeEventStreamException exception, CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested
            ? new OperationCanceledException(cancellationToken)
            : Classify(exception.InnerException is AmazonBedrockRuntimeException
                or Amazon.Runtime.AmazonClientException or HttpRequestException ? exception.InnerException : exception);

    private static Exception Classify(Exception error) => error is ValidationException validation
        && LlmContextOverflow.BedrockValidation(validation.Message) ? ContextOverflow(validation) : error;

    private static LlmProviderException ContextOverflow(ValidationException error) => new(
        "Bedrock rejected input that exceeds the model context window.", LlmTransport.AwsBedrockConverse,
        error.StatusCode, error.ErrorCode, error.RequestId, innerException: new OverflowEvidence(error),
        isContextOverflow: true);

    private sealed class OverflowEvidence(ValidationException original) : Exception("Bedrock reported an input context overflow.")
    {
        internal ValidationException Original { get; } = original;
    }

    private sealed class EventStream(ConverseStreamResponse response,
        BedrockClientFactory.Lease<AmazonBedrockRuntimeClient> lease) : IBedrockEventStream
    {
        private readonly LlmOperationLifetime _readers = new();
        private ConverseStreamResponse? _response = response;
        private BedrockClientFactory.Lease<AmazonBedrockRuntimeClient>? _lease = lease;
        public string? RequestId { get; } = response.ResponseMetadata?.RequestId;

        [SuppressMessage("Design", "CA1031", Justification = "The reader preserves the original operation and independently refused enumerator cleanup.")]
        [SuppressMessage("Usage", "CA2219", Justification = "Iterator retirement executes finally; original read and cleanup failures must also surface on early reader disposal.")]
        public async IAsyncEnumerable<IEventStreamEvent> ReadAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            using LlmOperationLifetime.Lease reader = _readers.Enter(cancellationToken);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(reader.Token, _lease!.Token);
            cancellationToken = linked.Token;
            ThrowIfReadCanceled(cancellationToken);
            // C# forbids yield inside try/catch, so each MoveNextAsync is guarded on its own.
            IAsyncEnumerator<IEventStreamEvent>? events = null;
            Exception? failure = null;
            try
            {
                events = _response!.Stream.GetAsyncEnumerator(cancellationToken);
                while (true)
                {
                    bool moved;
                    IEventStreamEvent? current = null;
                    try
                    {
                        ThrowIfReadCanceled(cancellationToken);
                        moved = await events.MoveNextAsync();
                        ThrowIfReadCanceled(cancellationToken);
                        if (moved) current = events.Current;
                    }
                    catch (BedrockRuntimeEventStreamException exception)
                    {
                        failure = Unwrap(exception, cancellationToken);
                        break;
                    }
                    catch (ValidationException error) when (LlmContextOverflow.BedrockValidation(error.Message))
                    {
                        failure = ContextOverflow(error);
                        break;
                    }
                    catch (Exception error)
                    {
                        failure = error;
                        break;
                    }
                    if (!moved) break;
                    yield return current!;
                }
            }
            finally
            {
                if (events is not null)
                    try { await events.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception cleanup)
                    {
                        Exception safe = BedrockLlmClient.CleanupFailure(cleanup, events);
                        if (failure is not null)
                            throw new AggregateException("Bedrock stream reading and enumerator cleanup failed.", failure, safe);
                        throw safe;
                    }
                if (failure is not null) ExceptionDispatchInfo.Throw(failure);
            }
        }

        public ValueTask DisposeAsync() => new(_readers.RetireAsync(ReleaseAsync));

        private void ThrowIfReadCanceled(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            _lease!.ThrowIfCanceled();
            try { _readers.ThrowIfStopping(); }
            catch (ObjectDisposedException) { throw new OperationCanceledException("Bedrock stream ownership is retiring.", token); }
        }

        private Task ReleaseAsync()
        {
            try { _lease!.DisposeResponse(_response!); }
            finally { _lease!.Dispose(); }
            _response = null;
            _lease = null;
            return Task.CompletedTask;
        }
    }
}
