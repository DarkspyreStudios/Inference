using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Connect.Image.Providers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Darkspyre.Inference.Connect.Image.Tests;

public sealed partial class ComfyUiImageTransportTests
{
    private static readonly byte[] InlinePng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a4i8AAAAASUVORK5CYII=");
    private static readonly TimeSpan InlineBound = TimeSpan.FromSeconds(10);

    /// <summary>Exercises real HTTP/WebSocket ordering, frame ownership and retained output through scoped cleanup.</summary>
    [Fact]
    public async Task InlineCaptureSurvivesEarlyOutputAndSocketCloseAndCleansOnlyItsHistory()
    {
        await using InlineServer server = await InlineServer.StartAsync(async native =>
        {
            await native.EventAsync("executing", "foreign", "9");
            await native.BinaryAsync(new byte[] { 0 });
            await native.EventAsync("executing", native.Id, "sampler");
            await native.BinaryAsync(new byte[] { 0 });
            await native.EventAsync("executing", native.Id, "9");
            await native.EventAsync("executing", "foreign", "9");
            await native.BinaryAsync(new byte[] { 0 });
            await native.EventAsync("executing", native.Id, "9");
            byte[] frame = InlineFrame(InlinePng);
            await native.BinaryAsync(frame.AsMemory(0, 5), final: false);
            await native.BinaryAsync(frame.AsMemory(5));
            await native.BinaryAsync(frame);
            await native.EventAsync("execution_success", native.Id);
            await native.CloseOutputAsync();
        });
        await using ComfyUiImageTransport transport = CreateTransport(new HttpClientHandler());
        ImageConnection owner = Connection() with { Endpoint = server.Endpoint };
        ImageWorkflow workflow = InlineWorkflow();
        ImagePreparedReference reference = await transport.ResolveReferenceAsync(owner, workflow,
            new BinaryMediaSource("image/png", InlinePng, "guide.png"));
        Assert.DoesNotContain(Convert.ToBase64String(InlinePng), reference.ToString(), StringComparison.Ordinal);
        PreparedImageTransportRequest prepared = InlinePrepared(transport, workflow, "inline-roundtrip", reference);
        Assert.True(Json(prepared.ProviderRequest).GetProperty("inline").GetBoolean());
        string id = await transport.SubmitAsync(owner, prepared);
        Assert.Equal("inline-roundtrip", id);
        ImageTransportJobSnapshot captured = await WaitInlineAsync(transport, owner, workflow, id);
        Assert.Equal(ImageTransportJobState.Completed, captured.State);
        Assert.Equal(2, captured.Outputs!.Count);
        ImageTransportOutput output = captured.Outputs[0];
        Assert.Null(output.FileName);
        Assert.Null(output.File);
        ImageTransportDownload copy = await transport.DownloadAsync(owner, output);
        Assert.Equal("image/png", copy.ContentType);
        Assert.Equal(InlinePng, copy.Data.ToArray());
        Assert.Equal(InlinePng, (await transport.DownloadAsync(owner, captured.Outputs[1])).Data.ToArray());
        await Assert.ThrowsAsync<InferenceException>(() => transport.DownloadAsync(owner with { }, output));
        await Assert.ThrowsAsync<InferenceException>(() => transport.ReleaseJobAsync(owner with { }, workflow, id));
        await Assert.ThrowsAsync<InferenceException>(() => transport.TryCancelAsync(owner with { }, id, interruptRunning: true));
        Assert.Equal(Convert.ToBase64String(InlinePng), server.Prompt.GetProperty("prompt").GetProperty("1").GetProperty("inputs").GetProperty("base64_data").GetString());
        Assert.Equal(["prompt", "client_id", "prompt_id"], server.Prompt.EnumerateObject().Select(property => property.Name));

        Assert.False(await transport.ReleaseJobAsync(owner, workflow, id));
        Assert.Empty(server.Deleted);
        await Assert.ThrowsAsync<InferenceException>(() => transport.DownloadAsync(owner, output));
        Assert.Equal(InlinePng, copy.Data.ToArray());
        server.PublishTerminal();
        Assert.True(await transport.ReleaseJobAsync(owner, workflow, id));
        Assert.Equal([id], server.Deleted);
        Assert.True(server.ForeignHistory);
        Assert.Equal(1, server.PromptPosts);
        Assert.DoesNotContain(server.Requests, path => path.StartsWith("/upload", StringComparison.Ordinal) || path.StartsWith("/view", StringComparison.Ordinal));
    }

