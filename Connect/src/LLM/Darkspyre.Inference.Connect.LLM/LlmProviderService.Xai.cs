using Darkspyre.Inference.Common;
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
        T result;
        try { result = await dispatch(client.SharedHttp, snapshot, CombinedFence, consumption.Token).ConfigureAwait(false); }
        catch (Exception error) { _cleanupOutcomes.Record(error); throw; }
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

    internal Task<T> RunXaiMediaSnapshotAsync<T>(LlmConnection snapshot, Action outerFence,
        Func<XaiMediaDownloadClient, Action, CancellationToken, Task<T>> dispatch, CancellationToken token)
        => RunXaiSnapshotAsync(snapshot, outerFence, (_, selected, fence, current) =>
        {
            var client = (XaiResponsesLlmClient)ClientFor(selected.Transport);
            fence();
            return dispatch(client.MediaDownloads, fence, current);
        }, token);

    internal Task<XaiHttpDownload> OpenXaiFileDownloadSnapshotAsync(LlmConnection snapshot, string fileId,
        TimeSpan timeout, Action outerFence, CancellationToken token, Action<HttpRequestMessage>? configureHeaders = null)
        => OpenXaiDownloadSnapshotAsync(snapshot, outerFence, (_, fence, current) =>
        {
            XaiFileClient files = _xaiFiles ?? throw new NotSupportedException("The selected facade has no Files resource route.");
            return files.OpenDownloadAsync(snapshot, fileId, timeout, fence, current, configureHeaders);
        }, token);

    internal Task<XaiHttpDownload> OpenXaiMediaDownloadSnapshotAsync(LlmConnection snapshot, string url,
        TimeSpan timeout, Action outerFence, CancellationToken token)
        => OpenXaiDownloadSnapshotAsync(snapshot, outerFence, (client, fence, current) =>
            client.MediaDownloads.OpenAsync(url, timeout, fence, current), token);

    private async Task<XaiHttpDownload> OpenXaiDownloadSnapshotAsync(LlmConnection snapshot, Action outerFence,
        Func<XaiResponsesLlmClient, Action, CancellationToken, Task<XaiHttpDownload>> open, CancellationToken token)
    {
        LlmOperationLifetime.Lease operation = _lifetime.Enter(token);
        SdkHttpConsumption.Operation? consumption = null;
        XaiHttpDownload? download = null;
        bool retained = false;
        try
        {
            Fence();
            XaiFileRequestData.Connection(snapshot);
            Fence();
            if (ClientFor(snapshot.Transport) is not XaiResponsesLlmClient client)
                throw new NotSupportedException("The selected facade has no cross-family xAI route.");
            consumption = SdkHttpConsumption.Enter(client.SharedHttp, operation.Token);
            void CombinedFence() { Fence(); consumption.Fence(); }
            CombinedFence();
            download = await open(client, CombinedFence, consumption.Token).ConfigureAwait(false);
            download.RetainOwner(() => { consumption.Dispose(); operation.Dispose(); }, _cleanupOutcomes.Record);
            retained = true;
            CombinedFence();
            return download;
        }
        catch (Exception original)
        {
            _cleanupOutcomes.Record(original);
            if (download is not null)
                try { await download.CloseAsync().ConfigureAwait(false); }
                catch (XaiHttpDownload.CloseFailure cleanup) { throw new AggregateException([original, .. cleanup.InnerExceptions]); }
                catch (Exception cleanup) { throw new AggregateException(original, cleanup); }
                finally { download.ReleaseAfterDrain(); }
            throw;
        }
        finally
        {
            if (!retained) { consumption?.Dispose(); operation.Dispose(); }
        }

        void Fence() { ThrowIfOperationCanceled(operation.Token); outerFence(); ThrowIfOperationCanceled(operation.Token); }
    }
}
