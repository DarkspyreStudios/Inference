using System.Net;
using System.Security.Cryptography;
using Darkspyre.Inference.Tensor.Common;
using Darkspyre.Persistence;

namespace Darkspyre.Inference.Tensor.STT.Whisper.Tests;

internal static class WhisperFixture
{
    public static byte[] Header(bool english = false, bool turbo = false)
    {
        int state = turbo ? 1280 : 384, layers = turbo ? 32 : 4;
        int[] values = [0x67676d6c, english ? 51864 : turbo ? 51866 : 51865,
            1500, state, state / 64, layers, 448, state, state / 64, 4, turbo ? 128 : 80, 1];
        return values.SelectMany(BitConverter.GetBytes).ToArray();
    }

    public static TensorModelPackDefinition Pack(byte[] bytes, string id = "host/whisper") => new()
    {
        Key = new(id, "test-revision"),
        Capability = TensorCapability.SpeechTranscription,
        RuntimeArchitecture = TensorSpeechModels.WhisperRuntimeArchitecture,
        License = "MIT",
        SupportedBackends = new HashSet<TensorBackendKind> { TensorBackendKind.Cpu },
        Artifacts = [new() { Role = "model", RelativePath = "model.bin", Length = bytes.Length,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), Sources = [new("https://weights.example.test/model.bin")] }]
    };

    internal sealed class Handler(byte[] bytes) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }); }
    }

    internal sealed class Store : IPersistenceStore
    {
        private readonly Dictionary<(string Store, string Asset), byte[]> _data = [];
        public Stream? LastRead { get; private set; }
        public Task<bool> ExistsAsync(string storeName, string assetName, CancellationToken cancellationToken = default) => Task.FromResult(_data.ContainsKey((storeName, assetName)));
        public Task<Stream?> OpenReadAsync(string storeName, string assetName, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(LastRead = _data.TryGetValue((storeName, assetName), out byte[]? value) ? new MemoryStream(value, writable: false) : null);
        }
        public Task<IReadOnlyList<string>> EnumerateAsync(string storeName, string assetNamePrefix, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(_data.Keys.Where(key => key.Store == storeName && key.Asset.StartsWith(assetNamePrefix, StringComparison.Ordinal)).Select(key => key.Asset).ToArray());
        public async Task SaveAsync(string storeName, string assetName, Stream content, CancellationToken cancellationToken = default)
        { using var copy = new MemoryStream(); await content.CopyToAsync(copy, cancellationToken); _data[(storeName, assetName)] = copy.ToArray(); }
        public Task DeleteAsync(string storeName, string assetName, CancellationToken cancellationToken = default)
        { _data.Remove((storeName, assetName)); return Task.CompletedTask; }
    }

    internal sealed class Loader : IWhisperLoader
    {
        public List<Model> Models { get; } = [];
        public Action<Model>? Loaded { get; set; }
        public async Task<IWhisperModel> LoadAsync(TensorModelPackDefinition definition, TensorModelPackLease lease, CancellationToken token)
        {
            WhisperModelFile file = await WhisperModelFile.OpenAsync(lease.GetRequiredArtifact("model"), definition.Artifacts[0].Length, token);
            var model = new Model(file); Models.Add(model);
            Loaded?.Invoke(model);
            return model;
        }
    }

    internal sealed class Model(WhisperModelFile file) : IWhisperModel
    {
        public string Path => file.Path;
        public WhisperModelMetadata Metadata => file.Metadata;
        public bool Disposed { get; private set; }
        public List<(float[] Samples, string Language, bool Translate)> Calls { get; } = [];
        public Func<CancellationToken, Task>? Processing { get; set; }
        public async Task<WhisperTranscript> TranscribeAsync(float[] samples, string language, bool translate, CancellationToken token)
        {
            Assert.False(Disposed); Assert.True(File.Exists(Path));
            Calls.Add((samples, language, translate));
            if (Processing is not null) await Processing(token);
            token.ThrowIfCancellationRequested();
            Assert.False(Disposed);
            return new([new("hello", TimeSpan.Zero, TimeSpan.FromMilliseconds(100))], "en");
        }
        public async ValueTask DisposeAsync() { Disposed = true; await file.DisposeAsync(); }
    }

    internal sealed class Host : IAsyncDisposable
    {
        private readonly HttpClient _http;
        public Handler Handler { get; }
        public Store Store { get; } = new();
        public Loader Loader { get; } = new();
        public TensorModelPackManager Packs { get; }
        public TensorModelPackDefinition Pack { get; }
        public WhisperSpeechToTextInference Engine { get; }
        public Host(bool english = false, bool turbo = false)
        {
            byte[] bytes = Header(english, turbo);
            Pack = WhisperFixture.Pack(bytes);
            Handler = new(bytes); _http = new(Handler);
            Packs = new(new([Pack]), Store, _http);
            Engine = new(Packs, Loader);
        }
        public async Task LoadAsync() { await Packs.InstallAsync(Pack.Key); await Engine.LoadAsync(Pack.Key); }
        public async ValueTask DisposeAsync() { await Engine.DisposeAsync(); Packs.Dispose(); _http.Dispose(); }
    }
}
