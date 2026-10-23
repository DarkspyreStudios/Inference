using System.Globalization;
using System.Text.RegularExpressions;
using Darkspyre.Inference.Tensor.Common;
using Darkspyre.Inference.Tensor.Ggml;

namespace Darkspyre.Inference.Tensor.LLM.Qwen;

/// <summary>Resolves complete Qwen 3.5 family GGUF packs through bounded metadata inspection, never weight loading. A Qwen3
/// GGUF is reported Unsupported with the issue code <see cref="TensorInstalledPacks.RetiredArchitectureCode"/>.</summary>
public sealed partial class QwenModelResolver : TensorModelResolver
{
    private readonly GgmlRuntimeOwner _runtimeOwner;
    /// <summary>Creates a resolver over an optional repository source and artifact reader. Without a runtime owner it uses the shared one.</summary>
    public QwenModelResolver(ITensorModelSource? source = null, ITensorArtifactReader? reader = null,
        GgmlRuntimeOwner? runtimeOwner = null) : base(source, reader) => _runtimeOwner = runtimeOwner ?? GgmlRuntimeOwner.GetOrCreate();
    /// <inheritdoc />
    public override IReadOnlyList<TensorArtifactRequirement> Requirements => [new("model", "*.gguf")];
    /// <inheritdoc />
    public override string? AdditionalArtifactRolePrefix => "model-shard-";
    /// <inheritdoc />
    protected override string CompanionPath(string role, string path, string primary) =>
        primary[..(primary.LastIndexOf('/') + 1)] + Path.GetFileName(path);
    /// <inheritdoc />
    protected override IReadOnlyList<TensorSourceArtifact> SelectArtifacts(TensorModelCandidate candidate) =>
        CompleteSet(candidate, candidate.Artifacts.Single(artifact => artifact.Path == candidate.Selection.Variant));

