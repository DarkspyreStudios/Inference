using System.Text.Json;
using System.Text.Json.Serialization;

namespace Darkspyre.Inference.Connect.Image;

/// <summary>Provider-neutral image workflow metadata with adapter-owned opaque configuration.</summary>
public sealed record ImageWorkflow
{
    public required string Id { get; init; }

    public required string Label { get; init; }

    public string Category { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string Version { get; init; } = "1";

    public int DefaultWidth { get; init; } = 1_024;

    public int DefaultHeight { get; init; } = 1_024;

    public required ImageTransport Transport { get; init; }

    public string OutputContentType { get; init; } = "image/png";

    public string OutputExtension { get; init; } = "png";

    public string DefaultNegativePrompt { get; init; } = string.Empty;

    public bool RequiresReferenceImage { get; init; }

    /// <summary>Opaque configuration interpreted only by the selected image transport.</summary>
    public JsonElement TransportConfiguration
    {
        get;
        init => field = value.ValueKind == JsonValueKind.Undefined ? default : value.Clone();
    }
}

/// <summary>Host-owned image metadata needed to prepare and upload a provider reference.</summary>
public sealed record ImageReference
{
    public required Guid Id { get; init; }

    public string? FileName { get; init; }

    public string? ContentType { get; init; }
}

public enum ImageWorkflowOrigin { Host = 0, BuiltIn = 1 }

public sealed record ImageWorkflowSelection([property: JsonRequired] ImageWorkflowOrigin Origin, string Id);
