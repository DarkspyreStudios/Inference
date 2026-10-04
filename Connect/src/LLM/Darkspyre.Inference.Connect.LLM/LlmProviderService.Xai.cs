using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM;

public sealed partial class LlmProviderService
{
    internal bool SupportsXaiSnapshot
    {
        get
        {
            using var operation = _lifetime.Enter(CancellationToken.None);
            return _clients.TryGetValue(LlmTransport.XaiResponses, out var client) && HasFileRoute(client);
        }
    }

    internal void ValidateXaiAccess(LlmConnectionAccess access)
    {
        using var operation = _lifetime.Enter(CancellationToken.None);
        if (!ReferenceEquals(ConnectionAccess, access))
            throw new ArgumentException("The xAI binding and facade must borrow the same connection access.", nameof(access));
    }

    internal void ValidateXaiSnapshotRequest(LlmConnection snapshot, Action<XaiResponsesLlmClient> validate)
    {
        using var operation = _lifetime.Enter(CancellationToken.None);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(validate);
        ThrowIfOperationCanceled(operation.Token);
        XaiRequestHeaders.ValidateEndpoint(snapshot);
        if (ClientFor(snapshot.Transport) is not XaiResponsesLlmClient client)
            throw new NotSupportedException("The selected facade has no cross-family xAI route.");
        ThrowIfOperationCanceled(operation.Token);
        validate(client);
        ThrowIfOperationCanceled(operation.Token);
    }

    internal async Task<IReadOnlyList<LlmModel>> GetXaiCatalogSnapshotAsync(string connectionId,
        LlmConnection snapshot, ModelCatalogFamily family, Action callerFence, CancellationToken cancellationToken)
    {
        using var operation = _lifetime.Enter(cancellationToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.Version);
        ArgumentNullException.ThrowIfNull(callerFence);
        Fence();
        if (snapshot.Transport != LlmTransport.XaiResponses)
            throw new ArgumentException("A family catalog requires the selected xAI transport.", nameof(snapshot));
        ILlmClient client = ClientFor(snapshot.Transport);
        if (client is not ILlmModelCatalogClient)
            throw new NotSupportedException("The selected facade has no cross-family xAI catalog.");
        Fence();
        // Only the waiter belongs to this facade. The cache admits and owns shared producers.
        IReadOnlyList<LlmModel> models = await _modelLists.GetAsync(connectionId,
            new(snapshot.Version, snapshot.ResolvedContinuationScope), client, snapshot, family, operation.Token).ConfigureAwait(false);
        Fence();
        return models;

        void Fence() { ThrowIfOperationCanceled(operation.Token); callerFence(); ThrowIfOperationCanceled(operation.Token); }
    }

    internal async Task<T> RunXaiSnapshotAsync<T>(LlmConnection snapshot, Action outerFence,
        Func<HttpClient, LlmConnection, Action, CancellationToken, Task<T>> dispatch, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(outerFence);
        ArgumentNullException.ThrowIfNull(dispatch);
        using var operation = _lifetime.Enter(token);
        Fence();
        XaiFileRequestData.Connection(snapshot);
        Fence();
        if (ClientFor(snapshot.Transport) is not XaiResponsesLlmClient client)
            throw new NotSupportedException("The selected facade has no cross-family xAI route.");
        using var consumption = SdkHttpConsumption.Enter(client.SharedHttp, operation.Token);
        void CombinedFence() { Fence(); consumption.Fence(); }
        CombinedFence();
        T result = await dispatch(client.SharedHttp, snapshot, CombinedFence, consumption.Token).ConfigureAwait(false);
        CombinedFence();
        return consumption.Publish(result);

        void Fence() { ThrowIfOperationCanceled(operation.Token); outerFence(); ThrowIfOperationCanceled(operation.Token); }
    }

    internal Task<T> RunXaiFilesSnapshotAsync<T>(LlmConnection snapshot, Action outerFence,
        Func<XaiFileClient, LlmConnection, Action, CancellationToken, Task<T>> dispatch, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        return RunXaiSnapshotAsync(snapshot, outerFence, (_, selected, fence, current) =>
        {
            XaiFileClient files = _xaiFiles ?? throw new NotSupportedException("The selected facade has no Files resource route.");
            fence();
            return dispatch(files, selected, fence, current);
        }, token);
    }

}