    /// <inheritdoc />
    public override async Task<TensorModelResolution> ResolveCandidateAsync(TensorModelCandidate candidate, TensorBackendKind backend,
        ITensorArtifactReader reader, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();
        var evidence = new List<TensorCompatibilityEvidence>();
        TensorModelResolution Failure(TensorCompatibilityState state, string code, string message, TensorSourceState sourceState = TensorSourceState.Available) =>
            new(new(state, [new(code, message)], evidence.ToArray(), sourceState));
        IReadOnlyList<TensorBackendKind> compatible = GgmlRuntimeOwner.CompatibleBackends(backend, _runtimeOwner.Plan.TargetRid);
        if (!compatible.Any(TensorLlmModels.Backends.Contains))
        { return Failure(TensorCompatibilityState.Unsupported, "backend-unsupported", "This Qwen adapter does not support the selected backend."); }
        TensorSourceArtifact[] matches = candidate.Artifacts.Where(artifact => artifact.Path == candidate.Selection.Variant).ToArray();
        if (matches.Length != 1) { return Failure(TensorCompatibilityState.Unverified, "variant-missing", "The selected artifact is missing or ambiguous in the candidate."); }
        TensorSourceArtifact selected = matches[0];
        if (!selected.Path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
        { return Failure(TensorCompatibilityState.Unsupported, "format-unsupported", "This Qwen adapter requires GGUF artifacts."); }
        try
        {
            TensorSourceArtifact[] artifacts = CompleteSet(candidate, selected);
            if (string.IsNullOrWhiteSpace(candidate.License) || artifacts.Any(artifact => artifact.Length is not > 0
                || string.IsNullOrWhiteSpace(artifact.Provenance.License) || artifact.Sha256 is not { Length: 64 } hash || !hash.All(Uri.IsHexDigit)))
            { return Failure(TensorCompatibilityState.Unverified, "pack-metadata-incomplete", "Every artifact needs a pinned source, length, SHA-256 and license before resolution."); }
            var metadataReader = new TensorMetadataReader(reader);
            var headers = new List<(TensorSourceArtifact Artifact, GgufHeader Header)>();
            foreach (TensorSourceArtifact artifact in artifacts)
            {
                GgufHeader header = await metadataReader.ReadAsync(artifact, stream => GgufHeader.Read(stream), cancellationToken).ConfigureAwait(false);
                headers.Add((artifact, header));
                evidence.Add(new(artifact.Path, "gguf-header", $"GGUF {header.Version}; {header.Tensors.Count} tensors; {header.DataOffset} header/alignment bytes."));
                if (headers.Count == 1 && header.Values.GetValueOrDefault("general.architecture") is "qwen3")
                { return Failure(TensorCompatibilityState.Unsupported, TensorInstalledPacks.RetiredArchitectureCode, TensorLlmModels.Qwen3RetiredReason); }
            }
            TensorModelArtifactDefinition[] manifest = artifacts.Select((artifact, index) => TensorResolvedPack.Artifact(artifact,
                index == 0 ? "model" : $"model-shard-{index + 1}", artifact.Path)).ToArray();
            TensorModelPackDefinition definition = TensorResolvedPack.Create(candidate with { Selection = candidate.Selection with { Variant = artifacts[0].Path } }, TensorCapability.Llm,
                TensorLlmModels.QwenRuntimeArchitecture, TensorLlmModels.Backends, manifest);
            QwenModelCompatibility.Validate(definition);
            QwenModelCompatibility.ValidateHeaders(headers, definition);
            string architecture = QwenModelCompatibility.ArchitectureOf(headers[0].Header.Values);
            evidence.Add(new(artifacts[0].Path, "qwen-schema", $"Complete {architecture} tensor, tokenizer, chat-template, dimension, quantization and shard schema accepted by the loading policy."));
            evidence.Add(new(artifacts[0].Path, "context-length", Convert.ToString(headers[0].Header.Values[$"{architecture}.context_length"], CultureInfo.InvariantCulture)!));
            evidence.Add(new(artifacts[0].Path, "backend", $"{backend} has a compatible declared backend. Device availability and model allocation are separate checks."));
            return new(new(TensorCompatibilityState.Supported, [], evidence.ToArray()), definition);
        }
        catch (TensorSourceException exception)
        { return Failure(TensorCompatibilityState.Unverified, exception.Code, exception.Message, exception.State); }
        catch (Exception exception) when (exception is InvalidDataException or NotSupportedException or OverflowException or FormatException or ArgumentException)
        { return Failure(TensorCompatibilityState.Unsupported, "qwen-schema", $"The checkpoint metadata does not satisfy this adapter's Qwen GGUF schema: {exception.Message}"); }
        catch (IOException exception)
        { return Failure(TensorCompatibilityState.Unverified, "metadata-incomplete", $"The complete checkpoint header could not be inspected: {exception.Message}"); }
    }

    private static TensorSourceArtifact[] CompleteSet(TensorModelCandidate candidate, TensorSourceArtifact selected)
    {
        Match match = ShardName().Match(selected.Path);
        if (!match.Success) { return [selected]; }
        int count = int.Parse(match.Groups["count"].Value, CultureInfo.InvariantCulture);
        int number = int.Parse(match.Groups["number"].Value, CultureInfo.InvariantCulture);
        if (count < 1 || number < 1 || number > count || count > candidate.Artifacts.Count)
        { throw new TensorSourceException(TensorSourceState.Unverified, "shards-missing", "The complete numbered shard set is not listed."); }
        var artifacts = new TensorSourceArtifact[count];
        for (int index = 0; index < count; index++)
        {
            string path = $"{match.Groups["prefix"].Value}-{index + 1:D5}-of-{count:D5}.gguf";
            TensorSourceArtifact[] matches = candidate.Artifacts.Where(artifact => artifact.Path == path).ToArray();
            artifacts[index] = matches.Length == 1 ? matches[0]
                : throw new TensorSourceException(TensorSourceState.Unverified, "shards-missing", "A required GGUF shard is missing or ambiguous in the candidate.");
        }
        return artifacts;
    }

    [GeneratedRegex(@"^(?<prefix>.+)-(?<number>\d{5})-of-(?<count>\d{5})\.gguf$", RegexOptions.CultureInvariant)]
    private static partial Regex ShardName();
}
