using System.Security.Cryptography;
using System.Text;
using Darkspyre.Inference.Tensor.Common;
using Darkspyre.Inference.Tensor.Ggml;

namespace Darkspyre.Inference.Tensor.LLM.Qwen.Tests;

public sealed class QwenInstalledModelsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "darkspyre-qwen-installed-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }
    }

    [Fact]
    public async Task QwenReportsNoBuiltInAndSkipsAPackThatIsNotAGguf()
    {
        await using var owner = new GgmlRuntimeOwner(new(new("2026.10.11"), "osx-arm64", []), new FakeNativeLibraries());
        var provider = new QwenEngineProvider(owner);
        Assert.Empty(provider.BuiltInModels);
        TensorInstalledReport report = await ReportAsync(provider);
        Assert.Empty(report.Installed);
        Assert.Empty(report.Unsupported);

        await InstallAsync(provider, "source.gguf", "not a GGUF checkpoint"u8.ToArray());

        report = await ReportAsync(provider);
        Assert.Empty(report.Installed);
        Assert.Empty(report.Unsupported);
    }

    [Fact]
    public async Task QwenRetiresAQwen3HeaderAndAcceptsQwen35AndQwen4ExpHeaders()
    {
        await using var owner = new GgmlRuntimeOwner(new(new("2026.10.11"), "osx-arm64", []), new FakeNativeLibraries());
        var provider = new QwenEngineProvider(owner);
        TensorModelPackDefinition qwen35 = await InstallAsync(provider, "qwen35.gguf", Checkpoint("qwen35"));
        TensorModelPackDefinition qwen4 = await InstallAsync(provider, "qwen4exp.gguf", Checkpoint("qwen4exp"));
        TensorModelPackDefinition qwen3 = await InstallAsync(provider, "qwen3.gguf", Checkpoint("qwen3"));

        TensorInstalledReport report = await ReportAsync(provider);

        Assert.Equal(new[] { qwen35.Key, qwen4.Key }.OrderBy(key => key.ToString(), StringComparer.Ordinal),
            report.Installed.Select(model => model.Key).OrderBy(key => key.ToString(), StringComparer.Ordinal));
        TensorUnsupportedPack retired = Assert.Single(report.Unsupported);
        Assert.Equal((qwen3.Key, TensorLlmModels.Qwen3RetiredReason), (retired.Definition.Key, retired.Reason));
    }

    [Fact]
    public async Task CompanionsResolveWithTheirRolesAndBackendSetsOnlyForQwen4Exp()
    {
        Directory.CreateDirectory(_root);
        string projector = Path.Combine(_root, "mmproj.gguf"), draft = Path.Combine(_root, "draft.gguf");
        byte[] projectorBytes = Checkpoint("clip"), draftBytes = Checkpoint("qwen4exp", draft: true);
        await File.WriteAllBytesAsync(projector, projectorBytes);
        await File.WriteAllBytesAsync(draft, draftBytes);
        TensorArtifactLocation vision = new(TensorLlmModels.VisionProjectorRole, new TensorModelLocation.LocalFile(projector),
            ExpectedSha256: Convert.ToHexStringLower(SHA256.HashData(projectorBytes)));
        TensorArtifactLocation mtp = new(TensorLlmModels.MtpDraftRole, new TensorModelLocation.LocalFile(draft),
            ExpectedSha256: Convert.ToHexStringLower(SHA256.HashData(draftBytes)));
        var resolver = new QwenModelResolver();
        TensorModelSelection selection = await SelectionAsync("qwen4exp", Checkpoint("qwen4exp"));
        TensorModelResolution both = await resolver.ResolveAsync(selection with { Companions = [vision, mtp] });
        Assert.Equal(TensorCompatibilityState.Supported, both.Evaluation.State);
        Assert.Equal(["model", "mtp-draft", "vision-projector"], both.Definition!.Artifacts.Select(artifact => artifact.Role).Order(StringComparer.Ordinal));
        var ggml = new HashSet<TensorBackendKind> { TensorBackendKind.GgmlCpu, TensorBackendKind.GgmlMetal, TensorBackendKind.GgmlCuda, TensorBackendKind.GgmlVulkan };
        Assert.True(ggml.SetEquals(both.Definition.SupportedBackends));
        TensorModelResolution visionOnly = await resolver.ResolveAsync(selection with { Companions = [vision] });
        Assert.Equal(TensorCompatibilityState.Supported, visionOnly.Evaluation.State);
        Assert.True(ggml.Append(TensorBackendKind.Cpu).ToHashSet().SetEquals(visionOnly.Definition!.SupportedBackends));
        TensorModelSelection old = await SelectionAsync("qwen35", Checkpoint("qwen35"));
        TensorModelResolution refused = await resolver.ResolveAsync(old with { Companions = [vision, mtp] });
        Assert.Equal(TensorCompatibilityState.Unsupported, refused.Evaluation.State);
        Assert.Contains("supported for Qwen 3.8 Flash Next (qwen4exp) models only", Assert.Single(refused.Evaluation.Issues).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownGgmlTypeIsRefusedWithTheTensorNameAndType()
    {
        var resolver = new QwenModelResolver();
        TensorModelSelection selection = await SelectionAsync("gyro", Checkpoint("qwen4exp", tensorType: 145));
        TensorModelResolution resolution = await resolver.ResolveAsync(selection);
        Assert.Equal(TensorCompatibilityState.Unsupported, resolution.Evaluation.State);
        string message = Assert.Single(resolution.Evaluation.Issues).Message;
        Assert.Contains("token_embd.weight", message, StringComparison.Ordinal);
        Assert.Contains("GGML tensor type 145", message, StringComparison.Ordinal);
    }

    private async Task<TensorModelSelection> SelectionAsync(string name, byte[] bytes)
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, name + ".gguf");
        await File.WriteAllBytesAsync(path, bytes);
        return new(new TensorModelLocation.LocalFile(path), License: "Apache-2.0",
            ExpectedSha256: Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    private async Task<TensorInstalledReport> ReportAsync(QwenEngineProvider provider)
    {
        using var packs = new TensorModelPackManager(new([]), Path.Combine(_root, provider.Definition.ModelStoreKey));
        return await provider.GetInstalledModelsAsync(packs);
    }

    private async Task<TensorModelPackDefinition> InstallAsync(QwenEngineProvider provider, string name, byte[] bytes)
    {
        Directory.CreateDirectory(_root);
        string source = Path.Combine(_root, name);
        await File.WriteAllBytesAsync(source, bytes);
        var definition = new TensorModelPackDefinition
        {
            Key = new("imported/" + name, "test"),
            Capability = TensorCapability.Llm,
            RuntimeArchitecture = TensorLlmModels.QwenRuntimeArchitecture,
            License = "Apache-2.0",
            SupportedBackends = TensorLlmModels.Backends,
            Artifacts = [new() { Role = "model", RelativePath = name, Length = bytes.Length,
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), Sources = [new(new Uri(source).AbsoluteUri)] }]
        };
        QwenModelCompatibility.Validate(definition);
        using var packs = new TensorModelPackManager(new([definition]), Path.Combine(_root, provider.Definition.ModelStoreKey));
        await packs.InstallAsync(definition.Key);
        return definition;
    }

    // The smallest GGUF v3 checkpoint the Qwen 3.5 schema accepts: one full-attention layer of hidden size 8, a two-token
    // BPE vocabulary, a chat template and four F32 tensors whose data follows the 32-byte aligned header.
    private static byte[] Checkpoint(string architecture, uint tensorType = 0, bool draft = false)
    {
        (string Name, ulong[] Shape)[] tensors =
        [
            ("token_embd.weight", [8, 2]), (architecture == "qwen4exp" ? "output_hc_norm.weight" : "output_norm.weight", [8]),
            ("blk.0.attn_norm.weight", [8]), ("blk.0.ffn_norm.weight", [8])
        ];
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            var values = new List<(string Key, Action Write)>
            {
                ("general.architecture", () => String(writer, architecture)),
                ($"{architecture}.block_count", () => UInt32(writer, 1)),
                ($"{architecture}.embedding_length", () => UInt32(writer, 8)),
                ($"{architecture}.attention.head_count", () => UInt32(writer, 2)),
                ($"{architecture}.attention.head_count_kv", () => UInt32(writer, 2)),
                ($"{architecture}.context_length", () => UInt32(writer, 4096)),
                ($"{architecture}.full_attention_interval", () => UInt32(writer, 1)),
                ("tokenizer.ggml.model", () => String(writer, "gpt2")),
                ("tokenizer.ggml.tokens", () => Strings(writer, ["a", "b"])),
                ("tokenizer.ggml.merges", () => Strings(writer, ["a b"])),
                ("tokenizer.chat_template", () => String(writer, "{{ messages }}"))
            };
            foreach (string key in new[] { "ssm.inner_size", "ssm.state_size", "ssm.group_count", "ssm.time_step_rank", "ssm.conv_kernel" })
                values.Add(($"{architecture}.{key}", () => UInt32(writer, 1)));
            if (draft) values.Add(("qwen4exp.nextn_predict_layers", () => UInt32(writer, 1)));
            writer.Write("GGUF"u8);
            writer.Write(3u);
            writer.Write((ulong)tensors.Length);
            writer.Write((ulong)values.Count);
            foreach ((string key, Action write) in values)
            {
                Text(writer, key);
                write();
            }
            ulong offset = 0;
            foreach ((string name, ulong[] shape) in tensors)
            {
                Text(writer, name);
                writer.Write((uint)shape.Length);
                foreach (ulong dimension in shape) writer.Write(dimension);
                writer.Write(name == "token_embd.weight" ? tensorType : 0u); // F32 unless the test supplies an unknown type.
                writer.Write(offset);
                offset += shape.Aggregate(4UL, (bytes, dimension) => bytes * dimension);
                offset = (offset + 31) / 32 * 32;
            }
            writer.Write(new byte[(32 - stream.Length % 32) % 32]);
            writer.Write(new byte[offset]);
        }
        return stream.ToArray();
    }

    private static void Text(BinaryWriter writer, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        writer.Write((ulong)bytes.Length);
        writer.Write(bytes);
    }

    private static void String(BinaryWriter writer, string value)
    {
        writer.Write(8u);
        Text(writer, value);
    }

    private static void Strings(BinaryWriter writer, string[] values)
    {
        writer.Write(9u);
        writer.Write(8u);
        writer.Write((ulong)values.Length);
        foreach (string value in values) Text(writer, value);
    }

    private static void UInt32(BinaryWriter writer, uint value)
    {
        writer.Write(4u);
        writer.Write(value);
    }
}
