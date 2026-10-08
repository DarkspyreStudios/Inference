using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.Voice.Tests;

internal static class VoiceTestData
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    internal static InferenceStreamLimits Limits(int events = 16) => new(events, 65536, 65536);
    internal static LlmConnection Connection => new()
    {
        Version = "test-version",
        Transport = LlmTransport.XaiResponses,
        Endpoint = new XaiConnectionOptions().GetEndpoint(),
        Credential = new LlmCredential { OAuth = new() { AccessToken = "dummy-test-token" } }.Serialize()
    };
    internal static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal static void AssertRestFailure(LlmProviderException failure)
    {
        Assert.Equal(HttpStatusCode.TooManyRequests, failure.StatusCode);
        Assert.Equal("rate_limit_[REDACTED]_[REDACTED]_[REDACTED]_[REDACTED]_[REDACTED]", failure.ProviderCode);
        Assert.Equal("request-[REDACTED]-[REDACTED]-[REDACTED]", failure.RequestId);
        Assert.Equal(TimeSpan.FromSeconds(17), failure.RetryAfter);
        foreach (string value in new[] { "private speech source", "dummy-test-token", "binding-secret", "family-secret", "default-secret", "provider-secret" })
            Assert.DoesNotContain(value, failure.ToString(), StringComparison.Ordinal);
    }
    internal static string Type(byte[] bytes)
    {
        using JsonDocument json = JsonDocument.Parse(bytes);
        return json.RootElement.GetProperty("type").GetString()!;
    }
}

internal sealed class VoiceConnectionSource : ILlmConnectionSource
{
    public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
        => Task.FromResult<LlmConnection?>(VoiceTestData.Connection with
        {
            AdditionalHeaders = new Dictionary<string, string> { ["X-Binding"] = "binding-secret" }
        });
    public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion,
        CancellationToken cancellationToken = default) => throw new InvalidOperationException("No authenticator refreshes this fixture credential.");
}

internal sealed class RejectedVoiceHttp(string path) : HttpMessageHandler
{
    internal int Sends { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Sends++;
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(path, request.RequestUri!.AbsolutePath);
        Assert.Equal("dummy-test-token", request.Headers.Authorization!.Parameter);
        Assert.Equal("binding-secret", Assert.Single(request.Headers.GetValues("X-Binding")));
        Assert.Equal("family-secret", Assert.Single(request.Headers.GetValues("X-Family")));
        Assert.Equal("default-secret", Assert.Single(request.Headers.GetValues("X-Default")));
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("""
                {"error":{"code":"rate_limit_dummy-test-token_binding-secret_family-secret_default-secret_provider-secret","message":"private speech source","refresh_token":"provider-secret"}}
                """)
        };
        response.Headers.Add("x-request-id", "request-binding-secret-family-secret-default-secret");
        response.Headers.RetryAfter = new(TimeSpan.FromSeconds(17));
        return Task.FromResult(response);
    }
}

// Only the network is replaced. Each test drives the owning production session and codec.
internal sealed class VoiceSocket : WebSocket
{
    private readonly Channel<(byte[] Bytes, WebSocketMessageType Type)> _incoming = Channel.CreateUnbounded<(byte[], WebSocketMessageType)>();
    private readonly Channel<int> _received = Channel.CreateUnbounded<int>();
    private int _receiveCount;
    internal List<(byte[] Bytes, WebSocketMessageType Type)> Sent { get; } = [];
    internal bool Disposed { get; private set; }
    internal void Json(string json) => _incoming.Writer.TryWrite((Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text));
    internal void End() => _incoming.Writer.TryWrite(([], WebSocketMessageType.Close));
    internal Task<WebSocket> ConnectAsync(Uri uri, HttpRequestMessage headers, Action fence,
        Action<Exception> cleanup, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); fence();
        Assert.Equal("wss", uri.Scheme);
        Assert.Equal("Bearer", headers.Headers.Authorization?.Scheme);
        return Task.FromResult<WebSocket>(this);
    }
    internal async Task WaitReceivedAsync(int count)
    {
        using var timeout = new CancellationTokenSource(VoiceTestData.Timeout);
        while (await _received.Reader.ReadAsync(timeout.Token) < count) { }
    }
    public override ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType,
        bool endOfMessage, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Assert.True(endOfMessage);
        Sent.Add((buffer.ToArray(), messageType));
        return ValueTask.CompletedTask;
    }
    public override async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var frame = await _incoming.Reader.ReadAsync(cancellationToken);
        Assert.True(frame.Bytes.Length <= buffer.Length);
        frame.Bytes.CopyTo(buffer);
        _received.Writer.TryWrite(++_receiveCount);
        return new(frame.Bytes.Length, frame.Type, true);
    }
    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType,
        bool endOfMessage, CancellationToken cancellationToken) => SendAsync(buffer.AsMemory(), messageType, endOfMessage, cancellationToken).AsTask();
    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        var result = await ReceiveAsync(buffer.AsMemory(), cancellationToken);
        return new(result.Count, result.MessageType, result.EndOfMessage);
    }
    public override WebSocketCloseStatus? CloseStatus => null;
    public override string? CloseStatusDescription => null;
    public override string? SubProtocol => null;
    public override WebSocketState State => Disposed ? WebSocketState.Closed : WebSocketState.Open;
    public override void Abort() => Dispose();
    public override void Dispose() => Disposed = true;
    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => throw new NotSupportedException();
    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => throw new NotSupportedException();
}

