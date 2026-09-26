using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Connect.Audio;
using Darkspyre.Inference.Connect.TTS;

namespace Darkspyre.Inference.Setup.Examples;

/// <summary>Host composition for two distinct Audio tasks; neither adapter starts its external server.</summary>
public static class AudioSetupExample
{
    public static async Task<IReadOnlyList<SetupDraft>> CreateDraftsAsync(
        TtsProviderService speech, AudioProviderService audio,
        ISetupCommitTarget<TtsSetupChanges> speechTarget, ISetupCommitTarget<AudioSetupChanges> audioTarget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(speech);
        ArgumentNullException.ThrowIfNull(audio);
        // Opening settings is local. Render either draft with SetupEditorExample.Render.
        // Nothing reaches the host until the person presses Save.
        TtsSettingsManager speechManager = speech.CreateSettingsManager(speechTarget);
        AudioSettingsManager audioManager = audio.CreateSettingsManager(audioTarget);
        return
        [
            new(speechManager, await speechManager.OpenAsync(TtsTransport.PocketTtsHttp,
                new(SetupChangeIntent.Create, Guid.NewGuid().ToString("N"), null), cancellationToken: cancellationToken).ConfigureAwait(false)),
            new(audioManager, await audioManager.OpenAsync(AudioTransport.TangoFluxHttp,
                new(SetupChangeIntent.Create, Guid.NewGuid().ToString("N"), null), cancellationToken: cancellationToken).ConfigureAwait(false))
        ];
    }

    /// <summary>
    /// The host reads its saved typed configuration and resolves protected headers separately.
    /// Defaults are explicit request values, not a library-owned application settings store.
    /// </summary>
    public static Task<AudioGenerationResult> GenerateAsync(AudioProviderService audio,
        AudioSetupConfiguration saved, IReadOnlyDictionary<string, string> protectedHeaders,
        string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(protectedHeaders);
        IAudioGenerationClient client = audio.Bind(saved.Connection with { Headers = protectedHeaders });
        return client.GenerateAsync(new()
        {
            Prompt = prompt,
            Duration = saved.Duration,
            Steps = saved.Steps,
            GuidanceScale = saved.GuidanceScale,
            Seed = saved.Seed,
            UseCache = saved.UseCache
        }, cancellationToken);
    }
}
