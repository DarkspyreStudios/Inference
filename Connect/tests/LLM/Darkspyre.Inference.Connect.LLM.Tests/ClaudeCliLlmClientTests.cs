using Darkspyre.Inference.Connect.LLM.Internal;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Providers;
using LlmReasoningLevel = Darkspyre.Inference.Common.LLM.LlmReasoningLevel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class ClaudeCliLlmClientTests
{
    [Fact]
    public async Task ChatArgumentsAreIsolated()
    {
        var runner = new RecordingRunner(new(0,
            """{"type":"result","subtype":"success","is_error":false,"result":"OK","stop_reason":"end_turn"}""",
            string.Empty));
        using var http = new HttpClient(new UnexpectedHttpHandler());
        var client = new ClaudeCliLlmClient(http, runner, RateLimits());

        await ReadAllAsync(client.StreamAsync(CliConnection(),
            new() { ModelId = "claude-test", Messages = [new("user", "test")] }));

        IReadOnlyList<string> arguments = Assert.Single(runner.Invocations).Arguments;
        int start = arguments.ToList().IndexOf(ClaudeCliConfiguration.IsolationArguments[0]);
        Assert.Equal(ClaudeCliConfiguration.IsolationArguments, arguments.Skip(start).Take(ClaudeCliConfiguration.IsolationArguments.Count));
    }

    [Fact]
    public async Task AccountUsageIsEveryWindowATurnReportedWithoutRunningTheCliAgain()
    {
        var runner = new RecordingRunner(new(0,
            """{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","resetsAt":1792633200,"rateLimitType":"five_hour","overageStatus":"rejected","overageDisabledReason":"org_level_disabled","isUsingOverage":false,"unifiedWindows":{"five_hour":{"utilization":0.13,"resetsAt":1792633200},"seven_day":{"utilization":0.34,"resetsAt":1793008800}}}}"""
            + "\n"
            + """{"type":"result","subtype":"success","is_error":false,"result":"OK","stop_reason":"end_turn"}""",
            string.Empty));
        using var http = CatalogHttp();
        var client = new ClaudeCliLlmClient(http, runner, RateLimits());
        await using var service = new LlmProviderService(new ResourcePreparationTests.Source(CliConnection()), [client]);

        LlmProviderException none = await Assert.ThrowsAsync<LlmProviderException>(() => service.GetAccountUsageAsync("connection"));
        Assert.Equal("account_usage_not_observed", none.ProviderCode);

        await ReadAllAsync(service.StreamAsync("connection",
            new() { ModelId = "cli-returned-alias", Messages = [new("user", "test")] }));
        LlmAccountUsageSnapshot usage = await service.GetAccountUsageAsync("connection");

        LlmQuotaWindow fiveHour = Assert.Single(usage.Quotas, quota => quota.Id == "five_hour");
        Assert.Equal(0.87, fiveHour.RemainingFraction!.Value, 3);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792633200), fiveHour.ResetsAt);
        Assert.Equal(LlmQuotaState.Available, fiveHour.State);
        LlmQuotaWindow sevenDay = Assert.Single(usage.Quotas, quota => quota.Id == "seven_day");
        Assert.Equal(0.66, sevenDay.RemainingFraction!.Value, 3);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1793008800), sevenDay.ResetsAt);
        Assert.Equal(LlmQuotaState.Available, sevenDay.State);
        Assert.Equal(LlmQuotaState.Exhausted, Assert.Single(usage.Quotas, quota => quota.Id == "overage").State);
        Assert.Single(runner.Invocations);
    }

    private sealed class RecordingLogger : ILogger<ClaudeRateLimitLog>
    {
        private readonly Lock _gate = new();
        private readonly List<(LogLevel, string)> _entries = [];

        internal IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get { lock (_gate) return [.. _entries]; }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_gate) _entries.Add((logLevel, formatter(state, exception)));
        }
    }

    private static ClaudeRateLimitLog RateLimits() => new(NullLogger<ClaudeRateLimitLog>.Instance);

    [Fact]
    public async Task ClaudeSignInRequestsInferenceScopeAndOneYearLifetime()
    {
        JsonNode? exchange = null;
        using var http = new HttpClient(new UnexpectedHttpHandler(request =>
        {
            exchange = JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"access_token":"account-token","expires_in":31536000,"scope":"user:inference"}""")
            };
        }));
        var authenticator = new ClaudeAuthenticator(http);

        LlmAuthorizationSession session = authenticator.Start();
        LlmCredential credential = await authenticator.CompleteAsync(session, "code", session.State);

        Assert.Contains("scope=user%3Ainference&", session.AuthorizeUrl, StringComparison.Ordinal);
        Assert.Equal(31536000, exchange!["expires_in"]!.GetValue<int>());
        Assert.Equal("account-token", credential.OAuth!.AccessToken);
    }

    private static LlmConnection CliConnection() => new()
    {
        Version = "v1",
        Transport = LlmTransport.AnthropicClaudeCli,
        Endpoint = "claude",
        Credential = new LlmCredential { OAuth = new() { AccessToken = "account-token" } }.Serialize()
    };

    // Anthropic's model listing, as a signed-in connection discovers its models.
    private static HttpClient CatalogHttp() => new(new UnexpectedHttpHandler(_ => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""{"data":[{"id":"cli-returned-alias","display_name":"CLI model choice"}],"has_more":false}""")
    }));

    private static async Task<IReadOnlyList<T>> ReadAllAsync<T>(IAsyncEnumerable<T> source)
    {
        var results = new List<T>();
        await foreach (T item in source)
        {
            results.Add(item);
        }
        return results;
    }

    private sealed class RecordingRunner(ClaudeCliProcessResult result) : IClaudeCliRunner
    {
        public List<ClaudeCliInvocation> Invocations { get; } = [];
        public Action<ClaudeCliInvocation>? OnInvocation { get; init; }

        public Task<ClaudeCliProcessResult> RunAsync(
            ClaudeCliInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            Invocations.Add(invocation);
            OnInvocation?.Invoke(invocation);
            return Task.FromResult(result);
        }
    }

    private sealed class UnexpectedHttpHandler(Func<HttpRequestMessage, HttpResponseMessage>? respond = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return respond is null ? throw new InvalidOperationException("OAuth prompt unexpectedly used HTTP.")
                : Task.FromResult(respond(request));
        }
    }
}
