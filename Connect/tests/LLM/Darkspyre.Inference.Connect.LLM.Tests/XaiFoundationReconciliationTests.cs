using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class XaiFoundationReconciliationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GrokDoesNotInterpretUnqualifiedHeadersAsOpenAiAccountEvidence(bool stream)
    {
        using var handler = new QuotaHandler(stream);
        using var http = new HttpClient(handler);
        var client = new XaiResponsesLlmClient(http);
        var connection = new LlmConnection
        {
            Version = "1",
            Transport = LlmTransport.XaiResponses,
            Endpoint = "https://api.x.ai/v1",
            Credential = new LlmCredential { OAuth = new() { AccessToken = "controlled", AccountId = "account" } }.Serialize(),
            CapabilityOverrides = new() { SupportsStreaming = stream }
        };
        var request = new LlmChatRequest { ModelId = "grok-4.7", Messages = [new("user", "question")] };
        int observations = 0;
        int completions = 0;
        await foreach (LlmStreamEvent item in client.StreamAsync(connection, request))
        {
            if (item is AccountUsageObservedEvent) observations++;
            if (item is LlmChatCompletionEvent) completions++;
        }
        Assert.Equal(0, observations);
        Assert.Equal(1, completions);
    }

    [Fact]
    public void TransportAllocationKeepsEveryExistingIdentityAndAllThreeNewIdentities()
    {
        (LlmTransport Transport, int Id)[] expected = [(LlmTransport.OpenAiResponses, 0), (LlmTransport.OpenAiCompatibleChat, 1),
            (LlmTransport.AnthropicMessages, 2), (LlmTransport.OpenAiCodex, 3), (LlmTransport.AwsBedrockConverse, 4),
            (LlmTransport.GoogleAntigravityCli, 6), (LlmTransport.AnthropicClaudeCli, 7), (LlmTransport.AwsBedrockAgentCore, 8),
            (LlmTransport.SelfHost, 9), (LlmTransport.AppleFoundationModels, 10), (LlmTransport.GoogleVertexAi, 11),
            (LlmTransport.GoogleGeminiApi, 12), (LlmTransport.XaiResponses, 13),
            (LlmTransport.AzureOpenAiResponses, 14), (LlmTransport.AzureOpenAiChat, 15)];
        foreach ((LlmTransport transport, int id) in expected) Assert.Equal(id, (int)transport);
        Assert.False(Enum.IsDefined((LlmTransport)5));
    }

    [Fact]
    public void ActualForeignAuthSettingsAndContinuationCallersReleaseTheirGeneration()
    {
        WeakReference generation = ExerciseGeneration();
        for (int attempt = 0; generation.IsAlive && attempt < 20; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Thread.Sleep(10);
        }
        Assert.False(generation.IsAlive, "The actual Grok auth, settings and continuation callers retain their generation.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ExerciseGeneration()
    {
        string directory = Path.GetDirectoryName(typeof(XaiFoundationReconciliationTests).Assembly.Location)!;
        var generation = new ConnectGeneration(directory);
        var weak = new WeakReference(generation);
        try
        {
            Assembly connect = generation.LoadFromAssemblyPath(typeof(ConnectLlmJson).Assembly.Location);
            Assembly common = generation.LoadFromAssemblyPath(typeof(InferenceJson).Assembly.Location);
            Assembly fixture = generation.LoadFromAssemblyPath(typeof(XaiFoundationReconciliationTests).Assembly.Location);
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(connect));
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(common));
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(fixture));
            Type caller = fixture.GetType(typeof(XaiFoundationReconciliationTests).FullName!, true)!;
            var operation = (Task)caller.GetMethod(nameof(ForeignCallerAsync), BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
            operation.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        }
        finally { generation.Unload(); }
        return weak;
    }

    private static async Task ForeignCallerAsync()
    {
        AssemblyLoadContext generation = AssemblyLoadContext.GetLoadContext(typeof(XaiFoundationReconciliationTests).Assembly)!;
        Assert.True(generation.IsCollectible);
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(typeof(XaiAuthenticator).Assembly));
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(typeof(SetupChoiceField).Assembly));
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(typeof(LlmAttachment).Assembly));
        using var handler = new FoundationHandler();
        using var http = new HttpClient(handler);
        var auth = new XaiAuthenticator(http);
        LlmDeviceCode code = await auth.StartAsync();
        Assert.Equal("https://auth.x.ai/device", code.VerificationUrl);
        LlmCredential credential = await auth.CompleteAsync(code.DeviceAuthId, code.UserCode);
        Assert.Null(credential.ApiKey);
        Assert.Equal("principal", credential.OAuth!.AccountId);
        Assert.Equal("controlled-access", LlmCredential.Parse(credential.Serialize())!.OAuth!.AccessToken);
        var connection = new LlmConnection
        {
            Version = "v1",
            Transport = LlmTransport.XaiResponses,
            Endpoint = "https://api.x.ai/v1",
            Xai = new(),
            Credential = credential.Serialize()
        };
        var settings = new LlmSettings(ProviderDefinitions.GrokSubscription, LlmTransport.XaiResponses,
            new(SetupChangeIntent.Edit, "connection", "v1"), connection, "grok-4.7", null);
        SetupChoiceField tier = settings.Steps.SelectMany(step => step.Sections).SelectMany(section => section.Fields)
            .OfType<SetupChoiceField>().First(field => field.Description.Id == "xai-service-tier");
        Assert.Equal(SetupEditState.Applied, tier.SetValue("Priority").State);
        Assert.Equal(XaiServiceTier.Default, connection.Xai.ServiceTier);
        var setup = new LlmSetupConfiguration(settings.CurrentConnection(), settings.ModelId);
        setup = JsonSerializer.Deserialize(JsonSerializer.Serialize(setup, ConnectLlmJson.SetupConfiguration), ConnectLlmJson.SetupConfiguration)!;
        Assert.Equal(XaiServiceTier.Priority, setup.Connection.Xai!.ServiceTier);
        Assert.Equal("https://api.x.ai/v1", setup.Connection.Endpoint);
        connection = setup.Connection;
        var source = new Source(connection);
        await using (var access = new LlmConnectionAccess(source, [auth]))
        {
            var authorization = new LlmConnectionAuthorization("connection", access);
            ProviderAuthorizationHeader header = await authorization.GetAsync();
            Assert.Equal("Bearer", header.Scheme);
            Assert.Equal("controlled-access", header.Value);
            Assert.DoesNotContain("controlled-access", header.ToString());
        }
        Assert.Equal(0, source.Saves);
        var client = new XaiResponsesLlmClient(http);
        foreach (bool stream in new[] { false, true })
        {
            LlmConnection current = connection with { CapabilityOverrides = new() { SupportsStreaming = stream } };
            var request = new LlmChatRequest { ModelId = "grok-4.7", Messages = [new("user", "first")] };
            LlmChatResult? result = null;
            await foreach (LlmStreamEvent item in client.StreamAsync(current, request))
                if (item is LlmChatCompletionEvent completion) result = completion.Result;
            Assert.NotNull(result);
            Assert.Equal("answer", result.Text);
            Assert.DoesNotContain("opaque-secret", result.ToString() + result.Diagnostics!.RawFinalResponseJson);
            result = JsonSerializer.Deserialize(JsonSerializer.Serialize(result, ConnectLlmJson.ChatResult), ConnectLlmJson.ChatResult)!;
            Assert.NotNull(result.ContinuationState);
            JsonArray output = ProviderContinuationStateCodec.Decode(result.ContinuationState, current, request.ModelId);
            Assert.Equal("web_search_call", output[0]!["type"]!.GetValue<string>());
            Assert.Equal("opaque-secret", output[0]!["encrypted_content"]!.GetValue<string>());
            LlmConnection rotated = current with
            {
                Version = "v2",
                Credential = (credential with
                { OAuth = credential.OAuth with { AccessToken = "rotated" } }).Serialize()
            };
            Assert.True(ProviderContinuationStateCodec.IsBound(result.ContinuationState, rotated, request.ModelId));
            Assert.False(ProviderContinuationStateCodec.IsBound(result.ContinuationState,
                current with { Transport = LlmTransport.OpenAiResponses }, request.ModelId));
            request = request with
            {
                Messages = [new("user", "first"), new("assistant", result.Text) { ContinuationState = result.ContinuationState }, new("user", "next")]
            };
            await foreach (LlmStreamEvent item in client.StreamAsync(rotated, request)) { }
            using JsonDocument sent = JsonDocument.Parse(handler.LastBody!);
            JsonElement input = sent.RootElement.GetProperty("input");
            Assert.Equal("web_search_call", input[1].GetProperty("type").GetString());
            Assert.Equal("opaque-secret", input[1].GetProperty("encrypted_content").GetString());
            Assert.True(input[1].GetProperty("unknown").GetProperty("retained").GetBoolean());
            Assert.False(sent.RootElement.GetProperty("store").GetBoolean());
            Assert.Equal("priority", sent.RootElement.GetProperty("service_tier").GetString());
        }
        Assert.Equal(7, handler.Calls);
        var credentialBoundaries = new ConnectionAccessTests();
        foreach (string boundary in new[] { "resolve", "refresh", "save" })
        {
            await credentialBoundaries.CancellationAndRetirementDrainEveryCredentialBoundary(boundary, true, false);
            await credentialBoundaries.CancellationAndRetirementDrainEveryCredentialBoundary(boundary, true, true);
        }
        await credentialBoundaries.LateSaveCompletionIsDrainedAndNeverReturnsAuthorization();
    }

    private sealed class Source(LlmConnection connection) : ILlmConnectionSource
    {
        public int Saves { get; private set; }
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) => Task.FromResult<LlmConnection?>(connection);
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default)
        { Saves++; throw new InvalidOperationException("The controlled fresh credential should not be saved."); }
    }

    private sealed class FoundationHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? LastBody { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            string? path = request.RequestUri?.AbsoluteUri;
            string body = path switch
            {
                "https://auth.x.ai/oauth2/device/code" => """{"device_code":"controlled-device","user_code":"controlled-user","verification_uri":"https://auth.x.ai/device","expires_in":600,"interval":5}""",
                "https://auth.x.ai/oauth2/token" => """{"access_token":"controlled-access","refresh_token":"controlled-refresh","token_type":"Bearer","expires_in":21600,"scope":"openid api:access"}""",
                "https://api.x.ai/v1/me" => """{"user_id":"principal","team_id":"team","team_blocked":false}""",
                "https://api.x.ai/v1/responses" => """{"status":"completed","output":[{"type":"web_search_call","id":"hosted","encrypted_content":"opaque-secret","unknown":{"retained":true}},{"type":"message","id":"message","role":"assistant","content":[{"type":"output_text","text":"answer"}]}]}""",
                _ => throw new InvalidOperationException("The controlled fixture received an unexpected endpoint.")
            };
            bool streaming = false;
            if (path == "https://api.x.ai/v1/responses")
            {
                LastBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                using JsonDocument sent = JsonDocument.Parse(LastBody);
                streaming = sent.RootElement.GetProperty("stream").GetBoolean();
                Assert.True(request.Headers.Authorization?.ToString() is "Bearer controlled-access" or "Bearer rotated");
            }
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(streaming ? "data: {\"type\":\"response.completed\",\"response\":" + body + "}\n\n" : body,
                    Encoding.UTF8, streaming ? "text/event-stream" : "application/json")
            };
        }
    }

    private sealed class ConnectGeneration(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name?.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal) != true) return null;
            string path = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }

    private sealed class QuotaHandler(bool stream) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            const string result = "{\"status\":\"completed\",\"output\":[]}";
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(stream ? "data: {\"type\":\"response.completed\",\"response\":" + result + "}\n\n" : result,
                    Encoding.UTF8, stream ? "text/event-stream" : "application/json")
            };
            response.Headers.Add("x-ratelimit-limit-requests", "100");
            response.Headers.Add("x-ratelimit-remaining-requests", "90");
            response.Headers.Add("x-ratelimit-reset-requests", "1s");
            return Task.FromResult(response);
        }
    }
}
