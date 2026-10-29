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
        var client = new ClaudeCliLlmClient(http, runner, new NoSessionProcesses(), RateLimits());

        await ReadAllAsync(client.StreamAsync(CliConnection(),
            new() { ModelId = "claude-test", Messages = [new("user", "test")] }));

        IReadOnlyList<string> arguments = Assert.Single(runner.Invocations).Arguments;
        int start = arguments.ToList().IndexOf(ClaudeCliConfiguration.IsolationArguments[0]);
        Assert.Equal(ClaudeCliConfiguration.IsolationArguments, arguments.Skip(start).Take(ClaudeCliConfiguration.IsolationArguments.Count));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CatalogEffortFactsControlAliasCapabilitiesThroughTheFacade(bool supportsEffort)
    {
        var process = new CatalogProcess
        {
            ModelInfo = JsonElement.Parse(supportsEffort
                ? """{"value":"cli-returned-alias","displayName":"CLI model choice","supportsEffort":true,"supportedEffortLevels":["low"]}"""
                : """{"value":"cli-returned-alias","displayName":"CLI model choice","supportsEffort":false}"""
            )
        };
        using var http = new HttpClient(new UnexpectedHttpHandler());
        var client = new ClaudeCliLlmClient(http, new RecordingRunner(new(0, string.Empty, string.Empty)), process, RateLimits());
        LlmConnection connection = CliConnection() with
        { CapabilityOverrides = new() { ReasoningLevels = ClaudeCliLlmClient.ReasoningLevels } };
        await using var service = new LlmProviderService(new ResourcePreparationTests.Source(connection), [client]);

        LlmProviderCapabilities capabilities = await service.GetModelCapabilitiesAsync("connection", "cli-returned-alias");

        Assert.True(capabilities.ReasoningLevels.SetEquals(supportsEffort ? [LlmReasoningLevel.Low] : []));
    }

    [Fact]
    public async Task ExplicitClaude46ProfilesMapExtraHighDownBeforeInvocation()
    {
        const string model = "claude-opus-4-6";
        var runner = new RecordingRunner(new(0,
            """{"type":"result","subtype":"success","is_error":false,"result":"OK","stop_reason":"end_turn"}""",
            string.Empty));
        using var http = new HttpClient(new UnexpectedHttpHandler());
        var client = new ClaudeCliLlmClient(http, runner, new NoSessionProcesses(), RateLimits());
        LlmConnection connection = CliConnection() with
        {
            ModelCapabilityOverrides = new Dictionary<string, LlmCapabilityOverrides>
            {
                [model] = new() { ReasoningLevels = ClaudeCliLlmClient.ReasoningLevels }
            }
        };
        IReadOnlySet<LlmReasoningLevel> offered = client.GetCapabilities(connection, model).ReasoningLevels;
        Assert.True(offered.SetEquals([LlmReasoningLevel.Low, LlmReasoningLevel.Medium,
            LlmReasoningLevel.High, LlmReasoningLevel.Maximum]));

        await ReadAllAsync(client.StreamAsync(connection, new LlmChatRequest
        {
            ModelId = model,
            Messages = [new("user", "Reply with OK.")],
            ReasoningLevel = LlmReasoningLevel.ExtraHigh
        }));

        IReadOnlyList<string> mapped = Assert.Single(runner.Invocations).Arguments;
        Assert.Equal("high", mapped[mapped.ToList().IndexOf("--effort") + 1]);
    }

    [Fact]
    public async Task BelowMinimumEffortUsesTheLowestCliSetting()
    {
        var runner = new RecordingRunner(new(0,
            """{"type":"result","subtype":"success","is_error":false,"result":"OK","stop_reason":"end_turn"}""", string.Empty));
        using var http = new HttpClient(new UnexpectedHttpHandler());
        var client = new ClaudeCliLlmClient(http, runner, new NoSessionProcesses(), RateLimits());

        await ReadAllAsync(client.StreamAsync(CliConnection(), new LlmChatRequest
        { ModelId = "unrecognized-model", Messages = [new("user", "test")], ReasoningLevel = LlmReasoningLevel.None }));

        IReadOnlyList<string> arguments = Assert.Single(runner.Invocations).Arguments;
        Assert.Equal("low", arguments[arguments.ToList().IndexOf("--effort") + 1]);
    }

    [Fact]
    public async Task FailedTerminalPreservesSanitizedErrors()
    {
        const string detail = """{"access_token":"diagnostic-secret","detail":"The selected model is unavailable."}""";
        string output = new JsonObject
        {
            ["type"] = "result",
            ["subtype"] = "error_during_execution",
            ["is_error"] = true,
            ["errors"] = new JsonArray(JsonValue.Create(detail), JsonValue.Create("Quota exceeded. catalog-secret"))
        }.ToJsonString();
        var runner = new RecordingRunner(new(0, output, string.Empty));
        using var http = new HttpClient(new UnexpectedHttpHandler());
        var client = new ClaudeCliLlmClient(http, runner, new NoSessionProcesses(), RateLimits());
        LlmConnection connection = CliConnection() with
        {
            Credential = new LlmCredential { OAuth = new() { AccessToken = "catalog-secret" } }.Serialize()
        };

        LlmProviderException failure = await Assert.ThrowsAsync<LlmProviderException>(() => ReadAllAsync(
            client.StreamAsync(connection, new() { ModelId = "claude-test", Messages = [new("user", "test")] })));

        Assert.Equal("claude_cli_error", failure.ProviderCode);
        Assert.Contains("The selected model is unavailable.", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Quota exceeded.", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("diagnostic-secret", failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("catalog-secret", failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedExitShowsTheCliErrorWithoutGuessedSignInGuidance()
    {
        string output = new JsonObject
        {
            ["type"] = "result",
            ["subtype"] = "error_during_execution",
            ["is_error"] = true,
            ["apiKeySource"] = "oauth credential",
            ["errors"] = new JsonArray(JsonValue.Create("You've hit your usage limit"))
        }.ToJsonString();
        var runner = new RecordingRunner(new(1, output, "Request rejected (429) for auth catalog-secret"));
        using var http = new HttpClient(new UnexpectedHttpHandler());
        var client = new ClaudeCliLlmClient(http, runner, new NoSessionProcesses(), RateLimits());
        LlmConnection connection = CliConnection() with
        {
            Credential = new LlmCredential { OAuth = new() { AccessToken = "catalog-secret" } }.Serialize()
        };

        LlmProviderException failure = await Assert.ThrowsAsync<LlmProviderException>(() => ReadAllAsync(
            client.StreamAsync(connection, new() { ModelId = "claude-test", Messages = [new("user", "test")] })));

        Assert.StartsWith("Claude CLI exited with code 1. CLI detail: You've hit your usage limit", failure.Message, StringComparison.Ordinal);
        Assert.Contains("CLI error output: Request rejected (429)", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sign-in", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("catalog-secret", failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AccountUsageIsEveryWindowATurnReportedWithoutRunningTheCliAgain()
    {
        var runner = new RecordingRunner(new(0,
            """{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","resetsAt":1792633200,"rateLimitType":"five_hour","overageStatus":"rejected","overageDisabledReason":"org_level_disabled","isUsingOverage":false,"unifiedWindows":{"five_hour":{"utilization":0.13,"resetsAt":1792633200},"seven_day":{"utilization":0.34,"resetsAt":1793008800}}}}"""
            + "\n"
            + """{"type":"result","subtype":"success","is_error":false,"result":"OK","stop_reason":"end_turn"}""",
            string.Empty));
        using var http = new HttpClient(new UnexpectedHttpHandler());
        var client = new ClaudeCliLlmClient(http, runner, new CatalogProcess(), RateLimits());
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

    [Fact]
    public async Task ARateLimitEventIsLoggedOnceAndARepeatIsNot()
    {
        const string info = """{"status":"allowed","rateLimitType":"five_hour","resetsAt":1900000000,"isUsingOverage":false}""";
        var runner = new RecordingRunner(new(0,
            $$"""{"type":"rate_limit_event","rate_limit_info":{{info}},"session_id":"private-session"}"""
            + "\n"
            + """{"type":"result","subtype":"success","is_error":false,"result":"OK","stop_reason":"end_turn"}""",
            string.Empty));
        using var http = new HttpClient(new UnexpectedHttpHandler());
        var logger = new RecordingLogger();
        var client = new ClaudeCliLlmClient(http, runner, new CatalogProcess(), new ClaudeRateLimitLog(logger));
        await using var service = new LlmProviderService(new ResourcePreparationTests.Source(CliConnection()), [client]);

        for (int turn = 0; turn < 2; turn++)
        {
            await ReadAllAsync(service.StreamAsync("connection",
                new() { ModelId = "cli-returned-alias", Messages = [new("user", "test")] }));
        }

        (LogLevel level, string message) = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, level);
        Assert.Equal($"Claude rate_limit_event on connection (five_hour): fields status, rateLimitType, resetsAt, isUsingOverage, "
            + $"{info.Length} characters", message);
    }

    /// <summary>
    /// The object's shape belongs to the CLI, so the log holds its field names and its length and no field's value. A
    /// field the CLI gains later could name an account or an organization (DSA-844).
    /// </summary>
    [Fact]
    public async Task ARateLimitEventLogsItsFieldNamesAndLengthAndNoValue()
    {
        const string info = """{"status":"allowed","rateLimitType":"five_hour","resetsAt":1900000000,"organizationName":"harbor lighthouse"}""";
        var runner = new RecordingRunner(new(0,
            $$"""{"type":"rate_limit_event","rate_limit_info":{{info}},"session_id":"private-session"}"""
            + "\n"
            + """{"type":"result","subtype":"success","is_error":false,"result":"OK","stop_reason":"end_turn"}""",
            string.Empty));
        using var http = new HttpClient(new UnexpectedHttpHandler());
        var logger = new RecordingLogger();
        var client = new ClaudeCliLlmClient(http, runner, new CatalogProcess(), new ClaudeRateLimitLog(logger));
        await using var service = new LlmProviderService(new ResourcePreparationTests.Source(CliConnection()), [client]);

        await ReadAllAsync(service.StreamAsync("connection",
            new() { ModelId = "cli-returned-alias", Messages = [new("user", "test")] }));

        (LogLevel level, string message) = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, level);
        Assert.Equal($"Claude rate_limit_event on connection (five_hour): fields status, rateLimitType, resetsAt, organizationName, "
            + $"{info.Length} characters", message);
        Assert.DoesNotContain("harbor lighthouse", message, StringComparison.Ordinal);
        Assert.DoesNotContain("1900000000", message, StringComparison.Ordinal);
        Assert.DoesNotContain("allowed", message, StringComparison.Ordinal);
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
    public async Task CatalogOAuthProtocolFailureRedactsTheActualHttpHeadersWithoutLaunchingTheCli()
    {
        using var handler = new UnexpectedHttpHandler(request =>
        {
            Assert.Equal(new Uri("https://api.anthropic.com/v1/models?limit=1000"), request.RequestUri);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("not-json test-token header-value") };
            response.Headers.Add("x-request-id", "request-test-token-header-value-1");
            return response;
        });
        using var http = new HttpClient(handler);
        http.DefaultRequestHeaders.Add("X-Trace", "header-value");
        var runner = new RecordingRunner(new(0, string.Empty, string.Empty));
        var client = new ClaudeCliLlmClient(http, runner, new NoSessionProcesses(), RateLimits());
        LlmConnection connection = CliConnection() with
        { Credential = new LlmCredential { OAuth = new() { AccessToken = "test-token" } }.Serialize() };

        LlmProviderException failure = await Assert.ThrowsAsync<LlmProviderException>(() => client.ListModelsAsync(connection));

        Assert.Equal("Anthropic model discovery returned invalid JSON.", failure.Message);
        foreach (string secret in new[] { "test-token", "header-value" })
            Assert.DoesNotContain(secret, failure.ToString() + failure.SanitizedResponseBody + failure.RequestId, StringComparison.Ordinal);
        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public async Task ModelDiscoveryWithoutCatalogOAuthPreservesCliChoicesAndRetiresItsProcess()
    {
        var process = new CatalogProcess();
        var runner = new RecordingRunner(new(0, string.Empty, string.Empty));
        using var http = new HttpClient(new UnexpectedHttpHandler());
        var client = new ClaudeCliLlmClient(http, runner, process, RateLimits());

        IReadOnlyList<LlmModel> models = await client.ListModelsAsync(CliConnection());

        LlmModel model = Assert.Single(models);
        Assert.Equal("cli-returned-alias", model.Id);
        Assert.Equal("CLI model choice", model.DisplayName);
        Assert.Equal("preserved", model.CatalogEntry!.Value.GetProperty("unmappedOption").GetString());
        Assert.Empty(runner.Invocations);
        Assert.True(process.Disposed);
        Assert.False(Directory.Exists(process.Invocation!.WorkingDirectory));
    }

    [Fact]
    public async Task AccountTokenReachesBothChildEnvironmentsAndRemovesConflictingVariables()
    {
        var runner = new RecordingRunner(new(0,
            """{"type":"result","subtype":"success","is_error":false,"result":"OK","stop_reason":"end_turn"}""",
            string.Empty));
        using var http = new HttpClient(new UnexpectedHttpHandler());
        var client = new ClaudeCliLlmClient(http, runner, new NoSessionProcesses(), RateLimits());
        LlmConnection connection = CliConnection() with
        {
            Credential = new LlmCredential { OAuth = new() { AccessToken = "account-token" } }.Serialize()
        };

        await ReadAllAsync(client.StreamAsync(connection, new() { ModelId = "claude-test", Messages = [new("user", "test")] }));

        ClaudeCliInvocation invocation = Assert.Single(runner.Invocations);
        Assert.Equal("account-token", ClaudeCliProcessRunner.CreateStartInfo(invocation).Environment[ClaudeCliEnvironment.OAuthTokenVariable]);
        Assert.Equal("account-token", ClaudeCliSessionProcessFactory.CreateStartInfo(
            new("claude", [], Path.GetTempPath()) { AccountToken = invocation.AccountToken }).Environment[ClaudeCliEnvironment.OAuthTokenVariable]);
        var inherited = new System.Diagnostics.ProcessStartInfo();
        inherited.Environment["ANTHROPIC_API_KEY"] = "key";
        inherited.Environment["ANTHROPIC_AUTH_TOKEN"] = "auth";
        ClaudeCliEnvironment.Apply(inherited, "account-token");
        Assert.False(inherited.Environment.ContainsKey("ANTHROPIC_API_KEY"));
        Assert.False(inherited.Environment.ContainsKey("ANTHROPIC_AUTH_TOKEN"));
    }

    [Fact]
    public async Task AConnectionWithoutASignInLeavesTheChildEnvironmentAsInherited()
    {
        var runner = new RecordingRunner(new(0,
            """{"type":"result","subtype":"success","is_error":false,"result":"OK","stop_reason":"end_turn"}""",
            string.Empty));
        using var http = new HttpClient(new UnexpectedHttpHandler());
        var client = new ClaudeCliLlmClient(http, runner, new NoSessionProcesses(), RateLimits());

        await ReadAllAsync(client.StreamAsync(CliConnection(), new() { ModelId = "claude-test", Messages = [new("user", "test")] }));

        ClaudeCliInvocation invocation = Assert.Single(runner.Invocations);
        Assert.Null(invocation.AccountToken);
        var inherited = new System.Diagnostics.ProcessStartInfo();
        inherited.Environment["ANTHROPIC_API_KEY"] = "key";
        ClaudeCliEnvironment.Apply(inherited, invocation.AccountToken);
        Assert.Equal("key", inherited.Environment["ANTHROPIC_API_KEY"]);
    }

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

        LlmAuthorizationSession session = authenticator.Start(LlmProviderService.ClaudeManualRedirectUri);
        LlmCredential credential = await authenticator.CompleteAsync(session, "code", session.State);

        Assert.Contains("scope=user%3Ainference&", session.AuthorizeUrl, StringComparison.Ordinal);
        Assert.Equal(31536000, exchange!["expires_in"]!.GetValue<int>());
        Assert.Equal("account-token", credential.OAuth!.AccessToken);
    }

    /// <summary>Only a first-party sign-in counts as signed in; a CLI that cannot start is not installed.</summary>
    [Theory]
    [InlineData("""{"loggedIn":true,"apiProvider":"bedrock"}""", "SignedOut")]
    [InlineData("""{"loggedIn":true,"apiProvider":"firstParty"}""", "SignedIn")]
    public async Task OnlyAFirstPartySignInIsSignedIn(string output, string expected)
    {
        using var http = new HttpClient(new UnexpectedHttpHandler());
        var client = new ClaudeCliLlmClient(http, new RecordingRunner(new(0, output, string.Empty)), new NoSessionProcesses(), RateLimits());

        Assert.Equal(Enum.Parse<ExternalAuthenticationState>(expected), await client.CheckAuthenticationAsync(CliConnection()));
    }

    [Fact]
    public async Task ACliThatCannotStartIsNotInstalled()
    {
        using var http = new HttpClient(new UnexpectedHttpHandler());
        var runner = new RecordingRunner(new(0, string.Empty, string.Empty))
        {
            OnInvocation = invocation => throw new ClaudeCliUnavailableException(invocation.Executable, new FileNotFoundException())
        };
        var client = new ClaudeCliLlmClient(http, runner, new NoSessionProcesses(), RateLimits());

        Assert.Equal(ExternalAuthenticationState.NotInstalled, await client.CheckAuthenticationAsync(CliConnection()));
    }

    private static LlmConnection CliConnection() => new()
    {
        Version = "v1",
        Transport = LlmTransport.AnthropicClaudeCli,
        Endpoint = "claude",
        Credential = null
    };

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

    private sealed class NoSessionProcesses : IClaudeCliSessionProcessFactory
    {
        public Task<IClaudeCliSessionProcess> StartAsync(
            ClaudeCliSessionInvocation invocation, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class CatalogProcess : IClaudeCliSessionProcessFactory, IClaudeCliSessionProcess
    {
        private string? _reply;
        internal JsonElement ModelInfo { get; init; } = JsonElement.Parse(
            """{"value":"cli-returned-alias","displayName":"CLI model choice","unmappedOption":"preserved"}""");
        public ClaudeCliSessionInvocation? Invocation { get; private set; }
        public bool Disposed { get; private set; }

        public Task<IClaudeCliSessionProcess> StartAsync(ClaudeCliSessionInvocation invocation, CancellationToken cancellationToken = default)
        {
            Invocation = invocation;
            return Task.FromResult<IClaudeCliSessionProcess>(this);
        }

        public ValueTask WriteLineAsync(string line, CancellationToken cancellationToken = default)
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement request = document.RootElement;
            Assert.Equal("control_request", request.GetProperty("type").GetString());
            Assert.Equal("initialize", request.GetProperty("request").GetProperty("subtype").GetString());
            _reply = new JsonObject
            {
                ["type"] = "control_response",
                ["response"] = new JsonObject
                {
                    ["subtype"] = "success",
                    ["request_id"] = request.GetProperty("request_id").GetString(),
                    ["response"] = new JsonObject
                    {
                        ["models"] = new JsonArray(JsonNode.Parse(ModelInfo.GetRawText()))
                    }
                }
            }.ToJsonString();
            return ValueTask.CompletedTask;
        }

        public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken = default)
        {
            string? reply = _reply;
            _reply = null;
            return ValueTask.FromResult(reply);
        }

        public Task<ClaudeCliProcessResult> WaitForExitAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Model discovery must consume the initialize reply before process retirement.");

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
