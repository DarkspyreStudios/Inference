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

    [Fact]
    public void EffectiveDefaultsPreserveNullInheritanceAndExplicitOverrides()
    {
        var defaults = new XaiTtsOptions(2, true, new Dictionary<string, string> { ["a"] = "b" });
        XaiTtsOptions inherited = Effective(defaults, new())!;
        Assert.Equal(2, inherited.OptimizeStreamingLatency); Assert.True(inherited.TextNormalization);
        Assert.Equal("b", inherited.Replace!["a"]);
        XaiTtsOptions replaced = Effective(defaults, new(0, false, new Dictionary<string, string>()))!;
        Assert.Equal(0, replaced.OptimizeStreamingLatency); Assert.False(replaced.TextNormalization); Assert.Empty(replaced.Replace!);
        Assert.Null(Effective(null, null));
        Assert.False(new TtsCapabilities(false, false, []).SupportsReplacementUpdates);
        Assert.False(new TtsCapabilities(false, false, []).SupportsUtteranceClearing);
        static XaiTtsOptions? Effective(XaiTtsOptions? defaults, XaiTtsOptions? options) =>
            (XaiTtsOptions?)typeof(XaiTtsOptions).Assembly.GetType("Darkspyre.Inference.Connect.TTS.TtsData", true)!
                .GetMethod("EffectiveOptions", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [defaults, options]);
    }

    [Fact]
    public void OwnedUnionRefusesUnknownOrMalformedVariantsAndNestedOldSetup()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"type\":\"future\"}", ConnectTtsJson.TtsStreamEvent));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"type\":\"audio\",\"Audio\":null}", ConnectTtsJson.TtsStreamEvent));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"type\":\"replacements-updated\"}", ConnectTtsJson.TtsStreamEvent));
        Assert.Throws<ArgumentNullException>(() => new TtsReplacementsUpdated(null!));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"Identity\":{\"Id\":\"i\",\"Intent\":0},\"Configuration\":{\"Connection\":{\"Endpoint\":\"https://example.invalid/\"},\"Xai\":{}},\"Headers\":{\"Set\":{},\"Remove\":[]}}", ConnectTtsJson.TtsSetupChanges));
        var replacements = new Dictionary<string, string> { ["actual"] = "spoken" };
        var observed = new TtsReplacementsUpdated(replacements); replacements.Clear();
        Assert.Equal("spoken", observed.Replace["actual"]);
        TtsStreamEvent completed = new TtsUtteranceCompleted("actual");
        Assert.IsType<TtsUtteranceCompleted>(JsonSerializer.Deserialize(JsonSerializer.Serialize(completed, ConnectTtsJson.TtsStreamEvent), ConnectTtsJson.TtsStreamEvent));
    }
}
