using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Realtime;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.Realtime;

/// <summary>Explicit OAuth-only realtime composition. Sessions remain independently caller-owned.</summary>
public sealed class XaiRealtimeProvider(HttpClient httpClient) : IRealtimeProvider, IDisposable, IAsyncDisposable
{
    private HttpClient? _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly LlmOperationLifetime _lifetime = new();
    private readonly LlmCleanupOutcomes _cleanup = new();
    private readonly ConditionalWeakTable<RealtimeConversationReference, Issuance> _receipts = new();
    public RealtimeTransport Transport => RealtimeTransport.XaiRealtime;
    public InferenceSetupDefinition SetupDefinition { get; } = new(new("darkspyre.connect.xai.realtime", "xAI realtime voice",
        InferenceImplementationKind.Connect, InferenceTask.RealtimeVoice), "Use the selected xAI OAuth connection for an explicit realtime session.");
    public Task<RealtimeCapabilities> GetCapabilitiesAsync(RealtimeConnection connection, string? modelId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var operation = _lifetime.Enter(cancellationToken);
        if (connection.Transport != Transport) throw XaiRealtimeProtocol.Invalid();
        Fence(operation.Token);
        return Task.FromResult(new RealtimeCapabilities
        {
            InputEncodings = Enum.GetValues<RealtimeAudioEncoding>().ToHashSet(),
            OutputEncodings = Enum.GetValues<RealtimeAudioEncoding>().ToHashSet(),
            SupportedHostedToolKinds = XaiRealtimeProtocol.HostedKinds.ToHashSet(),
            InputSampleRates = XaiRealtimeProtocol.Rates,
            OutputSampleRates = XaiRealtimeProtocol.Rates,
            SupportsManualTurns = true,
            SupportsServerVad = true,
            SupportsClientTools = true,
            SupportsStructuredToolOutput = true,
            SupportsResumption = true,
            SupportsBinaryTransport = true,
            SupportsPronunciationReplacement = true,
            SupportsForcedMessages = true
        });
    }
    public async Task<IRealtimeVoiceSession> OpenSessionAsync(RealtimeConnection connection, RealtimeSessionConfig config,
        InferenceStreamLimits limits, XaiFamilyBinding? xai = null, IInferenceResourceAccess? resources = null, CancellationToken cancellationToken = default)
    {
        using var operation = _lifetime.Enter(cancellationToken);
        using var consumption = SdkHttpConsumption.Enter(_http!, operation.Token);
        CancellationToken token = consumption.Token;
        RealtimeConnection admitted = XaiRealtimeProtocol.Connection(connection, xai);
        RealtimeSessionConfig options = XaiRealtimeProtocol.Config(config);
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.MaximumBufferedEvents <= 0 || limits.MaximumBufferedPayloadBytes <= 0 || limits.MaximumMessageBytes <= 0) throw XaiRealtimeProtocol.Invalid();
        if (options.Resumption?.Conversation is { } imported && !_receipts.TryGetValue(imported, out _)) throw XaiRealtimeProtocol.Unsupported();
        Fence(operation.Token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(admitted.RequestTimeout);
        XaiFamilyBinding acquired = await xai!.AcquireBoundAsync(selected =>
        {
            if (!string.Equals(admitted.Endpoint.AbsoluteUri.TrimEnd('/'), selected.Endpoint.TrimEnd('/'), StringComparison.Ordinal)) throw XaiRealtimeProtocol.Invalid();
        }, cancellationToken: deadline.Token).ConfigureAwait(false);
        OpeningFence();
        LlmConnection selected = await acquired.AcquireAsync(deadline.Token).ConfigureAwait(false);
        OpeningFence();
        string? expectedModel = ValidateReceipt(options.Resumption?.Conversation, xai, selected, options.ModelId);
        var toolSecrets = new List<string>();
        byte[] update = await XaiRealtimeProtocol.UpdateAsync(options, OpeningFence, deadline.Token, toolSecrets.Add).ConfigureAwait(false);
        if (update.LongLength > limits.MaximumMessageBytes) throw XaiRealtimeProtocol.Capacity();
        InferenceResourceSelectionVersion? scope = resources is null ? null : await SpeechResourceWriter.ReadScopeAsync(resources, OpeningFence, deadline.Token).ConfigureAwait(false);
        OpeningFence();
        var headers = new HttpRequestMessage();
        ClientWebSocket? socket = null; XaiRealtimeSession? session = null; XaiRealtimeSession? result = null;
        var failures = new List<Exception>();
        try
        {
            XaiRequestHeaders.Apply(headers, selected, false); headers.Headers.Accept.Clear();
            foreach (var pair in admitted.AdditionalHeaders)
                if (!headers.Headers.TryAddWithoutValidation(pair.Key, pair.Value)) throw XaiRealtimeProtocol.Invalid();
            var credential = LlmCredential.Parse(selected.Credential);
            var secrets = headers.Headers.SelectMany(pair => pair.Value).Concat(new[] { credential?.OAuth?.AccessToken, credential?.OAuth?.RefreshToken, credential?.OAuth?.IdToken }
                .OfType<string>()).Concat(toolSecrets).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
            socket = new ClientWebSocket(); socket.Options.CollectHttpResponseDetails = true;
            foreach (var pair in headers.Headers) socket.Options.SetRequestHeader(pair.Key, string.Join(", ", pair.Value));
            var query = new List<string>();
            if (options.ModelId is { } model) query.Add("model=" + Uri.EscapeDataString(model));
            if (options.Resumption?.Conversation is { } conversation) query.Add("conversation_id=" + Uri.EscapeDataString(conversation.ConversationId));
            var uri = new UriBuilder(ProviderHttp.BuildUri(selected, "realtime")) { Scheme = "wss", Port = -1, Query = string.Join("&", query) }.Uri;
            OpeningFence();
            try { await socket.ConnectAsync(uri, _http!, deadline.Token).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException)
            { OpeningFence(); throw XaiErrorClassifier.SpeechUpgradeFailure(socket.HttpStatusCode, error); }
            OpeningFence();
            session = new(socket, limits, resources, scope, options,
                (id, observedModel) => Issue(id, xai, selected, options.ModelId, observedModel), secrets,
                ObserveReceipt, InvalidateReceipt, expectedModel);
            socket = null;
            await session.InitializeAsync(update, deadline.Token).ConfigureAwait(false);
            OpeningFence(); result = session;
        }
        catch (Exception error) { failures.Add(error); }
        try { headers.Dispose(); }
        catch (Exception error) { AddCleanup(error, headers); }
        if (failures.Count != 0)
        {
            try { if (session is not null) await session.DisposeAsync().ConfigureAwait(false); else socket?.Dispose(); }
            catch (Exception error) { AddCleanup(error, (object?)session ?? socket!); }
            Throw(failures);
        }
        try { OpeningFence(); return consumption.Publish(result!); }
        catch (Exception original)
        {
            try { await result!.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error)
            {
                Exception cleanup = Cleanup(error, result!); _cleanup.Record(cleanup); throw new AggregateException(original, cleanup);
            }
            throw;
        }
        void OpeningFence() { Fence(token); consumption.Fence(); deadline.Token.ThrowIfCancellationRequested(); Fence(token); }
        void AddCleanup(Exception error, object owner) { Exception cleanup = Cleanup(error, owner); _cleanup.Record(cleanup); failures.Add(cleanup); }
    }

