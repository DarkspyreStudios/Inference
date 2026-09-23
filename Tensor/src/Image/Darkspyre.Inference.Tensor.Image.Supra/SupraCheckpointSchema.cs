using TensorSharp.Runtime;

namespace Darkspyre.Inference.Tensor.Image.Supra;

/// <summary>One metadata policy for both remote inspection and installed loading, before native allocation.</summary>
internal static class SupraCheckpointSchema
{
    internal static void ValidateDit(IReadOnlyDictionary<string, TorchTensorInfo> tensors, IReadOnlyDictionary<string, long> config)
    {
        if (!config.TryGetValue("patch", out long patch) || patch != 2
            || !config.TryGetValue("ctx_len", out long context) || context != SupraTextTokenizer.MaximumLength)
        { throw new InvalidDataException("Supra checkpoint architecture metadata is incompatible."); }
        void Shape(string name, params long[] shape)
        {
            if (!tensors.TryGetValue(name, out TorchTensorInfo? info) || !info.Shape.SequenceEqual(shape)
                || (info.Dtype is not (TorchStorageDtype.Float32 or TorchStorageDtype.BFloat16)
                    && !(name == "config.uncond_mask" && info.Dtype == TorchStorageDtype.Int64)))
            { throw new InvalidDataException($"Supra tensor '{name}' has an incompatible shape or dtype."); }
        }
        void Linear(string name, int output, int input)
        { Shape(name + ".weight", output, input); Shape(name + ".bias", output); }
        Shape("pos_embed", 1, 256, 576);
        Shape("config.uncond_text", SupraTextTokenizer.MaximumLength, FlanT5Encoder.HiddenSize);
        Shape("config.uncond_mask", SupraTextTokenizer.MaximumLength);
        Linear("x_embed", 576, 16);
        Linear("t_embed.mlp.0", 576, 256); Linear("t_embed.mlp.2", 576, 576);
        Linear("ctx_proj", 576, FlanT5Encoder.HiddenSize);
        for (int index = 0; index < 14; index++)
        {
            string prefix = $"blocks.{index}.";
            Linear(prefix + "self_attn.qkv", 1728, 576); Linear(prefix + "self_attn.proj", 576, 576);
            Linear(prefix + "cross_attn.q", 576, 576); Linear(prefix + "cross_attn.kv", 1152, 576);
            Linear(prefix + "cross_attn.proj", 576, 576);
            Linear(prefix + "mlp.0", 2304, 576); Linear(prefix + "mlp.2", 576, 2304);
            Linear(prefix + "adaln.1", 3456, 576);
        }
        Linear("final.adaln.1", 1152, 576); Linear("final.linear", 16, 576);
    }

    internal static void ValidateText(IReadOnlyDictionary<string, SafetensorTensorInfo> tensors)
    {
        SafeShape(tensors, "shared.weight", 32128, FlanT5Encoder.HiddenSize);
        SafeShape(tensors, "encoder.final_layer_norm.weight", 768);
        SafeShape(tensors, "encoder.block.0.layer.0.SelfAttention.relative_attention_bias.weight", 32, 12);
        for (int index = 0; index < 12; index++)
        {
            string prefix = $"encoder.block.{index}.";
            SafeShape(tensors, prefix + "layer.0.layer_norm.weight", 768);
            SafeShape(tensors, prefix + "layer.1.layer_norm.weight", 768);
            foreach (string name in new[] { "q", "k", "v", "o" })
            { SafeShape(tensors, prefix + $"layer.0.SelfAttention.{name}.weight", 768, 768); }
            SafeShape(tensors, prefix + "layer.1.DenseReluDense.wi_0.weight", 2048, 768);
            SafeShape(tensors, prefix + "layer.1.DenseReluDense.wi_1.weight", 2048, 768);
            SafeShape(tensors, prefix + "layer.1.DenseReluDense.wo.weight", 768, 2048);
        }
    }

    internal static void ValidateVae(IReadOnlyDictionary<string, SafetensorTensorInfo> tensors)
    {
        void Affine(string name, int size)
        { SafeShape(tensors, name + ".weight", size); SafeShape(tensors, name + ".bias", size); }
        void Conv(string name, int output, int input, int kernel)
        { SafeShape(tensors, name + ".weight", output, input, kernel, kernel); SafeShape(tensors, name + ".bias", output); }
        void Resnet(string name, int output, int input)
        {
            Affine(name + ".norm1", input); Affine(name + ".norm2", output);
            Conv(name + ".conv1", output, input, 3); Conv(name + ".conv2", output, output, 3);
            if (input != output || tensors.ContainsKey(name + ".conv_shortcut.weight"))
            { Conv(name + ".conv_shortcut", output, input, 1); }
        }
        Conv("post_quant_conv", 4, 4, 1); Conv("decoder.conv_in", 512, 4, 3);
        Resnet("decoder.mid_block.resnets.0", 512, 512); Resnet("decoder.mid_block.resnets.1", 512, 512);
        const string attention = "decoder.mid_block.attentions.0.";
        Affine(attention + "group_norm", 512);
        foreach (string name in new[] { "query", "key", "value", "proj_attn" })
        { SafeShape(tensors, attention + name + ".weight", 512, 512); SafeShape(tensors, attention + name + ".bias", 512); }
        int channels = 512;
        for (int block = 0; block < 4; block++)
        {
            int output = block < 2 ? 512 : block == 2 ? 256 : 128;
            string prefix = $"decoder.up_blocks.{block}.";
            for (int resnet = 0; resnet < 3; resnet++)
            { Resnet(prefix + $"resnets.{resnet}", output, channels); channels = output; }
            if (block < 3) { Conv(prefix + "upsamplers.0.conv", channels, channels, 3); }
            else if (tensors.ContainsKey(prefix + "upsamplers.0.conv.weight"))
            { throw new InvalidDataException("Supra's VAE requires exactly three upsampling blocks."); }
        }
        Affine("decoder.conv_norm_out", 128); Conv("decoder.conv_out", 3, 128, 3);
    }

    private static void SafeShape(IReadOnlyDictionary<string, SafetensorTensorInfo> tensors, string name, params long[] shape)
    {
        if (!tensors.TryGetValue(name, out SafetensorTensorInfo? info) || !info.Shape.SequenceEqual(shape)
            || info.Dtype is not (SafetensorDtype.F32 or SafetensorDtype.F16 or SafetensorDtype.BF16))
        { throw new InvalidDataException($"Pipeline tensor '{name}' has an incompatible shape or dtype."); }
    }
}
