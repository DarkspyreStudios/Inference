using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.Resources;

/// <summary>Explicit batch and deferred facade. No provider adapter is currently implemented.</summary>
public sealed class LlmResourceProviderService : ILlmBatchClient, IDisposable, IAsyncDisposable
{
    private LlmConnectionAccess? _connectionAccess;
    private readonly LlmOperationLifetime _lifetime = new();

    /// <summary>Borrows the existing connection access owner without acquiring credentials.</summary>
    public LlmResourceProviderService(LlmConnectionAccess connectionAccess)
        => _connectionAccess = connectionAccess ?? throw new ArgumentNullException(nameof(connectionAccess));

    public Task<LlmBatch> CreateAsync(string connectionId, string name, CancellationToken cancellationToken = default)
        => Task.FromException<LlmBatch>(Unavailable(cancellationToken));

    public Task AddRequestsAsync(string connectionId, LlmBatch batch, IReadOnlyList<LlmBatchRequest> requests,
        IInferenceResourceAccess? resources = null, CancellationToken cancellationToken = default)
        => Task.FromException(Unavailable(cancellationToken));

    public Task<LlmBatch> GetAsync(string connectionId, LlmBatch batch, CancellationToken cancellationToken = default)
        => Task.FromException<LlmBatch>(Unavailable(cancellationToken));

    public Task<LlmBatchPage> ListAsync(string connectionId, LlmResourcePageRequest page, CancellationToken cancellationToken = default)
        => Task.FromException<LlmBatchPage>(Unavailable(cancellationToken));

    public Task<LlmBatchRequestPage> ListRequestsAsync(string connectionId, LlmBatch batch,
        LlmResourcePageRequest page, CancellationToken cancellationToken = default)
        => Task.FromException<LlmBatchRequestPage>(Unavailable(cancellationToken));

    public Task<LlmBatchResultPage> GetResultsAsync(string connectionId, LlmBatch batch,
        LlmResourcePageRequest page, LlmMediaMaterializationOptions materialization,
        IInferenceResourceAccess? resources = null, CancellationToken cancellationToken = default)
        => Task.FromException<LlmBatchResultPage>(Unavailable(cancellationToken));

    public Task<LlmBatch> CancelAsync(string connectionId, LlmBatch batch, CancellationToken cancellationToken = default)
        => Task.FromException<LlmBatch>(Unavailable(cancellationToken));

    public Task<LlmDeferredCompletion> SubmitDeferredAsync(string connectionId, LlmChatRequest request,
        IInferenceResourceAccess? resources = null, CancellationToken cancellationToken = default)
        => Task.FromException<LlmDeferredCompletion>(Unavailable(cancellationToken));

    public Task<LlmDeferredResult> GetDeferredAsync(string connectionId, LlmDeferredCompletion completion,
        IInferenceResourceAccess? resources = null, CancellationToken cancellationToken = default)
        => Task.FromException<LlmDeferredResult>(Unavailable(cancellationToken));

    public Task<LlmBatch> CreateFromRequestsAsync(string connectionId, string name,
        IReadOnlyList<LlmBatchRequest> requests, IInferenceResourceAccess? resources = null,
        CancellationToken cancellationToken = default)
        => Task.FromException<LlmBatch>(Unavailable(cancellationToken));

    private NotSupportedException Unavailable(CancellationToken cancellationToken)
    {
        using LlmOperationLifetime.Lease operation = _lifetime.Enter(cancellationToken);
        return new NotSupportedException("No batch or deferred resource adapter is implemented.");
    }

    /// <summary>Fences new work, drains admitted work and releases the borrowed access reference.</summary>
    public ValueTask DisposeAsync() => new(_lifetime.RetireAsync(() =>
    {
        Interlocked.Exchange(ref _connectionAccess, null);
        return Task.CompletedTask;
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
        services.AddScoped<LlmResourceProviderService>();
        services.AddScoped<ILlmBatchClient>(provider => provider.GetRequiredService<LlmResourceProviderService>());
        return services;
    }
}
