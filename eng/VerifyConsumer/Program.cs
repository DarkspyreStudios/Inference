using System.Reflection;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Connect.Audio;
using Darkspyre.Inference.Connect.Image;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.Realtime;
using Darkspyre.Inference.Connect.Resources;
using Darkspyre.Inference.Connect.STT;
using Darkspyre.Inference.Connect.TTS;
using Darkspyre.Inference.Connect.Video;
using Darkspyre.Inference.Tensor.Decision.Laya;
using Darkspyre.Inference.Tensor.Image.Supra;
using Darkspyre.Inference.Tensor.LLM.Qwen;
using Darkspyre.Inference.Tensor.Registry;
using Darkspyre.Inference.Tensor.STT.Whisper;
using Darkspyre.Inference.UI.Settings;
using Microsoft.Extensions.DependencyInjection;

if (args.Length is not (2 or 3) || args.Length == 3 && args[2] is not ("absent" or "cpu" or "metal" or "auto"))
{ throw new ArgumentException("Usage: VerifyConsumer <family-version> <source-commit> [absent|cpu|metal|auto]"); }
string expected = args[0] + "+" + args[1];
Type[] surfaces = [typeof(IInferenceSettingsManager), typeof(SetupChoiceCapabilities), typeof(LlmProviderService),
    typeof(ImageProviderService), typeof(TtsProviderService), typeof(AudioProviderService), typeof(SttProviderService),
    typeof(VideoProviderService), typeof(RealtimeProviderService), typeof(ILlmBatchClient), typeof(LlmResourceProviderService), typeof(ConnectResourcesJson), typeof(QwenSetup),
    typeof(SupraSetup), typeof(LayaSetup), typeof(WhisperSetup), typeof(TensorEngineRegistry), typeof(InferenceSettingsEditor),
    typeof(SetupEditorState), typeof(SetupNumberInput<long>), typeof(SetupNumberInput<decimal>)];
foreach (Type type in surfaces)
{
    string? version = type.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
    if (version != expected) { throw new InvalidOperationException("Wrong assembly version: " + type + " -> " + version); }
    Console.WriteLine(type.FullName + " -> " + version);
}
if (ConnectResourcesJson.LlmBatchRequest.Type != typeof(LlmBatchRequest)
    || ConnectResourcesJson.LlmDeferredResult.Type != typeof(LlmDeferredResult))
{ throw new InvalidOperationException("Resources metadata contracts are missing."); }
if (typeof(InferenceSettings).GetProperty(nameof(InferenceSettings.HasUnsavedChanges)) is null
    || typeof(InferenceSettings).GetProperty(nameof(InferenceSettings.SaveOutcomeUncertain)) is null)
{ throw new InvalidOperationException("Settings correction contracts are missing."); }
if (TensorSpeechModels.Whisper.Count != 12) { throw new InvalidOperationException("Whisper variants missing."); }
await using ServiceProvider services = new ServiceCollection().AddTensorEngines().BuildServiceProvider();
if (services.GetRequiredService<TensorEngineRegistry>().Definitions.Count != 4) { throw new InvalidOperationException("Tensor engine registrations missing."); }
Console.WriteLine("Four-package consumer loaded the release contracts, four Tensor engines and twelve Whisper variants.");
Console.WriteLine("Whisper native runtime: " + Whisper.net.WhisperFactory.GetRuntimeInfo());
if (args.Length == 3) { await GgmlRuntimeVerification.RunAsync(args[2]); }
