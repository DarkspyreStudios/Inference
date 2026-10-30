using System.Text.Json;
using Darkspyre.LocalServices;
using Darkspyre.LocalServices.Contracts;

namespace Darkspyre.Inference.Connect.HostTools;

/// <summary>Runs one MCP tools/call for a registered session and returns its MCP CallToolResult.</summary>
public delegate Task<JsonElement> HostToolsCallHandler(JsonElement parameters, CancellationToken cancellationToken);

/// <summary>
/// One subscription to the host-tools service. A host registers sessions and answers their calls; an MCP program lists
/// a session's tools and forwards its calls.
/// </summary>
public sealed class HostToolsClient : IAsyncDisposable
{
    // Register, unregister and list are in-memory operations in the service; 30 s only stops a stuck service from
    // holding a session's opening or an MCP tools/list forever.
    private const int MessageDeadline = 30_000;
    // A tool call runs as long as the host tool needs, including approvals; the agent CLI's cancellation ends it.
    private const int CallDeadline = int.MaxValue;
    private readonly IServiceSubscription _subscription;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, HostToolsCallHandler> _sessions = new(StringComparer.Ordinal);

    internal HostToolsClient(IServiceSubscription subscription)
    {
        _subscription = subscription;
        _subscription.CallbackHandler = AnswerAsync;
    }

    /// <summary>Completes when the subscription closes; its sessions are gone from the service from then on.</summary>
    public Task Closed => _subscription.Closed;

    /// <summary>Registers a session's MCP tools/list result and the handler that runs its calls. Disposal unregisters it.</summary>
    public async Task<IAsyncDisposable> RegisterAsync(string session, JsonElement toolsList, HostToolsCallHandler call,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(session);
        ArgumentNullException.ThrowIfNull(call);
        lock (_gate) _sessions.Add(session, call);
        try
        {
            await InvokeAsync(HostToolsProtocol.Register, new HostToolsRegistration(session, toolsList), MessageDeadline, cancellationToken).ConfigureAwait(false);
            return new Registration(this, session);
        }
        catch
        {
            lock (_gate) _sessions.Remove(session);
            throw;
        }
    }

    /// <summary>Returns a registered session's MCP tools/list result.</summary>
    public async Task<JsonElement> ListAsync(string session, CancellationToken cancellationToken = default) =>
        await InvokeAsync(HostToolsProtocol.List, new HostToolsSession(session), MessageDeadline, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The host-tools service returned no tool list.");

    /// <summary>Runs an MCP tools/call on the host that registered the session and returns its CallToolResult.</summary>
    public async Task<JsonElement> CallAsync(string session, JsonElement parameters, CancellationToken cancellationToken = default) =>
        await InvokeAsync(HostToolsProtocol.Call, new HostToolsCall(session, parameters), CallDeadline, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The host-tools service returned no tool result.");

    /// <summary>Releases the subscription. Every session registered through it leaves the service.</summary>
    public ValueTask DisposeAsync() => _subscription.DisposeAsync();

    private async Task<JsonElement?> InvokeAsync<T>(string operation, T message, int deadline, CancellationToken cancellationToken)
    {
        using var body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(message, HostToolsProtocol.Json));
        // The host-tools service keeps no store, so no operation of it takes a write lock and none is held back from the
        // connection's requests for one that does.
        ServiceCallResult result = await _subscription.InvokeAsync(operation, body, deadline, ServiceRequestClass.Read, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.Status != ServiceResultStatus.Succeeded)
            throw new IOException($"The host-tools service did not complete {operation}: {result.Status}{(result.Error is { } error ? $" ({error.Code})" : "")}.");
        return result.Body is { Length: > 0 } bytes ? JsonSerializer.Deserialize<JsonElement>(bytes) : null;
    }

    private async ValueTask<ServiceCallbackResult> AnswerAsync(CapabilityRequestMessage request, CancellationToken cancellationToken)
    {
        HostToolsCall? call = request.Operation == HostToolsProtocol.Call ? request.Payload.Deserialize<HostToolsCall>(HostToolsProtocol.Json) : null;
        HostToolsCallHandler? handler = null;
        if (call is not null) lock (_gate) handler = _sessions.GetValueOrDefault(call.Session);
        if (handler is null) return new(ServiceResultStatus.Failed, Error: new(ServiceErrorCode.InvalidState, Guid.NewGuid()));
        return new(ServiceResultStatus.Succeeded, await handler(call!.Parameters, cancellationToken).ConfigureAwait(false));
    }

    private async Task UnregisterAsync(string session)
    {
        lock (_gate) _sessions.Remove(session);
        if (_subscription.Closed.IsCompleted) return;
        await InvokeAsync(HostToolsProtocol.Unregister, new HostToolsSession(session), MessageDeadline, CancellationToken.None).ConfigureAwait(false);
    }

    private sealed class Registration(HostToolsClient client, string session) : IAsyncDisposable
    {
        private int _disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) await client.UnregisterAsync(session).ConfigureAwait(false);
        }
    }
}
