using System.Globalization;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Reads fixed Gemini cache routes from admitted connection data without issuing binding authority.</summary>
internal static class GeminiCacheReads
{
    internal static async Task<LlmContextCache> GetAsync(HttpClient http, LlmConnection admittedConnection,
        LlmExplicitCacheReference importedReference, string ownerIssuedScopeId,
        Action<CancellationToken> ownerFence, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(importedReference);
        string id = ResourceId(importedReference.ResourceName);
        string model = GeminiApiLlmClient.ParseModel(importedReference.ModelId);
        string key = Validate(http, admittedConnection, ownerIssuedScopeId, ownerFence, token);
        using var request = Request("/" + Uri.EscapeDataString(id), key);
        return await GoogleCacheHttp.GetAsync(http, request, LlmTransport.GoogleGeminiApi,
            root => ParseReceipt(root, ownerIssuedScopeId, importedReference.ResourceName, model),
            ownerFence, token).ConfigureAwait(false);
    }

    internal static async Task<LlmContextCachePage> ListAsync(HttpClient http, LlmConnection admittedConnection,
        LlmContextCacheList request, string ownerIssuedScopeId,
        Action<CancellationToken> ownerFence, CancellationToken token)
    {
        GoogleCacheData.ValidatePage(request);
        string key = Validate(http, admittedConnection, ownerIssuedScopeId, ownerFence, token);
        string query = "?pageSize=" + request.PageSize.ToString(CultureInfo.InvariantCulture)
            + (request.PageToken is null ? string.Empty : "&pageToken=" + Uri.EscapeDataString(request.PageToken));
        using var message = Request(query, key);
        return await GoogleCacheHttp.ListAsync(http, message, LlmTransport.GoogleGeminiApi,
            root => GoogleCacheData.Page(root, item => ParseReceipt(item, ownerIssuedScopeId, null, null)),
            ownerFence, token).ConfigureAwait(false);
    }

    private static string Validate(HttpClient http, LlmConnection connection, string scopeId,
        Action<CancellationToken> ownerFence, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeId);
        ArgumentNullException.ThrowIfNull(ownerFence);
        token.ThrowIfCancellationRequested();
        ownerFence(token);
        GeminiApiLlmClient.Validate(connection);
        return GeminiApiLlmClient.Key(connection);
    }

    private static HttpRequestMessage Request(string suffix, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Get,
            GeminiApiLlmClient.DefaultEndpoint + "/v1beta/cachedContents" + suffix);
        try { request.Headers.Add("x-goog-api-key", key); }
        catch (FormatException)
        {
            request.Dispose();
            throw new LlmProviderException("The Gemini API key cannot form a request header.", LlmTransport.GoogleGeminiApi);
        }
        return request;
    }

    private static LlmContextCache ParseReceipt(JsonElement root, string scopeId, string? expectedName, string? expectedModel) =>
        GoogleCacheData.Receipt(root, scopeId, name =>
        {
            _ = ResourceId(name);
            if (expectedName is not null && name != expectedName) throw new JsonException();
        }, name =>
        {
            const string prefix = "models/";
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) throw new JsonException();
            string model = GeminiApiLlmClient.ParseModel(name[prefix.Length..]);
            if (expectedModel is not null && model != expectedModel) throw new JsonException();
            return model;
        }, vertex: false);

    private static string ResourceId(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        const string prefix = "cachedContents/";
        if (!name.StartsWith(prefix, StringComparison.Ordinal)) throw new ArgumentException("Use a Gemini cache resource name.", nameof(name));
        string id = name[prefix.Length..];
        // IDs are opaque data, encoded once as one path segment, never resolved as a URI.
        if (id.Length == 0 || id is "." or ".." || id.ContainsAny(['/', '\\']) || id.Any(char.IsControl))
            throw new ArgumentException("Use a single Gemini cache resource ID.", nameof(name));
        return id;
    }
}
