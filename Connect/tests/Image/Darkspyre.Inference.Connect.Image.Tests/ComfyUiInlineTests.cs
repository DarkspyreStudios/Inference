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
