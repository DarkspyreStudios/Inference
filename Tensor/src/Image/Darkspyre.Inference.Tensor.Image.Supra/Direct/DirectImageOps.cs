using TensorSharp;
using TsTensor = TensorSharp.Tensor;

namespace Darkspyre.Inference.Tensor.Image.Supra.Direct;

/// <summary>Image operations over contiguous F32 channels-last <c>[batch, height, width, channels]</c> tensors.
/// Ported from the TensorSharp fork's <c>DirectImageOps</c>.</summary>
internal static class DirectImageOps
{
    /// <summary>Group normalization of <paramref name="input"/> into a new tensor, with an optional per-channel gain
    /// and bias.</summary>
    public static TsTensor GroupNorm(DirectContext context, TsTensor input, int groups, TsTensor? gain = null,
        TsTensor? bias = null, float epsilon = 1e-5f)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(input);
        ValidateImage(context, input);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(groups);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(epsilon);

        long batch = input.Sizes[0];
        long height = input.Sizes[1];
        long width = input.Sizes[2];
        long channels = input.Sizes[3];
        if (channels % groups != 0)
        {
            throw new ArgumentException($"Channel count {channels} must be divisible by group count {groups}.",
                nameof(groups));
        }

        ValidateAffine(gain, channels, nameof(gain));
        ValidateAffine(bias, channels, nameof(bias));

        long channelsPerGroup = channels / groups;
        using TsTensor groupedView = input.View(batch, height, width, groups, channelsPerGroup);
        using TsTensor groupedPermutation = groupedView.Permute(0, 3, 1, 2, 4);
        using TsTensor grouped = Ops.NewContiguous(groupedPermutation);
        using TsTensor groupedRows = grouped.View(batch * groups, height * width * channelsPerGroup);
        using TsTensor normalizedRows = DirectOps.LayerNorm(context, groupedRows, null, null, epsilon);
        using TsTensor normalizedGroups = normalizedRows.View(batch, groups, height, width, channelsPerGroup);
        using TsTensor outputPermutation = normalizedGroups.Permute(0, 2, 3, 1, 4);
        using TsTensor outputGroups = Ops.NewContiguous(outputPermutation);
        TsTensor output = outputGroups.View(batch, height, width, channels);
        try
        {
            using TsTensor outputRows = output.View(batch * height * width, channels);
            if (gain is not null)
            {
                DirectOps.MulColsRows(context, outputRows, gain);
            }

            if (bias is not null)
            {
                DirectOps.AddBiasRows(context, outputRows, bias);
            }

            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    /// <summary>Nearest-neighbour spatial upsample of <paramref name="input"/> by an integer
    /// <paramref name="scale"/> into a new tensor.</summary>
    public static TsTensor NearestUpsample2D(DirectContext context, TsTensor input, int scale = 2)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(input);
        ValidateImage(context, input);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scale);
        if (scale == 1)
        {
            return Ops.NewContiguous(input);
        }

        long batch = input.Sizes[0];
        long height = input.Sizes[1];
        long width = input.Sizes[2];
        long channels = input.Sizes[3];

        using TsTensor widthRows = input.View(batch * height * width, 1, channels);
        TsTensor? widthExpanded = null;
        try
        {
            widthExpanded = widthRows.CopyRef();
            for (int index = 1; index < scale; index++)
            {
                TsTensor previous = widthExpanded;
                // TensorSharp declares the result non-nullable; null allocates a new tensor.
                widthExpanded = Ops.Concat(null!, 1, previous, widthRows);
                previous.Dispose();
            }

            using TsTensor wide = widthExpanded.View(batch, height, width * scale, channels);
            using TsTensor heightRows = wide.View(batch * height, 1, width * scale * channels);
            TsTensor? heightExpanded = null;
            try
            {
                heightExpanded = heightRows.CopyRef();
                for (int index = 1; index < scale; index++)
                {
                    TsTensor previous = heightExpanded;
                    // TensorSharp declares the result non-nullable; null allocates a new tensor.
                    heightExpanded = Ops.Concat(null!, 1, previous, heightRows);
                    previous.Dispose();
                }

                using TsTensor resultView = heightExpanded.View(batch, height * scale, width * scale, channels);
                return Ops.NewContiguous(resultView);
            }
            finally
            {
                heightExpanded?.Dispose();
            }
        }
        finally
        {
            widthExpanded?.Dispose();
        }
    }

    private static void ValidateImage(DirectContext context, TsTensor input)
    {
        if (input.DimensionCount != 4)
        {
            throw new ArgumentException("Image tensor must have shape [batch, height, width, channels].",
                nameof(input));
        }

        if (input.ElementType != DType.Float32 || !input.IsContiguous())
        {
            throw new ArgumentException("Image tensor must be contiguous F32.", nameof(input));
        }

        if (!ReferenceEquals(input.Allocator, context.Allocator))
        {
            throw new ArgumentException("Image tensor must use the DirectContext allocator.", nameof(input));
        }
    }

    private static void ValidateAffine(TsTensor? value, long channels, string name)
    {
        if (value is null)
        {
            return;
        }

        if (value.ElementType != DType.Float32 || !value.IsContiguous() || value.DimensionCount != 1
            || value.Sizes[0] != channels)
        {
            throw new ArgumentException($"{name} must be a contiguous F32 tensor with shape [{channels}].", name);
        }
    }
}
