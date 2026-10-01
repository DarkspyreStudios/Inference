using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Setup;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.TTS.Tests;

public sealed class PocketTtsTests
{
    private static readonly string[] ServerVoices = ["server-voice"];
    private static readonly string[] TestVoices = ["voice"];

    [Theory]
    [InlineData(null, null, "{\"text\":\"\\u003C\\u0026\\u003E\",\"voice\":null,\"speaker\":null,\"speed\":1,\"response_format\":\"wav\"}")]
    [InlineData("", 1.25, "{\"text\":\"\\u003C\\u0026\\u003E\",\"voice\":\"\",\"speaker\":\"\",\"speed\":1.25,\"response_format\":\"wav\"}")]
    public async Task BridgeJsonRetainsExactWireBytesAndNullVersusEmptyVoice(string? voice, double? speed, string expected)
    {
        using var handler = new Handler(async (request, token) =>
        {
            Assert.Equal(expected, await request.Content!.ReadAsStringAsync(token));
            Assert.Equal("application/json", request.Content.Headers.ContentType!.MediaType);
            return Wave();
        });
        using var http = new HttpClient(handler);
        await new PocketTtsHttpProvider(http).Bind(Connection() with { PocketTts = new() { Protocol = PocketTtsProtocol.BridgeJson } })
            .SpeakAsync(new() { Text = "<&>", VoiceId = voice, Speed = speed });
    }

