using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.Resources.Internal;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.Resources;

/// <summary>Explicit batch and deferred facade over the borrowed LLM connection and HTTP owners.</summary>
public sealed class LlmResourceProviderService : ILlmBatchClient, IDisposable, IAsyncDisposable
{
    private LlmConnectionAccess? _connectionAccess;
    private LlmProviderService? _providerService;
    private readonly LlmOperationLifetime _lifetime;
    private readonly LlmCleanupOutcomes _cleanup = new();

    /// <summary>Borrows the existing connection access owner without acquiring credentials.</summary>
    public LlmResourceProviderService(LlmConnectionAccess connectionAccess, LlmProviderService? providerService = null)
    {
        ArgumentNullException.ThrowIfNull(connectionAccess);
        providerService?.ValidateXaiAccess(connectionAccess);
        _connectionAccess = connectionAccess;
        _providerService = providerService;
        _lifetime = new();
    }

    public Task<LlmBatch> CreateAsync(string connectionId, string name, CancellationToken cancellationToken = default)
        => RunAsync(connectionId, LlmResourceOperation.CreateBatch, null, null, name,
            (protocol, token) => protocol.CreateAsync(name, token), cancellationToken);

    public Task AddRequestsAsync(string connectionId, LlmBatch batch, IReadOnlyList<LlmBatchRequest> requests,
        IInferenceResourceAccess? resources = null, CancellationToken cancellationToken = default)
        => StageAsync(connectionId, LlmResourceOperation.AddBatchRequests, batch, null, requests, resources, cancellationToken);

    public Task<LlmBatch> GetAsync(string connectionId, LlmBatch batch, CancellationToken cancellationToken = default)
        => RunAsync(connectionId, LlmResourceOperation.GetBatch, batch, null, null,
            (protocol, token) => protocol.GetAsync(batch.Id, token), cancellationToken);

    public Task<LlmBatchPage> ListAsync(string connectionId, LlmResourcePageRequest page, CancellationToken cancellationToken = default)
        => RunAsync(connectionId, LlmResourceOperation.ListBatches, null, page, null,
            (protocol, token) => protocol.ListAsync(token), cancellationToken);

    public Task<LlmBatchRequestPage> ListRequestsAsync(string connectionId, LlmBatch batch,
        LlmResourcePageRequest page, CancellationToken cancellationToken = default)
        => RunAsync(connectionId, LlmResourceOperation.ListBatchRequests, batch, page, null,
            async (protocol, token) =>
            {
                await protocol.AuthorizeAsync(batch.Id, token).ConfigureAwait(false);
                return await protocol.ListRequestsAsync(batch.Id, token).ConfigureAwait(false);
            }, cancellationToken);

    public Task<LlmBatchResultPage> GetResultsAsync(string connectionId, LlmBatch batch,
        LlmResourcePageRequest page, LlmMediaMaterializationOptions materialization,
        IInferenceResourceAccess? resources = null, CancellationToken cancellationToken = default)
        => Task.FromException<LlmBatchResultPage>(Unavailable(cancellationToken));

    public Task<LlmBatch> CancelAsync(string connectionId, LlmBatch batch, CancellationToken cancellationToken = default)
        => RunAsync(connectionId, LlmResourceOperation.CancelBatch, batch, null, null,
            async (protocol, token) =>
            {
                await protocol.AuthorizeAsync(batch.Id, token).ConfigureAwait(false);
                return await protocol.CancelAsync(batch.Id, token).ConfigureAwait(false);
            }, cancellationToken);

