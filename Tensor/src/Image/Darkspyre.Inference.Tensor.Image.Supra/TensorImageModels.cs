using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Tensor.Image.Supra;

public static class TensorImageModels
{
    public const string SupraRuntimeArchitecture = "supra2-dit-flan-t5-sd-vae";
    private const string SupraRevision = "10dec6e4b4b5d1c44fd1d7d3fe5e50137333da5b";
    private const string T5Revision = "7bcac572ce56db69c1ea7c8af255c5d7c9672fc2";
    private const string VaeRevision = "31f26fdeee1355a5c34592e401dd41e45d25a493";

    public static TensorModelPackDefinition Supra2Image { get; } = new()
    {
        Key = new TensorModelKey("SupraLabs/Supra2-IMG", SupraRevision),
        Capability = TensorCapability.Image,
        RuntimeArchitecture = SupraRuntimeArchitecture,
        License = "Apache-2.0 AND MIT",
        ProjectUri = new Uri("https://huggingface.co/SupraLabs/Supra2-IMG"),
        SupportedBackends = new HashSet<TensorBackendKind>
        {
            TensorBackendKind.Cpu,
            TensorBackendKind.Cuda
        },
        Artifacts =
        [
            Artifact(
                "supra-model",
                "supra/model_final_ema.pt",
                416_651_529,
                "e96aa0537e0fd120af82b3a606dd50c90ae079ac7aa97b38a8da4882daeeee48",
                "SupraLabs/Supra2-IMG",
                SupraRevision,
                "model_final_ema.pt"),
            Artifact(
                "text-model",
                "flan-t5/model.safetensors",
                990_345_061,
                "1dfb70afdcedceb9f9fae2f9b68e004ad934361fb35b9b2bd50b45ea90790fc8",
                "google/flan-t5-base",
                T5Revision,
                "model.safetensors"),
            Artifact(
                "text-tokenizer",
                "flan-t5/spiece.model",
                791_656,
                "d60acb128cf7b7f2536e8f38a5b18a05535c9e14c7a355904270e15b0945ea86",
                "google/flan-t5-base",
                T5Revision,
                "spiece.model"),
            Artifact(
                "vae-model",
                "sd-vae-ft-mse/diffusion_pytorch_model.safetensors",
                334_643_276,
                "a1d993488569e928462932c8c38a0760b874d166399b14414135bd9c42df5815",
                "stabilityai/sd-vae-ft-mse",
                VaeRevision,
                "diffusion_pytorch_model.safetensors")
        ]
    };

    private static TensorModelArtifactDefinition Artifact(
        string role,
        string relativePath,
        long length,
        string sha256,
        string repository,
        string revision,
        string remotePath) =>
        new()
        {
            Role = role,
            RelativePath = relativePath,
            Length = length,
            Sha256 = sha256,
            Provenance = new(new("https://huggingface.co/"), repository, revision, remotePath,
                role == "vae-model" ? "MIT" : "Apache-2.0"),
            Sources =
            [
                new Uri($"https://huggingface.co/{repository}/resolve/{revision}/{remotePath}?download=true")
            ]
        };
}
