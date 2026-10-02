using System.Text.Json;
using System.Text.Json.Serialization;
using Darkspyre.Inference.Common;

namespace Darkspyre.Inference.Connect.LLM;

public sealed record XaiConnectionSelection([property: JsonRequired] string ConnectionId);

/// <summary>Borrows the selected credential-access owner; carries no independent credential cache.</summary>
public sealed class XaiFamilyBinding
{
    private readonly LlmConnectionAccess _access;
    private readonly LlmProviderService? _service;
    private readonly LlmConnection? _snapshot;

    public XaiFamilyBinding(XaiConnectionSelection selection, LlmConnectionAccess access,
        LlmProviderService? service = null)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentException.ThrowIfNullOrWhiteSpace(selection.ConnectionId);
        Selection = selection;
        _access = access ?? throw new ArgumentNullException(nameof(access));
        service?.ValidateXaiAccess(access);
        _service = service;
        Authorization = new BindingAuthorization(this);
    }

    private XaiFamilyBinding(XaiFamilyBinding source, LlmConnection snapshot)
    {
        Selection = source.Selection;
        _access = source._access;
        _service = source._service;
        _snapshot = snapshot;
        Authorization = new BindingAuthorization(this);
    }

    public XaiConnectionSelection Selection { get; }
    public IProviderAuthorization Authorization { get; }

    internal bool HasCatalogService => _service?.SupportsXaiSnapshot is true;

    internal void RequireCatalogService()
    {
        if (!HasCatalogService)
            throw new NotSupportedException("Cross-family catalog operations require the selected LLM facade.");
    }

    internal Task<T> RunCatalogAsync<T>(LlmConnection snapshot, Action fence,
        Func<HttpClient, LlmConnection, Action, CancellationToken, Task<T>> operation, CancellationToken token)
    {
        RequireCatalogService();
        return _service!.RunXaiSnapshotAsync(snapshot, fence, operation, token);
    }

    internal Task<T> RunFilesAsync<T>(LlmConnection snapshot, Action fence,
        Func<Providers.XaiFileClient, LlmConnection, Action, CancellationToken, Task<T>> operation, CancellationToken token)
    {
        RequireCatalogService();
        return _service!.RunXaiFilesSnapshotAsync(snapshot, fence, operation, token);
    }

    public Task<LlmConnection> AcquireAsync(CancellationToken cancellationToken = default)
        => AcquireAsync(null, cancellationToken);

    internal async Task<XaiFamilyBinding> AcquireBoundAsync(Action<LlmConnection> validate,
        CancellationToken cancellationToken = default)
    {
        LlmConnection snapshot = await AcquireAsync(validate, cancellationToken).ConfigureAwait(false);
        return new(this, snapshot);
    }

    private Task<LlmConnection> AcquireAsync(Action<LlmConnection>? validate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_snapshot is not null)
        {
            ValidateConnection(_snapshot);
            validate?.Invoke(_snapshot);
            return Task.FromResult(_snapshot);
        }
        return _access.AcquireAsync(Selection.ConnectionId, connection =>
        {
            ValidateConnection(connection);
            validate?.Invoke(connection);
        }, cancellationToken);
    }

    private static void ValidateConnection(LlmConnection connection)
    {
        Internal.RuntimeInputAdmission.Validate(connection);
        if (connection.Transport != LlmTransport.XaiResponses)
            throw new ArgumentException("A cross-family xAI binding requires the xAI transport.");
        if (!string.Equals(connection.Endpoint.TrimEnd('/'), (connection.Xai ?? new()).GetEndpoint(), StringComparison.Ordinal))
            throw new ArgumentException("The xAI endpoint must match the selected typed region.");
        _ = Internal.XaiAuthorization.Require(connection);
    }

    private sealed class BindingAuthorization(XaiFamilyBinding owner) : IProviderAuthorization
    {
        public async ValueTask<ProviderAuthorizationHeader> GetAsync(CancellationToken cancellationToken = default)
        {
            LlmConnection connection = await owner.AcquireAsync(cancellationToken).ConfigureAwait(false);
            return new("Bearer", LlmCredential.Parse(connection.Credential)!.OAuth!.AccessToken);
        }
    }
}

internal sealed class XaiFamilyBindingJsonConverter : JsonConverter<XaiFamilyBinding>
{
    public override bool HandleNull => true;
    public override XaiFamilyBinding? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Null ? null : throw new NotSupportedException("Runtime xAI bindings are not portable data.");
    public override void Write(Utf8JsonWriter writer, XaiFamilyBinding value, JsonSerializerOptions options)
    {
        if (value is null)
            writer.WriteNullValue();
        else
            throw new NotSupportedException("Runtime xAI bindings are not portable data.");
    }
}
