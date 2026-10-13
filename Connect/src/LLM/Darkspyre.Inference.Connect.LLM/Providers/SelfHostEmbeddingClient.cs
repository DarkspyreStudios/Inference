using Darkspyre.Inference.Common.Embedding;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>
/// Embeds through the host's in-process engine. The connection's endpoint is the engine ID; the engine is Common's
/// <see cref="IEmbeddingInference"/>, so Connect references no engine library.
/// </summary>
internal sealed class SelfHostEmbeddingClient(ILlmSelfHost host) : ILlmEmbeddingClient
{
    public bool Supports(LlmTransport transport) => transport == LlmTransport.SelfHost;

    public void ValidateRequest(LlmConnection connection, LlmEmbeddingRequest request)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(request);
        if (SelfHostLlmClient.UnsupportedOptions(connection) is { } issue)
            throw new NotSupportedException(issue.Message);
        if (string.IsNullOrWhiteSpace(connection.Endpoint))
            throw new ArgumentException("A SelfHost connection names its engine in its endpoint.", nameof(connection));
        if (request.ProviderOptions.Count > 0)
            throw new ArgumentException("A SelfHost engine accepts no provider request options.", nameof(request));
        if (request.Dimensions is <= 0)
            throw new ArgumentException("A requested vector size must be positive.", nameof(request));
    }

    public async Task<LlmEmbeddingResult> EmbedAsync(LlmConnection connection, LlmEmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(connection, request);
        IEmbeddingInference engine = await host.GetEmbeddingInferenceAsync(connection.Endpoint, cancellationToken).ConfigureAwait(false);
        EmbeddingResult result = await engine.EmbedAsync(new()
        {
            ModelId = request.ModelId,
            Inputs = request.Inputs.Select(text => (EmbeddingInput)new TextEmbeddingInput(text)).ToArray(),
            Kind = request.Kind,
            Instruction = request.Instruction,
            Dimensions = request.Dimensions,
            Truncate = request.Truncate,
        }, cancellationToken).ConfigureAwait(false);
        return new()
        {
            Vectors = result.Vectors,
            Usage = new(result.Usage.InputTokens, result.Usage.InputTokens),
            ModelId = result.Provenance.ModelId,
            ModelFingerprint = result.ModelFingerprint,
            TruncatedInputs = result.TruncatedInputs,
            Diagnostics = new(200, null, null),
        };
    }
}