    [SuppressMessage("Design", "CA1031", Justification = "The admitted deferred boundary preserves separately observed uploads, dispatch and actual cleanup outcomes.")]
    public async Task<LlmDeferredCompletion> SubmitDeferredAsync(string connectionId, LlmChatRequest request,
        IInferenceResourceAccess? resources = null, CancellationToken cancellationToken = default)
    {
        using LlmOperationLifetime.Lease operation = _lifetime.Enter(cancellationToken);
        CancellationToken token = operation.Token;
        void Fence()
        {
            token.ThrowIfCancellationRequested();
            try { _lifetime.ThrowIfStopping(); }
            catch (ObjectDisposedException error)
            { throw new OperationCanceledException("The resource facade is stopping.", error, token); }
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentNullException.ThrowIfNull(request);
        if (resources is not null && request.Resources is not null && !ReferenceEquals(resources, request.Resources))
            throw new ArgumentException("The request and operation must borrow the same resource access.", nameof(resources));
        Fence();
        LlmChatRequest entered = ClientTools.Snapshot(request) with
        {
            Resources = resources ?? request.Resources,
            HostedTools = HostedTools.Snapshot(request.HostedTools)
        };
        Fence();
        LlmProviderService? core = _providerService;
        if (core is null || !core.SupportsXaiSnapshot)
            throw new NotSupportedException("The resource facade requires an implemented xAI LLM core route.");
        Fence();
        var observation = new XaiResourceHttp.Observation(LlmResourceOperation.SubmitDeferred);
        try
        {
            LlmConnection snapshot = await _connectionAccess!.AcquireAsync(connectionId, selected =>
            {
                Fence();
                core.ValidateXaiSnapshotRequest(selected, client =>
                {
                    var codec = new XaiDeferredChatCodec(client.SharedHttp);
                    codec.ValidateSubmissionRequest(selected, entered, codec.GetCapabilities(selected, entered.ModelId), entered.Resources);
                    Fence();
                    // This sealed private codec prepares immediately without I/O. Validate native history before refresh/upload.
                    LlmConnection prepared = codec.PrepareAsync(selected, token).GetAwaiter().GetResult();
                    _ = ContinuationStateProjection.Project(entered, prepared);
                    Fence();
                });
                Fence();
            }, token).ConfigureAwait(false);
            Fence();
            LlmDeferredCompletion result = await core.RunXaiSnapshotAsync(snapshot, Fence,
                (http, selected, ownerFence, innerToken) => entered.Resources is null
                    ? new XaiDeferredProtocol(http, selected, connectionId, observation, ownerFence)
                        .SubmitAsync(entered, null, innerToken)
                    : core.RunXaiFilesSnapshotAsync(selected, ownerFence, (files, current, filesFence, filesToken) =>
                        new XaiDeferredProtocol(http, current, connectionId, observation, filesFence)
                            .SubmitAsync(entered, files, filesToken), innerToken), token).ConfigureAwait(false);
            Fence();
            return result;
        }
        catch (Exception error)
        {
            Exception reported = XaiResourceHttp.OperationFailure(error, observation);
            _cleanup.Record(reported);
            throw reported;
        }
    }

    [SuppressMessage("Design", "CA1031", Justification = "Consume-once retrieval retains actual response, partial commit and independent cleanup facts without retry.")]
    public async Task<LlmDeferredResult> GetDeferredAsync(string connectionId, LlmDeferredCompletion completion,
        IInferenceResourceAccess? resources = null, CancellationToken cancellationToken = default)
    {
        using LlmOperationLifetime.Lease operation = _lifetime.Enter(cancellationToken);
        CancellationToken token = operation.Token;
        void Fence()
        {
            token.ThrowIfCancellationRequested();
            try { _lifetime.ThrowIfStopping(); }
            catch (ObjectDisposedException error)
            { throw new OperationCanceledException("The resource facade is stopping.", error, token); }
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        XaiDeferredProtocol.ValidateCompletion(completion);
        LlmDeferredCompletion entered = completion with { Scope = completion.Scope with { } };
        Fence();
        LlmProviderService? core = _providerService;
        if (core is null || !core.SupportsXaiSnapshot)
            throw new NotSupportedException("The resource facade requires an implemented xAI LLM core route.");
        Fence();
        var observation = new XaiResourceHttp.Observation(LlmResourceOperation.GetDeferred) { SelectedDeferred = entered };
        try
        {
            LlmConnection snapshot = await _connectionAccess!.AcquireAsync(connectionId, selected =>
            {
                Fence();
                core.ValidateXaiSnapshotRequest(selected, _ => XaiDeferredProtocol.ValidateConnection(selected, entered));
                Fence();
            }, token).ConfigureAwait(false);
            Fence();
            LlmDeferredResult result = await core.RunXaiSnapshotAsync(snapshot, Fence,
                (http, selected, ownerFence, innerToken) => core.RunXaiFilesSnapshotAsync(selected, ownerFence,
                    (files, current, filesFence, filesToken) => new XaiDeferredProtocol(http, current, connectionId, observation, filesFence)
                        .GetAsync(entered, files, resources, filesToken), innerToken), token).ConfigureAwait(false);
            Fence();
            return result;
        }
        catch (Exception error)
        {
            Exception reported = XaiResourceHttp.OperationFailure(error, observation);
            _cleanup.Record(reported);
            throw reported;
        }
    }

    public async Task<LlmBatch> CreateFromRequestsAsync(string connectionId, string name,
        IReadOnlyList<LlmBatchRequest> requests, IInferenceResourceAccess? resources = null,
        CancellationToken cancellationToken = default)
        => await StageAsync(connectionId, LlmResourceOperation.CreateBatchFromRequests, null, name,
            requests, resources, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Batch creation returned no receipt.");

    [SuppressMessage("Design", "CA1031", Justification = "The admitted staging boundary retains actual uploads, batch mutations and each independent cleanup failure.")]
    private async Task<LlmBatch?> StageAsync(string connectionId, LlmResourceOperation kind, LlmBatch? batch,
        string? name, IReadOnlyList<LlmBatchRequest> requests, IInferenceResourceAccess? resources,
        CancellationToken cancellationToken)
    {
        using LlmOperationLifetime.Lease operation = _lifetime.Enter(cancellationToken);
        CancellationToken token = operation.Token;
        void Fence()
        {
            token.ThrowIfCancellationRequested();
            try { _lifetime.ThrowIfStopping(); }
            catch (ObjectDisposedException error)
            { throw new OperationCanceledException("The resource facade is stopping.", error, token); }
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        if (kind == LlmResourceOperation.AddBatchRequests)
        {
            ArgumentNullException.ThrowIfNull(batch);
            XaiBatchProtocol.ValidateBatch(batch);
            batch = batch with { Scope = batch.Scope with { }, Counts = batch.Counts with { } };
        }
        else ArgumentNullException.ThrowIfNull(name);
        Fence();
        IReadOnlyList<LlmBatchRequest> entered = XaiBatchRequestPreparation.Snapshot(requests, resources);
        Fence();
        LlmProviderService? core = _providerService;
        if (core is null || !core.SupportsXaiSnapshot)
            throw new NotSupportedException("The resource facade requires an implemented xAI LLM core route.");
        var observation = new XaiResourceHttp.Observation(kind, batch);
        try
        {
            LlmConnection snapshot = await _connectionAccess!.AcquireAsync(connectionId, selected =>
            {
                Fence();
                XaiBatchProtocol.ValidateConnection(selected, batch);
                core.ValidateXaiSnapshotRequest(selected, client =>
                    _ = XaiBatchRequestPreparation.Project(selected, entered, client, resources));
                Fence();
            }, token).ConfigureAwait(false);
            Fence();
            LlmBatch? result = await core.RunXaiSnapshotAsync(snapshot, Fence, (http, selected, ownerFence, innerToken) =>
                core.RunXaiFilesSnapshotAsync(selected, ownerFence, async (files, current, filesFence, filesToken) =>
                {
                    var preparation = new XaiBatchRequestPreparation(entered, resources, observation, filesFence, filesToken);
                    var errors = new List<Exception>();
                    LlmBatch? created = null;
                    try
                    {
                        filesFence();
                        IReadOnlyList<LlmBatchRequest>? projected = null;
                        core.ValidateXaiSnapshotRequest(current, client =>
                            projected = XaiBatchRequestPreparation.Project(current, entered, client, resources));
                        filesFence();
                        var protocol = new XaiBatchProtocol(http, current, connectionId, null, observation, filesFence, preparation.Secrets);
                        if (batch is not null) await protocol.AuthorizeAsync(batch.Id, filesToken).ConfigureAwait(false);
                        filesFence();
                        await preparation.PrepareAsync(current, connectionId, projected!, files,
                            callback => core.ValidateXaiSnapshotRequest(current, callback), _cleanup.Record).ConfigureAwait(false);
                        filesFence();
                        if (batch is not null)
                            await protocol.AddRequestsAsync(batch.Id, preparation.BuildInlineBody(), filesToken).ConfigureAwait(false);
                        else if (preparation.InlineUtf8Bytes <= XaiBatchRequestPreparation.MaximumRequestBytes)
                        {
                            var body = preparation.BuildInlineBody();
                            filesFence();
                            created = await protocol.CreateAsync(name!, filesToken).ConfigureAwait(false);
                            filesFence();
                            await protocol.AddRequestsAsync(created.Id, body, filesToken).ConfigureAwait(false);
                        }
                        else
                        {
                            Stream jsonl = preparation.CreateJsonl();
                            var scope = new Darkspyre.Inference.Common.Media.InferenceResourceScope(
                                ProviderHttp.BuildUri(current, "files").AbsoluteUri, connectionId);
                            var observer = preparation.UploadObserver(scope, null, null, true);
                            LlmProviderFile uploaded = await files.UploadAsync(current, jsonl, "batch.jsonl", null,
                                filesFence, filesToken, observer.OnDispatched, observer.OnObserved, preparation.Secrets).ConfigureAwait(false);
                            filesFence();
                            created = await protocol.CreateFromFileAsync(name!, uploaded.Id, filesToken).ConfigureAwait(false);
                        }
                        filesFence();
                    }
                    catch (Exception error) { errors.Add(XaiResourceHttp.SafeFailure(error, observation, forceSafe: true)); }
                    finally
                    {
                        try { await preparation.DisposeAsync().ConfigureAwait(false); }
                        catch (Exception error) { errors.Add(XaiResourceHttp.SafeFailure(error, observation, forceSafe: true)); }
                    }
                    if (errors.Count == 1) ExceptionDispatchInfo.Throw(errors[0]);
                    if (errors.Count > 1) throw XaiResourceHttp.CombineFailures(errors);
                    filesFence();
                    return created;
                }, innerToken), token).ConfigureAwait(false);
            Fence();
            return result;
        }
        catch (Exception error)
        {
            Exception reported = XaiResourceHttp.OperationFailure(error, observation);
            _cleanup.Record(reported);
            throw reported;
        }
    }

    [SuppressMessage("Design", "CA1031", Justification = "The admitted boundary records actual cleanup and preserves separately observed effects in safe resource failure carriers.")]
    private async Task<T> RunAsync<T>(string connectionId, LlmResourceOperation kind, LlmBatch? batch,
        LlmResourcePageRequest? page, string? name,
        Func<XaiBatchProtocol, CancellationToken, Task<T>> dispatch, CancellationToken cancellationToken)
    {
        using LlmOperationLifetime.Lease operation = _lifetime.Enter(cancellationToken);
        CancellationToken token = operation.Token;
        void Fence()
        {
            token.ThrowIfCancellationRequested();
            try { _lifetime.ThrowIfStopping(); }
            catch (ObjectDisposedException error)
            { throw new OperationCanceledException("The resource facade is stopping.", error, token); }
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        if (kind == LlmResourceOperation.CreateBatch) ArgumentNullException.ThrowIfNull(name);
        if (kind is LlmResourceOperation.GetBatch or LlmResourceOperation.CancelBatch or LlmResourceOperation.ListBatchRequests)
        {
            ArgumentNullException.ThrowIfNull(batch);
            XaiBatchProtocol.ValidateBatch(batch);
        }
        if (kind is LlmResourceOperation.ListBatches or LlmResourceOperation.ListBatchRequests)
            ArgumentNullException.ThrowIfNull(page);
        LlmResourcePageRequest? enteredPage = page is null ? null : new(page.Limit, page.PaginationToken);
        LlmProviderService? core = _providerService;
        if (core is null || !core.SupportsXaiSnapshot)
            throw new NotSupportedException("The resource facade requires an implemented xAI LLM core route.");
        Fence();
        var observation = new XaiResourceHttp.Observation(kind, batch);
        try
        {
            LlmConnection snapshot = await _connectionAccess!.AcquireAsync(connectionId, connection =>
            {
                Fence();
                XaiBatchProtocol.ValidateConnection(connection, batch);
                Fence();
            }, token).ConfigureAwait(false);
            Fence();
            T result = await core.RunXaiSnapshotAsync(snapshot, Fence, (http, connection, ownerFence, innerToken) =>
                dispatch(new XaiBatchProtocol(http, connection, connectionId, enteredPage, observation, ownerFence), innerToken),
                token).ConfigureAwait(false);
            Fence();
            return result;
        }
        catch (Exception error)
        {
            _cleanup.Record(error);
            throw XaiResourceHttp.OperationFailure(error, observation);
        }
    }

    private NotSupportedException Unavailable(CancellationToken cancellationToken)
    {
        using LlmOperationLifetime.Lease operation = _lifetime.Enter(cancellationToken);
        return new NotSupportedException("This batch or deferred operation is not implemented.");
    }

    /// <summary>Fences new work, drains admitted work and releases the borrowed access reference.</summary>
    public ValueTask DisposeAsync() => new(_lifetime.RetireAsync(() =>
    {
        Interlocked.Exchange(ref _connectionAccess, null);
        Interlocked.Exchange(ref _providerService, null);
        return _cleanup.ReleaseAsync();
    }));

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public override string ToString() => nameof(LlmResourceProviderService);
}

public static class ServiceCollectionExtensions
{
    /// <summary>Registers a scoped facade over an existing scoped connection access. No adapter is activated.</summary>
    public static IServiceCollection AddLlmResources(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped(provider => new LlmResourceProviderService(
            provider.GetRequiredService<LlmConnectionAccess>(), provider.GetService<LlmProviderService>()));
        services.AddScoped<ILlmBatchClient>(provider => provider.GetRequiredService<LlmResourceProviderService>());
        return services;
    }
}
