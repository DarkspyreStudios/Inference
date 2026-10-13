using System.Collections.ObjectModel;
using System.Text.Json;

namespace Darkspyre.Inference.Connect.LLM.Internal;

internal static class ProviderTokenCounting
{
    internal static readonly TimeSpan ControlTimeout = TimeSpan.FromSeconds(100);

    internal static async Task<LlmProviderInputTokenCount> SendAsync(HttpClient http, HttpRequestMessage request,
        LlmTransport transport, string modelId, bool anthropic, CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        using var consumption = SdkHttpConsumption.Enter(http, cancellationToken); cancellationToken = consumption.Token;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? ControlTimeout);
        try
        {
            LlmProviderInputTokenCount result;
            {
                using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                    deadline.Token).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
                if (!response.IsSuccessStatusCode)
                    throw new LlmProviderException("The provider rejected token counting. Check credentials, model access, request configuration and quota.",
                        transport, response.StatusCode, retryAfter: response.Headers.RetryAfter?.Delta);
                await using Stream stream = await SdkHttpConsumption.ReadAsStreamAsync(response.Content, deadline.Token).ConfigureAwait(false);
                using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: deadline.Token).ConfigureAwait(false);
                JsonElement root = document.RootElement;
                result = new LlmProviderInputTokenCount
                {
                    ModelId = modelId,
                    InputTokens = RequiredCount(root, anthropic ? "input_tokens" : "totalTokens"),
                    CachedInputTokens = anthropic ? null : OptionalCount(root, "cachedContentTokenCount"),
                    BillableCharacters = anthropic ? null : OptionalCount(root, "totalBillableCharacters"),
                    InputTokenDetails = anthropic ? [] : Details(root, "promptTokensDetails"),
                    CacheTokenDetails = anthropic ? [] : Details(root, "cacheTokensDetails")
                };
                if (result.CachedInputTokens > result.InputTokens) throw new JsonException();
                deadline.Token.ThrowIfCancellationRequested();
            }
            return consumption.Publish(result);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException(cancellationToken); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        { throw new LlmProviderException("Provider token counting timed out.", transport); }
        catch (OperationCanceledException)
        { throw new LlmProviderException("Provider token counting was interrupted.", transport); }
        catch (Exception error) when (error is HttpRequestException or JsonException or InvalidOperationException or IOException)
        {
            if (deadline.IsCancellationRequested) throw new LlmProviderException("Provider token counting timed out.", transport);
            // Count responses and transport diagnostics are untrusted and can contain credentials or prompt content.
            throw new LlmProviderException("Provider token counting failed or returned invalid counts.", transport);
        }
    }

    private static long RequiredCount(JsonElement root, string name) =>
        OptionalCount(root, name) ?? throw new JsonException();

    private static long? OptionalCount(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long count) && count >= 0
            ? count : throw new JsonException();
    }

    private static ReadOnlyCollection<LlmTokenCountDetail> Details(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value)) return [];
        if (value.ValueKind != JsonValueKind.Array) throw new JsonException();
        var counts = new List<LlmTokenCountDetail>();
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (!item.TryGetProperty("modality", out JsonElement modality) || modality.ValueKind != JsonValueKind.String)
                throw new JsonException();
            LlmTokenModality kind = modality.GetString() switch
            {
                "TEXT" => LlmTokenModality.Text,
                "IMAGE" => LlmTokenModality.Image,
                "AUDIO" => LlmTokenModality.Audio,
                "VIDEO" => LlmTokenModality.Video,
                "DOCUMENT" => LlmTokenModality.Document,
                _ => LlmTokenModality.Unknown
            };
            counts.Add(new(kind, RequiredCount(item, "tokenCount")));
        }
        return counts.AsReadOnly();
    }
}