    [Fact]
    public async Task VoiceMetadataRetainsWebCaseInsensitivityUnknownMembersAndNullEntries()
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"VoiceOptions":[null,{"ID":"voice-id","Name":"A voice","Category":null}],"future":true}""",
                System.Text.Encoding.UTF8, "application/json")
        }));
        using var http = new HttpClient(handler);
        TtsVoice voice = Assert.Single(await new PocketTtsHttpProvider(http).Bind(Connection()).ListVoicesAsync());
        Assert.Equal("voice-id", voice.Id);
        Assert.Equal("A voice", voice.DisplayName);
        Assert.Null(voice.Description);
    }

    [Theory]
    [InlineData("PocketTtsVoiceList", "{\"voices\":[],\"voiceOptions\":[{\"id\":\"voice\",\"name\":null,\"category\":null}]}")]
    [InlineData("PocketTtsVoiceOption", "{\"id\":\"voice\",\"name\":null,\"category\":null}")]
    [InlineData("PocketTtsBridgePayload", "{\"text\":\"text\",\"voice\":null,\"speaker\":null,\"speed\":1,\"response_format\":\"wav\"}")]
    [InlineData("ProviderPipeline", "")]
    public void ActualCollectiblePocketTtsMetadataAndHttpCallersRetire(string family, string json)
    {
        WeakReference generation = ExercisePocketGeneration(family, json);
        for (int attempt = 0; generation.IsAlive && attempt < 20; attempt++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10);
        }
        Assert.False(generation.IsAlive, "PocketTTS metadata or settled HTTP callers retain the collectible generation.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ExercisePocketGeneration(string family, string json)
    {
        string path = typeof(PocketTtsHttpProvider).Assembly.Location;
        var generation = new PocketGeneration(Path.GetDirectoryName(path)!);
        var weak = new WeakReference(generation);
        try
        {
            Assembly tts = generation.LoadFromAssemblyPath(path);
            if (family != "ProviderPipeline")
            {
                Type owner = tts.GetType("Darkspyre.Inference.Connect.TTS.PocketTtsJsonContext", throwOnError: true)!;
                object context = owner.GetProperty("Default")!.GetValue(null)!;
                JsonTypeInfo metadata = (JsonTypeInfo)owner.GetProperty(family)!.GetValue(context)!;
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(metadata.Type.Assembly));
                Assert.Same(context, metadata.Options.TypeInfoResolver);
                object value = JsonSerializer.Deserialize(json, metadata)!;
                Assert.NotNull(JsonSerializer.Deserialize(JsonSerializer.Serialize(value, metadata), metadata));
            }
            else
            {
                using var handler = new Handler(async (request, token) =>
                {
                    if (request.Method == HttpMethod.Get)
                        return new(HttpStatusCode.OK) { Content = new StringContent("{\"voices\":[\"voice\"]}", System.Text.Encoding.UTF8, "application/json") };
                    Assert.Equal("{\"text\":\"text\",\"voice\":null,\"speaker\":null,\"speed\":1,\"response_format\":\"wav\"}", await request.Content!.ReadAsStringAsync(token));
                    return Wave();
                });
                using var http = new HttpClient(handler);
                Type providerType = tts.GetType(typeof(PocketTtsHttpProvider).FullName!, throwOnError: true)!;
                object provider = Activator.CreateInstance(providerType, http)!;
                Type connectionType = tts.GetType(typeof(TtsConnection).FullName!, throwOnError: true)!;
                object connection = Activator.CreateInstance(connectionType)!;
                connectionType.GetProperty("Endpoint")!.SetValue(connection, new Uri("https://provider.invalid"));
                object options = connectionType.GetProperty("PocketTts")!.GetValue(connection)!;
                PropertyInfo protocol = options.GetType().GetProperty("Protocol")!;
                protocol.SetValue(options, Enum.Parse(protocol.PropertyType, "BridgeJson"));
                object client = providerType.GetMethod("Bind")!.Invoke(provider, [connection])!;
                ((Task)client.GetType().GetMethod("ListVoicesAsync")!.Invoke(client, [CancellationToken.None])!).GetAwaiter().GetResult();
                Type requestType = tts.GetType(typeof(TtsRequest).FullName!, throwOnError: true)!;
                object request = Activator.CreateInstance(requestType)!;
                requestType.GetProperty("Text")!.SetValue(request, "text");
                Task operation = (Task)client.GetType().GetMethod("SpeakAsync")!.Invoke(client, [request, CancellationToken.None])!;
                operation.GetAwaiter().GetResult();
                object result = operation.GetType().GetProperty("Result")!.GetValue(operation)!;
                object provenance = result.GetType().GetProperty("Provenance")!.GetValue(result)!;
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(provenance.GetType().Assembly));
                Assert.Equal(2, handler.Calls);
            }
        }
        finally { generation.Unload(); }
        return weak;
    }

    private sealed class PocketGeneration(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name?.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal) != true) return null;
            string path = Path.Combine(directory, assemblyName.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }

    [Fact]
    public async Task VoiceOptionsPreserveServerIdentifiersAndDescriptions()
    {
        using var handler = new Handler((request, _) =>
        {
            Assert.Equal("/api/voices", request.RequestUri!.AbsolutePath);
            return Task.FromResult(Json(new { voiceOptions = new[] { new { id = "voice-id", name = "A voice", category = "Built-in" } } }));
        });
        using var http = new HttpClient(handler);
        TtsVoice voice = Assert.Single(await new PocketTtsHttpProvider(http).Bind(Connection()).ListVoicesAsync());
        Assert.Equal("voice-id", voice.Id);
        Assert.Equal("A voice", voice.DisplayName);
        Assert.Equal("Built-in", voice.Description);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task EmptyVoiceListFallsBackToHealthButHttpFailuresDoNot()
    {
        using var handler = new Handler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/voices", StringComparison.Ordinal)
            ? Json(new { voices = Array.Empty<string>() }) : Json(new { voices = ServerVoices })));
        using var http = new HttpClient(handler);
        ITtsClient client = new PocketTtsHttpProvider(http).Bind(Connection());
        Assert.Equal("server-voice", Assert.Single(await client.ListVoicesAsync()).Id);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task MultipartUsesExactFieldsAndDoesNotPretendToSupportSpeed()
    {
        using var handler = new Handler(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/tts", request.RequestUri!.AbsolutePath);
            string body = await request.Content!.ReadAsStringAsync(token);
            Assert.Contains("name=text", body, StringComparison.Ordinal);
            Assert.Contains("name=output_format", body, StringComparison.Ordinal);
            Assert.Contains("name=voice_url", body, StringComparison.Ordinal);
            Assert.Contains("test speech", body, StringComparison.Ordinal);
            Assert.DoesNotContain("speed", body, StringComparison.Ordinal);
            return Wave();
        });
        using var http = new HttpClient(handler);
        ITtsClient client = new PocketTtsHttpProvider(http).Bind(Connection());
        Assert.False((await client.GetCapabilitiesAsync()).SupportsSpeed);
        TtsResult result = await client.SpeakAsync(new() { Text = "test speech", VoiceId = "voice", Speed = 1 });
        Assert.Equal("audio/wav", result.ContentType);
        Assert.Equal(46, result.Audio.Length);
        Assert.Equal(client.Descriptor.Id, result.Provenance.ImplementationId);
        InferenceException error = await Assert.ThrowsAsync<InferenceException>(() => client.SpeakAsync(new() { Text = "test", Speed = 2 }));
        Assert.Equal(InferenceFailureCode.UnsupportedFeature, error.Code);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task BridgeJsonPreservesVoiceSpeakerSpeedAndFormat()
    {
        using var handler = new Handler(async (request, token) =>
        {
            using JsonDocument body = await JsonDocument.ParseAsync(await request.Content!.ReadAsStreamAsync(token), cancellationToken: token);
            Assert.Equal("test speech", body.RootElement.GetProperty("text").GetString());
            Assert.Equal("voice", body.RootElement.GetProperty("voice").GetString());
            Assert.Equal("voice", body.RootElement.GetProperty("speaker").GetString());
            Assert.Equal(1.25, body.RootElement.GetProperty("speed").GetDouble());
            Assert.Equal("wav", body.RootElement.GetProperty("response_format").GetString());
            return Wave();
        });
        using var http = new HttpClient(handler);
        ITtsClient client = new PocketTtsHttpProvider(http).Bind(Connection() with { PocketTts = new() { Protocol = PocketTtsProtocol.BridgeJson } });
        Assert.True((await client.GetCapabilitiesAsync()).SupportsSpeed);
        await client.SpeakAsync(new() { Text = "test speech", VoiceId = "voice", Speed = 1.25 });
    }

    [Theory]
    [InlineData(401, InferenceFailureCode.AuthenticationRequired)]
    [InlineData(403, InferenceFailureCode.AccessDenied)]
    [InlineData(302, InferenceFailureCode.ProtocolFailure)]
    [InlineData(500, InferenceFailureCode.ExecutionFailed)]
    public async Task HttpFailuresAreTypedSanitizedAndNeverRetried(int status, InferenceFailureCode code)
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("private response") }));
        using var http = new HttpClient(handler);
        InferenceException error = await Assert.ThrowsAsync<InferenceException>(() => new PocketTtsHttpProvider(http).Bind(Connection()).ListVoicesAsync());
        Assert.Equal(code, error.Code);
        Assert.DoesNotContain("private response", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("RIFFbrokenWAVE")]
    public async Task InvalidAudioIsRejected(string data)
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(data) }));
        using var http = new HttpClient(handler);
        InferenceException error = await Assert.ThrowsAsync<InferenceException>(() => new PocketTtsHttpProvider(http).Bind(Connection()).SpeakAsync(new() { Text = "test" }));
        Assert.Equal(InferenceFailureCode.ProtocolFailure, error.Code);
    }

    [Fact]
    public async Task CallerCancellationRemainsCancellationAndTimeoutIsDistinct()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new Handler((_, token) => { cancellation.Cancel(); token.ThrowIfCancellationRequested(); return Task.FromResult(Wave()); });
        using var http = new HttpClient(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PocketTtsHttpProvider(http).Bind(Connection())
            .SpeakAsync(new() { Text = "test" }, cancellation.Token));
        using var timeoutHandler = new Handler((_, _) => throw new TaskCanceledException());
        using var timeoutHttp = new HttpClient(timeoutHandler);
        InferenceException error = await Assert.ThrowsAsync<InferenceException>(() => new PocketTtsHttpProvider(timeoutHttp).Bind(Connection()).SpeakAsync(new() { Text = "test" }));
        Assert.Equal(InferenceFailureCode.TimedOut, error.Code);
        Assert.Equal(1, timeoutHandler.Calls);
    }

    [Theory]
    [InlineData("https://another.test/tts")]
    [InlineData("//another.test/tts")]
    [InlineData("\\\\another.test\\tts")]
    public void EndpointPathsCannotSendCredentialsToAnotherOrigin(string path)
    {
        using var http = new HttpClient();
        InferenceException error = Assert.Throws<InferenceException>(() => new PocketTtsHttpProvider(http)
            .Bind(Connection() with { PocketTts = new() { SpeakPath = path } }));
        Assert.Equal(InferenceFailureCode.InvalidRequest, error.Code);
    }

    [Fact]
    public async Task SetupSaveIsTypedAndDoesNotConnectOrDiscardHeaders()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("No requests expected."));
        using var http = new HttpClient(handler);
        var target = new Target();
        var service = new TtsProviderService([new PocketTtsHttpProvider(http)]);
        TtsSettingsManager manager = service.CreateSettingsManager(target);
        TtsSettings session = await manager.OpenAsync(TtsTransport.PocketTtsHttp, new(SetupChangeIntent.Edit, "connection", "v1"), new(Connection() with { Headers = new Dictionary<string, string> { ["Authorization"] = "private-key" } }));
        SetupRepeatableGroupField group = Assert.Single(Fields(session).OfType<SetupRepeatableGroupField>());
        SetupSecretField secret = Assert.Single(Assert.Single(group.Rows).Fields.OfType<SetupSecretField>());
        Assert.True(secret.HasValue);
        Assert.Equal(SetupEditState.Applied, secret.SetValue(SetupSecretEdit.Replace("new-key")).State);
        Assert.Equal(SetupCommitState.Committed, (await manager.SaveAsync(session)).State);
        Assert.Equal("v1", target.Changes!.Identity.ExpectedVersion);
        Assert.Equal("new-key", target.Changes.Headers.Set["Authorization"]);
        Assert.Empty(target.Changes.Headers.Remove);
        Assert.Empty(target.Changes.Configuration.Connection.Headers);
        Assert.Null(session.Status.Ready.Value);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task SetupCreateAndEditRoundTripTypedDefaultsWithoutConnecting()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("No requests expected."));
        using var http = new HttpClient(handler);
        var service = new TtsProviderService([new PocketTtsHttpProvider(http)]);
        var createTarget = new Target();
        TtsSettingsManager createManager = service.CreateSettingsManager(createTarget);
        TtsSettings create = await createManager.OpenAsync(TtsTransport.PocketTtsHttp, new(SetupChangeIntent.Create, "connection", null));
        Assert.Equal(SetupEditState.Applied, Assert.Single(Fields(create).OfType<SetupChoiceField>(),
            field => field.Description.Id == "voice").SetValue("host-voice").State);
        Assert.Equal(SetupCommitState.Committed, (await createManager.SaveAsync(create)).State);
        Assert.Equal("host-voice", createTarget.Changes!.Configuration.VoiceId);
        var editTarget = new Target();
        TtsSettingsManager editManager = service.CreateSettingsManager(editTarget);
        TtsSettings edit = await editManager.OpenAsync(TtsTransport.PocketTtsHttp, new(SetupChangeIntent.Edit, "connection", "saved-v1"), createTarget.Changes.Configuration);
        Assert.Equal("host-voice", Assert.Single(Fields(edit).OfType<SetupChoiceField>(), field => field.Description.Id == "voice").Value);
        Assert.Equal(SetupEditState.Applied, Assert.Single(Fields(edit).OfType<SetupChoiceField>(),
            field => field.Description.Id == "protocol").SetValue(nameof(PocketTtsProtocol.BridgeJson)).State);
        Assert.Equal(SetupEditState.Applied, Assert.Single(Fields(edit).OfType<SetupDecimalField>()).SetValue(1.25m).State);
        Assert.Equal(SetupCommitState.Committed, (await editManager.SaveAsync(edit)).State);
        Assert.Equal("saved-v1", editTarget.Changes!.Identity.ExpectedVersion);
        Assert.Equal("host-voice", editTarget.Changes.Configuration.VoiceId);
        Assert.Equal(1.25, editTarget.Changes.Configuration.Speed);
        Assert.Equal(PocketTtsProtocol.BridgeJson, editTarget.Changes.Configuration.Connection.PocketTts.Protocol);
        Assert.Null(edit.Status.Ready.Value);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task SetupSeparatesVoiceChecksFromSynthesisAndExposesProtocolConstraints()
    {
        using var handler = new Handler((request, _) => Task.FromResult(request.Method == HttpMethod.Get ? Json(new { voices = TestVoices }) : Wave()));
        using var http = new HttpClient(handler);
        var service = new TtsProviderService([new PocketTtsHttpProvider(http)]);
        TtsSettingsManager manager = service.CreateSettingsManager(new Target());
        TtsSettings session = await manager.OpenAsync(TtsTransport.PocketTtsHttp, new(SetupChangeIntent.Create, "new", null));
        await manager.RunAsync(session, Assert.Single(session.Actions, action => action.Purpose == SetupActionPurpose.CheckReadiness).Id);
        Assert.True(session.Status.Verified.Value);
        Assert.Null(session.Status.Ready.Value);
        SetupActionDescriptor synthesize = Assert.Single(session.Actions, action => action.Purpose == SetupActionPurpose.Test);
        Assert.True(synthesize.SideEffects.HasFlag(SetupSideEffect.PotentialBilling));
        Assert.Equal(SetupActionState.Completed, (await manager.RunAsync(session, synthesize.Id)).State);
        Assert.True(session.Status.Ready.Value);
        SetupDecimalField speed = Assert.Single(Fields(session).OfType<SetupDecimalField>());
        Assert.Equal(1, speed.Maximum);
        speed.SetValue(2);
        Assert.False(session.Validate().IsValid);
        Assert.Single(Fields(session).OfType<SetupChoiceField>(), field => field.Description.Id == "protocol").SetValue(nameof(PocketTtsProtocol.BridgeJson));
        Assert.True(session.Validate().IsValid);
        Assert.True(session.Status.Ready.IsStale);
    }

    [Fact]
    public void RegistrationExposesAnAudioTaskWithoutStartingAnything()
    {
        var services = new ServiceCollection();
        services.AddTtsConnect();
        using ServiceProvider container = services.BuildServiceProvider();
        InferenceSetupDefinition definition = Assert.Single(container.GetRequiredService<TtsProviderService>().SetupDefinitions);
        Assert.Equal(InferenceCapability.Audio, Assert.Single(definition.Implementation.Tasks).GetCapability());
    }

    [Fact]
    public async Task SetupDirtyStateReturnsToCleanAfterProtocolAndHeaderRowReverts()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("No requests expected."));
        using var http = new HttpClient(handler);
        var service = new TtsProviderService([new PocketTtsHttpProvider(http)]);
        TtsSettingsManager manager = service.CreateSettingsManager(new Target());
        TtsSettings session = await manager.OpenAsync(TtsTransport.PocketTtsHttp, new(SetupChangeIntent.Edit, "id", "v1"), new(Connection()));
        SetupChoiceField Protocol() => Assert.Single(Fields(session).OfType<SetupChoiceField>(), field => field.Description.Id == "protocol");
        Assert.False(session.HasUnsavedChanges);
        Assert.Equal(SetupChoiceCapabilities.Bounded, Protocol().Capabilities);
        Protocol().SetValue(nameof(PocketTtsProtocol.BridgeJson));
        Assert.True(session.HasUnsavedChanges);
        Protocol().SetValue(nameof(PocketTtsProtocol.Multipart));
        Assert.False(session.HasUnsavedChanges);
        Assert.Single(Fields(session).OfType<SetupRepeatableGroupField>()).Add();
        Assert.True(session.HasUnsavedChanges);
        SetupRepeatableGroupField group = Assert.Single(Fields(session).OfType<SetupRepeatableGroupField>());
        group.Remove(Assert.Single(group.Rows).Id);
        Assert.False(session.HasUnsavedChanges);
    }

    private static IEnumerable<SetupField> Fields(TtsSettings settings) => settings.Steps.SelectMany(step => step.Sections).SelectMany(section => section.Fields);
    private static TtsConnection Connection() => new() { Endpoint = new("https://example.test/api") };
    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    private static HttpResponseMessage Wave()
    {
        // One 16-bit mono sample, 16 kHz. Complete RIFF/fmt/data chunks.
        byte[] bytes = Convert.FromHexString("524946462600000057415645666D74201000000001000100803E0000007D00000200100064617461020000000000");
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new("audio/wav");
        return response;
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return send(request, cancellationToken); }
    }
    private sealed class Target : ISetupCommitTarget<TtsSetupChanges>
    {
        public TtsSetupChanges? Changes { get; private set; }
        public Task<SetupCommitResult> CommitAsync(TtsSetupChanges changes, CancellationToken cancellationToken = default)
        { Changes = changes; return Task.FromResult(new SetupCommitResult(SetupCommitState.Committed, "v2")); }
    }
}
