using System.Text.Json;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Sends an Anthropic <c>messages/count_tokens</c> request and reads its <c>input_tokens</c>.</summary>
internal static class AnthropicTokenCounting
{
    internal static readonly TimeSpan ControlTimeout = TimeSpan.FromSeconds(100);

    /// <summary>
    /// Sends <paramref name="request"/> within <see cref="ControlTimeout"/>. A refusal carries the response's
    /// <c>Retry-After</c>; a failure or an invalid count never carries the response text, which can hold credentials or
    /// prompt content.
    /// </summary>
    internal static async Task<LlmProviderInputTokenCount> SendAsync(HttpClient http, HttpRequestMessage request,
        LlmTransport transport, string modelId, CancellationToken cancellationToken)
    {
        using var consumption = SdkHttpConsumption.Enter(http, cancellationToken); cancellationToken = consumption.Token;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ControlTimeout);
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
                result = new LlmProviderInputTokenCount { ModelId = modelId, InputTokens = InputTokens(document.RootElement) };
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
