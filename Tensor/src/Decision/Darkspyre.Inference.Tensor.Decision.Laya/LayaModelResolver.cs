using System.Text.Json;
using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Tensor.Decision.Laya;

/// <summary>Three-artifact Laya ONNX resolution without creating an ONNX Runtime session.</summary>
public sealed class LayaModelResolver(ITensorModelSource? source = null, ITensorArtifactReader? reader = null)
    : TensorModelResolver(source, reader)
{
    public override IReadOnlyList<TensorArtifactRequirement> Requirements =>
        [new("model", "*.onnx"), new("tokenizer", "tokenizer.json"), new("agent-config", "rl_agent_config.json")];

    public override async Task<TensorModelResolution> ResolveCandidateAsync(TensorModelCandidate candidate, TensorBackendKind backend,
        ITensorArtifactReader reader, CancellationToken cancellationToken = default)
    {
        static TensorModelResolution Fail(TensorCompatibilityState state, string code, string message) => new(new(state, [new(code, message)], []));
        cancellationToken.ThrowIfCancellationRequested();
        if (backend is not (TensorBackendKind.Auto or TensorBackendKind.Cpu or TensorBackendKind.CoreMl))
            return Fail(TensorCompatibilityState.Unsupported, "backend-unsupported", "Laya supports Core ML and CPU.");
        try
        {
            var manifest = new List<TensorModelArtifactDefinition>();
            foreach (TensorArtifactRequirement requirement in Requirements)
            {
                if (!candidate.ArtifactRoles.TryGetValue(requirement.Role, out string? path))
                    return Fail(TensorCompatibilityState.Unverified, "companion-missing", $"The '{requirement.Role}' artifact is missing.");
                TensorSourceArtifact? artifact = candidate.Artifacts.SingleOrDefault(item => item.Path == path);
                if (artifact is null || artifact.Length is not > 0 || artifact.Sha256 is not { Length: 64 } hash
                    || !hash.All(Uri.IsHexDigit) || string.IsNullOrWhiteSpace(artifact.Provenance.License))
                    return Fail(TensorCompatibilityState.Unverified, "pack-metadata-incomplete", "Every Laya artifact needs a size, SHA-256 and license; use explicit Import to establish missing integrity.");
                manifest.Add(TensorResolvedPack.Artifact(artifact, requirement.Role, requirement.Role == "model" ? "laya.onnx" : requirement.FileName));
            }
            var metadata = new TensorMetadataReader(reader);
            TensorSourceArtifact Find(string role) => candidate.Artifacts.Single(item => item.Path == candidate.ArtifactRoles[role]);
            LayaAgentConfiguration configuration = await metadata.ReadAsync(Find("agent-config"), stream =>
            {
                if (stream.Length > 4 * 1024 * 1024) throw new InvalidDataException("Agent configuration exceeds its metadata limit.");
                using JsonDocument document = JsonDocument.Parse(stream);
                return LayaAgentConfiguration.Parse(document.RootElement);
            }, cancellationToken).ConfigureAwait(false);
            await metadata.ReadAsync(Find("tokenizer"), stream =>
            {
                if (stream.Length > 16 * 1024 * 1024) throw new InvalidDataException("Tokenizer exceeds its metadata limit.");
                using JsonDocument document = JsonDocument.Parse(stream);
                return LayaTokenizer.Parse(document.RootElement);
            }, cancellationToken).ConfigureAwait(false);
            await metadata.ReadAsync(Find("model"), stream => { LayaOnnxMetadata.Validate(stream, configuration.MaximumLength); return true; }, cancellationToken).ConfigureAwait(false);
            TensorModelPackDefinition definition = TensorResolvedPack.Create(candidate, TensorCapability.Decision,
                TensorDecisionModels.LayaRuntimeArchitecture, new HashSet<TensorBackendKind> { TensorBackendKind.CoreMl, TensorBackendKind.Cpu }, manifest);
            LayaModelCompatibility.Validate(definition);
            return new(new(TensorCompatibilityState.Supported, [], [new(Find("model").Path, "laya-onnx",
                "Embedded ONNX metadata and fixed input shapes match the agent configuration; tokenizer and calibration parsed. Load still validates the native graph.")]), definition);
        }
        catch (TensorSourceException exception) { return new(new(TensorCompatibilityState.Unverified, [new(exception.Code, exception.Message)], [], exception.State)); }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or OverflowException or FormatException)
        { return Fail(TensorCompatibilityState.Unsupported, "laya-schema", "The artifacts do not satisfy the Laya ONNX, tokenizer and agent configuration contract."); }
        catch (IOException) { return Fail(TensorCompatibilityState.Unverified, "metadata-incomplete", "The Laya metadata could not be inspected completely."); }
    }
}