    private RealtimeConversationReference Issue(string id, XaiFamilyBinding binding, LlmConnection selected, string? routingModel, string? observedModel)
    {
        LlmCredential? credential = LlmCredential.Parse(selected.Credential);
        string? account = credential?.OAuth?.AccountId;
        if (account is not null && SecretRedactor.ContainsProtectedValue(account, credential, [])) throw XaiRealtimeProtocol.Malformed();
        byte[] bytes = XaiRealtimeProtocol.Message("xai-realtime-scope", writer =>
        {
            writer.WriteString("endpoint", selected.Endpoint); XaiRealtimeProtocol.WriteString(writer, "account", account);
            XaiRealtimeProtocol.WriteString(writer, "model", observedModel);
        });
        var reference = new RealtimeConversationReference(id, new("xai.realtime", Encoding.UTF8.GetString(bytes)));
        LlmOperationLifetime.Lease admission;
        try { admission = _lifetime.Enter(default); }
        catch (ObjectDisposedException) { return reference; }
        using (admission) _receipts.Add(reference, new(new(binding), selected.Version, selected.Endpoint, routingModel, observedModel));
        return reference;
    }
    private string? ValidateReceipt(RealtimeConversationReference? reference, XaiFamilyBinding binding, LlmConnection selected, string? routingModel)
    {
        if (reference is null) return null;
        if (_receipts.TryGetValue(reference, out Issuance? proof))
        {
            lock (proof)
                if (!proof.Contradicted && proof.Binding.TryGetTarget(out var original) && ReferenceEquals(original, binding) && proof.Version == selected.Version
                    && proof.Endpoint == selected.Endpoint && proof.RoutingModel == routingModel)
                    return proof.ObservedModel;
            throw XaiRealtimeProtocol.Unsupported();
        }
        // The selected owner has no pre-socket actual realtime-model observation. A routing
        // selector or imported scope cannot supply that missing cross-owner compatibility fact.
        throw XaiRealtimeProtocol.Unsupported();
    }
    private void ObserveReceipt(RealtimeConversationReference reference, string model)
    {
        LlmOperationLifetime.Lease admission;
        try { admission = _lifetime.Enter(default); }
        catch (ObjectDisposedException) { return; }
        using var admitted = admission;
        if (_receipts.TryGetValue(reference, out Issuance? proof))
        {
            lock (proof)
            {
                if (proof.ObservedModel is not null && proof.ObservedModel != model) proof.Contradicted = true;
                else proof.ObservedModel = model;
            }
        }
    }
    private void InvalidateReceipt(RealtimeConversationReference reference)
    {
        LlmOperationLifetime.Lease admission;
        try { admission = _lifetime.Enter(default); }
        catch (ObjectDisposedException) { return; }
        using var admitted = admission;
        if (_receipts.TryGetValue(reference, out Issuance? proof)) lock (proof) { proof.Contradicted = true; }
    }
    private sealed class Issuance(WeakReference<XaiFamilyBinding> binding, string version, string endpoint, string? routingModel, string? observedModel)
    {
        internal WeakReference<XaiFamilyBinding> Binding { get; } = binding;
        internal string Version { get; } = version;
        internal string Endpoint { get; } = endpoint;
        internal string? RoutingModel { get; } = routingModel;
        internal string? ObservedModel { get; set; } = observedModel;
        internal bool Contradicted { get; set; }
    }
    private void Fence(CancellationToken token)
    { token.ThrowIfCancellationRequested(); try { _lifetime.ThrowIfStopping(); } catch (ObjectDisposedException) { throw new OperationCanceledException(token); } }
    internal static Exception Cleanup(Exception error, object owner) => LlmResourcePreparation.AsCleanupFailure(
        error is OperationCanceledException canceled ? new SafeCancellation(canceled) : new SafeEvidence(error), refusedOwner: owner);
    private sealed class SafeEvidence(Exception original) : Exception("The owned realtime resource could not be released.")
    { internal Exception Original { get; } = original; }
    private sealed class SafeCancellation(OperationCanceledException original) : OperationCanceledException("The owned realtime cleanup was interrupted.", original.CancellationToken)
    { internal Exception Original { get; } = original; }
    internal static Exception TransportFailure(Exception error) => error is OperationCanceledException canceled
        ? new TransportCancellation(canceled) : new InferenceException(InferenceFailureCode.ExecutionFailed,
            "The realtime transport failed with unknown remote effects.", new TransportEvidence(error));
    private sealed class TransportEvidence(Exception original) : Exception("The realtime transport failed.")
    { internal Exception Original { get; } = original; }
    private sealed class TransportCancellation(OperationCanceledException original) : OperationCanceledException("The realtime transport was interrupted.", original.CancellationToken)
    { internal Exception Original { get; } = original; }
    internal static void Throw(List<Exception> failures)
    { if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw(); if (failures.Count > 1) throw new AggregateException(failures); }
    public void Dispose() => RetireAsync().GetAwaiter().GetResult();
    public ValueTask DisposeAsync() => new(RetireAsync());
    private Task RetireAsync() => _lifetime.RetireAsync(() => { _http = null; _receipts.Clear(); return _cleanup.ReleaseAsync(); });
}
