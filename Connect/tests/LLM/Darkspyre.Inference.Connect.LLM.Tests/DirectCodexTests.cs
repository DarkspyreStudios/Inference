using System.Net;
using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;
using LlmReasoningLevel = Darkspyre.Inference.Common.LLM.LlmReasoningLevel;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class DirectCodexTests
{
    private const string Completed = """
        data: {"type":"response.output_text.delta","delta":"Hello"}

        data: {"type":"response.completed","response":{"id":"response","status":"completed","output":[{"type":"message","id":"msg","role":"assistant","phase":"final_answer","content":[{"type":"output_text","text":"Hello"}]}],"usage":{"input_tokens":5,"output_tokens":2,"total_tokens":7}}}

        """;

    [Fact]
    public async Task ReportedTokensTravelWithTheStateAndCountTheEncryptedHistory()
    {
        using var handler = new Handler(request => request.Method == HttpMethod.Get
            ? Json("{\"models\":[{\"slug\":\"model\",\"context_window\":100000}]}")
            : Sse(ReasonedToolCall.Replace("\"id\":\"r\"}", "\"id\":\"r\",\"usage\":{\"input_tokens\":1200,\"output_tokens\":80,\"total_tokens\":1280}}",
                StringComparison.Ordinal)));
        var client = new OpenAiCodexLlmClient(new HttpClient(handler));
        LlmChatResult first = await Complete(client, Connection(), Request());
        Assert.Equal((1200L, 80L), (first.ContinuationState!.InputTokens, first.ContinuationState.OutputTokens));
        LlmChatRequest followup = Request() with
        {
            Messages = [new("user", "first"),
                new("assistant", first.Text) { ToolCalls = first.ToolCalls, ContinuationState = first.ContinuationState },
                new("user", "") { ToolResults = [new LlmToolResult("call", LlmToolOutput.FromText("answer"))] }]
        };

        ContextEstimate estimate = client.EstimateInputTokens(Connection(), followup)!.Value;
        LlmRequestAccounting accounting = await new LlmProviderService(new Source(Connection()), [client], [new RefreshAuthenticator()])
            .MeasureRequestAsync("codex", followup);

        Assert.True(estimate.IncludesProviderReport);
        Assert.InRange(estimate.Tokens, 1281, 1400); // The report, plus the tokenized tool result after it.
        Assert.Equal(estimate.Tokens, accounting.UsedContextTokens);
        Assert.Equal(LlmContextMeasurementSource.Mixed, accounting.Source);
    }

    private const string ReasonedToolCall = """
        data: {"type":"response.output_item.done","output_index":1,"item":{"type":"function_call","id":"fc","call_id":"call","name":"echo","arguments":"{}"}}

        data: {"type":"response.output_item.done","output_index":0,"item":{"type":"reasoning","id":"reason","encrypted_content":"opaque","summary":[{"type":"summary_text","text":"Thinking"}]}}

        data: {"type":"response.completed","response":{"status":"completed","output":[],"id":"r"}}

        """;

    [Fact]
    public async Task OmitCarriesAnotherModelsToolHistoryWithoutItsStateAndReportsTheCount()
    {
        (Handler handler, LlmProviderService providers, LlmChatRequest followup) = await OtherModelFollowupAsync();
        using (handler)
        {
            LlmChatResult result = await providers.CompleteAsync("codex", followup);

            using var sent = JsonDocument.Parse(handler.Body!);
            string?[] types = [.. sent.RootElement.GetProperty("input").EnumerateArray()
                .Select(item => item.TryGetProperty("type", out JsonElement type) ? type.GetString() : "message")];
            Assert.DoesNotContain("reasoning", types);
            Assert.Contains("function_call", types);
            Assert.Contains("function_call_output", types);
            Assert.DoesNotContain("opaque", handler.Body!, StringComparison.Ordinal);
            Assert.Equal(1, result.OmittedContinuationStateCount);
            Assert.NotNull(followup.Messages[1].ContinuationState);
        }
    }

    /// <summary>A model reply with reasoning state and a tool call, then a follow-up history for another model.</summary>
    private static async Task<(Handler Handler, LlmProviderService Providers, LlmChatRequest Followup)> OtherModelFollowupAsync()
    {
        var handler = new Handler(request => request.Method == HttpMethod.Get
            ? Json("{\"models\":[{\"slug\":\"model\",\"context_window\":100000},{\"slug\":\"other\",\"context_window\":100000}]}")
            : Sse(ReasonedToolCall));
        var client = new OpenAiCodexLlmClient(new HttpClient(handler));
        var providers = new LlmProviderService(new Source(Connection()), [client], [new RefreshAuthenticator()]);
        LlmChatResult first = await providers.CompleteAsync("codex", Request());
        handler.Response = request => request.Method == HttpMethod.Get
            ? Json("{\"models\":[{\"slug\":\"other\",\"context_window\":100000}]}")
            : Sse(Completed);
        LlmChatRequest followup = Request() with
        {
            ModelId = "other",
            UnboundContinuationState = LlmUnboundContinuationState.Omit,
            Messages = [new("user", "first"),
                new("assistant", first.Text) { ToolCalls = first.ToolCalls, ContinuationState = first.ContinuationState },
                new("user", "") { ToolResults = [new LlmToolResult("call", LlmToolOutput.FromText("answer"))] }]
        };
        return (handler, providers, followup);
    }

    private sealed class Source(LlmConnection connection) : ILlmConnectionSource
    {
        public LlmConnection Connection { get; set; } = connection;
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<LlmConnection?>(Connection);
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default)
        {
            Assert.Equal(Connection.Version, expectedVersion);
            Connection = Connection with { Credential = credentialJson, Version = Guid.NewGuid().ToString() };
            return Task.CompletedTask;
        }
    }

    private sealed class RefreshAuthenticator : ILlmAuthenticator
    {
        public LlmTransport Transport => LlmTransport.OpenAiCodex;
        public int Calls { get; private set; }
        public Task<LlmCredential> EnsureFreshAsync(LlmCredential credential, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(credential with { OAuth = credential.OAuth! with { AccessToken = "fresh-" + Calls.ToString(System.Globalization.CultureInfo.InvariantCulture) } });
        }
    }

    internal static LlmConnection Connection() => new()
    {
        Version = "1",
        Transport = LlmTransport.OpenAiCodex,
        Endpoint = "https://provider.test/backend-api/codex",
        Credential = Credential("account")
    };
    private static string Credential(string account) => new LlmCredential
    { OAuth = new() { AccessToken = "token", AccountId = account } }.Serialize();
    private static LlmChatRequest Request() => new() { ModelId = "model", Messages = [new("user", "hello")] };
    private static HttpResponseMessage Sse(string text) => new(HttpStatusCode.OK)
    { Content = new StringContent(text, Encoding.UTF8, "text/event-stream") };
    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK)
    { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private static async Task<LlmChatResult> Complete(OpenAiCodexLlmClient client, LlmConnection connection, LlmChatRequest request) =>
        Assert.Single((await Read(client.StreamAsync(connection, request))).OfType<LlmChatCompletionEvent>()).Result;
    private static async Task<List<LlmStreamEvent>> Read(IAsyncEnumerable<LlmStreamEvent> source)
    {
        var result = new List<LlmStreamEvent>();
        await foreach (var item in source) { result.Add(item); }
        return result;
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Response { get; set; } = response;
        public string? Body { get; private set; }
        public Uri? Uri { get; private set; }
        public HttpMethod? Method { get; private set; }
        public Dictionary<string, string> Headers { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
        public int Calls { get; private set; }
        public List<string> AuthorizationHistory { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Uri = request.RequestUri;
            Method = request.Method;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Headers = request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase);
            if (Headers.TryGetValue("Authorization", out var authorization)) { AuthorizationHistory.Add(authorization); }
            return Response(request);
        }
    }
}
