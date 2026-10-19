using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;
using Handler = Darkspyre.Inference.Connect.LLM.Tests.DirectCodexTests.Handler;

namespace Darkspyre.Inference.Connect.LLM.Tests;

/// <summary>The Grok library (DSA-1070): device sign-in, the subscription proxy route and model list, speed tier and
/// continuation of reasoning.</summary>
public sealed class GrokTests
{
    private const string DeviceCode = """
        {"device_code":"device-1","user_code":"ABCD","verification_uri":"https://accounts.x.ai/device","expires_in":900,"interval":1}
        """;
    private const string Tokens = """{"access_token":"grok-access","token_type":"Bearer","expires_in":3600,"refresh_token":"refresh"}""";

    /// <summary>The device login runs through the shared seam: a pending code keeps polling, slow_down widens the interval
    /// by five seconds, and the approved code is saved only after /v1/me names the account (B2, S1).</summary>
    [Fact]
    public async Task DeviceLoginPollsThroughTheSeamAndSavesTheAccountAfterApproval()
    {
        var polls = new Queue<HttpResponseMessage>([Error("authorization_pending"), Error("slow_down"), Json(Tokens)]);
        using var handler = new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/oauth2/device/code" => Json(DeviceCode),
            "/oauth2/token" => polls.Dequeue(),
            "/v1/me" => Json("""{"user_id":"user-1","team_blocked":false}"""),
            _ => throw new InvalidOperationException(request.RequestUri.ToString())
        });
        var source = new Source(Connection() with { Credential = null });
        using var service = Service(source, handler);

        LlmDeviceCode code = await service.StartDeviceLoginAsync(LlmTransport.XaiResponses);
        LlmAuthPendingException pending = await Assert.ThrowsAsync<LlmAuthPendingException>(() =>
            service.CompleteDeviceLoginAsync(LlmTransport.XaiResponses, "grok", code.DeviceAuthId, code.UserCode, "1"));
        LlmAuthPendingException slow = await Assert.ThrowsAsync<LlmAuthPendingException>(() =>
            service.CompleteDeviceLoginAsync(LlmTransport.XaiResponses, "grok", code.DeviceAuthId, code.UserCode, "1"));
        Assert.Equal(0, source.Saves);
        await service.CompleteDeviceLoginAsync(LlmTransport.XaiResponses, "grok", code.DeviceAuthId, code.UserCode, "1");

        Assert.Equal(("ABCD", 1), (code.UserCode, code.IntervalSeconds));
        Assert.Equal(1, code.AfterPending(pending).IntervalSeconds);
        Assert.Equal(6, code.AfterPending(slow).IntervalSeconds);
        Assert.Equal(1, source.Saves);
        Assert.Equal(("grok-access", "user-1"), (source.Saved!.OAuth!.AccessToken, source.Saved.OAuth.AccountId));
    }

    /// <summary>Setup offers Grok's device sign-in and no region; the connection uses the subscription proxy, and the check
    /// after approval marks the connection signed in (B1, B2, S1).</summary>
    [Fact]
    public async Task SetupSignsInWithTheDeviceCodeAndUsesTheSubscriptionProxy()
    {
        using var handler = new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/oauth2/device/code" => Json(DeviceCode),
            "/oauth2/token" => Json(Tokens),
            "/v1/me" => Json("""{"user_id":"user-1"}"""),
            _ => throw new InvalidOperationException(request.RequestUri.ToString())
        });
        using var service = Service(new Source(Connection() with { Credential = null }), handler);
        LlmSettingsManager manager = service.CreateSettingsManager(new Target());
        LlmSettings settings = await manager.OpenAsync(LlmTransport.XaiResponses,
            new(SetupChangeIntent.Create, "grok", null), null, "grok-4.7");

        SetupActionDescriptor signIn = Assert.Single(settings.Actions, action => action.Purpose == SetupActionPurpose.Authenticate);
        await manager.RunAsync(settings, signIn.Id);
        SetupActionResult approved = await manager.RunAsync(settings,
            Assert.IsType<SetupDeviceAuthorization>(settings.Interaction).Continue.Id);

        Assert.Equal(("authorize", "Sign in with Grok"), (signIn.Id, signIn.DisplayName));
        Assert.DoesNotContain(settings.Steps.SelectMany(step => step.Sections).SelectMany(section => section.Fields),
            field => field.Description.Id == "xai-region");
        Assert.Equal("https://cli-chat-proxy.grok.com/v1", settings.CurrentConnection().Endpoint);
        Assert.Equal(SetupActionState.Completed, approved.State);
        Assert.True(settings.Status.Authenticated.Value);
        Assert.Equal(SetupProviderGroup.Subscription, GrokDefinition.Value.Group);
        Assert.NotNull(GrokDefinition.Value.Icon);
    }

    /// <summary>A denied or expired code and a team-blocked account each end sign-in with their message and save nothing.</summary>
    [Theory]
    [InlineData(false, "Grok sign-in was denied or expired. Start sign-in again.")]
    [InlineData(true, "This Grok account is not entitled to API access.")]
    public async Task RefusedSignInSavesNothing(bool teamBlocked, string message)
    {
        using var handler = new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/oauth2/token" => teamBlocked ? Json(Tokens) : Error("access_denied"),
            "/v1/me" => Json("""{"user_id":"user-1","team_blocked":true}"""),
            _ => throw new InvalidOperationException(request.RequestUri.ToString())
        });
        var source = new Source(Connection() with { Credential = null });
        using var service = Service(source, handler);

        Exception refusal = await Assert.ThrowsAnyAsync<Exception>(() =>
            service.CompleteDeviceLoginAsync(LlmTransport.XaiResponses, "grok", "device-1", "ABCD", "1"));

        Assert.Equal(message, refusal.Message);
        Assert.IsType(teamBlocked ? typeof(InferenceException) : typeof(LlmProviderException), refusal);
        Assert.Equal(0, source.Saves);
    }

    /// <summary>The request goes to the subscription proxy with the headers its auth middleware reads.</summary>
    [Fact]
    public async Task RequestsGoToTheSubscriptionProxyWithItsAuthHeaders()
    {
        using var handler = new Handler(_ => DirectCodexTests.Sse(DirectCodexTests.Completed));
        var client = new GrokResponsesLlmClient(new HttpClient(handler));

        await Complete(client, Connection(), Request("grok-4.7"));

        Assert.Equal("https://cli-chat-proxy.grok.com/v1/responses", handler.Uri!.AbsoluteUri);
        Assert.Equal(("Bearer test-token", "xai-grok-cli", "authenticate-response"),
            (handler.Headers["Authorization"], handler.Headers["X-XAI-Token-Auth"], handler.Headers["x-authenticateresponse"]));
    }

    /// <summary>The fast tier is sent as priority; a turn that names no tier takes the saved tier (B16, S5).</summary>
    [Theory]
    [InlineData(LlmServiceTier.Fast, XaiServiceTier.Default, "priority")]
    [InlineData(null, XaiServiceTier.Priority, "priority")]
    public async Task FastTierSendsPriority(LlmServiceTier? tier, XaiServiceTier saved, string expected)
    {
        using var handler = new Handler(_ => DirectCodexTests.Sse(DirectCodexTests.Completed));
        var client = new GrokResponsesLlmClient(new HttpClient(handler));
        LlmConnection connection = Connection(new() { ServiceTier = saved });
        LlmProviderCapabilities capabilities = client.GetCapabilities(connection, "grok-4.7");

        await Complete(client, connection, LlmRequestNormalizer.Normalize(connection, Request("grok-4.7") with { ServiceTier = tier }, capabilities));

        Assert.Equal(expected, (string?)JsonNode.Parse(handler.Body!)!["service_tier"]);
        Assert.Equal([LlmServiceTier.Standard, LlmServiceTier.Fast], capabilities.ServiceTiers.Order());
    }

    /// <summary>The model list is the proxy's GET models: each row's context_window is the model's window and its
    /// reasoning_efforts are its levels. The proxy serves no compaction route, so the host compacts (B4, B10).</summary>
    [Fact]
    public async Task ModelListReadsTheProxyCatalogAndTheHostCompacts()
    {
        using var handler = new Handler(_ => Json("""
            {"data":[{"id":"grok-4.7","name":"Grok 4.7","owned_by":"xAI","context_window":256000,
              "reasoning_efforts":[{"value":"xhigh","default":false},{"value":"high","default":true},{"value":"low","default":false}]}]}
            """));
        var client = new GrokResponsesLlmClient(new HttpClient(handler));

        LlmModel model = Assert.Single(await client.ListModelsAsync(Connection()));

        Assert.Equal("https://cli-chat-proxy.grok.com/v1/models", handler.Uri!.AbsoluteUri);
        Assert.Equal(("grok-4.7", "Grok 4.7", 256000L), (model.Id, model.DisplayName, model.MaxContextTokens));
        Assert.True(model.Capabilities!.ReasoningLevels.SetEquals([LlmReasoningLevel.ExtraHigh, LlmReasoningLevel.High, LlmReasoningLevel.Low]));
        Assert.Equal(LlmCompactionSide.Host, model.Capabilities.Agent.CompactionSide);
    }

    /// <summary>A Grok reply holding reasoning keeps it in its continuation state, and the next request replays it verbatim
    /// and in order.</summary>
    [Fact]
    public async Task ReplyWithReasoningRoundTrips()
    {
        const string output = """
            [{"type":"reasoning","id":"r","summary":[],"encrypted_content":"thought"},
             {"type":"message","id":"m","role":"assistant","status":"completed","content":[{"type":"output_text","text":"Found.","annotations":[]}]}]
            """;
        using var handler = new Handler(_ => DirectCodexTests.Sse("data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp\",\"status\":\"completed\",\"output\":"
            + output.ReplaceLineEndings("") + ",\"usage\":{\"input_tokens\":5,\"output_tokens\":2,\"total_tokens\":7}}}\n\n"));
        var client = new GrokResponsesLlmClient(new HttpClient(handler));
        LlmConnection connection = Connection();

        LlmChatResult reply = await Complete(client, connection, Request("grok-4.7"));
        await Complete(client, connection, Request("grok-4.7") with
        {
            Messages = [new("user", "hello"), new("assistant", reply.Text) { ContinuationState = reply.ContinuationState }, new("user", "more")]
        });

        JsonArray replayed = (JsonArray)JsonNode.Parse(handler.Bodies[1])!["input"]!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(output), new JsonArray([.. replayed.Skip(1).Take(2).Select(item => item!.DeepClone())])),
            replayed.ToJsonString());
    }

    internal static LlmConnection Connection(XaiConnectionOptions? options = null)
    {
        options ??= new();
        return new()
        {
            Version = "1",
            Transport = LlmTransport.XaiResponses,
            Endpoint = XaiRequestHeaders.SubscriptionEndpoint,
            Xai = options,
            Credential = new LlmCredential { OAuth = new() { AccessToken = "test-token", AccountId = "account" } }.Serialize()
        };
    }

    private static LlmChatRequest Request(string model) => new() { ModelId = model, Messages = [new("user", "hello")] };

    private static LlmProviderService Service(Source source, Handler handler) =>
        new(source, [new GrokResponsesLlmClient(new HttpClient(handler))], [new GrokAuthenticator(new HttpClient(handler))]);

    private static async Task<LlmChatResult> Complete(ResponsesLlmClientBase client, LlmConnection connection, LlmChatRequest request)
    {
        LlmChatResult? result = null;
        await foreach (LlmStreamEvent item in client.StreamAsync(connection, request))
            if (item is LlmChatCompletionEvent completion) result = completion.Result;
        return result!;
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Error(string code) => new(HttpStatusCode.BadRequest)
    { Content = new StringContent("{\"error\":\"" + code + "\"}", Encoding.UTF8, "application/json") };

    private sealed class Source(LlmConnection connection) : ILlmConnectionSource
    {
        public int Saves { get; private set; }
        public LlmCredential? Saved { get; private set; }
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<LlmConnection?>(connection);
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion,
            CancellationToken cancellationToken = default)
        {
            Saves++;
            Saved = LlmCredential.Parse(credentialJson);
            return Task.CompletedTask;
        }
    }

    private sealed class Target : ISetupCommitTarget<LlmSetupChanges>
    {
        public Task<SetupCommitResult> CommitAsync(LlmSetupChanges changes, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
