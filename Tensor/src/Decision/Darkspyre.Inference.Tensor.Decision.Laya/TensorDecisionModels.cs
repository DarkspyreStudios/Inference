using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Tensor.Decision.Laya;

public static class TensorDecisionModels
{
    public static TensorModelPackDefinition LayaTypedDecisions { get; } = new()
    {
        Key = new TensorModelKey(
            "convaiinnovations/laya-typed-decisions",
            "f9ab0b228f0fc0f14d873dbc99038f135c2da1b2"),
        Capability = TensorCapability.Decision,
        RuntimeArchitecture = "laya-modernbert",
        License = "Apache-2.0",
        ProjectUri = new Uri("https://huggingface.co/convaiinnovations/laya-typed-decisions"),
        SupportedBackends = new HashSet<TensorBackendKind>
        {
            TensorBackendKind.Cpu,
            TensorBackendKind.Cuda
        },
        Artifacts =
        [
            Artifact(
                "model",
                "model.safetensors",
                842_609_220,
                "4fa56de72383a9d3efa9cfa78955733c81b9fc8067a587ca4beb82c78107a24e"),
            Artifact(
                "encoder-config",
                "encoder/config.json",
                2_084,
                "5268d24ad3b77c8151de5dcb0762ba4391619aad9ab0bda33e36fb083cfeae6d"),
            Artifact(
                "agent-config",
                "rl_agent_config.json",
                847,
                "ebf0cd524d92342a6be5e48e9fca3d7c2babfb5a56ccd79d2171ef5d8c7f7be8"),
            Artifact(
                "tokenizer",
                "tokenizer/tokenizer.json",
                3_583_228,
                "6c8aaa9a542084f2457eab775d4eeb51f92a70c0fd9de28d5edb0ddec3c08d30"),
            Artifact(
                "tokenizer-config",
                "tokenizer/tokenizer_config.json",
                337,
                "08d4cf3ac4dca381759441b85b91a6d40e688471dcd33d15d6649eb0a9a854d1")
        ]
    };

    private static TensorModelArtifactDefinition Artifact(
        string role,
        string relativePath,
        long length,
        string sha256) =>
        new()
        {
            Role = role,
            RelativePath = relativePath,
            Length = length,
            Sha256 = sha256,
            Sources =
            [
                new Uri(
                    $"https://huggingface.co/convaiinnovations/laya-typed-decisions/resolve/" +
                    $"{LayaTypedDecisionsRevision}/{relativePath}?download=true")
            ]
        };

    private const string LayaTypedDecisionsRevision = "f9ab0b228f0fc0f14d873dbc99038f135c2da1b2";
}
