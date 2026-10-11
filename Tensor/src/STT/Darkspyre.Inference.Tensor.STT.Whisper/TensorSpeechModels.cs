using System.Collections.Frozen;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Tensor.STT.Whisper;

/// <summary>All twelve initial variants, pinned to actual publisher content rather than approximate model sizes.</summary>
public static class TensorSpeechModels
{
    /// <summary>The runtime architecture name of Whisper GGML packs.</summary>
    public const string WhisperRuntimeArchitecture = "whisper-ggml";
    /// <summary>The Hugging Face repository the Whisper weights come from.</summary>
    public const string Repository = "ggerganov/whisper.cpp";
    /// <summary>The pinned repository revision of the Whisper weights.</summary>
    public const string Revision = "5359861c739e955e79d9a303bcbc70fb988958b1";
    /// <summary>The built-in Whisper pack definitions.</summary>
    public static IReadOnlyList<TensorModelPackDefinition> Whisper { get; } = Array.AsReadOnly<TensorModelPackDefinition>([
        Create("base", 147951465L, "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe"),
        Create("base.en", 147964211L, "a03779c86df3323075f5e796cb2ce5029f00ec8869eee3fdfb897afe36c6d002"),
        Create("large-v1", 3094623691L, "7d99f41a10525d0206bddadd86760181fa920438b6b33237e3118ff6c83bb53d"),
        Create("large-v2", 3094623691L, "9a423fe4d40c82774b6af34115b8b935f34152246eb19e80e376071d3f999487"),
        Create("large-v3-turbo", 1624555275L, "1fc70f774d38eb169993ac391eea357ef47c88757ef72ee5943879b7e8e2bc69"),
        Create("large-v3", 3095033483L, "64d182b440b98d5203c4f9bd541544d84c605196c4f7b845dfa11fb23594d1e2"),
        Create("medium", 1533763059L, "6c14d5adee5f86394037b4e4e8b59f1673b6cee10e3cf0b11bbdbee79c156208"),
        Create("medium.en", 1533774781L, "cc37e93478338ec7700281a7ac30a10128929eb8f427dda2e865faa8f6da4356"),
        Create("small", 487601967L, "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b"),
        Create("small.en", 487614201L, "c6138d6d58ecc8322097e0f987c32f1be8bb0a18532a3f88f734d1bbf9c41e5d"),
        Create("tiny", 77691713L, "be07e048e1e599ad46341c8d2a135645097a538221678b7acdd1b1919c6e1b21"),
        Create("tiny.en", 77704715L, "921e4cf8686fdd993dcd081a5da5b6c365bfde1162e72b08d75ac75289920b1f")
    ]);

    private static TensorModelPackDefinition Create(string variant, long bytes, string hash)
    {
        string filename = $"ggml-{variant}.bin";
        return new()
        {
            Key = new($"whisper/{variant}", Revision),
            Capability = TensorCapability.SpeechTranscription,
            RuntimeArchitecture = WhisperRuntimeArchitecture,
            License = "MIT",
            ProjectUri = new($"https://huggingface.co/{Repository}"),
            SupportedBackends = new[] { TensorBackendKind.Cpu }.ToFrozenSet(),
            Artifacts = Array.AsReadOnly<TensorModelArtifactDefinition>([new()
            {
                Role = "model", RelativePath = filename, Length = bytes, Sha256 = hash,
                Provenance = new(new("https://huggingface.co/"), Repository, Revision, filename, "MIT"),
                Sources = Array.AsReadOnly<Uri>([new($"https://huggingface.co/{Repository}/resolve/{Revision}/{filename}")])
            }])
        };
    }

    internal static TensorModelPackDefinition? KnownContent(TensorModelPackDefinition definition) =>
        definition.Artifacts.Count == 1 ? KnownContent(definition.Artifacts[0].Length, definition.Artifacts[0].Sha256) : null;

    internal static TensorModelPackDefinition? KnownContent(long? length, string? hash) => Whisper.FirstOrDefault(pack =>
        pack.Artifacts[0].Length == length && string.Equals(pack.Artifacts[0].Sha256, hash, StringComparison.OrdinalIgnoreCase));

    internal static SpeechModelInfo Info(TensorModelPackDefinition definition)
    {
        TensorModelPackDefinition? known = KnownContent(definition);
        bool? english = known is null ? null : known.Key.ModelId.EndsWith(".en", StringComparison.Ordinal);
        InferenceSupport translation = known is null ? InferenceSupport.Unknown
            : english == false && !known.Key.ModelId.EndsWith("-turbo", StringComparison.Ordinal)
                ? InferenceSupport.Supported : InferenceSupport.Unsupported;
        return new(definition.Key, known?.Key.ModelId ?? definition.Key.ModelId,
            definition.Artifacts.Sum(artifact => artifact.Length), english, translation);
    }
}
