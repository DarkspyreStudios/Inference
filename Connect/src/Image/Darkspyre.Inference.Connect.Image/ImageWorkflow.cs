using System.Text.Json;
using System.Text.Json.Serialization;

namespace Darkspyre.Inference.Connect.Image;

/// <summary>Provider-neutral image workflow metadata with adapter-owned opaque configuration.</summary>
public sealed record ImageWorkflow
{
    /// <summary>The workflow identity within its catalog.</summary>
    public required string Id { get; init; }

    /// <summary>The name shown to people.</summary>
    public required string Label { get; init; }

    /// <summary>The group the workflow is listed under; empty for none.</summary>
    public string Category { get; init; } = string.Empty;

    /// <summary>Text that explains what the workflow produces.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>The workflow definition version.</summary>
    public string Version { get; init; } = "1";

    /// <summary>The output width in pixels when a request names none.</summary>
    public int DefaultWidth { get; init; } = 1_024;

    /// <summary>The output height in pixels when a request names none.</summary>
    public int DefaultHeight { get; init; } = 1_024;

    /// <summary>The image transport that runs the workflow.</summary>
    public required ImageTransport Transport { get; init; }

    /// <summary>The media type of the produced image.</summary>
    public string OutputContentType { get; init; } = "image/png";

    /// <summary>The file extension of the produced image, without a dot.</summary>
    public string OutputExtension { get; init; } = "png";

    /// <summary>The negative prompt used when a request supplies none.</summary>
    public string DefaultNegativePrompt { get; init; } = string.Empty;

    /// <summary>Whether a request must supply a reference image.</summary>
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
    /// <summary>The host's identity for the image.</summary>
    public required Guid Id { get; init; }

    /// <summary>The image's file name, when known.</summary>
    public string? FileName { get; init; }

    /// <summary>The image's media type, when known.</summary>
    public string? ContentType { get; init; }
}

/// <summary>The catalog an image workflow comes from.</summary>
public enum ImageWorkflowOrigin
{
    /// <summary>The host's workflow catalog.</summary>
    Host = 0,
    /// <summary>The workflows built into Connect.</summary>
    BuiltIn = 1
}

/// <summary>Names one workflow and the catalog it comes from.</summary>
/// <param name="Origin">The catalog that holds the workflow.</param>
/// <param name="Id">The workflow identity within that catalog.</param>
public sealed record ImageWorkflowSelection([property: JsonRequired] ImageWorkflowOrigin Origin, string Id);
