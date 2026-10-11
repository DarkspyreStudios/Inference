using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Decision;
using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Tensor.Decision.Laya;

/// <summary>
/// Measures Laya requests from a pack's tokenizer and agent configuration alone, without the model.
/// </summary>
public sealed class LayaInputMeter : IDecisionInputMeasurement
{
    private readonly ILayaTokenizer _tokenizer;
    private readonly LayaAgentConfiguration _configuration;

    internal LayaInputMeter(ILayaTokenizer tokenizer, LayaAgentConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(tokenizer);
        ArgumentNullException.ThrowIfNull(configuration);
        _tokenizer = tokenizer;
        _configuration = configuration;
    }

    /// <summary>Loads a meter from a tokenizer file and an agent configuration file. Throws <see cref="InferenceException"/> when a file is missing or malformed.</summary>
    public static async Task<LayaInputMeter> LoadAsync(
        string tokenizer,
        string agentConfiguration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokenizer);
        ArgumentNullException.ThrowIfNull(agentConfiguration);
        try
        {
            LayaTokenizer loadedTokenizer = await LayaTokenizer.CreateAsync(tokenizer, cancellationToken).ConfigureAwait(false);
            LayaAgentConfiguration configuration = await LayaAgentConfiguration.LoadAsync(
                agentConfiguration, cancellationToken).ConfigureAwait(false);
            return new LayaInputMeter(loadedTokenizer, configuration);
        }
        catch (FileNotFoundException exception)
        {
            throw new InferenceException(InferenceFailureCode.ModelUnavailable, exception.Message, exception);
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException)
        {
            throw new InferenceException(
                InferenceFailureCode.ModelCorrupt,
                "The Laya tokenizer or agent configuration is malformed.",
                exception);
        }
    }

    /// <inheritdoc />
    public Task<DecisionInputMeasurement> MeasureAsync(
        DecisionInferenceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        LayaDecisionInference.ValidateRequest(request);
        return Task.FromResult(Measure(request, cancellationToken));
    }

    internal DecisionInputMeasurement Measure(DecisionInferenceRequest request, CancellationToken cancellationToken)
    {
        int stateTokens = LayaSequenceBuilder.EncodeState(_tokenizer, request.State).Count;
        var budgets = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (DecisionQuestion question in request.Questions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LayaFrame frame = LayaSequenceBuilder.Frame(
                _tokenizer, question, _configuration.MaximumLength, _configuration.MaximumHeadLength);
            budgets.Add(question.Id, frame.StateBudget);
        }

        return new DecisionInputMeasurement { StateTokens = stateTokens, StateTokenBudgets = budgets };
    }
}