    /// <summary>Pre-completion stream loss fails without history or file reconstruction.</summary>
    [Fact]
    public async Task InlineStreamLossFailsWithoutFileRecovery()
    {
        await using InlineServer server = await InlineServer.StartAsync(async native =>
        {
            await native.EventAsync("executing", native.Id, "9");
            await native.CloseOutputAsync();
        });
        await using ComfyUiImageTransport transport = CreateTransport(new HttpClientHandler());
        ImageConnection owner = Connection() with { Endpoint = server.Endpoint };
        ImageWorkflow workflow = InlineWorkflow(reference: false);
        string id = await transport.SubmitAsync(owner, InlinePrepared(transport, workflow, "lost"));
        Assert.Equal(ImageTransportJobState.Failed, (await WaitInlineAsync(transport, owner, workflow, id)).State);
        Assert.DoesNotContain(server.Requests, path => path.StartsWith("/history", StringComparison.Ordinal) || path.StartsWith("/view", StringComparison.Ordinal));
        Assert.False(await transport.ReleaseJobAsync(owner, workflow, id));
        Assert.Equal(1, server.PromptPosts);
    }

    /// <summary>Retirement drains an unfinished receive pump and prevents further publications.</summary>
    [Fact]
    public async Task InlineRetirementDrainsTheActiveCapture()
    {
        await using InlineServer server = await InlineServer.StartAsync(native => native.EventAsync("executing", native.Id, "9"));
        ComfyUiImageTransport transport = CreateTransport(new HttpClientHandler());
        ImageConnection owner = Connection() with { Endpoint = server.Endpoint };
        ImageWorkflow workflow = InlineWorkflow(reference: false);
        string id = await transport.SubmitAsync(owner, InlinePrepared(transport, workflow, "retired"));
        await transport.DisposeAsync().AsTask().WaitAsync(InlineBound);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => transport.GetJobAsync(owner, id, workflow));
        Assert.DoesNotContain(server.Requests, path => path.StartsWith("/view", StringComparison.Ordinal));
    }

    /// <summary>A real oversize fragmented binary message fails instead of retaining unbounded output.</summary>
    [Fact]
    public async Task InlinePayloadLimitFailsTheCapture()
    {
        await using InlineServer server = await InlineServer.StartAsync(async native =>
        {
            await native.EventAsync("executing", native.Id, "9");
            byte[] block = new byte[64 * 1024];
            InlineFrame(InlinePng).CopyTo(block, 0);
            for (int index = 0; index < ComfyFrameReader.MaximumBytes / block.Length; index++)
                await native.BinaryAsync(block, final: false);
            await native.BinaryAsync(new byte[] { 0 });
        });
        await using ComfyUiImageTransport transport = CreateTransport(new HttpClientHandler());
        ImageConnection owner = Connection() with { Endpoint = server.Endpoint };
        ImageWorkflow workflow = InlineWorkflow(reference: false);
        string id = await transport.SubmitAsync(owner, InlinePrepared(transport, workflow, "oversize"));
        Assert.Equal(ImageTransportJobState.Failed, (await WaitInlineAsync(transport, owner, workflow, id)).State);
        Assert.False(await transport.ReleaseJobAsync(owner, workflow, id));
    }

    /// <summary>Absent queue/history after lost acknowledgement cannot prove a delayed native admission is gone.</summary>
    [Fact]
    public async Task InlineAmbiguousDispatchStaysPendingUntilTerminalHistoryAppears()
    {
        var admit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using InlineServer server = await InlineServer.StartAsync(async native =>
        {
            await admit.Task.WaitAsync(InlineBound);
            native.PublishTerminal();
            terminal.SetResult();
        });
        await using ComfyUiImageTransport transport = CreateTransport(new HttpClientHandler());
        ImageConnection owner = Connection() with { Endpoint = server.Endpoint };
        ImageWorkflow workflow = InlineWorkflow(reference: false);
        using var cancellation = new CancellationTokenSource();
        Task submit = transport.SubmitAsync(owner, InlinePrepared(transport, workflow, "ambiguous"), cancellation.Token);
        await server.PromptEntered.Task.WaitAsync(InlineBound);
        await cancellation.CancelAsync();
        Exception error = await Assert.ThrowsAnyAsync<Exception>(() => submit.WaitAsync(InlineBound));
        Assert.Contains(FlattenInline(error), failure => failure is ImageJobCleanupException { MayStillBeRunning: true });
        Assert.False(await transport.ReleaseJobAsync(owner, workflow, "ambiguous"));
        Assert.Empty(server.Deleted);
        Assert.Equal(1, server.PromptPosts);
        admit.SetResult();
        await terminal.Task.WaitAsync(InlineBound);
        Assert.True(await transport.ReleaseJobAsync(owner, workflow, "ambiguous"));
        Assert.Equal(["ambiguous"], server.Deleted);
        Assert.True(server.ForeignHistory);
        Assert.Equal(1, server.PromptPosts);
    }

    /// <summary>Confirmed scoped release evicts its live registry entry without touching another owner or history.</summary>
    [Fact]
    public async Task ConfirmedInlineReleaseRemovesItsLiveRegistryEntry()
    {
        await using InlineServer server = await InlineServer.StartAsync(async native =>
        {
            await native.EventAsync("executing", native.Id, "9");
            await native.BinaryAsync(InlineFrame(InlinePng));
            await native.EventAsync("execution_success", native.Id);
            native.PublishTerminal();
        });
        await using ComfyUiImageTransport transport = CreateTransport(new HttpClientHandler());
        ImageConnection owner = Connection() with { Endpoint = server.Endpoint };
        ImageWorkflow workflow = InlineWorkflow(reference: false);
        string id = await transport.SubmitAsync(owner, InlinePrepared(transport, workflow, "registry-release"));
        Assert.Equal(ImageTransportJobState.Completed, (await WaitInlineAsync(transport, owner, workflow, id)).State);
        Assert.Equal(1, InlineRegistryCount(transport));
        await Assert.ThrowsAsync<InferenceException>(() => transport.ReleaseJobAsync(owner with { }, workflow, id));
        Assert.Equal(1, InlineRegistryCount(transport));
        Assert.True(await transport.ReleaseJobAsync(owner, workflow, id));
        Assert.Equal(0, InlineRegistryCount(transport));
        Assert.Equal([id], server.Deleted);
        Assert.True(server.ForeignHistory);
    }

    /// <summary>A held failed-submit control request has one independent cleanup budget and cannot retain the local socket.</summary>
    [Fact]
    public async Task FailedInlineSubmitBoundsHeldCleanupAndDrainsCapture()
    {
        var admit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using InlineServer server = await InlineServer.StartAsync(_ => admit.Task);
        using var handler = new HeldInlineControlHandler();
        var log = new InlineCleanupLogger();
        await using var transport = new ComfyUiImageTransport(new StubHttpClientFactory(handler), log);
        ImageConnection owner = Connection() with { Endpoint = server.Endpoint, RequestTimeout = TimeSpan.FromMinutes(5) };
        ImageWorkflow workflow = InlineWorkflow(reference: false);
        using var cancellation = new CancellationTokenSource();
        try
        {
            Task submit = transport.SubmitAsync(owner, InlinePrepared(transport, workflow, "held-cleanup"), cancellation.Token);
            await server.PromptEntered.Task.WaitAsync(InlineBound);
            await cancellation.CancelAsync();
            await handler.Entered.Task.WaitAsync(InlineBound);
            using var drain = new CancellationTokenSource(InlineBound);
            await server.ObserveCaptureCloseAsync(drain.Token);
            Exception error = await Assert.ThrowsAnyAsync<Exception>(() => submit.WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.IsNotType<TimeoutException>(error);
            Assert.Contains(FlattenInline(error), failure => failure is ImageJobCleanupException { MayStillBeRunning: true });
            Assert.True(handler.Cancelled.Task.IsCompletedSuccessfully);
            Assert.Equal(1, handler.Attempts);
            Assert.Equal(1, server.PromptPosts);
            Assert.Empty(server.Deleted);
            Assert.Equal(ImageTransportJobState.Failed, (await transport.GetJobAsync(owner, "held-cleanup", workflow)).State);
            Assert.Single(log.Messages, message => message.Contains("confirmed=False", StringComparison.Ordinal));
        }
        finally { admit.TrySetResult(); }
    }

    private static int InlineRegistryCount(ComfyUiImageTransport transport) =>
        Assert.IsAssignableFrom<System.Collections.IDictionary>(typeof(ComfyUiImageTransport)
            .GetField("_inlineJobs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(transport)).Count;

    private sealed class HeldInlineControlHandler() : DelegatingHandler(new HttpClientHandler())
    {
        private int _attempts;
        internal int Attempts => Volatile.Read(ref _attempts);
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!request.RequestUri!.AbsolutePath.StartsWith("/history/", StringComparison.Ordinal))
                return await base.SendAsync(request, cancellationToken);
            Interlocked.Increment(ref _attempts);
            Entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            finally { if (cancellationToken.IsCancellationRequested) Cancelled.TrySetResult(); }
            throw new InvalidOperationException("The held control request cannot complete.");
        }
    }

    private sealed class InlineCleanupLogger : ILogger<ComfyUiImageTransport>
    {
        internal ConcurrentQueue<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Enqueue(formatter(state, exception));
    }

    private static IEnumerable<Exception> FlattenInline(Exception exception) => exception is AggregateException aggregate
        ? aggregate.InnerExceptions.SelectMany(FlattenInline) : [exception];

    private static byte[] InlineFrame(byte[] png)
    {
        byte[] frame = new byte[png.Length + 8];
        BinaryPrimitives.WriteUInt32BigEndian(frame, 1);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4), 2);
        png.CopyTo(frame, 8);
        return frame;
    }

    private static PreparedImageTransportRequest InlinePrepared(ComfyUiImageTransport transport, ImageWorkflow workflow, string id,
        ImagePreparedReference? reference = null) => transport.Prepare(new()
        { Workflow = workflow, PositivePrompt = "draw", CacheKey = id, References = reference is null ? [] : [reference] });

    private static ImageWorkflow InlineWorkflow(bool reference = true) => new()
    {
        Id = "inline", Label = "Inline", Transport = ImageTransport.ComfyUi, RequiresReferenceImage = reference,
        TransportConfiguration = Json(reference ? """
            {"outputNodeId":"9","defaultParameters":{},"workflow":{
            "1":{"class_type":"easy loadImageBase64","inputs":{"base64_data":"${referenceImageBase64}","image_output":"Hide"}},
            "2":{"class_type":"Prompt","inputs":{"text":"${positivePrompt}","width":"${width}","height":"${height}","seed":"${seed}"}},
            "9":{"class_type":"SaveImageWebsocket","inputs":{"images":["2",0]}}}}
            """ : """
            {"outputNodeId":"9","defaultParameters":{},"workflow":{
            "2":{"class_type":"Prompt","inputs":{"text":"${positivePrompt}","width":"${width}","height":"${height}","seed":"${seed}"}},
            "9":{"class_type":"SaveImageWebsocket","inputs":{"images":["2",0]}}}}
            """)
    };

    private static async Task<ImageTransportJobSnapshot> WaitInlineAsync(ComfyUiImageTransport transport, ImageConnection connection,
        ImageWorkflow workflow, string id)
    {
        using var timeout = new CancellationTokenSource(InlineBound);
        while (true)
        {
            ImageTransportJobSnapshot snapshot = await transport.GetJobAsync(connection, id, workflow, timeout.Token);
            if (snapshot.State is ImageTransportJobState.Completed or ImageTransportJobState.Failed) return snapshot;
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class InlineServer : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly TaskCompletionSource<WebSocket> _socket = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _history;
        private int _promptPosts;
        private InlineServer(WebApplication app) => _app = app;
        internal string Endpoint => _app.Urls.Single();
        internal string Id { get; private set; } = "";
        internal JsonElement Prompt { get; private set; }
        internal int PromptPosts => Volatile.Read(ref _promptPosts);
        internal bool ForeignHistory { get; private set; } = true;
        internal ConcurrentQueue<string> Requests { get; } = new();
        internal ConcurrentQueue<string> Deleted { get; } = new();
        internal TaskCompletionSource PromptEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void PublishTerminal() => _history = true;

        internal static async Task<InlineServer> StartAsync(Func<InlineServer, Task> onPrompt)
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            WebApplication app = builder.Build();
            var native = new InlineServer(app);
            app.UseWebSockets();
            app.Run(async context =>
            {
                string path = context.Request.Path.Value!;
                native.Requests.Enqueue(path);
                if (path == "/ws")
                {
                    using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
                    native._socket.SetResult(socket);
                    await native._closed.Task;
                    return;
                }
                if (path == "/prompt")
                {
                    using JsonDocument document = await JsonDocument.ParseAsync(context.Request.Body);
                    native.Prompt = document.RootElement.Clone();
                    native.Id = native.Prompt.GetProperty("prompt_id").GetString()!;
                    Interlocked.Increment(ref native._promptPosts);
                    native.PromptEntered.SetResult();
                    await onPrompt(native);
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(JsonSerializer.Serialize(new Dictionary<string, string> { ["prompt_id"] = native.Id }));
                    return;
                }
                if (path == "/queue")
                {
                    await context.Response.WriteAsync("""{"queue_running":[],"queue_pending":[[0,"foreign",{}]]}""");
                    return;
                }
                if (path == "/history" && context.Request.Method == "POST")
                {
                    using JsonDocument deletion = await JsonDocument.ParseAsync(context.Request.Body);
                    foreach (JsonElement id in deletion.RootElement.GetProperty("delete").EnumerateArray())
                    {
                        native.Deleted.Enqueue(id.GetString()!);
                        if (id.GetString() == native.Id) native._history = false;
                        if (id.GetString() == "foreign") native.ForeignHistory = false;
                    }
                    await context.Response.WriteAsync("{}");
                    return;
                }
                if (path == "/history/" + native.Id)
                {
                    string result = native._history ? "{" + JsonSerializer.Serialize(native.Id) + ":{\"status\":{\"status_str\":\"success\",\"completed\":true}}}" : "{}";
                    await context.Response.WriteAsync(result);
                    return;
                }
                context.Response.StatusCode = 404;
            });
            await app.StartAsync();
            return native;
        }

        internal async Task EventAsync(string type, string prompt, string? node = null)
        {
            WebSocket socket = await _socket.Task.WaitAsync(InlineBound);
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(new { type, data = new { prompt_id = prompt, node } });
            await socket.SendAsync(json.AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None);
        }

        internal async Task BinaryAsync(ReadOnlyMemory<byte> data, bool final = true)
        {
            WebSocket socket = await _socket.Task.WaitAsync(InlineBound);
            await socket.SendAsync(data, WebSocketMessageType.Binary, final, CancellationToken.None);
        }

        internal async Task ObserveCaptureCloseAsync(CancellationToken token)
        {
            WebSocket socket = await _socket.Task.WaitAsync(token);
            try
            {
                ValueWebSocketReceiveResult result = await socket.ReceiveAsync(new byte[1].AsMemory(), token);
                Assert.Equal(WebSocketMessageType.Close, result.MessageType);
            }
            catch (WebSocketException) { }
        }

        internal async Task CloseOutputAsync()
        {
            WebSocket socket = await _socket.Task.WaitAsync(InlineBound);
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "complete", CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            _closed.TrySetResult();
            using var timeout = new CancellationTokenSource(InlineBound);
            await _app.StopAsync(timeout.Token);
            await _app.DisposeAsync();
        }
    }
}
