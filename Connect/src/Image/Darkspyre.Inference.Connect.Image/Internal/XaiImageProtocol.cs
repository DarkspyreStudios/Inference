using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.Image;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.Image.Internal;

/// <summary>Pure native body/result mapping. Resolved references must be admitted by the owning transport.</summary>
internal static class XaiImageProtocol
{
    internal static void Validate(ImageGenerationRequest request, ImageWorkflow workflow) =>
        _ = BuildControls(Controls(request, workflow));

    internal static (string Route, JsonObject Body) Build(ImageGenerationRequest request,
        ImageWorkflow workflow, IReadOnlyList<JsonObject> references) =>
        Build(Controls(request, workflow), references);

    internal static (string Route, JsonObject Body) Build(ImageTransportPreparationRequest request,
        IReadOnlyList<JsonObject> references)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Build(new ImageControls(request.Workflow, request.PositivePrompt, request.NegativePrompt,
            request.Width, request.Height, request.Seed, request.Steps, request.GuidanceScale,
            request.Count, request.References.Count), references);
    }

    private static ImageControls Controls(ImageGenerationRequest request, ImageWorkflow workflow)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ModelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workflow.Id);
        if (!string.Equals(request.ModelId, workflow.Id, StringComparison.Ordinal))
            throw new ArgumentException("The image request does not match its workflow.", nameof(request));
        ArgumentNullException.ThrowIfNull(request.References);
        foreach (InferenceMediaSource source in request.References)
        {
            switch (source)
            {
                case BinaryMediaSource: break;
                case UrlMediaSource url:
                    ArgumentNullException.ThrowIfNull(url.Url);
                    XaiMediaData.RequireHttpUrl(url.Url.OriginalString);
                    break;
                case FileMediaSource file:
                    ArgumentNullException.ThrowIfNull(file.File);
                    ArgumentException.ThrowIfNullOrWhiteSpace(file.File.FileId);
                    break;
                case ResourceMediaSource resource:
                    ArgumentNullException.ThrowIfNull(resource.Resource);
                    break;
                case null: throw new ArgumentException("An image reference cannot be null.", nameof(request));
                default: throw new NotSupportedException("Unknown image reference source.");
            }
        }
        return new(workflow, request.Prompt, request.NegativePrompt, request.Width, request.Height,
            request.Seed, request.Steps, request.GuidanceScale, request.Count, request.References.Count);
    }

    private static (string Route, JsonObject Body) Build(ImageControls controls,
        IReadOnlyList<JsonObject> references)
    {
        ArgumentNullException.ThrowIfNull(references);
        JsonObject body = BuildControls(controls);
        if (references.Count != controls.ReferenceCount)
            throw new ArgumentException("Invalid ordered image references.", nameof(references));
        JsonObject[] nativeReferences = references.Select(CopyReference).ToArray();
        if (nativeReferences.Length == 1) body["image"] = nativeReferences[0];
        else if (nativeReferences.Length > 1) body["images"] = new JsonArray(nativeReferences.Cast<JsonNode>().ToArray());
        return (nativeReferences.Length == 0 ? "images/generations" : "images/edits", body);
    }

    private static JsonObject BuildControls(ImageControls controls)
    {
        ArgumentNullException.ThrowIfNull(controls.Workflow);
        if (controls.Workflow.Transport != ImageTransport.XaiImagine)
            throw new NotSupportedException("The image workflow is not an xAI workflow.");
        ArgumentException.ThrowIfNullOrWhiteSpace(controls.Workflow.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(controls.Prompt);
        if (controls.Count is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(controls));
        if (controls.ReferenceCount > 5 || controls.Workflow.RequiresReferenceImage && controls.ReferenceCount == 0)
            throw new ArgumentException("Invalid image reference count.", nameof(controls));
        if (!string.IsNullOrEmpty(controls.NegativePrompt) || controls.Width is not null || controls.Height is not null
            || controls.Seed is not null || controls.Steps is not null || controls.GuidanceScale is not null)
            throw new NotSupportedException("The supplied image sampling control has no xAI mapping.");
        JsonElement configuration = controls.Workflow.TransportConfiguration;
        if (configuration.ValueKind != JsonValueKind.Object) throw new ArgumentException("An xAI image workflow requires native model controls.", nameof(controls));
        var body = new JsonObject
        {
            ["model"] = XaiMediaData.RequiredString(configuration, "model", static _ => false),
            ["prompt"] = controls.Prompt,
            ["n"] = controls.Count,
            ["response_format"] = "b64_json"
        };
        foreach (JsonProperty property in configuration.EnumerateObject())
        {
            if (property.Name == "model") continue;
            switch (property.Name)
            {
                case "aspect_ratio":
                    if (controls.ReferenceCount == 1 && property.Value.ValueKind != JsonValueKind.Null)
                        throw new NotSupportedException("Single-image edits derive their aspect ratio from the reference.");
                    RequireChoice(property.Value, ["1:1", "3:4", "4:3", "9:16", "16:9", "2:3", "3:2", "9:19.5", "19.5:9", "9:20", "20:9", "1:2", "2:1", "21:9", "5:2", "auto"]);
                    break;
                case "resolution": RequireChoice(property.Value, ["1k", "1.5k", "2k"]); break;
                case "quality": RequireChoice(property.Value, ["low", "medium", "auto"]); break;
                case "user":
                    if (property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw InvalidControl();
                    break;
                case "storage_options": ValidateStorage(property.Value); break;
                default: throw new NotSupportedException("An image workflow control has no xAI mapping.");
            }
            body[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        }
        return body;
    }

    private sealed record ImageControls(ImageWorkflow Workflow, string Prompt, string? NegativePrompt,
        int? Width, int? Height, long? Seed, int? Steps, double? GuidanceScale, int Count, int ReferenceCount);

    private static JsonObject CopyReference(JsonObject reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        // REST image sources carry exactly one native URL or file ID, not arbitrary metadata or inferred links.
        if (reference.Count != 1 || reference.First() is not { Key: var field, Value: JsonValue value }
            || field is not ("url" or "file_id") || !value.TryGetValue(out string? text) || string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("An image reference has no supported native source.", nameof(reference));
        if (field == "url")
        {
            try { XaiMediaData.RequireHttpUrl(text); }
            catch (JsonException) { throw new ArgumentException("An image reference requires a valid HTTP or HTTPS URL.", nameof(reference)); }
        }
        return new JsonObject { [field] = text };
    }

    private static void RequireChoice(JsonElement value, string[] choices)
    {
        if (value.ValueKind == JsonValueKind.Null) return;
        if (value.ValueKind != JsonValueKind.String || !choices.Contains(value.GetString(), StringComparer.Ordinal)) throw InvalidControl();
    }

    private static void ValidateStorage(JsonElement storage)
    {
        if (storage.ValueKind == JsonValueKind.Null) return;
        if (storage.ValueKind != JsonValueKind.Object) throw InvalidControl();
        _ = XaiMediaData.RequiredString(storage, "filename", static _ => false);
        foreach (JsonProperty property in storage.EnumerateObject())
            switch (property.Name)
            {
                case "filename": break;
                case "expires_after":
                    if (property.Value.ValueKind != JsonValueKind.Null
                        && (!property.Value.TryGetInt64(out long expiry) || expiry > 2_592_000)) throw InvalidControl();
                    break;
                case "public_url":
                    if (property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null) break;
                    if (property.Value.ValueKind != JsonValueKind.Object) throw InvalidControl();
                    foreach (JsonProperty option in property.Value.EnumerateObject())
                        if (option.Name != "expires_after" || option.Value.ValueKind != JsonValueKind.Null
                            && (!option.Value.TryGetInt64(out long seconds) || seconds is < 3_600 or > 2_592_000)) throw InvalidControl();
                    break;
                default: throw new NotSupportedException("An image storage control has no xAI mapping.");
            }
    }

    internal static XaiImageResult Parse(string body, Func<string, bool> protectedValue, Action fence)
    {
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out JsonElement data)
            || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() is < 1 or > 10) throw XaiMediaData.Malformed();
        var images = new List<XaiImageArtifact>();
        foreach (JsonElement row in data.EnumerateArray())
        {
            fence();
            if (row.ValueKind != JsonValueKind.Object) throw XaiMediaData.Malformed();
            string encoded = XaiMediaData.RequiredString(row, "b64_json", static _ => false);
            byte[] bytes;
            try { bytes = Convert.FromBase64String(encoded); }
            catch (FormatException) { throw XaiMediaData.Malformed(); }
            if (bytes.Length == 0) throw XaiMediaData.Malformed();
            string? contentType = XaiMediaData.OptionalString(row, "mime_type", protectedValue);
            JsonObject? file = XaiMediaData.FileOutput(row, protectedValue);
            var state = new JsonObject();
            if (file is not null) state["file_output"] = file;
            if (XaiMediaData.OptionalString(row, "storage_error", protectedValue) is not null) state["storage_failed"] = true;
            images.Add(new(bytes, contentType, XaiMediaData.Element(state)));
        }
        string? model = XaiMediaData.OptionalString(root, "model", protectedValue);
        JsonElement? usage = XaiMediaData.Usage(root);
        fence();
        return new(images.AsReadOnly(), model, usage);
    }

    private static ArgumentException InvalidControl() => new("An image workflow control has an invalid value.");
}

internal sealed record XaiImageArtifact(ReadOnlyMemory<byte> Data, string? ContentType, JsonElement ProviderState)
{
    public override string ToString() => "XaiImageArtifact { <artifact> }";
}

internal sealed record XaiImageResult(IReadOnlyList<XaiImageArtifact> Images, string? ModelId, JsonElement? Usage)
{
    public override string ToString() => "XaiImageResult { <observation> }";
}
