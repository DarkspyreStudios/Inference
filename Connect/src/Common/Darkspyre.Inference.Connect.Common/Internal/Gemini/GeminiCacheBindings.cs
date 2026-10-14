using System.Text.Json;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Derived receipt evidence for one facade, never a principal or provider catalog.</summary>
internal sealed class GeminiCacheBindings
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Binding> _bindings = new(StringComparer.Ordinal);
    private long _sequence;
    private bool _stopped;

    internal long BeginOperation() => Interlocked.Increment(ref _sequence);

    internal Binding Select(string connectionId, LlmConnection frozen, long sequence)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            if (!_bindings.TryGetValue(connectionId, out Binding? binding))
            { binding = new(frozen); _bindings.Add(connectionId, binding); }
            if (!SameSelection(binding.Connection, frozen))
            {
                if (sequence < binding.Sequence) throw Changed();
                binding.Connection = frozen;
                binding.Epoch++;
                binding.Scope = Guid.NewGuid().ToString("N");
                binding.NativeProject = null;
            }
            binding.Sequence = Math.Max(sequence, binding.Sequence);
            foreach (LlmExplicitCacheReference expired in binding.Observed.Keys
                .Where(reference => reference.ExpiresAt <= DateTimeOffset.UtcNow).ToArray())
                binding.Observed.Remove(expired);
            return binding;
        }
    }

    internal Admission Capture(Binding binding, LlmConnection snapshot, long sequence)
    {
        lock (_gate)
        {
            if (!SameSelection(binding.Connection, snapshot)) throw Changed();
            return new(this, binding, snapshot, binding.Epoch, binding.Scope, null, sequence);
        }
    }

    internal void ObserveNativeProject(Admission admission, string project)
    {
        lock (_gate)
        {
            Check(admission);
            if (admission.Binding.NativeProject is { } previous && previous != project) throw Changed();
            admission.Binding.NativeProject = project;
        }
    }

    internal Admission SelectReference(Admission operation, LlmExplicitCacheReference reference, bool allowDeleted)
    {
        lock (_gate)
        {
            Check(operation);
            if (reference.ExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow)
                throw new LlmContextCacheUnavailableException(LlmContextCacheUnavailableReason.Expired, reference);
            operation.Binding.Observed.TryGetValue(reference, out Observation? observed);
            if (observed?.Epoch != operation.Epoch || observed.Uncertain) observed = null;
            if (observed is { Deleted: true } && !allowDeleted)
                throw new LlmContextCacheUnavailableException(LlmContextCacheUnavailableReason.Missing, reference);
            return operation with { Reference = reference, Proof = observed, AllowDeleted = allowDeleted };
        }
    }

    internal Admission Admit(Admission operation, LlmContextCache receipt, Action ownerFence, bool afterMutation = false)
    {
        lock (_gate)
        {
            Check(operation);
            if (receipt.Reference.ScopeId != operation.Scope || receipt.Reference.ExpiresAt != receipt.ExpiresAt)
                throw new InvalidOperationException("The observed cache receipt does not match the admitted scope.");
            if (!afterMutation && operation.Binding.Observed.Values.Any(observed =>
                observed.Receipt.Reference.ResourceName == receipt.Reference.ResourceName
                && observed.Receipt.Reference.ModelId == receipt.Reference.ModelId
                && observed.MutationSequence > operation.Sequence)) throw Changed();
            if (operation.Binding.Observed.TryGetValue(receipt.Reference, out Observation? previous)
                && previous.Epoch == operation.Epoch && !previous.Deleted && !previous.Uncertain)
            {
                ownerFence();
                return operation with { Reference = receipt.Reference, Proof = previous };
            }
            // This is the owning facade's intrinsic fence, not a borrowed caller callback.
            ownerFence();
            var observed = new Observation(receipt, operation.Epoch);
            operation.Binding.Observed[receipt.Reference] = observed;
            return operation with { Reference = receipt.Reference, Proof = observed };
        }
    }

    internal Admission ChangedResource(Admission operation, bool deleted, bool uncertain, Action ownerFence)
    {
        lock (_gate)
        {
            if (!uncertain) { Check(operation); ownerFence(); }
            if (operation.Epoch != operation.Binding.Epoch || !SameSelection(operation.Binding.Connection, operation.Connection))
                return operation;
            long mutationSequence = Interlocked.Increment(ref _sequence);
            foreach (Observation observed in operation.Binding.Observed.Values.Where(observed =>
                observed.Receipt.Reference.ResourceName == operation.Reference!.ResourceName
                && observed.Receipt.Reference.ModelId == operation.Reference.ModelId))
            { observed.Deleted = deleted; observed.Uncertain = uncertain; observed.MutationSequence = mutationSequence; }
            return operation with { Proof = null };
        }
    }

    internal void Check(Admission admission)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            if (admission.Epoch != admission.Binding.Epoch
                || !SameSelection(admission.Binding.Connection, admission.Connection)
                || admission.Proof is { Uncertain: true }
                || admission.Proof is { Deleted: true } && !admission.AllowDeleted) throw Changed();
            if (admission.Reference is { } reference && reference.ExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow)
                throw new LlmContextCacheUnavailableException(LlmContextCacheUnavailableReason.Expired, reference);
        }
    }

    internal void Clear() { lock (_gate) { _stopped = true; _bindings.Clear(); } }

    private static bool SameSelection(LlmConnection left, LlmConnection right) =>
        left.Version == right.Version && left.Transport == right.Transport && left.Endpoint == right.Endpoint
        && left.Credential == right.Credential && left.Vertex == right.Vertex
        && left.CacheCredentialEvidence == right.CacheCredentialEvidence
        && SameOverrides(left.CapabilityOverrides, right.CapabilityOverrides)
        && left.ModelCapabilityOverrides.Count == right.ModelCapabilityOverrides.Count
        && left.ModelCapabilityOverrides.All(pair => right.ModelCapabilityOverrides.TryGetValue(pair.Key, out var value) && SameOverrides(pair.Value, value))
        && left.AdditionalHeaders.Count == right.AdditionalHeaders.Count
        && left.AdditionalHeaders.All(pair => right.AdditionalHeaders.TryGetValue(pair.Key, out string? value) && pair.Value == value)
        && left.ProviderOptions.Count == right.ProviderOptions.Count
        && left.ProviderOptions.All(pair => right.ProviderOptions.TryGetValue(pair.Key, out JsonElement value)
            && JsonElement.DeepEquals(pair.Value, value));

    private static bool SameOverrides(LlmCapabilityOverrides? left, LlmCapabilityOverrides? right) =>
        left is null || right is null ? left is null && right is null
        : left with { ReasoningLevels = null } == right with { ReasoningLevels = null }
            && (left.ReasoningLevels is null || right.ReasoningLevels is null
                ? left.ReasoningLevels is null && right.ReasoningLevels is null
                : left.ReasoningLevels.SetEquals(right.ReasoningLevels));

    private static InvalidOperationException Changed() => new("The selected cache binding changed during the admitted operation.");

    internal sealed class Binding(LlmConnection connection)
    {
        internal LlmConnection Connection { get; set; } = connection;
        internal long Sequence { get; set; }
        internal long Epoch { get; set; }
        internal string Scope { get; set; } = Guid.NewGuid().ToString("N");
        internal string? NativeProject { get; set; }
        internal Dictionary<LlmExplicitCacheReference, Observation> Observed { get; } = [];
    }

    internal sealed class Observation(LlmContextCache receipt, long epoch)
    {
        internal LlmContextCache Receipt { get; } = receipt;
        internal long Epoch { get; } = epoch;
        internal bool Deleted { get; set; }
        internal bool Uncertain { get; set; }
        internal long MutationSequence { get; set; }
    }

    internal sealed record Admission(GeminiCacheBindings Owner, Binding Binding, LlmConnection Connection,
        long Epoch, string Scope, LlmExplicitCacheReference? Reference, long Sequence,
        Observation? Proof = null, bool AllowDeleted = false)
    {
        internal void Check(LlmConnection connection, LlmExplicitCacheReference? reference = null)
        {
            Owner.Check(this);
            if (!SameSelection(Connection, connection) || reference is not null && Reference != reference) throw Changed();
        }
    }
}
