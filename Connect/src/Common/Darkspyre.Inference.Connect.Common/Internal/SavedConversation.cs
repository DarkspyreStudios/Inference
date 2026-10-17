using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>
/// The saved history of a conversation that runs on provider sessions it opens from that history, such as a CLI
/// process, and the measure of its next send. The conversation records each turn here and reports each model step's
/// usage; it reads the context status and the compaction limits from here. Every member is thread-safe.
/// </summary>
internal sealed class SavedConversation
{
    private readonly string _connectionId;
    private readonly LlmConnection _connection;
    private readonly ILlmContextEstimator _estimator;
    private readonly LearnedModelContext _learned;
    private readonly LlmAgentExecutionOptions _execution;
    // The text every provider session receives ahead of the history, such as instructions and a tool list.
    private readonly string? _framing;
    private readonly IInferenceResourceAccess? _resources;
    private readonly Func<string, LlmModel> _model;
    private readonly Lock _sync = new();
    private readonly Dictionary<string, long> _recordedAt = new(StringComparer.Ordinal);
    private LlmAgentContext _saved;
    // The last step's reported input and output, and the record sequence at that report.
    private long? _reportedTokens;
    private long _reportedMark;
    private long _sequence;
    // The local estimate of the first step of the open provider session, observed against its reported input.
    private long? _firstStepEstimate;

    /// <summary>
    /// A saved history of <paramref name="history"/>, measured with the provider chat client's estimator and the
    /// model's window and output limit that <paramref name="model"/> returns for a model id.
    /// </summary>
    internal SavedConversation(string connectionId, LlmConnection connection, ILlmContextEstimator estimator,
        LearnedModelContext learned, LlmAgentExecutionOptions execution, string? framing, IInferenceResourceAccess? resources,
        Func<string, LlmModel> model, LlmAgentContext history)
    {
        _connectionId = connectionId;
        _connection = connection;
        _estimator = estimator;
        _learned = learned;
        _execution = execution;
        _framing = framing;
        _resources = resources;
        _model = model;
        _saved = history;
    }

    /// <summary>The saved history at its current revision.</summary>
    internal LlmAgentContext Saved
    {
        get { lock (_sync) { return _saved; } }
    }

    /// <summary>Replaces the entry with the same id in place, or appends it, as the next revision.</summary>
    internal void Upsert(LlmAgentContextEntry entry)
    {
        lock (_sync)
        {
            var entries = _saved.Entries.ToList();
            int index = entries.FindIndex(existing => existing.Id == entry.Id);
            if (index >= 0) entries[index] = entry;
            else entries.Add(entry);
            _saved = _saved with { Entries = entries, Revision = checked(_saved.Revision + 1) };
            _recordedAt[entry.Id] = ++_sequence;
        }
    }

    /// <summary>Removes the entry with this id, as the next revision.</summary>
    internal void Remove(string id)
    {
        lock (_sync)
        {
            _saved = _saved with { Entries = [.. _saved.Entries.Where(entry => entry.Id != id)], Revision = checked(_saved.Revision + 1) };
            _recordedAt.Remove(id);
        }
    }

    /// <summary>
    /// A provider session opened with <paramref name="opening"/>. With <paramref name="replaceHistory"/>, the opening
    /// becomes the next revision of the saved history. Earlier reports describe another session, so they are dropped.
    /// Returns the saved history's revision.
    /// </summary>
    internal long Install(LlmAgentContext opening, bool replaceHistory)
    {
        lock (_sync)
        {
            if (replaceHistory) _saved = opening with { Revision = checked(_saved.Revision + 1) };
            _reportedTokens = null;
            _recordedAt.Clear();
            _firstStepEstimate = null;
            return _saved.Revision;
        }
    }

    /// <summary>
    /// One model step's reported input and output tokens. The next measure starts from them. The open session's first
    /// report teaches the model's rate against the local estimate of that step.
    /// </summary>
    internal void ObserveReport(string modelId, int inputTokens, int? outputTokens)
    {
        lock (_sync)
        {
            _reportedTokens = (long)inputTokens + (outputTokens ?? 0);
            _reportedMark = _sequence;
            if (_firstStepEstimate is long estimate && Limits(modelId) is { } limits)
                _learned.ObserveReport(_connectionId, modelId, estimate, inputTokens, limits.UsableWindow);
            _firstStepEstimate = null;
        }
    }

