using System.Reflection;
using System.Text.Json;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Common.STT;

namespace Darkspyre.Inference.Connect.TTS.Tests;

public sealed class VoiceContractMigrationTests
{
    [Theory]
    [InlineData(typeof(ITtsClient))]
    [InlineData(typeof(ISpeechTranscriptionClient))]
    public void HandedOffClientsOwnRetirement(Type type)
    {
        Assert.True(typeof(IDisposable).IsAssignableFrom(type));
        Assert.True(typeof(IAsyncDisposable).IsAssignableFrom(type));
    }

    [Theory]
    [InlineData(typeof(TtsResult))]
    [InlineData(typeof(TtsUtteranceCompleted))]
    public void CompletedAudioCarriesPortableResource(Type type) =>
        Assert.Equal(typeof(InferenceResourceDescriptor), type.GetProperty("Resource")?.PropertyType);

    [Fact]
    public void OneSavedDefaultsOwner()
    {
        Assert.Equal(typeof(XaiTtsOptions), typeof(TtsConnection).GetProperty("XaiDefaults")?.PropertyType);
        Assert.Null(typeof(TtsSetupConfiguration).GetProperty("Xai"));
    }

    [Fact]
    public void OldSavedDefaultsRefuseInsteadOfDisappearing() => Assert.Throws<JsonException>(() =>
        JsonSerializer.Deserialize("{\"Connection\":{\"Endpoint\":\"https://example.invalid/\"},\"Xai\":{\"TextNormalization\":false}}", ConnectTtsJson.TtsSetupConfiguration));

    [Fact]
    public void ConveniencePreservesTerminalEvents() => Assert.Equal(typeof(IAsyncEnumerable<TtsStreamEvent>),
        typeof(ITtsStreamingClient).GetMethod(nameof(ITtsStreamingClient.SpeakStreamAsync))!.ReturnType);

    [Fact]
    public void ReusableSessionExposesPublishedControlCommands()
    {
        Assert.NotNull(typeof(ITtsStreamingSession).GetMethod("UpdateReplacementsAsync"));
        Assert.NotNull(typeof(ITtsStreamingSession).GetMethod("ClearUtteranceAsync"));
        Assert.NotNull(typeof(ConnectTtsJson).GetProperty("TtsStreamEvent", BindingFlags.Public | BindingFlags.Static));
    }
}