internal sealed class HeldStream : MemoryStream
{
    private readonly bool _holdRead;
    private readonly bool _holdWrite;
    internal HeldStream(byte[] bytes, bool holdRead = false, bool holdWrite = false)
    {
        Write(bytes); Position = 0;
        _holdRead = holdRead; _holdWrite = holdWrite;
    }
    internal TaskCompletionSource Entered { get; } = VoiceTestData.Signal();
    internal TaskCompletionSource Canceled { get; } = VoiceTestData.Signal();
    internal TaskCompletionSource Release { get; } = VoiceTestData.Signal();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_holdRead)
        {
            using var registration = cancellationToken.Register(() => Canceled.TrySetResult());
            Entered.TrySetResult();
            await Release.Task;
        }
        return await base.ReadAsync(buffer, cancellationToken);
    }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_holdWrite)
        {
            using var registration = cancellationToken.Register(() => Canceled.TrySetResult());
            Entered.TrySetResult();
            await Release.Task;
        }
        await base.WriteAsync(buffer, CancellationToken.None);
    }
}

internal sealed class VoiceResources : IInferenceResourceAccess
{
    public string WorkspaceId => "test-workspace";
    public string SessionId => "test-session";
    internal VoiceWriteHandle Writer { get; init; } = new();
    internal VoiceReadHandle Reader { get; } = new();
    public Task<InferenceResourceDescriptor?> GetAsync(InferenceResourceReference reference, CancellationToken cancellationToken = default)
        => Task.FromResult<InferenceResourceDescriptor?>(Reader.Resource);
    public Task<IInferenceResourceReadHandle> OpenReadAsync(InferenceResourceReference reference, CancellationToken cancellationToken = default)
        => Task.FromResult<IInferenceResourceReadHandle>(Reader);
    public Task<IInferenceResourceWriteHandle> OpenWriteAsync(InferenceResourceCreate resource, CancellationToken cancellationToken = default)
        => Task.FromResult<IInferenceResourceWriteHandle>(Writer);
    public Task<InferenceResourceSelection> GetSelectionAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new InferenceResourceSelection(WorkspaceId, SessionId, 0, []));
    public Task<InferenceResourceSelection> SelectAsync(IReadOnlyList<InferenceResourceReference> references, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<InferenceResourceSelection> ReplaceSelectionAsync(IReadOnlyList<InferenceResourceReference> references, long expectedRevision, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}

internal sealed class VoiceWriteHandle : IInferenceResourceWriteHandle
{
    public InferenceResourceReference Reference { get; } = new("test-workspace", "generated-audio");
    internal HeldStream Stream { get; init; } = new([]);
    public Stream Content => Stream;
    internal Exception? CleanupFailure { get; init; }
    internal TaskCompletionSource Disposed { get; } = VoiceTestData.Signal();
    internal int CommitCount { get; private set; }
    internal byte[]? Bytes { get; private set; }
    public Task<InferenceResourceCommitResult> CommitAsync(long bytesWritten, InferenceResourceCommitOptions? options = null, CancellationToken cancellationToken = default)
    {
        Assert.True(options?.SelectForSession);
        Bytes = Stream.ToArray();
        Assert.Equal(Bytes.LongLength, bytesWritten);
        CommitCount++;
        return Task.FromResult(new InferenceResourceCommitResult(new(Reference, "test-session", null, null, bytesWritten),
            new("test-workspace", "test-session", 1, [Reference])));
    }
    public ValueTask DisposeAsync()
    {
        Stream.Dispose(); Disposed.TrySetResult();
        return CleanupFailure is null ? ValueTask.CompletedTask : ValueTask.FromException(CleanupFailure);
    }
}

internal sealed class VoiceReadHandle : IInferenceResourceReadHandle
{
    internal HeldStream Stream { get; } = new(Encoding.UTF8.GetBytes("reference text"), holdRead: true);
    public InferenceResourceDescriptor Resource { get; } = new(new("test-workspace", "reference"), "test-session", "reference.txt", "text/plain", 14);
    public Stream Content => Stream;
    internal bool Disposed { get; private set; }
    public ValueTask DisposeAsync() { Stream.Dispose(); Disposed = true; return ValueTask.CompletedTask; }
}
