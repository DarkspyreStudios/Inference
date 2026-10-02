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

    private async Task<(GeminiApiLlmClient Client, LlmConnection Connection, GeminiCacheBindings.Admission Admission)>
        ResolveContextCacheAsync(string connectionId, CancellationToken token)
    {
        long sequence = _cacheBindings.BeginOperation();
        LlmConnection connection = EmbeddingRequestData.Snapshot(await ResolveConnectionAsync(connectionId, token).ConfigureAwait(false));
        ThrowIfOperationCanceled(token);
        if (ClientFor(connection.Transport) is not GeminiApiLlmClient client)
            throw new NotSupportedException("No explicit context cache adapter is implemented.");
        GeminiApiLlmClient.Validate(connection);
        _ = GeminiApiLlmClient.Key(connection);
        GeminiCacheBindings.Binding binding = _cacheBindings.Select(connectionId, connection, sequence);
        return (client, connection, _cacheBindings.Capture(binding, connection, sequence));
    }

    private void CheckContextCache(GeminiCacheBindings.Admission admission, CancellationToken token)
    { ThrowIfOperationCanceled(token); _cacheBindings.Check(admission); }

    private async Task<LlmContextCache> CreateContextCacheAsync(string connectionId, LlmContextCacheCreate request, CancellationToken token)
    {
        using var operation = _lifetime.Enter(token); token = operation.Token;
        request = GoogleCacheData.Snapshot(request, DateTimeOffset.UtcNow);
        var selected = await ResolveContextCacheAsync(connectionId, token).ConfigureAwait(false);
        LlmContextCache receipt = await selected.Client.CreateCacheAsync(selected.Connection, request, selected.Admission.Scope,
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
    }

    private async Task<LlmContextCache> GetContextCacheAsync(string connectionId, LlmExplicitCacheReference reference, CancellationToken token)
    {
        using var operation = _lifetime.Enter(token); token = operation.Token;
        ValidateCacheReference(reference);
        var selected = await ResolveContextCacheAsync(connectionId, token).ConfigureAwait(false);
        LlmContextCache receipt = await selected.Client.GetCacheAsync(selected.Connection, reference, selected.Admission.Scope,
            current => CheckContextCache(selected.Admission, current), token).ConfigureAwait(false);
        CheckContextCache(selected.Admission, token);
        _ = _cacheBindings.Admit(selected.Admission, receipt);
        return receipt;
    }

    private async Task<LlmContextCachePage> ListContextCachesAsync(string connectionId, LlmContextCacheList request, CancellationToken token)
    {
        using var operation = _lifetime.Enter(token); token = operation.Token;
        GoogleCacheData.ValidatePage(request);
        var selected = await ResolveContextCacheAsync(connectionId, token).ConfigureAwait(false);
        LlmContextCachePage page = await selected.Client.ListCachesAsync(selected.Connection, request, selected.Admission.Scope,
            current => CheckContextCache(selected.Admission, current), token).ConfigureAwait(false);
        CheckContextCache(selected.Admission, token);
        return page;
    }

    private async Task<GeminiCacheBindings.Admission> RequireContextCacheAsync(string connectionId, LlmConnection connection,
        GeminiApiLlmClient client, LlmExplicitCacheReference reference, long sequence, bool allowDeleted, CancellationToken token)
    {
        ValidateCacheReference(reference);
        GeminiCacheBindings.Binding binding = _cacheBindings.Select(connectionId, connection, sequence);
        GeminiCacheBindings.Admission admission = _cacheBindings.Capture(binding, connection, sequence);
        GeminiCacheBindings.Observation known = _cacheBindings.RequireKnown(admission, reference, allowDeleted);
        if (known.Epoch != admission.Epoch)
        {
            LlmContextCache checkedReceipt = await client.GetCacheAsync(connection, reference, admission.Scope,
                current => CheckContextCache(admission, current), token).ConfigureAwait(false);
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
        ValidateCacheReference(reference);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(expiry, DateTimeOffset.UtcNow);
        var selected = await ResolveContextCacheAsync(connectionId, token).ConfigureAwait(false);
        var admission = await RequireContextCacheAsync(connectionId, selected.Connection, selected.Client, reference, selected.Admission.Sequence, false, token).ConfigureAwait(false);
        LlmContextCache? observed = null;
        try
        {
            LlmContextCache receipt = await selected.Client.UpdateCacheExpiryAsync(selected.Connection, admission.Reference!, expiry, admission.Scope,
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
    }

    private async Task<LlmContextCacheDeleteResult> DeleteContextCacheAsync(string connectionId,
        LlmExplicitCacheReference reference, CancellationToken token)
    {
        using var operation = _lifetime.Enter(token); token = operation.Token;
        ValidateCacheReference(reference);
        var selected = await ResolveContextCacheAsync(connectionId, token).ConfigureAwait(false);
        var admission = await RequireContextCacheAsync(connectionId, selected.Connection, selected.Client, reference, selected.Admission.Sequence, true, token).ConfigureAwait(false);
        LlmContextCacheDeleteResult? observed = null;
        try
        {
            LlmContextCacheDeleteResult receipt = await selected.Client.DeleteCacheAsync(selected.Connection, admission.Reference!,
                candidate => { CheckContextCache(admission, token); return candidate == admission.Reference; },
                current => CheckContextCache(admission, current), token).ConfigureAwait(false);
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
    }

    private static void ValidateCacheReference(LlmExplicitCacheReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        _ = GeminiCacheReads.ResourceId(reference.ResourceName);
        _ = GeminiApiLlmClient.ParseModel(reference.ModelId);
    }

    private LlmConnection WithContextCache(LlmConnection connection, ILlmClient client) =>
        client is GeminiApiLlmClient ? connection with { CacheBindings = _cacheBindings } : connection;

    private void ValidateCacheEstimate(string connectionId, LlmConnection connection, ILlmClient client,
        LlmChatRequest request, long sequence)
    {
        if (client is not GeminiApiLlmClient || request.ExplicitCache is not { } reference) return;
        GeminiApiLlmClient.ValidateCachedSuffix(request);
        ValidateCacheReference(reference);
        GeminiCacheBindings.Binding binding = _cacheBindings.Select(connectionId, connection, sequence);
        _ = _cacheBindings.RequireKnown(_cacheBindings.Capture(binding, connection, sequence), reference, false);
        // A replaced selection is not revalidated for advisory accounting; occupancy stays unknown.
    }

    private async Task<(LlmConnection Connection, LlmChatRequest Request)> AdmitContextCacheAsync(string connectionId,
        LlmConnection connection, ILlmClient client, LlmChatRequest request, long sequence, CancellationToken token)
    {
        connection = WithContextCache(connection, client);
        if (client is not GeminiApiLlmClient gemini || request.ExplicitCache is not { } reference) return (connection, request);
        GeminiApiLlmClient.ValidateCachedSuffix(request);
        var admission = await RequireContextCacheAsync(connectionId, connection, gemini, reference, sequence, false, token).ConfigureAwait(false);
        return (connection with { CacheAdmission = admission }, request with { ExplicitCache = admission.Reference });
    }
}
