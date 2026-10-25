using System.Reflection;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Connect.Audio;
using Darkspyre.Inference.Connect.Image;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.TTS;
using Darkspyre.Inference.Tensor.Decision.Laya;
using Darkspyre.Inference.Tensor.Image.Supra;
using Darkspyre.Inference.Tensor.LLM.Qwen;
using Darkspyre.Inference.Tensor.Registry;
using Darkspyre.Inference.Tensor.STT.Whisper;
using Darkspyre.Inference.UI.Settings;
using Microsoft.Extensions.DependencyInjection;

if (args.Length != 2)
{ throw new ArgumentException("Usage: VerifyConsumer <family-version> <source-commit>"); }
string expected = args[0] + "+" + args[1];
Type[] surfaces = [typeof(IInferenceSettingsManager), typeof(SetupChoiceCapabilities), typeof(LlmProviderService),
    typeof(ImageProviderService), typeof(TtsProviderService), typeof(AudioProviderService),
    typeof(QwenSetup),
    typeof(SupraSetup), typeof(LayaSetup), typeof(WhisperSetup), typeof(TensorEngineRegistry),
    typeof(Darkspyre.Inference.UI.Settings.Avalonia.InferenceSettingsEditor), typeof(Darkspyre.Inference.UI.Settings.Blazor.InferenceSettingsEditor),
    typeof(SetupEditorState), typeof(Darkspyre.Inference.UI.Settings.Avalonia.SetupNumberInput<long>),
    typeof(Darkspyre.Inference.UI.Settings.Avalonia.SetupNumberInput<decimal>)];
foreach (Type type in surfaces)
{
    string? version = type.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
    if (version != expected) { throw new InvalidOperationException("Wrong assembly version: " + type + " -> " + version); }
    Console.WriteLine(type.FullName + " -> " + version);
}
if (typeof(InferenceSettings).GetProperty(nameof(InferenceSettings.HasUnsavedChanges)) is null
    || typeof(InferenceSettings).GetProperty(nameof(InferenceSettings.SaveOutcomeUncertain)) is null)
{ throw new InvalidOperationException("Settings correction contracts are missing."); }
if (TensorSpeechModels.Whisper.Count != 12) { throw new InvalidOperationException("Whisper variants missing."); }
await using ServiceProvider services = new ServiceCollection().AddTensorEngines().BuildServiceProvider();
if (services.GetRequiredService<TensorEngineRegistry>().Definitions.Count != 6) { throw new InvalidOperationException("Tensor engine registrations missing."); }
Console.WriteLine("Library consumer loaded the release contracts, six Tensor engines and twelve Whisper variants.");
Console.WriteLine("Whisper native runtime: " + Whisper.net.WhisperFactory.GetRuntimeInfo());
// The packages copy this runtime's host-tools service and MCP program beside the output. Connecting installs the service
// from that payload, which verifies its manifest, digests and executable flags, and starts it.
string hostTools = Path.Combine(AppContext.BaseDirectory, Darkspyre.Inference.Connect.HostTools.HostToolsRuntime.PackageFolder);
await using (await Darkspyre.Inference.Connect.HostTools.HostToolsRuntime.ConnectAsync(hostTools)) { }
string mcp = Path.Combine(AppContext.BaseDirectory, "hosttools", "mcp",
    Darkspyre.Inference.Connect.HostTools.HostToolsProtocol.McpExecutableName + (OperatingSystem.IsWindows() ? ".exe" : ""));
if (!File.Exists(mcp)) { throw new InvalidOperationException("The host-tools MCP program is missing: " + mcp); }
Console.WriteLine("Host-tools service installed and connected from the package payload; MCP program present.");
