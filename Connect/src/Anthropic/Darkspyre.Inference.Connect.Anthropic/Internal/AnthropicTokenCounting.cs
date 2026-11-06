using System.Text.Json;
using System.Text.Json.Nodes;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>
/// Anthropic's <c>messages/count_tokens</c> route: which projected requests it counts as sent, and the request that reads
/// their <c>input_tokens</c>.
/// </summary>
internal static class AnthropicTokenCounting
{
    internal static readonly TimeSpan ControlTimeout = TimeSpan.FromSeconds(100);

    /// <summary>
    /// Whether the count route counts <paramref name="payload"/> as it would be sent. It refuses every server tool except
    /// the advisor, and image or document blocks with a <c>url</c> or <c>file</c> source. Nothing is removed to make a
    /// payload countable.
    /// </summary>
    internal static bool Countable(JsonObject payload) =>
        !(payload["tools"] is JsonArray tools && tools.Any(tool => tool is JsonObject entry
            && (string?)entry["type"] is { } type && type != "advisor_20260301"))
        && !HoldsReferencedSource(payload["system"])
        && !HoldsReferencedSource(payload["messages"]);

    private static bool HoldsReferencedSource(JsonNode? node) => node switch
    {
        JsonArray items => items.Any(HoldsReferencedSource),
        JsonObject item => item["source"] is JsonObject source && (string?)source["type"] is "url" or "file"
            || HoldsReferencedSource(item["content"]),
        _ => false
    };

    /// <summary>
    /// Sends <paramref name="request"/> within <see cref="ControlTimeout"/> and returns its <c>input_tokens</c>. A
    /// failure or an invalid count never carries the response text, which can hold credentials or prompt content.
    /// </summary>
    internal static async Task<long> SendAsync(HttpClient http, HttpRequestMessage request,
        LlmTransport transport, CancellationToken cancellationToken)
    {
        using var consumption = SdkHttpConsumption.Enter(http, cancellationToken); cancellationToken = consumption.Token;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ControlTimeout);
        try
        {
            long result;
            {
                using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                    deadline.Token).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
                if (!response.IsSuccessStatusCode)
                    throw new LlmProviderException("The provider rejected token counting. Check credentials, model access, request configuration and quota.",
                        transport, response.StatusCode, retryAfter: response.Headers.RetryAfter?.Delta);
                await using Stream stream = await SdkHttpConsumption.ReadAsStreamAsync(response.Content, deadline.Token).ConfigureAwait(false);
                using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: deadline.Token).ConfigureAwait(false);
                result = InputTokens(document.RootElement);
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

    private static long InputTokens(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("input_tokens", out JsonElement value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long count) && count >= 0
            ? count : throw new JsonException();
}
