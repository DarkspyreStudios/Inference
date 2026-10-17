using System.Text.Json;
using Darkspyre.LocalServices;
using Darkspyre.LocalServices.Contracts;

namespace Darkspyre.Inference.Connect.HostTools;

/// <summary>
/// Routes host-tools requests. It keeps only an in-memory map from session ID to the registering host connection and the
/// session's tool list; a call is passed to that host as a callback.
/// </summary>
internal sealed class HostToolsRouter
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Route> _routes = new(StringComparer.Ordinal);

    /// <summary>The control host that carries callbacks. The backend sets it before it publishes the service.</summary>
    internal ServiceControlHost? Control { get; set; }

    internal async ValueTask<ServiceOperationResult> HandleAsync(ServiceRequestContext context, CancellationToken cancellationToken)
    {
        switch (context.Request.Operation)
        {
            case HostToolsProtocol.Register:
                HostToolsRegistration registration = await ReadAsync<HostToolsRegistration>(context, cancellationToken).ConfigureAwait(false);
                lock (_gate) _routes[registration.Session] = new(context.ConnectionId, JsonSerializer.SerializeToUtf8Bytes(registration.ToolsList));
                return new(ServiceResultStatus.Succeeded);
            case HostToolsProtocol.Unregister:
                HostToolsSession removed = await ReadAsync<HostToolsSession>(context, cancellationToken).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_routes.TryGetValue(removed.Session, out Route? owned) && owned.ConnectionId == context.ConnectionId)
                        _routes.Remove(removed.Session);
                }
                return new(ServiceResultStatus.Succeeded);
            case HostToolsProtocol.List:
                HostToolsSession listed = await ReadAsync<HostToolsSession>(context, cancellationToken).ConfigureAwait(false);
                return Find(listed.Session) is { } route ? new(ServiceResultStatus.Succeeded, new MemoryStream(route.ToolsList)) : UnknownSession();
            case HostToolsProtocol.Call:
                HostToolsCall call = await ReadAsync<HostToolsCall>(context, cancellationToken).ConfigureAwait(false);
                if (Find(call.Session) is not { } host) return UnknownSession();
                // The callback has the time left on the MCP program's request; at least 1 ms, which the callback requires.
                int deadline = (int)Math.Clamp(context.RemainingTime.TotalMilliseconds, 1, int.MaxValue);
                ServiceCallbackResult answer = await Control!.CallbackAsync(host.ConnectionId, context.Request.RequestId, HostToolsProtocol.Call,
                    JsonSerializer.SerializeToElement(call, HostToolsProtocol.Json), deadline, cancellationToken).ConfigureAwait(false);
                return answer is { Status: ServiceResultStatus.Succeeded, Payload: { } result }
                    ? new(ServiceResultStatus.Succeeded, new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(result)))
                    : new(answer.Status, Error: answer.Error);
            default:
                return new(ServiceResultStatus.Failed, Error: new(ServiceErrorCode.InvalidState, Guid.NewGuid()));
        }
    }

    /// <summary>Removes every session a closed host connection registered.</summary>
    internal Task ReleaseAsync(Guid connectionId)
    {
        lock (_gate)
        {
            foreach (string session in _routes.Where(route => route.Value.ConnectionId == connectionId).Select(route => route.Key).ToArray())
                _routes.Remove(session);
        }
        return Task.CompletedTask;
    }

    private Route? Find(string session)
    {
        lock (_gate) return _routes.GetValueOrDefault(session);
    }

    private static async Task<T> ReadAsync<T>(ServiceRequestContext context, CancellationToken cancellationToken) =>
        await JsonSerializer.DeserializeAsync<T>(context.Body, HostToolsProtocol.Json, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The host-tools request has no body.");

    private static ServiceOperationResult UnknownSession() => new(ServiceResultStatus.Failed, Error: new(ServiceErrorCode.InvalidState, Guid.NewGuid()));

    private sealed record Route(Guid ConnectionId, byte[] ToolsList);
}
