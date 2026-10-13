using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>
/// Owns passive quota observations for one dependency generation. Share one instance between its
/// chat and native-agent facades. Its owning service provider disposes it when that generation retires.
/// </summary>
public sealed class LlmAccountUsageObservations : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private long _resolutionOrder;
    private bool _disposed;

    internal Reservation Reserve(string connectionId)
    {
        lock (_gate) { return new(this, connectionId, checked(++_resolutionOrder)); }
    }

    private Binding Bind(Reservation reservation, LlmConnection connection)
    {
        string scope = ProviderContinuationStateCodec.Scope(connection);
        string connectionId = reservation.ConnectionId;
        lock (_gate)
        {
            if (_disposed) { return new(this, connectionId, null); }
            if (!_entries.TryGetValue(connectionId, out Entry? entry)
                || entry.Version != connection.Version || entry.Transport != connection.Transport || entry.Scope != scope)
            {
                // Versions are opaque. Resolution start order fences an old completion without
                // inventing an ordering for the host's concurrency tokens.
                if (entry is not null && reservation.Order < entry.ResolutionOrder)
                { return new(this, connectionId, null); }
                entry?.Clear();
                entry = new(connection.Version, connection.Transport, scope, reservation.Order);
                _entries[connectionId] = entry;
            }
            entry.ResolutionOrder = Math.Max(entry.ResolutionOrder, reservation.Order);
            return new(this, connectionId, entry);
        }
    }

    private LlmAccountUsageSnapshot? Get(Reservation reservation, LlmConnection connection)
    {
        Binding binding = Bind(reservation, connection);
        lock (_gate)
        {
            return !_disposed && _entries.TryGetValue(reservation.ConnectionId, out Entry? current)
                && ReferenceEquals(current, binding.Entry) ? current.Snapshot : null;
        }
    }

    internal sealed class Reservation(LlmAccountUsageObservations owner, string connectionId, long order)
    {
        internal string ConnectionId { get; } = connectionId;
        internal long Order { get; } = order;
        internal Binding Bind(LlmConnection connection) => owner.Bind(this, connection);
        internal LlmAccountUsageSnapshot? Get(LlmConnection connection) => owner.Get(this, connection);
    }

    private void Record(Binding binding, LlmAccountUsageSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_disposed || binding.Entry is null || !_entries.TryGetValue(binding.ConnectionId, out Entry? current)
                || !ReferenceEquals(current, binding.Entry)) { return; }
            foreach (LlmQuotaWindow quota in snapshot.Quotas)
            {
                if (!current.Quotas.TryGetValue(quota.Id, out LlmQuotaWindow? prior)
                    || prior.Observation?.ObservedAt <= quota.Observation?.ObservedAt)
                { current.Quotas[quota.Id] = quota; }
            }
            foreach (LlmCreditBalance credit in snapshot.Credits)
            {
                if (!current.Credits.TryGetValue(credit.Id, out LlmCreditBalance? prior)
                    || prior.Observation?.ObservedAt <= credit.Observation?.ObservedAt)
                { current.Credits[credit.Id] = credit; }
            }
            current.Snapshot = new()
            {
                RetrievedAt = current.Snapshot?.RetrievedAt > snapshot.RetrievedAt ? current.Snapshot.RetrievedAt : snapshot.RetrievedAt,
                Quotas = Array.AsReadOnly(current.Quotas.Values.ToArray()),
                Credits = Array.AsReadOnly(current.Credits.Values.ToArray())
            };
        }
    }

    /// <summary>Fences further observations and releases all generation-owned measurements.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (Entry entry in _entries.Values)
            { entry.Clear(); }
            _entries.Clear();
        }
    }

    internal sealed class Binding(LlmAccountUsageObservations owner, string connectionId, Entry? entry)
    {
        internal string ConnectionId { get; } = connectionId;
        internal Entry? Entry { get; } = entry;
        internal void Record(LlmAccountUsageSnapshot snapshot) => owner.Record(this, snapshot);
    }

    internal sealed class Entry(string version, LlmTransport transport, string scope, long resolutionOrder)
    {
        internal string Version { get; } = version;
        internal LlmTransport Transport { get; } = transport;
        internal string Scope { get; } = scope;
        internal long ResolutionOrder { get; set; } = resolutionOrder;
        internal Dictionary<string, LlmQuotaWindow> Quotas { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, LlmCreditBalance> Credits { get; } = new(StringComparer.Ordinal);
        internal LlmAccountUsageSnapshot? Snapshot { get; set; }
        internal void Clear() { Quotas.Clear(); Credits.Clear(); Snapshot = null; }
    }
}
