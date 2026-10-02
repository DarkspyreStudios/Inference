using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM;

public sealed partial class LlmProviderService
{
    private sealed class BoundContextCache(LlmProviderService owner, string connectionId) : ILlmContextCacheClient
    {
        public Task<LlmContextCache> CreateAsync(LlmContextCacheCreate request, CancellationToken cancellationToken = default) =>
            owner.CreateContextCacheAsync(connectionId, request, cancellationToken);
        public Task<LlmContextCache> GetAsync(LlmExplicitCacheReference reference, CancellationToken cancellationToken = default) =>
            owner.GetContextCacheAsync(connectionId, reference, cancellationToken);
        public Task<LlmContextCachePage> ListAsync(LlmContextCacheList request, CancellationToken cancellationToken = default) =>
            owner.ListContextCachesAsync(connectionId, request, cancellationToken);
        public Task<LlmContextCache> UpdateExpiryAsync(LlmExplicitCacheReference reference, DateTimeOffset expiresAt, CancellationToken cancellationToken = default) =>
            owner.UpdateContextCacheAsync(connectionId, reference, expiresAt, cancellationToken);
        public Task<LlmContextCacheDeleteResult> DeleteAsync(LlmExplicitCacheReference reference, CancellationToken cancellationToken = default) =>
            owner.DeleteContextCacheAsync(connectionId, reference, cancellationToken);
    }

    private async Task<(ILlmContextCacheOperation Client, LlmConnection Connection, GeminiCacheBindings.Admission Admission)>
        ResolveContextCacheAsync(string connectionId, CancellationToken token,
            LlmExplicitCacheReference? reference = null, LlmContextCacheCreate? create = null, bool requireKnown = false, bool allowDeleted = false)
    {
        long sequence = _cacheBindings.BeginOperation();
        LlmConnection connection = EmbeddingRequestData.Snapshot(await ResolveConnectionAsync(connectionId, token).ConfigureAwait(false));
        ThrowIfOperationCanceled(token);
        if (ClientFor(connection.Transport) is not ILlmContextCacheAdapter adapter)
            throw new NotSupportedException("No explicit context cache adapter is implemented.");
        adapter.ValidateCacheConnection(connection);
        if (reference is not null) adapter.ValidateCacheReference(connection, reference);
        if (requireKnown) _cacheBindings.RequireObserved(connectionId, reference!, allowDeleted);
        if (create is not null) adapter.ValidateCacheCreate(connection, create);
        ILlmContextCacheOperation client = await adapter.PrepareCacheOperationAsync(connection, ThrowIfOperationCanceled, token).ConfigureAwait(false);
        ThrowIfOperationCanceled(token);
        connection = client.Connection with { CacheOperation = client };
        GeminiCacheBindings.Binding binding = _cacheBindings.Select(connectionId, connection, sequence);
        return (client, connection, _cacheBindings.Capture(binding, connection, sequence));
    }

    private void CheckContextCache(GeminiCacheBindings.Admission admission, CancellationToken token)
    { ThrowIfOperationCanceled(token); _cacheBindings.Check(admission); }

    private async Task<T> CaptureCacheCleanupAsync<T>(Func<Task<T>> action)
    {
        try { return await action().ConfigureAwait(false); }
        catch (Exception error) { _cacheBindings.RecordCleanupFailure(error); throw; }
    }

    private async Task<LlmContextCache> CreateContextCacheAsync(string connectionId, LlmContextCacheCreate request, CancellationToken token)
    {
        using var operation = _lifetime.Enter(token); token = operation.Token;
        return await CaptureCacheCleanupAsync(async () =>
        {
            request = GoogleCacheData.Snapshot(request, DateTimeOffset.UtcNow);
            var selected = await ResolveContextCacheAsync(connectionId, token, create: request).ConfigureAwait(false);
            LlmContextCache receipt = await selected.Client.CreateAsync(request, selected.Admission,
                current => CheckContextCache(selected.Admission, current), token).ConfigureAwait(false);
            try
            {
                CheckContextCache(selected.Admission, token);
                _ = _cacheBindings.Admit(selected.Admission, receipt);
                return receipt;
            }
            catch (OperationCanceledException error)
            { throw new LlmContextCacheCreateCancelledException(request.Correlation, token, error, observedReceipt: receipt); }
            catch (InvalidOperationException error)
            { throw new LlmContextCacheCreateUncertainException(request.Correlation, error, observedReceipt: receipt); }
        }).ConfigureAwait(false);
    }