    /// <summary>
    /// The context status of the next send with <paramref name="input"/>. With a reported step, its input and output
    /// plus the entries recorded after that report and the input; otherwise the whole request a new provider session
    /// would receive. With <paramref name="observe"/>, a whole-request estimate is kept for the session's first report.
    /// </summary>
    internal async Task<LlmAgentContextStatus> StatusAsync(string modelId, LlmChatMessage? input, bool observe,
        CancellationToken cancellationToken)
    {
        LlmAgentContext saved;
        long? reported;
        LlmChatMessage[] after;
        lock (_sync)
        {
            saved = _saved;
            reported = _reportedTokens;
            after = [.. saved.Entries.Where(entry => _recordedAt.TryGetValue(entry.Id, out long at) && at > _reportedMark)
                .Select(entry => entry.Message)];
        }
        LlmChatMessage[] added = input is null ? [] : [input];
        var resources = await ResolveAsync(saved, added, cancellationToken).ConfigureAwait(false);
        ContextEstimate estimate;
        if (reported is long tokens)
        {
            LlmChatMessage[] tail = [.. after, .. added];
            long local = await LocalAsync(modelId, null, tail, resources, cancellationToken).ConfigureAwait(false);
            estimate = new(tokens + Correct(modelId, local), IncludesProviderReport: true, EntirelyReported: tail.Length == 0);
        }
        else
        {
            long local = await LocalAsync(modelId, _framing, [.. saved.Entries.Select(entry => entry.Message), .. added],
                resources, cancellationToken).ConfigureAwait(false);
            if (observe) lock (_sync) { _firstStepEstimate = local; }
            estimate = new(Correct(modelId, local));
        }
        LlmModel model = _model(modelId);
        LlmAgentContextPolicy policy = ManagedAgentPolicy.Normalize(_execution, model).ContextPolicy;
        return ManagedAgentPolicy.Status(modelId, saved.Revision, estimate, policy, model, _learned.Limit(_connectionId, modelId));
    }

    /// <summary>
    /// The corrected size of the whole request a provider session opened with <paramref name="context"/> receives, with
    /// the estimate margin.
    /// </summary>
    internal async ValueTask<long> MeasureAsync(LlmAgentContext context, string modelId, IReadOnlyList<LlmChatMessage> added,
        CancellationToken cancellationToken) =>
        ManagedAgentPolicy.WithMargin(new ContextEstimate(
            Correct(modelId, await EstimateAsync(context, modelId, added, cancellationToken).ConfigureAwait(false)))).Tokens;

    /// <summary>The local, uncorrected size of the whole request a provider session opened with <paramref name="context"/> receives.</summary>
    internal async ValueTask<long> EstimateAsync(LlmAgentContext context, string modelId, IReadOnlyList<LlmChatMessage> added,
        CancellationToken cancellationToken)
    {
        var resources = await ResolveAsync(context, added, cancellationToken).ConfigureAwait(false);
        return await LocalAsync(modelId, _framing, [.. context.Entries.Select(entry => entry.Message), .. added], resources,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The corrected size of a text sent as one user message.</summary>
    internal async ValueTask<long?> EstimateTextAsync(string modelId, string text, CancellationToken cancellationToken) =>
        Correct(modelId, await LocalAsync(modelId, null, [new("user", text)], ResourceSizes.None, cancellationToken)
            .ConfigureAwait(false));

    /// <summary>The compaction limits of a model under the one limit rule; null when no window is known.</summary>
    internal CompactionLimits? Limits(string modelId)
    {
        LlmModel model = _model(modelId);
        return CompactionLimits.For(model.MaxContextTokens, _learned.Limit(_connectionId, modelId),
            model.MaxOutputTokens, ManagedAgentPolicy.Normalize(_execution, model).ContextPolicy);
    }

    private long Correct(string modelId, long local) => _learned.Correct(_connectionId, modelId, local);

    /// <summary>
    /// The local count of a request with these messages, as the provider's chat client counts it. A whole-request
    /// measure passes the framing as instructions.
    /// </summary>
    private async ValueTask<long> LocalAsync(string modelId, string? instructions, LlmChatMessage[] messages,
        IReadOnlyDictionary<InferenceResourceReference, InferenceResourceDescriptor?> resources,
        CancellationToken cancellationToken)
    {
        if (messages.Length == 0 && string.IsNullOrWhiteSpace(instructions)) return 0;
        return (await _estimator.EstimateAsync(_connection, new LlmChatRequest
        {
            ModelId = modelId,
            Instructions = instructions,
            Messages = messages
        }, resources, cancellationToken).ConfigureAwait(false))?.Tokens ?? 0;
    }

    private async Task<IReadOnlyDictionary<InferenceResourceReference, InferenceResourceDescriptor?>> ResolveAsync(
        LlmAgentContext context, IReadOnlyList<LlmChatMessage> added, CancellationToken cancellationToken)
    {
        InferenceResourceReference[] references = [.. context.Entries.SelectMany(entry => ResourceSizes.References(entry.Message))
            .Concat(context.SelectedResources).Concat(added.SelectMany(ResourceSizes.References))];
        return _resources is { } access && references.Length > 0
            ? await ResourceSizes.ResolveAsync(access, references, cancellationToken).ConfigureAwait(false)
            : ResourceSizes.None;
    }
}
