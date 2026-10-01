using System.Net;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class LlmTextTokenizerTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    private const string Tokens = """{"token_ids":[{"token_id":4294967295,"string_token":"display","token_bytes":[0,255,195]}]}""";
    private static LlmConnection Connection => new()
    {
        Version = "v1",
        Transport = LlmTransport.XaiResponses,
        Endpoint = "https://api.x.ai/v1",
        Xai = new(),
        Credential = new LlmCredential { OAuth = new() { AccessToken = "old", AccountId = "principal" } }.Serialize()
    };

    [Fact]
    public async Task CooperativeGateObservesCancellationWhenAnEarlierCallbackIsRemovedDuringUnwind()
    {
        var gate = new Gate { Boundary = "read" };
        using var caller = new CancellationTokenSource();
        Task operation = gate.WaitAsync("read", caller.Token);
        await gate.Entered.Task.WaitAsync(Bound);
        using CancellationTokenRegistration newer = caller.Token.Register(() =>
        {
            // Unwind the canceled waiter before cancellation visits its older observation callback.
            gate.Release.TrySetCanceled(caller.Token);
            try { operation.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
        });
        await caller.CancelAsync().WaitAsync(Bound);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.True(caller.IsCancellationRequested);
        Assert.True(gate.Stopped.Task.IsCompletedSuccessfully,
            "Cooperative cancellation must remain observable after the wait unwinds and removes its callback.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshUsesOneResolutionAndConditionalSaveWithoutCatalogOrGeneration(bool rotate)
    {
        var gate = new Gate();
        using var source = new Source(gate);
        using var auth = new Authenticator(gate) { Rotate = rotate };
        using var handler = new Handler(gate);
        using var http = new HttpClient(handler);
        await using var service = new LlmProviderService(source, [new XaiResponsesLlmClient(http)], [auth]);
        LlmTokenization result = await service.TokenizeAsync("connection", "manual-model", "text");
        Assert.Single(result.Tokens);
        Assert.Equal(1, source.Reads);
        Assert.Equal(1, auth.Calls);
        Assert.Equal(rotate ? 1 : 0, source.Writes);
        Assert.Equal(rotate ? "fresh" : "old", handler.Authorization);
        Assert.Equal(1, handler.Calls);
        Assert.Equal("https://api.x.ai/v1/tokenize-text", handler.Uri);
        if (rotate) Assert.Equal("v1", source.ExpectedVersion);
        Assert.False(source.Disposed);
        Assert.False(auth.Disposed);
    }

    [Fact]
    public async Task UnsupportedAdapterFailsBeforeRefreshAndOtherAdapterOperations()
    {
        var gate = new Gate();
        using var source = new Source(gate);
        using var auth = new Authenticator(gate);
        using var client = new UnsupportedClient();
        await using var service = new LlmProviderService(source, [client], [auth]);
        await Assert.ThrowsAsync<NotSupportedException>(() => service.TokenizeAsync("connection", "model", "text"));
        Assert.Equal(1, source.Reads);
        Assert.Equal(0, auth.Calls);
        Assert.Equal(0, source.Writes);
        Assert.Equal(0, client.Calls);
        await service.DisposeAsync();
        Assert.False(client.Disposed);
    }

    [Theory]
    [InlineData("connection")]
    [InlineData("model")]
    [InlineData("text")]
    public async Task InvalidArgumentsFailBeforeSourceRead(string invalid)
    {
        var gate = new Gate();
        using var source = new Source(gate);
        using var handler = new Handler(gate);
        using var http = new HttpClient(handler);
        await using var service = new LlmProviderService(source, [new XaiResponsesLlmClient(http)]);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => service.TokenizeAsync(
            invalid == "connection" ? " " : "connection", invalid == "model" ? " " : "model", invalid == "text" ? null! : "text"));
        Assert.Equal(0, source.Reads);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task StaleRefreshSaveDoesNotDispatchOrRetry()
    {
        var gate = new Gate { Boundary = "refresh" };
        using var source = new Source(gate);
        using var auth = new Authenticator(gate);
        using var handler = new Handler(gate);
        using var http = new HttpClient(handler);
        await using var service = new LlmProviderService(source, [new XaiResponsesLlmClient(http)], [auth]);
        Task operation = service.TokenizeAsync("connection", "model", "text");
        try
        {
            await gate.Entered.Task.WaitAsync(Bound);
            source.Current = Connection with { Version = "replacement" };
            gate.Release.TrySetResult();
            await Assert.ThrowsAsync<InvalidOperationException>(() => operation);
            Assert.Equal(1, auth.Calls);
            Assert.Equal(0, source.Writes);
            Assert.Equal(0, handler.Calls);
            Assert.Equal("replacement", source.Current.Version);
        }
        finally { gate.Release.TrySetResult(); }
    }

    [Theory]
    [InlineData("resolve", false, false)]
    [InlineData("resolve", false, true)]
    [InlineData("resolve", true, false)]
    [InlineData("resolve", true, true)]
    [InlineData("refresh", false, false)]
    [InlineData("refresh", false, true)]
    [InlineData("refresh", true, false)]
    [InlineData("refresh", true, true)]
    [InlineData("save", true, false)]
    [InlineData("save", true, true)]
    [InlineData("save", false, false)]
    [InlineData("save", false, true)]
    [InlineData("http", false, false)]
    [InlineData("http", false, true)]
    [InlineData("http", true, false)]
    [InlineData("http", true, true)]
    [InlineData("read", false, false)]
    [InlineData("read", false, true)]
    [InlineData("read", true, false)]
    [InlineData("read", true, true)]
    public async Task CancellationAndRetirementDrainActualBoundaries(string boundary, bool retire, bool ignoreCancellation)
    {
        var gate = new Gate { Boundary = boundary, IgnoreCancellation = ignoreCancellation };
        using var source = new Source(gate);
        using var auth = new Authenticator(gate);
        using var handler = new Handler(gate);
        using var http = new HttpClient(handler);
        using var caller = new CancellationTokenSource();
        await using var service = new LlmProviderService(source, [new XaiResponsesLlmClient(http)], [auth]);
        Task operation = service.TokenizeAsync("connection", "model", "text", caller.Token);
        Task? stopping = null;
        try
        {
            await gate.Entered.Task.WaitAsync(Bound);
            stopping = retire ? service.DisposeAsync().AsTask() : caller.CancelAsync();
            await gate.Stopped.Task.WaitAsync(Bound);
            if (ignoreCancellation)
            {
                Assert.False(operation.IsCompleted);
                if (retire) Assert.False(stopping.IsCompleted);
            }
            gate.Release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(Bound));
            await stopping.WaitAsync(Bound);
            Assert.False(source.Disposed);
            Assert.False(auth.Disposed);
            Assert.False(handler.Disposed);
            if (retire)
            {
                Assert.Same(stopping, service.DisposeAsync().AsTask());
                await Assert.ThrowsAsync<ObjectDisposedException>(() => service.TokenizeAsync("connection", "model", "text"));
            }
            Assert.Equal(boundary == "save" && ignoreCancellation ? 1 : boundary is "http" or "read" ? 1 : 0, source.Writes);
            Assert.Equal(boundary is "http" or "read" ? 1 : 0, handler.Calls);
        }
        finally
        {
            gate.Release.TrySetResult();
            if (stopping is not null) await stopping.WaitAsync(Bound);
        }
    }

    [Theory]
    [InlineData("resolve")]
    [InlineData("refresh")]
    [InlineData("save")]
    [InlineData("http")]
    [InlineData("read")]
    public async Task SynchronousOwnerFenceRejectsLateResultsWithDelayedLinkedCancellation(string boundary)
    {
        var gate = new Gate { Boundary = boundary, IgnoreCancellation = true };
        var anchorGate = new Gate { Boundary = "anchor", IgnoreCancellation = true };
        using var source = new Source(gate) { Anchor = anchorGate };
        using var auth = new Authenticator(gate) { Rotate = boundary != "refresh" };
        using var handler = new Handler(gate);
        using var http = new HttpClient(handler);
        using var caller = new CancellationTokenSource();
        using var unblock = new ManualResetEventSlim();
        await using var access = new LlmConnectionAccess(source, [auth]);
        await using var service = new LlmProviderService(source, [new XaiResponsesLlmClient(http)], [auth], connectionAccess: access);
        Task anchor = service.TokenizeAsync("anchor", "model", "text");
        await anchorGate.Entered.Task.WaitAsync(Bound);
        CancellationToken ownerStop = source.AnchorToken;
        Task operation = service.TokenizeAsync("connection", "model", "text", caller.Token);
        Task? retirement = null;
        CancellationTokenRegistration blocker = default;
        var blocking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await gate.Entered.Task.WaitAsync(Bound);
            blocker = ownerStop.Register(() =>
            {
                blocking.TrySetResult();
                if (!unblock.Wait(Bound)) throw new TimeoutException("Controlled cancellation blocker was not released.");
            });
            retirement = service.DisposeAsync().AsTask();
            await blocking.Task.WaitAsync(Bound);
            Assert.True(ownerStop.IsCancellationRequested);
            Assert.False(source.TargetToken.IsCancellationRequested);
            gate.Release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(Bound));
            Assert.False(source.TargetToken.IsCancellationRequested);
            Assert.False(retirement.IsCompleted);
            Assert.Same(retirement, service.DisposeAsync().AsTask());
            Assert.Equal(boundary == "resolve" ? 0 : 1, auth.Calls);
            Assert.Equal(boundary is "save" or "http" or "read" ? 1 : 0, source.Writes);
            Assert.Equal(boundary is "http" or "read" ? 1 : 0, handler.Calls);
            Assert.False(source.Disposed);
            Assert.False(auth.Disposed);
            Assert.False(handler.Disposed);
        }
        finally
        {
            unblock.Set(); gate.Release.TrySetResult(); anchorGate.Release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => anchor.WaitAsync(Bound));
            await (retirement ?? service.DisposeAsync().AsTask()).WaitAsync(Bound);
            blocker.Dispose();
        }
        Assert.Equal(boundary is "resolve" or "refresh" ? "old" : "fresh", LlmCredential.Parse(source.Current.Credential)!.OAuth!.AccessToken);
        LlmConnection borrowed = await access.AcquireAsync("connection");
        Assert.NotNull(borrowed);
    }

    [Fact]
    public void OwnedMetadataPreservesTokenIdsTextAndByteMemory()
    {
        var result = new LlmTokenization([new(uint.MaxValue, "<literal>\u00e9", new byte[] { 0, 255, 195 })]);
        string json = JsonSerializer.Serialize(result, ConnectLlmJson.Tokenization);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement token = document.RootElement.GetProperty("Tokens")[0];
        Assert.Equal(uint.MaxValue, token.GetProperty("Id").GetUInt32());
        Assert.Equal("<literal>\u00e9", token.GetProperty("Text").GetString());
        Assert.Equal("AP/D", token.GetProperty("Bytes").GetString());
        LlmTokenization roundTrip = JsonSerializer.Deserialize(json, ConnectLlmJson.Tokenization)!;
        Assert.Equal(new byte[] { 0, 255, 195 }, roundTrip.Tokens[0].Bytes.ToArray());
    }

    [Fact]
    public void ActualForeignTokenizerMetadataAndRetirementReleaseTheirGeneration()
    {
        WeakReference generation = ExerciseGeneration();
        for (int attempt = 0; generation.IsAlive && attempt < 20; attempt++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10);
        }
        Assert.False(generation.IsAlive, "The actual tokenizer caller retains its collectible generation.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ExerciseGeneration()
    {
        string directory = Path.GetDirectoryName(typeof(LlmTextTokenizerTests).Assembly.Location)!;
        var generation = new ConnectGeneration(directory);
        var weak = new WeakReference(generation);
        try
        {
            Assembly connect = generation.LoadFromAssemblyPath(typeof(ConnectLlmJson).Assembly.Location);
            Assembly common = generation.LoadFromAssemblyPath(typeof(InferenceJson).Assembly.Location);
            Assembly fixture = generation.LoadFromAssemblyPath(typeof(LlmTextTokenizerTests).Assembly.Location);
            foreach ((Assembly actual, Assembly expected) in new[] { (connect, typeof(ConnectLlmJson).Assembly),
                (common, typeof(InferenceJson).Assembly), (fixture, typeof(LlmTextTokenizerTests).Assembly) })
            {
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(actual));
                Assert.Equal(expected.ManifestModule.ModuleVersionId, actual.ManifestModule.ModuleVersionId);
                Assert.NotSame(expected, actual);
            }
            Type caller = fixture.GetType(typeof(LlmTextTokenizerTests).FullName!, true)!;
            var operation = (Task)caller.GetMethod(nameof(ForeignCallerAsync), BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
            operation.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        }
        finally { generation.Unload(); }
        return weak;
    }

    [SuppressMessage("Performance", "CA1859", Justification = "This actual foreign caller verifies dispatch through the public tokenizer capability.")]
    private static async Task ForeignCallerAsync()
    {
        AssemblyLoadContext generation = AssemblyLoadContext.GetLoadContext(typeof(LlmTextTokenizerTests).Assembly)!;
        Assert.True(generation.IsCollectible);
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(typeof(LlmProviderService).Assembly));
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(typeof(InferenceJson).Assembly));
        var gate = new Gate();
        using var source = new Source(gate);
        using var auth = new Authenticator(gate);
        using var handler = new Handler(gate);
        using var http = new HttpClient(handler);
        await using (var service = new LlmProviderService(source, [new XaiResponsesLlmClient(http)], [auth]))
        {
            ILlmTextTokenizer tokenizer = service;
            LlmTokenization result = await tokenizer.TokenizeAsync("connection", "manual", "text");
            string json = JsonSerializer.Serialize(result, ConnectLlmJson.Tokenization);
            LlmTokenization roundTrip = JsonSerializer.Deserialize(json, ConnectLlmJson.Tokenization)!;
            Assert.Equal(uint.MaxValue, roundTrip.Tokens[0].Id);
            Assert.Equal("display", roundTrip.Tokens[0].Text);
            Assert.Equal(new byte[] { 0, 255, 195 }, roundTrip.Tokens[0].Bytes.ToArray());
            LlmToken token = JsonSerializer.Deserialize(JsonSerializer.Serialize(roundTrip.Tokens[0], ConnectLlmJson.Token), ConnectLlmJson.Token)!;
            Assert.Equal(new byte[] { 0, 255, 195 }, token.Bytes.ToArray());
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(ConnectLlmJson.Tokenization.Type.Assembly));
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(ConnectLlmJson.Token.Type.Assembly));
        }
        var fixture = new LlmTextTokenizerTests();
        foreach (string boundary in new[] { "resolve", "refresh", "save", "http", "read" })
            await fixture.SynchronousOwnerFenceRejectsLateResultsWithDelayedLinkedCancellation(boundary);
    }

    private sealed class Gate
    {
        internal string? Boundary { get; init; }
        internal bool IgnoreCancellation { get; init; }
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal async Task WaitAsync(string boundary, CancellationToken token)
        {
            if (boundary != Boundary) return;
            using CancellationTokenRegistration registration = token.Register(() => Stopped.TrySetResult());
            Entered.TrySetResult();
            await Release.Task.WaitAsync(IgnoreCancellation ? CancellationToken.None : token);
        }
    }

    private sealed class Source(Gate gate) : ILlmConnectionSource, IDisposable
    {
        internal Gate? Anchor { get; init; }
        internal CancellationToken AnchorToken { get; private set; }
        internal CancellationToken TargetToken { get; private set; }
        internal LlmConnection Current { get; set; } = Connection;
        internal int Reads { get; private set; }
        internal int Writes { get; private set; }
        internal string? ExpectedVersion { get; private set; }
        internal bool Disposed { get; private set; }
        public async Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
        {
            Reads++;
            if (connectionId == "anchor") { AnchorToken = cancellationToken; await Anchor!.WaitAsync("anchor", cancellationToken); }
            else { TargetToken = cancellationToken; await gate.WaitAsync("resolve", cancellationToken); }
            return Current;
        }
        public async Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default)
        {
            ExpectedVersion = expectedVersion;
            await gate.WaitAsync("save", cancellationToken);
            if (!gate.IgnoreCancellation) cancellationToken.ThrowIfCancellationRequested();
            if (Current.Version != expectedVersion) throw new InvalidOperationException("Connection changed.");
            Writes++;
            Current = Current with { Version = "v2", Credential = credentialJson };
        }
        public void Dispose() => Disposed = true;
    }

    private sealed class Authenticator(Gate gate) : ILlmAuthenticator, IDisposable
    {
        public LlmTransport Transport => LlmTransport.XaiResponses;
        internal bool Rotate { get; init; } = true;
        internal int Calls { get; private set; }
        internal bool Disposed { get; private set; }
        public async Task<LlmCredential> EnsureFreshAsync(LlmCredential credential, CancellationToken cancellationToken = default)
        {
            Calls++; await gate.WaitAsync("refresh", cancellationToken);
            return Rotate && credential.OAuth!.AccessToken == "old" ? credential with { OAuth = credential.OAuth with { AccessToken = "fresh" } } : credential;
        }
        public void Dispose() => Disposed = true;
    }

    private sealed class Handler(Gate gate) : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        internal string? Uri { get; private set; }
        internal string? Authorization { get; private set; }
        internal bool Disposed { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; Uri = request.RequestUri!.ToString(); Authorization = request.Headers.Authorization!.Parameter;
            await gate.WaitAsync("http", cancellationToken);
            return new(HttpStatusCode.OK) { Content = new Content(gate) };
        }
        protected override void Dispose(bool disposing) { if (disposing) Disposed = true; base.Dispose(disposing); }
    }

    private sealed class Content(Gate gate) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            await gate.WaitAsync("read", cancellationToken);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(Tokens), gate.IgnoreCancellation ? CancellationToken.None : cancellationToken);
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private sealed class UnsupportedClient : ILlmClient, IDisposable
    {
        public LlmTransport Transport => LlmTransport.XaiResponses;
        public LlmProviderDefinition ProviderDefinition => ProviderDefinitions.GrokSubscription;
        internal int Calls { get; private set; }
        internal bool Disposed { get; private set; }
        public LlmProviderCapabilities GetCapabilities(LlmConnection connection, string? modelId = null) { Calls++; throw new InvalidOperationException(); }
        public Task<IReadOnlyList<LlmModel>> ListModelsAsync(LlmConnection connection, CancellationToken cancellationToken = default) { Calls++; throw new InvalidOperationException(); }
        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmConnection connection, LlmChatRequest request, CancellationToken cancellationToken = default) { Calls++; throw new InvalidOperationException(); }
        public void Dispose() => Disposed = true;
    }

    private sealed class ConnectGeneration(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name is null || !assemblyName.Name.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal)) return null;
            string path = Path.Combine(directory, assemblyName.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }
}