    private async Task<LlmContextCache> GetContextCacheAsync(string connectionId, LlmExplicitCacheReference reference, CancellationToken token)
    {
        using var operation = _lifetime.Enter(token); token = operation.Token;
        return await CaptureCacheCleanupAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(reference);
            var selected = await ResolveContextCacheAsync(connectionId, token, reference).ConfigureAwait(false);
            LlmContextCache receipt = await selected.Client.GetAsync(reference, selected.Admission,
                current => CheckContextCache(selected.Admission, current), token).ConfigureAwait(false);
            CheckContextCache(selected.Admission, token);
            _ = _cacheBindings.Admit(selected.Admission, receipt);
            return receipt;
        }).ConfigureAwait(false);
    }

    private async Task<LlmContextCachePage> ListContextCachesAsync(string connectionId, LlmContextCacheList request, CancellationToken token)
    {
        using var operation = _lifetime.Enter(token); token = operation.Token;
        return await CaptureCacheCleanupAsync(async () =>
        {
            GoogleCacheData.ValidatePage(request);
            var selected = await ResolveContextCacheAsync(connectionId, token).ConfigureAwait(false);
            LlmContextCachePage page = await selected.Client.ListAsync(request, selected.Admission,
                current => CheckContextCache(selected.Admission, current), token).ConfigureAwait(false);
            CheckContextCache(selected.Admission, token);
            return page;
        }).ConfigureAwait(false);
    }

    private async Task<GeminiCacheBindings.Admission> RequireContextCacheAsync(string connectionId, LlmConnection connection,
        ILlmContextCacheOperation client, LlmExplicitCacheReference reference, long sequence, bool allowDeleted, CancellationToken token)
    {
        ((ILlmContextCacheAdapter)ClientFor(connection.Transport)).ValidateCacheReference(connection, reference);
        GeminiCacheBindings.Binding binding = _cacheBindings.Select(connectionId, connection, sequence);
        GeminiCacheBindings.Admission admission = _cacheBindings.Capture(binding, connection, sequence);
        GeminiCacheBindings.Observation known = _cacheBindings.RequireKnown(admission, reference, allowDeleted);
        if (known.Epoch != admission.Epoch)
        {
            LlmContextCache checkedReceipt = await CaptureCacheCleanupAsync(() => client.GetAsync(reference, admission,
                current => CheckContextCache(admission, current), token)).ConfigureAwait(false);
            CheckContextCache(admission, token);
            admission = _cacheBindings.Admit(admission, checkedReceipt);
        }
        else admission = admission with { Reference = known.Receipt.Reference, Proof = known, AllowDeleted = allowDeleted };
        CheckContextCache(admission, token);
        return admission;
    }

    private async Task<LlmContextCache> UpdateContextCacheAsync(string connectionId, LlmExplicitCacheReference reference,
        DateTimeOffset expiry, CancellationToken token)
    {
        using var operation = _lifetime.Enter(token); token = operation.Token;
        return await CaptureCacheCleanupAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(reference);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(expiry, DateTimeOffset.UtcNow);
            var selected = await ResolveContextCacheAsync(connectionId, token, reference, requireKnown: true).ConfigureAwait(false);
            var admission = await RequireContextCacheAsync(connectionId, selected.Connection, selected.Client, reference, selected.Admission.Sequence, false, token).ConfigureAwait(false);
            LlmContextCache? observed = null;
            try
            {
                LlmContextCache receipt = await selected.Client.UpdateExpiryAsync(admission.Reference!, expiry, admission,
                    current => CheckContextCache(admission, current), token).ConfigureAwait(false);
                observed = receipt;
                CheckContextCache(admission, token);
                admission = _cacheBindings.ChangedResource(admission, deleted: false, uncertain: true);
                _ = _cacheBindings.Admit(admission, receipt, afterMutation: true);
                return receipt;
            }
            catch (Exception error) when (error is LlmContextCacheMutationCancelledException or LlmContextCacheMutationUncertainException)
            { _ = _cacheBindings.ChangedResource(admission, deleted: false, uncertain: true); throw; }
            catch (Exception error) when (observed is not null)
            {
                _ = _cacheBindings.ChangedResource(admission, deleted: false, uncertain: true);
                if (error is OperationCanceledException)
                    throw new LlmContextCacheMutationCancelledException(LlmContextCacheMutation.UpdateExpiry, admission.Reference!, token,
                        error, observedUpdate: observed);
                throw new LlmContextCacheMutationUncertainException(LlmContextCacheMutation.UpdateExpiry, admission.Reference!,
                    error, observedUpdate: observed);
            }
        }).ConfigureAwait(false);
    }

    private async Task<LlmContextCacheDeleteResult> DeleteContextCacheAsync(string connectionId,
        LlmExplicitCacheReference reference, CancellationToken token)
    {
        using var operation = _lifetime.Enter(token); token = operation.Token;
        return await CaptureCacheCleanupAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(reference);
            var selected = await ResolveContextCacheAsync(connectionId, token, reference, requireKnown: true, allowDeleted: true).ConfigureAwait(false);
            var admission = await RequireContextCacheAsync(connectionId, selected.Connection, selected.Client, reference, selected.Admission.Sequence, true, token).ConfigureAwait(false);
            LlmContextCacheDeleteResult? observed = null;
            try
            {
                LlmContextCacheDeleteResult receipt = await selected.Client.DeleteAsync(admission.Reference!,
                    candidate => { CheckContextCache(admission, token); return candidate == admission.Reference; },
                    admission, current => CheckContextCache(admission, current), token).ConfigureAwait(false);
                observed = receipt;
                CheckContextCache(admission, token);
                _ = _cacheBindings.ChangedResource(admission, deleted: true, uncertain: false);
                return receipt;
            }
            catch (Exception error) when (error is LlmContextCacheMutationCancelledException or LlmContextCacheMutationUncertainException)
            { _ = _cacheBindings.ChangedResource(admission, deleted: false, uncertain: true); throw; }
            catch (Exception error) when (observed is not null)
            {
                _ = _cacheBindings.ChangedResource(admission, deleted: false, uncertain: true);
                if (error is OperationCanceledException)
                    throw new LlmContextCacheMutationCancelledException(LlmContextCacheMutation.Delete, admission.Reference!, token,
                        error, observedDelete: observed);
                throw new LlmContextCacheMutationUncertainException(LlmContextCacheMutation.Delete, admission.Reference!,
                    error, observedDelete: observed);
            }
        }).ConfigureAwait(false);
    }

    private LlmConnection WithContextCache(LlmConnection connection, ILlmClient client) =>
        client is ILlmContextCacheAdapter ? connection with { CacheBindings = _cacheBindings } : connection;

    private void ValidateCacheEstimate(string connectionId, LlmConnection connection, ILlmClient client,
        LlmChatRequest request, long sequence)
    {
        if (client is not ILlmContextCacheAdapter adapter || request.ExplicitCache is not { } reference) return;
        adapter.ValidateCachedSuffix(request);
        adapter.ValidateCacheReference(connection, reference);
        _cacheBindings.ValidateEstimate(connectionId, connection, reference, sequence);
        // A replaced selection is not revalidated for advisory accounting; occupancy stays unknown.
    }

    private async Task<(LlmConnection Connection, LlmChatRequest Request)> AdmitContextCacheAsync(string connectionId,
        LlmConnection connection, ILlmClient client, LlmChatRequest request, long sequence, CancellationToken token)
    {
        connection = WithContextCache(connection, client);
        if (client is not ILlmContextCacheAdapter adapter || request.ExplicitCache is not { } reference) return (connection, request);
        adapter.ValidateCachedSuffix(request);
        adapter.ValidateCacheReference(connection, reference);
        adapter.ValidateCacheConnection(connection);
        _cacheBindings.RequireObserved(connectionId, reference, false);
        ILlmContextCacheOperation selected = await adapter.PrepareCacheOperationAsync(connection, ThrowIfOperationCanceled, token).ConfigureAwait(false);
        ThrowIfOperationCanceled(token);
        connection = selected.Connection with { CacheBindings = _cacheBindings, CacheOperation = selected };
        var admission = await RequireContextCacheAsync(connectionId, connection, selected, reference, sequence, false, token).ConfigureAwait(false);
        return (connection with { CacheAdmission = admission }, request with { ExplicitCache = admission.Reference });
    }
}
