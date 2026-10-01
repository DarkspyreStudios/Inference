using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading.Channels;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class AccountUsageObservationTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.FromUnixTimeSeconds(1700000000);
    // CamelCase CLI wire follows claude-agent-sdk-python 0.2.163 message_parser.py; types.py
    // documents utilization as a fraction and resetsAt as Unix seconds, not milliseconds.
    private const string ClaudeRateLine = """
        {"type":"rate_limit_event","rate_limit_info":{"status":"allowed_warning","rateLimitType":"five_hour","utilization":0.75,"resetsAt":1700003600},"uuid":"quota","session_id":"session"}
        """;
    private const string ClaudeResultLine = """
        {"type":"result","subtype":"success","is_error":false,"result":"done","usage":{"input_tokens":2,"output_tokens":1}}
        """;

    [Theory]
    [InlineData("6m0s", 360)]
    [InlineData("1s", 1)]
    [InlineData("20ms", 0.02)]
    [InlineData("1h2m3.5s", 3723.5)]
    public void OpenAiHeadersPreserveCountsAndDurationUnits(string reset, double seconds)
    {
        using var response = Response("", ("x-ratelimit-limit-requests", "10"), ("x-ratelimit-remaining-requests", "0"),
            ("x-ratelimit-reset-requests", reset), ("x-ratelimit-limit-project-tokens", "100"),
            ("x-ratelimit-remaining-project-tokens", "25"));
        LlmAccountUsageSnapshot snapshot = OpenAiRateLimitHeaders.Parse(response.Headers, At)!;
        var quota = snapshot.Quotas[0];
        Assert.Equal(At + TimeSpan.FromSeconds(seconds), quota.ResetsAt);
        Assert.Equal(10m, quota.Used);
        Assert.Equal(LlmQuotaState.Exhausted, quota.State);
        Assert.Null(quota.WindowDuration);
        Assert.Equal(reset, quota.Observation!.RawReset);
        Assert.Equal("duration", quota.Observation.ResetUnit);
        Assert.Equal(0.75, snapshot.Quotas[1].UsedFraction);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("1s-junk")]
    [InlineData("99999999999999999999999999999999999999999h")]
    [InlineData("-1s")]
    public void MalformedDurationAndCountsStayUnknownAndDoNotFailGeneration(string reset)
    {
        using var response = Response("", ("x-ratelimit-limit-tokens", "10"), ("x-ratelimit-remaining-tokens", "invalid"),
            ("x-ratelimit-reset-tokens", reset));
        var quota = Assert.Single(OpenAiRateLimitHeaders.Parse(response.Headers, At)!.Quotas);
        Assert.Null(quota.Remaining); Assert.Null(quota.UsedFraction); Assert.Null(quota.ResetsAt);
        Assert.Equal(LlmQuotaState.Unknown, quota.State);
        Assert.Null(AccountUsageValues.Duration(reset));
    }

    [Fact]
    public void AnthropicHeadersKeepPriorityFamiliesAndRoundedTokenProvenance()
    {
        using var response = Response("", ("anthropic-ratelimit-requests-limit", "10"), ("anthropic-ratelimit-requests-remaining", "5"),
            ("anthropic-ratelimit-requests-reset", "2023-11-14T23:13:20Z"),
            ("anthropic-ratelimit-input-tokens-remaining", "1000"), ("anthropic-priority-output-tokens-limit", "2000"));
        var quotas = AnthropicRateLimitHeaders.Parse(response.Headers, At)!.Quotas;
        Assert.Equal(3, quotas.Count);
        Assert.False(quotas[0].IsApproximate); Assert.True(quotas[1].IsApproximate);
        Assert.Equal("priority-output-tokens", quotas[2].Id);
        Assert.Null(quotas[2].Remaining); Assert.Null(quotas[2].UsedFraction);
        Assert.Equal(At.AddHours(1), quotas[0].ResetsAt);
    }

    [Theory]
    [InlineData("2023-11-14", false)]
    [InlineData("2023-11-14T23:13:20", false)]
    [InlineData("11/14/2023 23:13:20Z", false)]
    [InlineData("2023-02-30T23:13:20Z", false)]
    [InlineData("2023-11-14T23:13:20.125Z", true)]
    [InlineData("2023-11-14T23:13:20+01:30", true)]
    [InlineData("2023-11-14t23:13:20z", true)]
    public void AnthropicResetsRequireFullRfc3339InsteadOfInventingLocalTime(string reset, bool valid)
    {
        using var response = Response("", ("anthropic-ratelimit-requests-limit", "10"), ("anthropic-ratelimit-requests-reset", reset));
        var quota = Assert.Single(AnthropicRateLimitHeaders.Parse(response.Headers, At)!.Quotas);
        Assert.Equal(valid, quota.ResetsAt.HasValue);
        Assert.Equal(valid ? reset : null, quota.Observation!.RawReset);
        Assert.Equal(valid ? "rfc3339" : null, quota.Observation.ResetUnit);
        if (valid) Assert.Equal(DateTimeOffset.Parse(reset, System.Globalization.CultureInfo.InvariantCulture), quota.ResetsAt);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("invalid", null)]
    [InlineData(null, null)]
    public void CodexPreservesNullableCreditFlagsAndMeterIdentifiers(string? unlimited, bool? expected)
    {
        using var response = Response("", ("x-codex-primary-used-percent", "12.5"),
            ("x-codex-primary-window-minutes", "60"), ("x-codex-primary-reset-at", "1700003600"),
            ("x-codex-secondary-primary-used-percent", "80"), ("x-codex-credits-balance", "2.5"));
        if (unlimited is not null) response.Headers.TryAddWithoutValidation("x-codex-credits-unlimited", unlimited);
        var snapshot = CodexRateLimitHeaders.Parse(response.Headers, At)!;
        Assert.Equal("codex:primary", snapshot.Quotas[0].Id);
        Assert.Equal("codex_secondary:primary", snapshot.Quotas[1].Id);
        Assert.Equal(0.125, snapshot.Quotas[0].UsedFraction);
        Assert.Equal(TimeSpan.FromHours(1), snapshot.Quotas[0].WindowDuration);
        Assert.Equal("percent", snapshot.Quotas[0].Observation!.UtilizationUnit);
        Assert.Equal(12.5m, snapshot.Quotas[0].Observation!.RawUtilization);
        Assert.Equal(expected, Assert.Single(snapshot.Credits).IsUnlimited);
        Assert.Null(snapshot.Credits[0].HasCredits);
    }

    [Theory]
    [InlineData("rate_limit_reached", false)]
    [InlineData("workspace_owner_usage_limit_reached", false)]
    [InlineData("workspace_member_usage_limit_reached", false)]
    [InlineData("workspace_owner_credits_depleted", true)]
    [InlineData("workspace_member_credits_depleted", true)]
    public void CodexReachedSignalsAffectOnlyTheReportedKind(string reached, bool credits)
    {
        using var response = Response("", ("x-codex-primary-window-minutes", "60"), ("x-codex-rate-limit-reached-type", reached));
        var snapshot = CodexRateLimitHeaders.Parse(response.Headers, At)!;
        Assert.Equal(credits ? LlmQuotaState.Unknown : LlmQuotaState.Exhausted, snapshot.Quotas[0].State);
        if (credits) { Assert.False(Assert.Single(snapshot.Credits).HasCredits); }
        else { Assert.Empty(snapshot.Credits); }
    }

    [Theory]
    [InlineData("five_hour")]
    [InlineData("seven_day")]
    [InlineData("seven_day_opus")]
    [InlineData("seven_day_sonnet")]
    [InlineData("seven_day_overage_included")]
    [InlineData("overage")]
    public void ClaudeSdkSchemaPreservesFractionAndUnixSecondsForEachBucket(string type)
    {
        JsonElement info = JsonElement.Parse("{\"status\":\"rejected\",\"rateLimitType\":\"" + type
            + "\",\"utilization\":0.75,\"resetsAt\":1700003600}");
        var quota = Assert.Single(ClaudeRateLimitEvent.Parse(info, At)!.Quotas);
        Assert.Equal(type, quota.Id); Assert.Equal(0.75, quota.UsedFraction);
        Assert.Equal(At.AddHours(1), quota.ResetsAt); Assert.Equal(LlmQuotaState.Exhausted, quota.State);
        Assert.Equal("fraction", quota.Observation!.UtilizationUnit); Assert.Equal("unix-seconds", quota.Observation.ResetUnit);
        Assert.Equal(0.75m, quota.Observation.RawUtilization); Assert.Equal("1700003600", quota.Observation.RawReset);
    }

    [Fact]
    public void ClaudeUnknownValuesAndOverageStaySeparate()
    {
        var snapshot = ClaudeRateLimitEvent.Parse(JsonElement.Parse("""
            {"status":"future","utilization":75,"resetsAt":1700003600000,"overageStatus":"rejected","overageResetsAt":1700003600}
            """), At)!;
        Assert.Single(snapshot.Quotas);
        Assert.Equal("overage", snapshot.Quotas[0].Id);
        Assert.Equal(LlmQuotaState.Exhausted, snapshot.Quotas[0].State);
        Assert.Null(ClaudeRateLimitEvent.Parse(JsonElement.Parse("{}"), At));
        Assert.Null(ClaudeRateLimitEvent.Parse(JsonElement.Parse("[]"), At));
    }

    [Fact]
    public void ClaudeOverageFieldsDoNotDiscardTheSameEventsReportedFraction()
    {
        var quota = Assert.Single(ClaudeRateLimitEvent.Parse(JsonElement.Parse("""
            {"status":"allowed","rateLimitType":"overage","utilization":0.75,"resetsAt":1700003600,"overageStatus":"rejected"}
            """), At)!.Quotas);
        Assert.Equal("overage", quota.Id); Assert.Equal(0.75, quota.UsedFraction);
        Assert.Equal(LlmQuotaState.Exhausted, quota.State); Assert.Equal(At.AddHours(1), quota.ResetsAt);
        Assert.Equal(0.75m, quota.Observation!.RawUtilization);
    }

    [Fact]
    public void StoreMergesBucketsByTheirOwnTimeAndFencesReplacedIdentityAndRetirement()
    {
        using var store = new LlmAccountUsageObservations();
        var connection = Connection(LlmTransport.OpenAiResponses);
        var binding = store.Reserve("a").Bind(connection);
        binding.Record(Snapshot("requests", At));
        binding.Record(Snapshot("tokens", At.AddMinutes(1)));
        binding.Record(Snapshot("requests", At.AddMinutes(-1)));
        var snapshot = store.Reserve("a").Get(connection)!;
        Assert.Equal(At.AddMinutes(1), snapshot.RetrievedAt);
        Assert.Equal(At, snapshot.Quotas[0].Observation!.ObservedAt);
        Assert.Null(store.Reserve("b").Get(connection));
        Assert.Null(store.Reserve("a").Get(connection with { Version = "2" }));
        binding.Record(Snapshot("stale", At.AddHours(1)));
        Assert.Null(store.Reserve("a").Get(connection with { Version = "2" }));
        var replacement = store.Reserve("a").Bind(connection with { Version = "2" });
        replacement.Record(Snapshot("new", At));
        Assert.Single(store.Reserve("a").Get(connection with { Version = "2" })!.Quotas);
        Assert.Null(store.Reserve("a").Get(connection with { Version = "2", Credential = new LlmCredential { ApiKey = "changed" }.Serialize() }));
        store.Dispose();
        replacement.Record(Snapshot("late", At));
        Assert.Null(replacement.Entry!.Snapshot);
        Assert.Null(store.Reserve("a").Get(connection));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FacadeSuppressesPrivateHeaderObservationsInBothHttpModes(bool streaming)
    {
        var body = streaming
            ? "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[]}}\n\n"
            : "{\"status\":\"completed\",\"output\":[]}";
        using var handler = new Handler(() => Response(body, ("x-ratelimit-limit-requests", "10"), ("x-ratelimit-remaining-requests", "3")));
        using var http = new HttpClient(handler);
        using var store = new LlmAccountUsageObservations();
        var connection = Connection(LlmTransport.OpenAiResponses) with { CapabilityOverrides = new() { SupportsStreaming = streaming } };
        var source = new Source(connection);
        await using var service = new LlmProviderService(source, [new OpenAiResponsesLlmClient(http)], [], observations: store);
        Assert.Null(await service.GetObservedAccountUsageAsync("a"));
        var events = await Collect(service.StreamAsync("a", new() { ModelId = "gpt-test", Messages = [new("user", "hi")] }));
        Assert.DoesNotContain(events, item => item is AccountUsageObservedEvent);
        Assert.Single(events.OfType<LlmChatCompletionEvent>());
        Assert.Equal(3m, Assert.Single((await service.GetObservedAccountUsageAsync("a"))!.Quotas).Remaining);
        Assert.Equal(1, handler.Calls);
        source.Connection = connection with { Version = "2" };
        Assert.Null(await service.GetObservedAccountUsageAsync("a"));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetObservedAccountUsageAsync("a", cancelled.Token));
    }

    [Fact]
    public async Task SuccessfulOAuthRefreshKeepsResponseObservationPollableAtPersistedVersion()
    {
        var original = Connection(LlmTransport.OpenAiResponses) with
        {
            Credential = new LlmCredential { OAuth = new() { AccessToken = "old-token", AccountId = "same-account" } }.Serialize(),
            CapabilityOverrides = new() { SupportsModelListing = false }
        };
        var source = new RefreshingSource(original);
        var authenticator = new RefreshingAuthenticator();
        using var handler = new Handler(() => Response("data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[]}}\n\n",
            ("x-ratelimit-remaining-requests", "3")));
        using var http = new HttpClient(handler);
        await using var service = new LlmProviderService(source, [new OpenAiResponsesLlmClient(http)], [authenticator]);
        await Collect(service.StreamAsync("a", new() { ModelId = "gpt-test" }));
        Assert.Equal("2", source.Connection.Version);
        Assert.Equal("new-token", LlmCredential.Parse(source.Connection.Credential)!.OAuth!.AccessToken);
        int providerCalls = handler.Calls, refreshCalls = authenticator.Calls;
        var observed = await service.GetObservedAccountUsageAsync("a");
        Assert.Equal(providerCalls, handler.Calls); Assert.Equal(refreshCalls, authenticator.Calls);
        Assert.NotNull(observed);
        Assert.Equal(3m, Assert.Single(observed.Quotas).Remaining);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task RefreshConfirmationDropsOldEvidenceWhenThePersistedCredentialIsReplacedOrDeleted(bool deleted, bool captured)
    {
        var original = Connection(LlmTransport.OpenAiResponses) with
        {
            Credential = new LlmCredential { OAuth = new() { AccessToken = "old-token", AccountId = "same-account" } }.Serialize(),
            CapabilityOverrides = new() { SupportsModelListing = false }
        };
        var source = new RefreshingSource(original, pauseConfirmation: true);
        source.CaptureConfirmationBeforeDelay = captured;
        using var store = new LlmAccountUsageObservations();
        using var handler = new Handler(() => Response("data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[]}}\n\n",
            ("x-ratelimit-remaining-requests", "3")));
        using var http = new HttpClient(handler);
        await using var service = new LlmProviderService(source, [new OpenAiResponsesLlmClient(http)], [new RefreshingAuthenticator()], observations: store);
        Task generation = Collect(service.StreamAsync("a", new() { ModelId = "gpt-test" }));
        await source.ConfirmationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        source.Connection = source.Connection with
        {
            Version = "3",
            Credential = new LlmCredential { OAuth = new() { AccessToken = "replacement-token", AccountId = "same-account" } }.Serialize()
        };
        store.Reserve("a").Bind(source.Connection).Record(Snapshot("new-good-bucket", At));
        source.DeleteOnConfirmation = deleted;
        source.ConfirmationRelease.TrySetResult();
        await generation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, handler.Calls);
        var current = store.Reserve("a").Get(source.Connection)!;
        Assert.Equal("new-good-bucket", Assert.Single(current.Quotas).Id);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ObservationConfirmationReadFailureDoesNotFailGeneration(bool ioFailure)
    {
        var original = Connection(LlmTransport.OpenAiResponses) with
        {
            Credential = new LlmCredential { OAuth = new() { AccessToken = "old-token", AccountId = "same-account" } }.Serialize(),
            CapabilityOverrides = new() { SupportsModelListing = false }
        };
        var source = new RefreshingSource(original)
        {
            ConfirmationError = ioFailure ? new IOException("Controlled source read failure.")
                : new InvalidOperationException("Controlled source read failure.")
        };
        using var handler = new Handler(() => Response("data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[]}}\n\n",
            ("x-ratelimit-remaining-requests", "3")));
        using var http = new HttpClient(handler);
        await using var service = new LlmProviderService(source, [new OpenAiResponsesLlmClient(http)], [new RefreshingAuthenticator()]);
        var events = await Collect(service.StreamAsync("a", new() { ModelId = "gpt-test" }));
        Assert.Single(events.OfType<LlmChatCompletionEvent>());
        Assert.Equal("2", source.Connection.Version);
        Assert.Equal(1, handler.Calls);
        Assert.Null(await service.GetObservedAccountUsageAsync("a"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LateConfirmationReadFailurePreservesCancellationAndRetirement(bool retire)
    {
        var original = Connection(LlmTransport.OpenAiResponses) with
        { Credential = new LlmCredential { OAuth = new() { AccessToken = "old-token", AccountId = "same-account" } }.Serialize() };
        var source = new RefreshingSource(original, pauseConfirmation: true)
        {
            IgnoreConfirmationCancellation = true,
            ConfirmationError = new IOException("Controlled late read failure.")
        };
        using var store = new LlmAccountUsageObservations();
        using var handler = new Handler(() => throw new InvalidOperationException("Cancelled confirmation cannot dispatch generation."));
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        await using var service = new LlmProviderService(source, [new OpenAiResponsesLlmClient(http)], [new RefreshingAuthenticator()], observations: store);
        Task generation = Collect(service.StreamAsync("a", new() { ModelId = "gpt-test" }, cancellation.Token));
        await source.ConfirmationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task? retirement = null;
        if (retire) retirement = service.DisposeAsync().AsTask();
        else cancellation.Cancel();
        source.ConfirmationRelease.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => generation);
        if (retirement is not null) await retirement.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, handler.Calls);
        Assert.Null(store.Reserve("a").Get(source.Connection));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RefreshConfirmationCancellationAndFacadeRetirementDoNotPublishOrRetainEvidence(bool retire)
    {
        var original = Connection(LlmTransport.OpenAiResponses) with
        { Credential = new LlmCredential { OAuth = new() { AccessToken = "old-token", AccountId = "same-account" } }.Serialize() };
        var source = new RefreshingSource(original, pauseConfirmation: true);
        using var store = new LlmAccountUsageObservations();
        using var handler = new Handler(() => throw new InvalidOperationException("No provider request may run after cancelled confirmation."));
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        await using var service = new LlmProviderService(source, [new OpenAiResponsesLlmClient(http)], [new RefreshingAuthenticator()], observations: store);
        Task generation = Collect(service.StreamAsync("a", new() { ModelId = "gpt-test" }, cancellation.Token));
        await source.ConfirmationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (retire) await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        else cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => generation);
        Assert.Equal(0, handler.Calls);
        Assert.Null(store.Reserve("a").Get(source.Connection));
    }

    [Fact]
    public void LateSameIdentityReservationKeepsBothBindingsValidAndRetirementClearsThem()
    {
        using var store = new LlmAccountUsageObservations();
        var connection = Connection(LlmTransport.OpenAiResponses);
        var delayed = store.Reserve("a");
        var newer = store.Reserve("a").Bind(connection);
        newer.Record(Snapshot("newer", At));
        var late = delayed.Bind(connection);
        late.Record(Snapshot("late-same-identity", At));
        newer.Record(Snapshot("still-valid", At));
        Assert.Same(newer.Entry, late.Entry);
        Assert.Equal(3, store.Reserve("a").Get(connection)!.Quotas.Count);
        store.Dispose();
        late.Record(Snapshot("after-disposal", At));
        Assert.Null(late.Entry!.Snapshot);
        Assert.Null(delayed.Bind(connection).Entry);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DelayedOldResolutionCannotDiscardNewerBoundObservations(bool oldGeneration)
    {
        var old = Connection(LlmTransport.OpenAiResponses) with { CapabilityOverrides = new() { SupportsModelListing = false } };
        var source = new DelayedFirstSource(old);
        int responses = 0;
        using var handler = new Handler(() => Response("data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[]}}\n\n",
            ("x-ratelimit-remaining-requests", Interlocked.Increment(ref responses) == 1 ? "8" : "3")));
        using var http = new HttpClient(handler);
        await using var service = new LlmProviderService(source, [new OpenAiResponsesLlmClient(http)]);
        Task delayed = oldGeneration
            ? Collect(service.StreamAsync("a", new() { ModelId = "gpt-test" }))
            : service.GetObservedAccountUsageAsync("a");
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        source.Connection = old with { Version = "2" };
        await Collect(service.StreamAsync("a", new() { ModelId = "gpt-test" }));
        Assert.Equal(8m, Assert.Single((await service.GetObservedAccountUsageAsync("a"))!.Quotas).Remaining);
        source.Release.TrySetResult();
        await delayed.WaitAsync(TimeSpan.FromSeconds(5));
        var current = await service.GetObservedAccountUsageAsync("a");
        Assert.NotNull(current);
        Assert.Equal(8m, Assert.Single(current.Quotas).Remaining);
    }

    [Fact]
    public async Task Anthropic429StoresEvidenceBeforeFailingWithoutInventingTokens()
    {
        using var handler = new Handler(() =>
        {
            var response = Response("{\"error\":{\"message\":\"limited\"}}", ("anthropic-ratelimit-requests-remaining", "0"));
            response.StatusCode = HttpStatusCode.TooManyRequests;
            return response;
        });
        using var http = new HttpClient(handler);
        await using var service = new LlmProviderService(new Source(Connection(LlmTransport.AnthropicMessages) with
        { Credential = new LlmCredential { ApiKey = "controlled-fixture-key" }.Serialize() }), [new AnthropicMessagesLlmClient(http)]);
        await Assert.ThrowsAsync<LlmProviderException>(() => Collect(service.StreamAsync("a", new() { ModelId = "claude-test", MaxOutputTokens = 8 })));
        Assert.Equal(LlmQuotaState.Exhausted, Assert.Single((await service.GetObservedAccountUsageAsync("a"))!.Quotas).State);
    }

    [Theory]
    [InlineData(LlmTransport.OpenAiCompatibleChat, true)]
    [InlineData(LlmTransport.OpenAiCompatibleChat, false)]
    [InlineData(LlmTransport.OpenAiCodex, true)]
    public async Task OtherHttpAdaptersRecordTheirOwnHeaderFamily(LlmTransport transport, bool streaming)
    {
        string body = transport == LlmTransport.OpenAiCodex
            ? "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[]}}\n\n"
            : streaming ? "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"
            : "{\"choices\":[{\"message\":{\"content\":\"hi\"},\"finish_reason\":\"stop\"}]}";
        string header = transport == LlmTransport.OpenAiCodex ? "x-codex-primary-used-percent" : "x-ratelimit-remaining-requests";
        using var handler = new Handler(() => Response(body, (header, "25")));
        using var http = new HttpClient(handler);
        var connection = Connection(transport) with
        {
            Credential = transport == LlmTransport.OpenAiCodex
                ? new LlmCredential { OAuth = new() { AccessToken = "fixture-token", AccountId = "fixture-account" } }.Serialize() : null,
            CapabilityOverrides = new() { SupportsStreaming = streaming, SupportsModelListing = false }
        };
        ILlmClient client = transport == LlmTransport.OpenAiCodex ? new OpenAiCodexLlmClient(http) : new OpenAiCompatibleChatLlmClient(http);
        await using var service = new LlmProviderService(new Source(connection), [client]);
        var events = await Collect(service.StreamAsync("a", new() { ModelId = "gpt-test", Messages = [new("user", "hi")] }));
        Assert.DoesNotContain(events, item => item is AccountUsageObservedEvent);
        var quota = Assert.Single((await service.GetObservedAccountUsageAsync("a"))!.Quotas);
        Assert.Equal(transport == LlmTransport.OpenAiCodex ? "codex:primary" : "requests", quota.Id);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public void InvalidHugeNativeWindowMetadataDoesNotCreateAnEmptyObservation()
    {
        using var response = Response("", ("x-codex-primary-window-minutes", decimal.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Null(CodexRateLimitHeaders.Parse(response.Headers, At));
        Assert.NotNull(AccountUsageValues.Duration("1000000000s"));
        using var duration = Response("", ("x-ratelimit-reset-requests", "922337203685s"), ("x-ratelimit-limit-requests", "10"));
        Assert.Null(Assert.Single(OpenAiRateLimitHeaders.Parse(duration.Headers, At)!.Quotas).ResetsAt);
    }

    [Fact]
    public async Task OpenRouterQueriesOnlyTheRecognizedHostAndOmitsPrivateLabels()
    {
        using var handler = new Handler(() => Response("""
            {"data":{"label":"private-person","limit":10,"limit_remaining":2,"limit_reset":"monthly","usage":8,"usage_daily":1,"usage_weekly":2,"usage_monthly":3,"is_free_tier":true,"free_model_daily_requests":{"used":3,"limit":50,"remaining":47}}}
            """));
        using var http = new HttpClient(handler);
        var client = new OpenAiCompatibleChatLlmClient(http);
        var connection = Connection(LlmTransport.OpenAiCompatibleChat) with { Endpoint = "https://openrouter.ai/api/v1" };
        await using var service = new LlmProviderService(new Source(connection), [client]);
        var snapshot = await service.GetAccountUsageAsync("a");
        Assert.Equal("https://openrouter.ai/api/v1/key", handler.Uri!.AbsoluteUri);
        Assert.Equal("free", snapshot.Plan); Assert.Equal(2, snapshot.Quotas.Count); Assert.Equal(4, snapshot.Metrics.Count);
        Assert.Equal(2m, snapshot.Quotas[0].Remaining);
        Assert.DoesNotContain("private-person", JsonSerializer.Serialize(snapshot, ConnectLlmJson.AccountUsageSnapshot));
        Assert.Null(await service.GetObservedAccountUsageAsync("a"));
        Assert.False(client.GetCapabilities(connection with { Endpoint = "https://openrouter.ai.evil.invalid/api/v1" }).SupportsAccountUsage);
        await Assert.ThrowsAsync<NotSupportedException>(() => client.GetAccountUsageAsync(Connection(LlmTransport.OpenAiCompatibleChat)));
        Assert.Throws<NotSupportedException>(() => client.GetCapabilities(Connection(LlmTransport.OpenAiCompatibleChat) with
        { CapabilityOverrides = new() { SupportsAccountUsage = true } }));
        Assert.False(client.GetCapabilities(connection with { CapabilityOverrides = new() { SupportsAccountUsage = false } }).SupportsAccountUsage);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public void OpenRouterUnlimitedRequiresExplicitNullAndMissingValuesStayUnknown()
    {
        Assert.True(Assert.Single(OpenRouterAccountUsageParser.Parse(JsonElement.Parse("{\"data\":{\"limit\":null}}"), At).Credits).IsUnlimited);
        Assert.Empty(OpenRouterAccountUsageParser.Parse(JsonElement.Parse("{\"data\":{}}"), At).Credits);
        Assert.Empty(OpenRouterAccountUsageParser.Parse(JsonElement.Parse("{\"data\":{\"limit\":\"invalid\"}}"), At).Credits);
    }

    [Fact]
    public async Task ClaudeChatRecordsTheSdkQuotaEventWithoutPublishingIt()
    {
        using var http = new HttpClient();
        var client = new ClaudeCliLlmClient(http, new Runner());
        await using var service = new LlmProviderService(new Source(Connection(LlmTransport.AnthropicClaudeCli)), [client]);
        var events = await Collect(service.StreamAsync("a", new() { ModelId = "claude-test", Messages = [new("user", "hi")] }));
        Assert.DoesNotContain(events, item => item is AccountUsageObservedEvent);
        var quota = Assert.Single((await service.GetObservedAccountUsageAsync("a"))!.Quotas);
        Assert.Equal(0.75, quota.UsedFraction);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700003600), quota.ResetsAt);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("resume")]
    [InlineData("fork")]
    public async Task DelayedNativeResolutionCannotReplaceTheNewerSharedIdentity(string operation)
    {
        using var store = new LlmAccountUsageObservations();
        var source = new DelayedFirstSource(Connection(LlmTransport.AnthropicClaudeCli));
        var factory = new Factory();
        await using var agents = new LlmAgentProviderService(source, [new ClaudeAgentClient(factory)], store);
        await using var llms = new LlmProviderService(source, [], [], observations: store);
        var resume = new LlmAgentResumeOptions { ModelId = "claude-test", Runtime = new() { WorkingDirectory = Environment.CurrentDirectory } };
        string thread = Guid.NewGuid().ToString();
        Task<ILlmAgentSession> delayed = operation switch
        {
            "start" => agents.StartSessionAsync("a", NativeOptions()),
            "resume" => agents.ResumeSessionAsync("a", thread, resume),
            _ => agents.ForkSessionAsync("a", thread, options: resume)
        };
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        source.Connection = source.Connection with { Version = "2" };
        var current = await agents.StartSessionAsync("a", NativeOptions());
        await CollectAgent(current.RunTurnAsync(new() { Input = [new LlmAgentTextContent("new identity")] }));
        var good = await llms.GetObservedAccountUsageAsync("a");
        Assert.NotNull(good);
        source.Release.TrySetResult();
        var old = await delayed.WaitAsync(TimeSpan.FromSeconds(5));
        await CollectAgent(old.RunTurnAsync(new() { Input = [new LlmAgentTextContent("old identity")] }));
        Assert.Same(good, await llms.GetObservedAccountUsageAsync("a"));
        await agents.DisposeAsync();
        Assert.All(factory.Processes, process => Assert.True(process.Disposed));
    }

    [Fact]
    public async Task SharedStandaloneStoreIsolatesNativeSessionsAndSurvivesBorrowedFacadeDisposal()
    {
        using var store = new LlmAccountUsageObservations();
        var source = new Source(Connection(LlmTransport.AnthropicClaudeCli));
        using var http = new HttpClient();
        var client = new ClaudeCliLlmClient(http, new Runner());
        var factory = new Factory();
        await using var agents = new LlmAgentProviderService(source, [new ClaudeAgentClient(factory)], store);
        await using var llms = new LlmProviderService(source, [client], [], observations: store);
        ILlmAgentSession first = await agents.StartSessionAsync("a", NativeOptions());
        ILlmAgentSession second = await agents.StartSessionAsync("b", NativeOptions());
        await CollectAgent(first.RunTurnAsync(new() { Input = [new LlmAgentTextContent("hi")] }));
        await CollectAgent(second.RunTurnAsync(new() { Input = [new LlmAgentTextContent("hi")] }));
        Assert.NotNull(await llms.GetObservedAccountUsageAsync("a")); Assert.NotNull(await llms.GetObservedAccountUsageAsync("b"));
        await llms.DisposeAsync();
        Assert.NotNull(store.Reserve("a").Get(source.Connection));
        source.Connection = source.Connection with { Version = "2" };
        Assert.Null(store.Reserve("a").Get(source.Connection));
        await CollectAgent(first.RunTurnAsync(new() { Input = [new LlmAgentTextContent("old session")] }));
        Assert.Null(store.Reserve("a").Get(source.Connection));
        Assert.Null(store.Reserve("b").Get(source.Connection));
        await agents.DisposeAsync();
        Assert.All(factory.Processes, process => Assert.True(process.Disposed));
        Assert.Throws<ObjectDisposedException>(() => llms.GetObservedAccountUsageAsync("a").GetAwaiter().GetResult());
        store.Dispose();
        Assert.Null(store.Reserve("a").Get(source.Connection));
    }

    [Fact]
    public void DiOwnerSharesOneDisposableStoreAcrossScopesWithinItsGeneration()
    {
        var services = new ServiceCollection();
        services.AddLlmConnect();
        using var provider = services.BuildServiceProvider();
        using var first = provider.CreateScope(); using var second = provider.CreateScope();
        var store = first.ServiceProvider.GetRequiredService<LlmAccountUsageObservations>();
        Assert.Same(store, second.ServiceProvider.GetRequiredService<LlmAccountUsageObservations>());
        var connection = Connection(LlmTransport.OpenAiResponses);
        var binding = store.Reserve("a").Bind(connection); binding.Record(Snapshot("tokens", At));
        provider.Dispose(); binding.Record(Snapshot("late", At));
        Assert.Null(binding.Entry!.Snapshot); Assert.Null(store.Reserve("a").Get(connection));
    }

    [Fact]
    public async Task FacadeDisposalCancelsAndDrainsAnObservationPollWaitingOnTheHost()
    {
        var source = new BlockingSource();
        await using var service = new LlmProviderService(source, []);
        Task<LlmAccountUsageSnapshot?> polling = service.GetObservedAccountUsageAsync("a");
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => polling);
        Assert.Throws<ObjectDisposedException>(() => service.GetObservedAccountUsageAsync("a").GetAwaiter().GetResult());
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("null", null)]
    public void OwnedMetadataRoundTripsExplicitAndUnknownUnlimited(string wire, bool? expected)
    {
        var snapshot = JsonSerializer.Deserialize("{\"RetrievedAt\":\"2023-11-14T22:13:20Z\",\"Credits\":[{\"Id\":\"credits\",\"IsUnlimited\":" + wire + "}]}", ConnectLlmJson.AccountUsageSnapshot)!;
        Assert.Equal(expected, snapshot.Credits[0].IsUnlimited);
        Assert.Equal(expected, Assert.Single(CodexAccountUsageParser.Parse(JsonElement.Parse("{\"credits\":{\"unlimited\":" + wire + "}}"), At).Credits).IsUnlimited);
        using var written = JsonDocument.Parse(JsonSerializer.Serialize(snapshot, ConnectLlmJson.AccountUsageSnapshot));
        Assert.Equal(wire, written.RootElement.GetProperty("Credits")[0].GetProperty("IsUnlimited").GetRawText());
        using var store = new LlmAccountUsageObservations();
        Assert.DoesNotContain("AccountUsageObservationBinding", JsonSerializer.Serialize(Connection(LlmTransport.OpenAiResponses)
            with
        { AccountUsageObservationBinding = store.Reserve("a").Bind(Connection(LlmTransport.OpenAiResponses)) }, ConnectLlmJson.Connection));
    }

    [Fact]
    public void OwnedMetadataRoundTripsObservedBucketProvenanceWithoutInventingExpiry()
    {
        var original = ClaudeRateLimitEvent.Parse(JsonElement.Parse(ClaudeRateLine).GetProperty("rate_limit_info"), At)!;
        string wire = JsonSerializer.Serialize(original, ConnectLlmJson.AccountUsageSnapshot);
        var roundtrip = JsonSerializer.Deserialize(wire, ConnectLlmJson.AccountUsageSnapshot)!;
        Assert.Equal(original.Quotas[0], roundtrip.Quotas[0]);
        Assert.Equal(At, roundtrip.Quotas[0].Observation!.ObservedAt);
        Assert.Null(roundtrip.Quotas[0].Observation!.ExpiresAt);
        var query = CodexAccountUsageParser.Parse(JsonElement.Parse("{\"credits\":{\"balance\":\"2.5\"}}"), At);
        Assert.Null(Assert.Single(query.Credits).IsUnlimited);
        Assert.Null(Assert.Single(query.Credits).HasCredits);
        Assert.Null(query.Credits[0].Observation);
        Assert.Null(Assert.Single(CodexAccountUsageParser.Parse(JsonElement.Parse("{\"credits\":{\"unlimited\":\"malformed\"}}"), At).Credits).IsUnlimited);
    }

    [Fact]
    public void ActualStoreFacadeNativeCallerAndOwnedMetadataReleaseTheirForeignGeneration()
    {
        WeakReference generation = ExerciseGeneration();
        for (int attempt = 0; generation.IsAlive && attempt < 30; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); }
        Assert.False(generation.IsAlive, "The settled controlled observation caller retains its actual Connect/Common generation.");
    }

    private static async Task ForeignCallerAsync()
    {
        Assert.Same(AssemblyLoadContext.GetLoadContext(typeof(LlmProviderService).Assembly), AssemblyLoadContext.GetLoadContext(typeof(LlmAttachment).Assembly));
        var fixture = new AccountUsageObservationTests();
        fixture.OpenAiHeadersPreserveCountsAndDurationUnits("6m0s", 360);
        fixture.AnthropicResetsRequireFullRfc3339InsteadOfInventingLocalTime("2023-11-14T23:13:20.125Z", true);
        await fixture.FacadeSuppressesPrivateHeaderObservationsInBothHttpModes(false);
        await fixture.SuccessfulOAuthRefreshKeepsResponseObservationPollableAtPersistedVersion();
        await fixture.ObservationConfirmationReadFailureDoesNotFailGeneration(true);
        await fixture.LateConfirmationReadFailurePreservesCancellationAndRetirement(true);
        await fixture.DelayedOldResolutionCannotDiscardNewerBoundObservations(true);
        await fixture.DelayedOldResolutionCannotDiscardNewerBoundObservations(false);
        await fixture.RefreshConfirmationDropsOldEvidenceWhenThePersistedCredentialIsReplacedOrDeleted(false, false);
        await fixture.RefreshConfirmationDropsOldEvidenceWhenThePersistedCredentialIsReplacedOrDeleted(true, false);
        await fixture.RefreshConfirmationDropsOldEvidenceWhenThePersistedCredentialIsReplacedOrDeleted(false, true);
        await fixture.RefreshConfirmationCancellationAndFacadeRetirementDoNotPublishOrRetainEvidence(true);
        fixture.LateSameIdentityReservationKeepsBothBindingsValidAndRetirementClearsThem();
        await fixture.DelayedNativeResolutionCannotReplaceTheNewerSharedIdentity("start");
        await fixture.DelayedNativeResolutionCannotReplaceTheNewerSharedIdentity("resume");
        await fixture.DelayedNativeResolutionCannotReplaceTheNewerSharedIdentity("fork");
        await fixture.SharedStandaloneStoreIsolatesNativeSessionsAndSurvivesBorrowedFacadeDisposal();
        fixture.OwnedMetadataRoundTripsExplicitAndUnknownUnlimited("true", true);
        fixture.OwnedMetadataRoundTripsExplicitAndUnknownUnlimited("false", false);
        fixture.OwnedMetadataRoundTripsExplicitAndUnknownUnlimited("null", null);
        fixture.OwnedMetadataRoundTripsObservedBucketProvenanceWithoutInventingExpiry();
        fixture.DiOwnerSharesOneDisposableStoreAcrossScopesWithinItsGeneration();
        await fixture.FacadeDisposalCancelsAndDrainsAnObservationPollWaitingOnTheHost();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ExerciseGeneration()
    {
        string directory = Path.GetDirectoryName(typeof(AccountUsageObservationTests).Assembly.Location)!;
        var generation = new Generation(directory);
        var weak = new WeakReference(generation);
        try
        {
            Assembly connect = generation.LoadFromAssemblyPath(typeof(ConnectLlmJson).Assembly.Location);
            Assembly common = generation.LoadFromAssemblyPath(typeof(InferenceJson).Assembly.Location);
            Assembly fixture = generation.LoadFromAssemblyPath(typeof(AccountUsageObservationTests).Assembly.Location);
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(connect)); Assert.Same(generation, AssemblyLoadContext.GetLoadContext(common));
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(fixture));
            Type caller = fixture.GetType(typeof(AccountUsageObservationTests).FullName!, true)!;
            var operation = (Task)caller.GetMethod(nameof(ForeignCallerAsync), BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
            operation.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        }
        finally { generation.Unload(); }
        return weak;
    }

    private sealed class Generation(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name?.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal) != true) return null;
            string path = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }

    private static LlmConnection Connection(LlmTransport transport) => new()
    { Version = "1", Transport = transport, Endpoint = transport == LlmTransport.AnthropicClaudeCli ? "claude" : "https://provider.invalid/v1" };
    private static LlmAccountUsageSnapshot Snapshot(string id, DateTimeOffset at) => new()
    { RetrievedAt = at, Quotas = [new() { Id = id, Observation = new() { ObservedAt = at, Source = "fixture" } }] };
    private static LlmAgentSessionOptions NativeOptions() => new() { ModelId = "claude-test", Runtime = new() { WorkingDirectory = Environment.CurrentDirectory } };
    private static async Task<List<LlmStreamEvent>> Collect(IAsyncEnumerable<LlmStreamEvent> stream)
    { var result = new List<LlmStreamEvent>(); await foreach (var item in stream) result.Add(item); return result; }
    private static async Task CollectAgent(IAsyncEnumerable<LlmAgentEvent> stream)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var item in stream.WithCancellation(deadline.Token)) Assert.IsNotType<AccountUsageObservedEvent>((object)item);
    }
    private static HttpResponseMessage Response(string body, params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        foreach (var header in headers) response.Headers.TryAddWithoutValidation(header.Name, header.Value);
        return response;
    }
    private sealed class Source(LlmConnection connection) : ILlmConnectionSource
    {
        public LlmConnection Connection { get; set; } = connection;
        public Task<LlmConnection?> GetConnectionAsync(string id, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); return Task.FromResult<LlmConnection?>(Connection); }
        public Task SaveCredentialAsync(string id, string credential, string expectedVersion, CancellationToken token = default) => throw new NotSupportedException();
    }
    private sealed class Handler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public Uri? Uri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Calls++; Uri = request.RequestUri; return Task.FromResult(response()); }
    }
    private sealed class RefreshingSource(LlmConnection connection, bool pauseConfirmation = false) : ILlmConnectionSource
    {
        private int _calls;
        public LlmConnection Connection { get; set; } = connection;
        public bool DeleteOnConfirmation { get; set; }
        public bool CaptureConfirmationBeforeDelay { get; set; }
        public bool IgnoreConfirmationCancellation { get; set; }
        public Exception? ConfirmationError { get; set; }
        public TaskCompletionSource ConfirmationEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ConfirmationRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<LlmConnection?> GetConnectionAsync(string id, CancellationToken token = default)
        {
            LlmConnection captured = Connection;
            if (Interlocked.Increment(ref _calls) == 2)
            {
                if (pauseConfirmation)
                {
                    ConfirmationEntered.TrySetResult();
                    await ConfirmationRelease.Task.WaitAsync(IgnoreConfirmationCancellation ? CancellationToken.None : token);
                }
                if (ConfirmationError is { } error) throw error;
            }
            return DeleteOnConfirmation ? null : CaptureConfirmationBeforeDelay ? captured : Connection;
        }
        public Task SaveCredentialAsync(string id, string credential, string expectedVersion, CancellationToken token = default)
        {
            Assert.Equal(Connection.Version, expectedVersion);
            Connection = Connection with { Credential = credential, Version = "2" };
            return Task.CompletedTask;
        }
    }
    private sealed class RefreshingAuthenticator : ILlmAuthenticator
    {
        public LlmTransport Transport => LlmTransport.OpenAiResponses;
        public int Calls { get; private set; }
        public Task<LlmCredential> EnsureFreshAsync(LlmCredential credential, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(credential with { OAuth = credential.OAuth! with { AccessToken = "new-token" } }); }
    }
    private sealed class DelayedFirstSource(LlmConnection connection) : ILlmConnectionSource
    {
        private int _calls;
        public LlmConnection Connection { get; set; } = connection;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<LlmConnection?> GetConnectionAsync(string id, CancellationToken token = default)
        {
            LlmConnection captured = Connection;
            if (Interlocked.Increment(ref _calls) == 1)
            { Entered.TrySetResult(); await Release.Task.WaitAsync(token); }
            return captured;
        }
        public Task SaveCredentialAsync(string id, string credential, string expectedVersion, CancellationToken token = default) => throw new NotSupportedException();
    }
    private sealed class BlockingSource : ILlmConnectionSource
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<LlmConnection?> GetConnectionAsync(string id, CancellationToken token = default)
        { Entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return null; }
        public Task SaveCredentialAsync(string id, string credential, string expectedVersion, CancellationToken token = default) => throw new NotSupportedException();
    }
    private sealed class Runner : IClaudeCliRunner
    {
        public Task<ClaudeCliProcessResult> RunAsync(ClaudeCliInvocation invocation, CancellationToken token = default) =>
            Task.FromResult(new ClaudeCliProcessResult(0, ClaudeRateLine + "\n" + ClaudeResultLine, ""));
    }
    private sealed class Factory : IClaudeCliSessionProcessFactory
    {
        internal List<Process> Processes { get; } = [];
        public Task<IClaudeCliSessionProcess> StartAsync(ClaudeCliSessionInvocation invocation, CancellationToken token = default)
        { var process = new Process(); Processes.Add(process); return Task.FromResult<IClaudeCliSessionProcess>(process); }
    }
    private sealed class Process : IClaudeCliSessionProcess
    {
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
        public bool Disposed { get; private set; }
        public ValueTask WriteLineAsync(string line, CancellationToken token = default)
        {
            JsonElement root = JsonElement.Parse(line);
            if (root.GetProperty("type").GetString() == "control_request")
                _lines.Writer.TryWrite("{\"type\":\"control_response\",\"response\":{\"subtype\":\"success\",\"request_id\":" + root.GetProperty("request_id").GetRawText() + "}}");
            else if (root.GetProperty("type").GetString() == "user")
            {
                _lines.Writer.TryWrite("{\"type\":\"user\",\"isReplay\":true,\"uuid\":" + root.GetProperty("uuid").GetRawText() + "}");
                _lines.Writer.TryWrite(ClaudeRateLine);
                _lines.Writer.TryWrite(ClaudeResultLine);
            }
            return ValueTask.CompletedTask;
        }
        public async ValueTask<string?> ReadLineAsync(CancellationToken token = default) => await _lines.Reader.ReadAsync(token);
        public Task<ClaudeCliProcessResult> WaitForExitAsync(CancellationToken token = default) => Task.FromResult(new ClaudeCliProcessResult(0, "", ""));
        public ValueTask DisposeAsync() { Disposed = true; _lines.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
}
