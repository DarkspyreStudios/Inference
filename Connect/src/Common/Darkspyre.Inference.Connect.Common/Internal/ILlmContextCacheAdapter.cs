using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Internal;

internal interface ILlmContextCacheAdapter
{
    void ValidateCacheConnection(LlmConnection connection);
    void ValidateCacheReference(LlmConnection connection, LlmExplicitCacheReference reference);
    void ValidateCacheCreate(LlmConnection connection, LlmContextCacheCreate request);
    void ValidateCachedSuffix(LlmChatRequest request);
    Task<ILlmContextCacheOperation> PrepareCacheOperationAsync(LlmConnection connection,
        Action<CancellationToken> fence, CancellationToken token);
}

// One operation snapshot borrows its adapter/authorization. The facade and SDK consumption own drainage.
internal interface ILlmContextCacheOperation
{
    LlmConnection Connection { get; }
    Task<LlmContextCache> CreateAsync(LlmContextCacheCreate request, GeminiCacheBindings.Admission admission,
        Action<CancellationToken> fence, CancellationToken token);
    Task<LlmContextCache> GetAsync(LlmExplicitCacheReference reference, GeminiCacheBindings.Admission admission,
        Action<CancellationToken> fence, CancellationToken token);
    Task<LlmContextCachePage> ListAsync(LlmContextCacheList request, GeminiCacheBindings.Admission admission,
        Action<CancellationToken> fence, CancellationToken token);
    Task<LlmContextCache> UpdateExpiryAsync(LlmExplicitCacheReference reference, DateTimeOffset expiry,
        GeminiCacheBindings.Admission admission, Action<CancellationToken> fence, CancellationToken token);
    Task<LlmContextCacheDeleteResult> DeleteAsync(LlmExplicitCacheReference reference,
        Func<LlmExplicitCacheReference, bool> authorizeAlreadyAbsent,
        GeminiCacheBindings.Admission admission, Action<CancellationToken> fence, CancellationToken token);
}
