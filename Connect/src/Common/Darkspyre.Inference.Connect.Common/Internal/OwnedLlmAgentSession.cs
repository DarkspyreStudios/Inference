using System.Diagnostics.CodeAnalysis;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Internal;

internal sealed class OwnedLlmAgentSession : ILlmAgentSession
{
    private ILlmAgentSession? _session;
    private readonly LlmOperationLifetime _lifetime = new();
    private readonly Lock _gate = new();
    private TaskCompletionSource? _retirement;
    private Action<OwnedLlmAgentSession>? _retired;

    internal OwnedLlmAgentSession(ILlmAgentSession session, Action<OwnedLlmAgentSession> retired)
    {
        _session = session;
        _retired = retired;
        ThreadId = session.ThreadId;
        SessionId = session.SessionId;
        CompactionResponsibility = session.CompactionResponsibility;
    }

    public string ThreadId { get; }
    public string SessionId { get; }
    public LlmCompactionResponsibility CompactionResponsibility { get; }

    public Task<LlmAgentCompactedEvent?> CompactNowAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync((session, token) => session.CompactNowAsync(token), cancellationToken);

    public Task<LlmAgentCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync((session, token) => session.GetCapabilitiesAsync(token), cancellationToken);

    public Task<LlmRequestAccounting> MeasureContextAsync(LlmAgentContext proposedContext,
        CancellationToken cancellationToken = default) =>
        InvokeAsync((session, token) => session.MeasureContextAsync(proposedContext, token), cancellationToken);

    public Task<LlmAgentContextStatus> GetContextStatusAsync(LlmAgentTurnRequest? prospectiveTurn = null,
        CancellationToken cancellationToken = default) =>
        InvokeAsync((session, token) => session.GetContextStatusAsync(prospectiveTurn, token), cancellationToken);

    public Task<LlmAgentContext> CaptureContextAsync(LlmAgentContextCaptureOptions? options = null,
        CancellationToken cancellationToken = default) =>
        InvokeAsync((session, token) => session.CaptureContextAsync(options, token), cancellationToken);

    public Task ReplaceContextAsync(LlmAgentContext context, long expectedRevision,
        CancellationToken cancellationToken = default) =>
        InvokeAsync((session, token) => session.ReplaceContextAsync(context, expectedRevision, token), cancellationToken);

    public IAsyncEnumerable<LlmAgentEvent> ContinueTurnAsync(LlmAgentTurnContinuation continuation,
        CancellationToken cancellationToken = default) =>
        StreamAsync((session, token) => session.ContinueTurnAsync(continuation, token), cancellationToken);

    public IAsyncEnumerable<LlmAgentEvent> RunTurnAsync(LlmAgentTurnRequest request,
        CancellationToken cancellationToken = default) =>
        StreamAsync((session, token) => session.RunTurnAsync(request, token), cancellationToken);

    public Task SteerAsync(string turnId, IReadOnlyList<LlmAgentContent> input,
        IReadOnlyList<LlmMessageAttachment> attachments, CancellationToken cancellationToken = default) =>
        InvokeAsync((session, token) => session.SteerAsync(turnId, input, attachments, token), cancellationToken);

    public Task InterruptAsync(string turnId, CancellationToken cancellationToken = default) =>
        InvokeAsync((session, token) => session.InterruptAsync(turnId, token), cancellationToken);

    public IAsyncEnumerable<LlmAgentEvent> ObserveAsync(CancellationToken cancellationToken = default) =>
        StreamAsync((session, token) => session.ObserveAsync(token), cancellationToken);

    public Task<IReadOnlyList<LlmAgentResource>> GetResourcesAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync((session, token) => session.GetResourcesAsync(token), cancellationToken);

    public Task<LlmAgentResourceOutput> ReadResourceOutputAsync(string resourceId, string? cursor,
        CancellationToken cancellationToken = default) =>
        InvokeAsync((session, token) => session.ReadResourceOutputAsync(resourceId, cursor, token), cancellationToken);

    public Task<LlmAgentResourceStopStatus> StopResourceAsync(string resourceId,
        CancellationToken cancellationToken = default) =>
        InvokeAsync((session, token) => session.StopResourceAsync(resourceId, token), cancellationToken);

    private async Task<T> InvokeAsync<T>(Func<ILlmAgentSession, CancellationToken, Task<T>> invoke, CancellationToken caller)
    {
        using LlmOperationLifetime.Lease operation = Enter(caller);
        T result = await invoke(_session!, operation.Token).ConfigureAwait(false);
        operation.Token.ThrowIfCancellationRequested();
        return result;
    }

    private async Task InvokeAsync(Func<ILlmAgentSession, CancellationToken, Task> invoke, CancellationToken caller)
    {
        using LlmOperationLifetime.Lease operation = Enter(caller);
        await invoke(_session!, operation.Token).ConfigureAwait(false);
        operation.Token.ThrowIfCancellationRequested();
    }

    private IAsyncEnumerable<LlmAgentEvent> StreamAsync(
        Func<ILlmAgentSession, CancellationToken, IAsyncEnumerable<LlmAgentEvent>> stream,
        CancellationToken caller) =>
        _lifetime.StreamAsync(token => stream(_session!, token), caller, Enter);

    private LlmOperationLifetime.Lease Enter(CancellationToken caller)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_retirement is not null, this);
            return _lifetime.Enter(caller);
        }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_retirement is not null) return new(_retirement.Task);
            _retirement = completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        _ = RetireAsync(completion);
        return new(completion.Task);
    }

    [SuppressMessage("Design", "CA1031", Justification = "The shared retirement completion preserves failures for the session and its owning facade.")]
    private async Task RetireAsync(TaskCompletionSource completion)
    {
        try
        {
            try
            {
                await _lifetime.RetireAsync(ReleaseAsync).ConfigureAwait(false);
                _retired?.Invoke(this);
            }
            finally { _retired = null; }
            completion.TrySetResult();
        }
        catch (Exception error) { completion.TrySetException(error); }
    }

    private async Task ReleaseAsync()
    {
        await _session!.DisposeAsync().ConfigureAwait(false);
        _session = null;
    }
}
