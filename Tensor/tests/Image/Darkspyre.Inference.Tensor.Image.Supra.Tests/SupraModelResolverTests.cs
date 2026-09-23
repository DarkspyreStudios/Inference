using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Tensor.Image.Supra.Tests;

public sealed class SupraModelResolverTests
{
    [Fact]
    public void InstalledCheckpointMetadataMatchesTheSharedPipelineSchema()
    {
        SupraMetadataFixture fixture = SupraMetadataFixture.Read();
        SupraCheckpointSchema.ValidateDit(fixture.DitInfo(), fixture.IntegerValues);
        SupraCheckpointSchema.ValidateText(SupraMetadataFixture.SafeInfo(fixture.Text));
        SupraCheckpointSchema.ValidateVae(SupraMetadataFixture.SafeInfo(fixture.Vae));
    }

    [Fact]
    public async Task BoundedResolutionAssemblesAllFourPinnedRolesAndPreservesBuiltinKey()
    {
        var source = new SupraMetadataFixture.Source(SupraMetadataFixture.Read());
        TensorModelResolution result = await new SupraModelResolver(source).ResolveAsync(new(source.Candidate.Selection));
        Assert.Equal(TensorCompatibilityState.Supported, result.Evaluation.State);
        Assert.Equal(TensorImageModels.Supra2Image.Key, result.Definition!.Key);
        Assert.Equal(4, result.Definition.Artifacts.Count);
        Assert.All(result.Definition.Artifacts, artifact => Assert.NotNull(artifact.Provenance));
        Assert.True(source.Reads.Sum(read => read.Length) < 1024 * 1024);
        Assert.All(source.Reads, read => Assert.Equal(TensorMetadataReadKind.HeaderRange, read.Kind));
        Assert.Contains(result.Evaluation.Evidence, item => item.Check == "text-tokenizer");
    }

    [Fact]
    public async Task SameSchemaFinetuneResolvesWithItsOwnKeyAndPinnedCompanions()
    {
        var source = new SupraMetadataFixture.Source(SupraMetadataFixture.Read());
        TensorSourceArtifact primary = source.Candidate.Artifacts[0] with
        {
            Sha256 = new('b', 64),
            Provenance = source.Candidate.Artifacts[0].Provenance with { Repository = "owner/finetune", Revision = new('2', 40) },
            DownloadUri = new("https://huggingface.co/owner/finetune/resolve/" + new string('2', 40) + "/model_final_ema.pt")
        };
        source.Candidate = source.Candidate with { Selection = source.Candidate.Selection with { Repository = "owner/finetune", Revision = new('2', 40) }, Artifacts = [primary] };
        TensorModelResolution result = await new SupraModelResolver(source).ResolveAsync(new(source.Candidate.Selection, TensorBackendKind.Cpu));
        Assert.Equal(TensorCompatibilityState.Supported, result.Evaluation.State);
        Assert.NotEqual(TensorImageModels.Supra2Image.Key, result.Definition!.Key);
        Assert.Equal("owner/finetune", result.Definition.Artifacts.Single(item => item.Role == "supra-model").Provenance!.Repository);
        Assert.Contains(result.Definition.Artifacts, item => item.Provenance!.Repository == "google/flan-t5-base");
    }

    [Theory]
    [InlineData("dit-shape")]
    [InlineData("dit-missing")]
    [InlineData("config")]
    [InlineData("text-shape")]
    [InlineData("vae-missing")]
    public async Task IncompatiblePipelineComponentCannotProduceAnInstallablePack(string change)
    {
        SupraMetadataFixture fixture = SupraMetadataFixture.Read();
        switch (change)
        {
            case "dit-shape": fixture.Dit["x_embed.weight"] = fixture.Dit["x_embed.weight"] with { Shape = [575, 16] }; break;
            case "dit-missing": fixture.Dit.Remove("blocks.13.self_attn.qkv.weight"); break;
            case "config": fixture.IntegerValues["patch"] = 4; break;
            case "text-shape": fixture.Text["encoder.block.11.layer.0.SelfAttention.q.weight"] = fixture.Text["encoder.block.11.layer.0.SelfAttention.q.weight"] with { Shape = [767, 768] }; break;
            case "vae-missing": fixture.Vae.Remove("decoder.up_blocks.2.upsamplers.0.conv.weight"); break;
        }
        var source = new SupraMetadataFixture.Source(fixture);
        TensorModelResolution result = await new SupraModelResolver(source).ResolveAsync(new(source.Candidate.Selection));
        Assert.Equal(TensorCompatibilityState.Unsupported, result.Evaluation.State);
        Assert.Null(result.Definition);
    }

    [Theory]
    [InlineData("text-model")]
    [InlineData("text-tokenizer")]
    [InlineData("vae-model")]
    public async Task MissingCompanionIsUnverifiedNotALoneInstallableDit(string role)
    {
        var source = new SupraMetadataFixture.Source(SupraMetadataFixture.Read()) { MissingRole = role };
        TensorModelResolution result = await new SupraModelResolver(source).ResolveAsync(new(source.Candidate.Selection));
        Assert.Equal(TensorCompatibilityState.Unverified, result.Evaluation.State);
        Assert.Equal(TensorSourceState.NotFound, result.Evaluation.SourceState);
        Assert.Null(result.Definition);
    }

    [Fact]
    public async Task MissingIntegrityAndUnsupportedBackendDoNotReadWeightHeaders()
    {
        var source = new SupraMetadataFixture.Source(SupraMetadataFixture.Read());
        var resolver = new SupraModelResolver(source);
        Assert.Equal(TensorCompatibilityState.Unsupported, (await resolver.EvaluateAsync(source.Candidate, TensorBackendKind.Mlx)).State);
        Assert.Equal(TensorCompatibilityState.Unverified, (await resolver.EvaluateAsync(source.Candidate with
        { Artifacts = [source.Candidate.Artifacts[0] with { Sha256 = null }] }, TensorBackendKind.Cpu)).State);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.EvaluateAsync(source.Candidate, TensorBackendKind.Cpu, new(true)));
        Assert.Empty(source.Reads);
    }
}
