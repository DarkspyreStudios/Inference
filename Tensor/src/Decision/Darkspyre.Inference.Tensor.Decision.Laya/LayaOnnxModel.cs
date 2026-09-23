using System.Globalization;
using Microsoft.ML.OnnxRuntime;

namespace Darkspyre.Inference.Tensor.Decision.Laya;

internal readonly record struct LayaModelOutput(float[] Logits, float ActionProbability);

/// <summary>
/// Runs the exported Laya DecisionModel graph. The graph has fixed input shapes, so every sequence is
/// padded to <see cref="MaximumLength"/> tokens and <see cref="MaximumOptions"/> option slots.
/// </summary>
internal sealed class LayaOnnxModel : ILayaDecisionModel
{
    internal const string MaximumLengthKey = "laya.max_len";
    internal const string MaximumOptionsKey = "laya.max_markers";
    internal const string ExportVersionKey = "laya.export_version";
    internal const string SourceHashKey = "laya.source_sha256";

    private static readonly string[] InputNames = ["input_ids", "attention_mask", "marker_pos", "marker_mask", "qtype"];
    private static readonly string[] OutputNames = ["logits", "act_logits"];

    private readonly InferenceSession _session;
    private bool _disposed;

    private LayaOnnxModel(InferenceSession session, int maximumLength, int maximumOptions)
    {
        _session = session;
        MaximumLength = maximumLength;
        MaximumOptions = maximumOptions;
    }

    public int MaximumLength { get; }

    public int MaximumOptions { get; }

    /// <summary>Creates the session and validates the graph against the agent configuration.</summary>
    /// <exception cref="InvalidDataException">The graph's metadata or input shapes do not match.</exception>
    public static LayaOnnxModel Create(byte[] model, SessionOptions options, int configuredMaximumLength)
    {
        var session = new InferenceSession(model, options);
        try
        {
            Dictionary<string, string> metadata = session.ModelMetadata.CustomMetadataMap;
            int maximumLength = MetadataInt(metadata, MaximumLengthKey);
            int maximumOptions = MetadataInt(metadata, MaximumOptionsKey);
            if (!metadata.ContainsKey(ExportVersionKey) || !metadata.ContainsKey(SourceHashKey))
            {
                throw new InvalidDataException($"The Laya model is missing '{ExportVersionKey}' or '{SourceHashKey}' metadata.");
            }

            if (maximumLength != configuredMaximumLength)
            {
                throw new InvalidDataException(
                    $"The Laya model accepts {maximumLength} tokens, but the agent configuration declares {configuredMaximumLength}.");
            }

            RequireShape(session, "input_ids", maximumLength);
            RequireShape(session, "attention_mask", maximumLength);
            RequireShape(session, "marker_pos", maximumOptions);
            RequireShape(session, "marker_mask", maximumOptions);
            return new LayaOnnxModel(session, maximumLength, maximumOptions);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public LayaModelOutput Forward(LayaSequence sequence, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int tokens = sequence.TokenIds.Length;
        int options = sequence.MarkerPositions.Length;
        if (tokens == 0 || tokens > MaximumLength)
        {
            throw new ArgumentException($"Laya requires between 1 and {MaximumLength} tokens.", nameof(sequence));
        }

        if (options == 0 || options > MaximumOptions)
        {
            throw new ArgumentException($"Laya requires between 1 and {MaximumOptions} decision markers.", nameof(sequence));
        }

        var inputIds = new long[MaximumLength];
        var attentionMask = new long[MaximumLength];
        Array.Fill(inputIds, LayaTokenizer.PaddingTokenId);
        for (int index = 0; index < tokens; index++)
        {
            inputIds[index] = sequence.TokenIds[index];
            attentionMask[index] = 1;
        }

        var markerPositions = new long[MaximumOptions];
        var markerMask = new bool[MaximumOptions];
        for (int index = 0; index < options; index++)
        {
            markerPositions[index] = sequence.MarkerPositions[index];
            markerMask[index] = true;
        }

        using var runOptions = new RunOptions();
        using CancellationTokenRegistration terminate = cancellationToken.Register(static state => ((RunOptions)state!).Terminate = true, runOptions);
        using OrtValue inputIdsValue = OrtValue.CreateTensorValueFromMemory(inputIds, [1, MaximumLength]);
        using OrtValue attentionMaskValue = OrtValue.CreateTensorValueFromMemory(attentionMask, [1, MaximumLength]);
        using OrtValue markerPositionsValue = OrtValue.CreateTensorValueFromMemory(markerPositions, [1, MaximumOptions]);
        using OrtValue markerMaskValue = OrtValue.CreateTensorValueFromMemory(markerMask, [1, MaximumOptions]);
        using OrtValue questionTypeValue = OrtValue.CreateTensorValueFromMemory(new long[] { sequence.QuestionType }, [1]);
        IDisposableReadOnlyCollection<OrtValue> run;
        try
        {
            run = _session.Run(
                runOptions,
                InputNames,
                [inputIdsValue, attentionMaskValue, markerPositionsValue, markerMaskValue, questionTypeValue],
                OutputNames);
        }
        catch (OnnxRuntimeException exception) when (cancellationToken.IsCancellationRequested)
        {
            // A terminated run fails inside ONNX Runtime; the caller sees the cancellation it asked for.
            throw new OperationCanceledException("The Laya evaluation was cancelled.", exception, cancellationToken);
        }

        using IDisposableReadOnlyCollection<OrtValue> outputs = run;
        cancellationToken.ThrowIfCancellationRequested();

        float[] logits = outputs[0].GetTensorDataAsSpan<float>()[..options].ToArray();
        float actionProbability = Softmax(outputs[1].GetTensorDataAsSpan<float>())[0];
        return new LayaModelOutput(logits, actionProbability);
    }

    internal static float[] Softmax(ReadOnlySpan<float> logits)
    {
        float maximum = float.NegativeInfinity;
        foreach (float logit in logits) { maximum = MathF.Max(maximum, logit); }
        var probabilities = new float[logits.Length];
        float sum = 0f;
        for (int index = 0; index < logits.Length; index++)
        {
            probabilities[index] = MathF.Exp(logits[index] - maximum);
            sum += probabilities[index];
        }

        for (int index = 0; index < probabilities.Length; index++) { probabilities[index] /= sum; }
        return probabilities;
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        _session.Dispose();
    }

    private static int MetadataInt(Dictionary<string, string> metadata, string key) =>
        metadata.TryGetValue(key, out string? text)
        && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value)
        && value > 0
            ? value
            : throw new InvalidDataException($"The Laya model has missing or invalid '{key}' metadata.");

    private static void RequireShape(InferenceSession session, string input, int length)
    {
        if (!session.InputMetadata.TryGetValue(input, out NodeMetadata? node)
            || node.Dimensions is not [1, var actual]
            || actual != length)
        {
            throw new InvalidDataException($"The Laya model input '{input}' must have shape [1, {length}].");
        }
    }
}
