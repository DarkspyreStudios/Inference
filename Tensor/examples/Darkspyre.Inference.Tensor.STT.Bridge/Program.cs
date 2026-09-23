using Darkspyre.Inference.Common;
using Darkspyre.Inference.Tensor.Common;
using Darkspyre.Inference.Tensor.STT.Bridge;
using Darkspyre.Inference.Tensor.STT.Whisper;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5097");
string modelId = builder.Configuration["WhisperModel"]
    ?? throw new InvalidOperationException("Set WhisperModel to the explicit model ID of an already-installed pack (for example whisper/tiny.en).");
TensorModelPackDefinition selected = TensorSpeechModels.Whisper.SingleOrDefault(pack => pack.Key.ModelId == modelId)
    ?? throw new InvalidOperationException("WhisperModel must identify one of the example's declared Whisper packs.");
string? storeRoot = builder.Configuration["TensorStore"];
var store = storeRoot is null ? new TensorDirectFilePersistStore() : new TensorDirectFilePersistStore(storeRoot);
using var packs = new TensorModelPackManager(new(TensorSpeechModels.Whisper), store);
await using var engine = new WhisperSpeechToTextInference(packs, TensorBackendKind.Cpu);
await engine.StartAsync();
await engine.LoadAsync(selected.Key); // No download fallback. Installation is an explicit host operation outside this example.
await using var app = builder.Build();
app.MapPost("/transcribe", async (PcmTranscriptionRequest request, CancellationToken token) =>
{
    if (request.Samples is null) return Results.BadRequest(new { Error = "Samples are required." });
    try
    {
        return Results.Ok(await engine.TranscribeAsync(new(new(request.Samples, request.SampleRate, request.Channels),
            new(request.Language, request.TranslateToEnglish), selected.Key.ModelId), token));
    }
    catch (InferenceException exception)
    {
        int status = exception.Code is InferenceFailureCode.InvalidRequest or InferenceFailureCode.UnsupportedFeature ? 400 : 503;
        return Results.Problem(title: exception.Code.ToString(), statusCode: status);
    }
});
try { await app.RunAsync(); }
finally { await engine.StopAsync(); }

namespace Darkspyre.Inference.Tensor.STT.Bridge
{
    /// <summary>Example transport DTO. The host maps it to the library's typed PCM contract.</summary>
    public sealed record PcmTranscriptionRequest(float[] Samples, int SampleRate, int Channels,
        string? Language = null, bool TranslateToEnglish = false);
}
