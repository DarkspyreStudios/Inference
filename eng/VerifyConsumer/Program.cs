using System.Reflection;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Connect.Audio;
using Darkspyre.Inference.Connect.Image;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.TTS;
using Darkspyre.Inference.Tensor.Decision.Laya;
using Darkspyre.Inference.Tensor.Image.Supra;
using Darkspyre.Inference.Tensor.LLM.Qwen;
using Darkspyre.Inference.Tensor.STT.Whisper;
using Darkspyre.Inference.UI.Settings;

if (args.Length != 2) { throw new ArgumentException("Usage: VerifyConsumer <family-version> <source-commit>"); }
string expected = args[0] + "+" + args[1];
Type[] surfaces = [typeof(IInferenceSetupSession), typeof(SetupChoiceCapabilities), typeof(LlmProviderService),
    typeof(ImageProviderService), typeof(TtsProviderService), typeof(AudioProviderService), typeof(QwenSetup),
    typeof(SupraSetup), typeof(LayaSetup), typeof(WhisperSetup), typeof(InferenceSettingsEditor),
    typeof(SetupEditorState), typeof(SetupNumberInput<long>), typeof(SetupNumberInput<decimal>)];
foreach (Type type in surfaces)
{
    string? version = type.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
    if (version != expected) { throw new InvalidOperationException("Wrong assembly version: " + type + " -> " + version); }
    Console.WriteLine(type.FullName + " -> " + version);
}
if (typeof(SetupSnapshot).GetProperty(nameof(SetupSnapshot.IsDirty)) is null
    || typeof(SetupSnapshot).GetProperty(nameof(SetupSnapshot.CommitOutcomeUncertain)) is null)
{ throw new InvalidOperationException("Settings correction contracts are missing."); }
if (TensorSpeechModels.Whisper.Count != 12) { throw new InvalidOperationException("Whisper variants missing."); }
Console.WriteLine("Four-package consumer loaded the release contracts and twelve Whisper variants.");
Console.WriteLine("Whisper native runtime: " + Whisper.net.WhisperFactory.GetRuntimeInfo());
