using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>The OpenAI API adapter's arbiter catalogue extension, private claim and own-release protocol.</summary>
internal static class ArbiterModelReservations
{
    private const string ProofHeader = "X-Gpu-Arbiter-Owner-Proof";
    private const string NameHeader = "X-Gpu-Arbiter-Owner-Name";

    internal static LlmModelListing ReadListing(JsonElement root, IReadOnlyList<LlmModel> models)
    {
        if (!root.TryGetProperty("supports_named_reservations", out JsonElement support)
            || support.ValueKind != JsonValueKind.True)
            return new(models, false, []);
        JsonElement states = Array(root, "reservation_models");
        var reservations = new List<LlmModelReservationState>();
        foreach (JsonElement state in states.EnumerateArray())
            reservations.Add(new(Text(state, "id"), Boolean(state, "loaded"), Holders(state, "owner_id")));
        // Available is local supporting-definition evidence, not a claim that inference or native loading succeeded.
        IReadOnlyList<LlmModel> available = models.Select(model => model with
        {
            Availability = model.CatalogEntry is { } entry && Boolean(entry, "available")
                ? LlmModelAvailability.Available : LlmModelAvailability.Unavailable
        }).ToArray();
        return new(available, true, reservations);
    }

    internal static void ApplyClaim(HttpRequestMessage request, LlmConnection connection)
    {
        // A public/custom header cannot substitute for the admitted scoped caller or survive an unsupported route.
        request.Headers.Remove(ProofHeader);
        request.Headers.Remove(NameHeader);
        if (!connection.SupportsModelReservations || connection.ModelOwner is not { } owner) return;
        request.Headers.Add(ProofHeader, owner.Proof);
        request.Headers.Add(NameHeader, Base64Url.EncodeToString(Encoding.UTF8.GetBytes(owner.Name)));
    }

    internal static async Task<LlmModelReleaseResult> ReleaseAsync(HttpClient http, LlmConnection connection,
        string modelId, IEnumerable<string> protectedValues, CancellationToken token)
    {
        using var consumption = SdkHttpConsumption.Enter(http, token);
        token = consumption.Token;
        using var request = new HttpRequestMessage(HttpMethod.Post,
            new Uri(ProviderHttp.BuildUri(connection, "models"), "/models/release"))
        {
            Content = new StringContent(new JsonObject { ["model"] = modelId }.ToJsonString(), Encoding.UTF8, "application/json")
        };
        ProviderHttp.ApplyHeaders(request, connection);
        ApplyClaim(request, connection);
        using HttpResponseMessage response = await http.SendAsync(request, token).ConfigureAwait(false);
        string[] secrets = [.. request.Headers.SelectMany(header => header.Value), .. protectedValues];
        LlmCredential? credential = LlmCredential.Parse(connection.Credential);
        await ProviderHttp.ThrowIfUnsuccessfulAsync(response, connection.Transport, credential,
            "Model reservation release", secrets, ProviderErrorReading.None, token).ConfigureAwait(false);
        string body = await SdkHttpConsumption.ReadAsStringAsync(response.Content, token).ConfigureAwait(false);
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement result = document.RootElement;
            IReadOnlyList<LlmModelHolder> holders = Holders(result, "ownerId");
            return consumption.Publish(new LlmModelReleaseResult(Text(result, "model"),
                !holders.Any(holder => holder.Id == connection.ModelOwner!.Id), Boolean(result, "loaded"), holders));
        }
        catch (JsonException error)
        {
            throw new LlmProviderException("Model reservation release returned invalid JSON.", connection.Transport,
                response.StatusCode, requestId: ProviderHttp.ReadRequestId(response, credential, secrets),
                sanitizedResponseBody: SecretRedactor.Sanitize(body, credential, secrets),
                innerException: SecretRedactor.Sanitize(error));
        }
    }

    private static LlmModelHolder[] Holders(JsonElement value, string idField) =>
        Array(value, "holders").EnumerateArray().Select(holder => new LlmModelHolder(Text(holder, idField), Text(holder, "name"))).ToArray();

    private static JsonElement Array(JsonElement value, string field) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(field, out JsonElement result)
            && result.ValueKind == JsonValueKind.Array ? result : throw new JsonException("Model reservation metadata is invalid.");

    private static string Text(JsonElement value, string field) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(field, out JsonElement result)
            && result.ValueKind == JsonValueKind.String ? result.GetString()! : throw new JsonException("Model reservation metadata is invalid.");

    private static bool Boolean(JsonElement value, string field) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(field, out JsonElement result)
            && result.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? result.GetBoolean() : throw new JsonException("Model reservation metadata is invalid.");
}
