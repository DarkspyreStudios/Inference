using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class XaiSignInTests
{
    public static TheoryData<string> InvalidOAuthResults => new()
    {
        "api-key", "blank-api-key", "secret-header", "aws", "google", "azure-secret", "azure-certificate",
        "empty-token", "whitespace-token", "newline-token", "nul-token"
    };

    [Theory]
    [MemberData(nameof(InvalidOAuthResults))]
    public async Task StatusRejectsIncompatibleOrMalformedOAuthWithoutEffects(string kind)
    {
        var source = new Source();
        source.Connection = source.Connection! with { Credential = InvalidOAuth(kind).Serialize() };
        var auth = new Authenticator();
        await using var service = new LlmProviderService(source, [], [auth]);
        Assert.False(await service.IsAuthenticatedAsync("id"));
        Assert.Equal(0, auth.Starts);
        Assert.Equal(0, auth.Polls);
        Assert.Equal(0, source.Saves);
    }

    [Theory]
    [MemberData(nameof(InvalidOAuthResults))]
    public async Task OnePollRejectsIncompatibleOrMalformedOAuthBeforeSave(string kind) =>
        await RefuseInvalidLoginResultAsync(kind, interactive: false);

    [Theory]
    [MemberData(nameof(InvalidOAuthResults))]
    public async Task InteractiveLoginRejectsIncompatibleOrMalformedOAuthBeforeSave(string kind) =>
        await RefuseInvalidLoginResultAsync(kind, interactive: true);

    private static async Task RefuseInvalidLoginResultAsync(string kind, bool interactive)
    {
        var source = new Source();
        var auth = new Authenticator { Credential = InvalidOAuth(kind) };
        await using var service = new LlmProviderService(source, [], [auth]);
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => interactive
            ? service.SignInAsync("id", new Prompt(_ => { }))
            : CompleteAsync(service, auth.Code));
        Assert.Equal("Grok sign-in did not return an OAuth credential.", error.Message);
        Assert.DoesNotContain("original-token", error.ToString());
        Assert.DoesNotContain("other-secret", error.ToString());
        Assert.Equal(interactive ? 1 : 0, auth.Starts);
        Assert.Equal(1, auth.Polls);
        Assert.Equal(0, source.Saves);
        Assert.Null(source.Saved);
        Assert.Equal("version", source.Connection!.Version);
    }

    private static LlmCredential InvalidOAuth(string kind)
    {
        var credential = new LlmCredential { OAuth = new() { AccessToken = "original-token" } };
        return kind switch
        {
            "api-key" => credential with { ApiKey = "other-secret" },
            "blank-api-key" => credential with { ApiKey = " " },
            "secret-header" => credential with { SecretHeaders = new Dictionary<string, string> { ["X-Key"] = "other-secret" } },
            "aws" => credential with { Aws = new() { SecretAccessKey = "other-secret" } },
            "google" => credential with { Google = new() { ServiceAccountJson = "other-secret" } },
            "azure-secret" => credential with { Azure = new LlmAzureSecret.ClientSecret("other-secret") },
            "azure-certificate" => credential with { Azure = new LlmAzureSecret.CertificatePkcs12("other-secret", "other-secret") },
            "empty-token" => credential with { OAuth = new() { AccessToken = "" } },
            "whitespace-token" => credential with { OAuth = new() { AccessToken = "bad token" } },
            "newline-token" => credential with { OAuth = new() { AccessToken = "token\r\nnew" } },
            "nul-token" => credential with { OAuth = new() { AccessToken = "token\0new" } },
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    [Fact]
    public async Task OnePollSavesTheCapturedVersionAndOnlyAnOAuthCredential()
    {
        var source = new Source();
        var auth = new Authenticator();
        await using var service = new LlmProviderService(source, [], [auth]);
        LlmDeviceCode code = await service.StartXaiLoginAsync();
        await CompleteAsync(service, code);
        Assert.Equal(1, auth.Starts);
        Assert.Equal(1, auth.Polls);
        Assert.Equal("version", source.ExpectedVersion);
        Assert.Equal("access-token", LlmCredential.Parse(source.Saved)!.OAuth!.AccessToken);
        Assert.Null(LlmCredential.Parse(source.Saved)!.ApiKey);
        Assert.Equal(1, source.Saves);
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("deleted")]
    [InlineData("other-transport")]
    public async Task InvalidConnectionFailsBeforePolling(string change)
    {
        var source = new Source();
        source.Connection = change switch
        {
            "stale" => source.Connection! with { Version = "changed" },
            "other-transport" => source.Connection! with { Transport = LlmTransport.OpenAiCodex },
            _ => null
        };
        var auth = new Authenticator();
        await using var service = new LlmProviderService(source, [], [auth]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => CompleteAsync(service, auth.Code));
        Assert.Equal(0, auth.Polls);
        Assert.Equal(0, source.Saves);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedOrDeletedConnectionRejectsTheDelayedResultWithoutRetry(bool deleted)
    {
        var gate = new Gate();
        var source = new Source();
        var auth = new Authenticator { PollGate = gate };
        await using var service = new LlmProviderService(source, [], [auth]);
        Task polling = CompleteAsync(service, auth.Code);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        source.Connection = deleted ? null : source.Connection! with { Version = "new-version", Credential = "replacement" };
        gate.Release.TrySetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => polling);
        Assert.Equal(1, auth.Polls);
        Assert.Equal(1, source.Saves);
        Assert.Null(source.Saved);
        Assert.Equal(deleted ? null : "replacement", source.Connection?.Credential);
    }

    [Theory]
    [InlineData("api-key")]
    [InlineData("mixed-key")]
    [InlineData("static-header")]
    [InlineData("empty-oauth")]
    public async Task AuthenticatorCannotSaveAnApiKeyOrStaticHeaderInPlaceOfOAuth(string kind)
    {
        LlmCredential invalid = kind switch
        {
            "api-key" => new() { ApiKey = "key" },
            "mixed-key" => new() { ApiKey = "key", OAuth = new() { AccessToken = "access" } },
            "static-header" => new() { OAuth = new() { AccessToken = "access" }, SecretHeaders = new Dictionary<string, string> { ["X-Key"] = "key" } },
            _ => new() { OAuth = new() { AccessToken = "" } }
        };
        var source = new Source();
        var auth = new Authenticator { Credential = invalid };
        await using var service = new LlmProviderService(source, [], [auth]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => CompleteAsync(service, auth.Code));
        Assert.Equal(1, auth.Polls);
        Assert.Equal(0, source.Saves);
    }

    [Fact]
    public async Task ApiKeySetterRejectsGrokAndAuthenticationDoesNotAcceptAKey()
    {
        var source = new Source();
        source.Connection = source.Connection! with { Credential = new LlmCredential { ApiKey = "key" }.Serialize() };
        await using var service = new LlmProviderService(source, []);
        Assert.False(await service.IsAuthenticatedAsync("id"));
        await Assert.ThrowsAsync<NotSupportedException>(() => service.SetApiKeyAsync("id", "another-key", "version"));
        Assert.Equal(0, source.Saves);
        source.Connection = source.Connection with { Credential = new LlmCredential { OAuth = new() { AccessToken = "access" } }.Serialize() };
        Assert.True(await service.IsAuthenticatedAsync("id"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingPollReturnsToItsOwnerWithoutSavingOrRetrying(bool slowDown)
    {
        var source = new Source();
        var auth = new Authenticator { Pending = new("Waiting for approval.") { SlowDown = slowDown } };
        await using var service = new LlmProviderService(source, [], [auth]);
        LlmAuthPendingException pending = await Assert.ThrowsAsync<LlmAuthPendingException>(() => CompleteAsync(service, auth.Code));
        Assert.Equal(slowDown, pending.SlowDown);
        Assert.Equal(1, auth.Polls);
        Assert.Equal(0, source.Saves);
        LlmDeviceCode next = XaiDevicePolling.AfterPending(auth.Code, pending);
        Assert.Equal(slowDown ? 6 : 1, next.IntervalSeconds);
        Assert.Equal(auth.Code.ExpiresAt, next.ExpiresAt);
        Assert.Equal(auth.Code.DeviceAuthId, next.DeviceAuthId);
    }

    [Fact]
    public async Task InteractiveSignInCapturesTheVersionBeforePrompting()
    {
        var source = new Source();
        var auth = new Authenticator();
        await using var service = new LlmProviderService(source, [], [auth]);
        var prompt = new Prompt(code =>
        {
            Assert.Equal(auth.Code, code);
            source.Connection = source.Connection! with { Version = "changed", Credential = "replacement" };
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SignInAsync("id", prompt));
        Assert.Equal(1, prompt.Calls);
        Assert.Equal(1, auth.Polls);
        Assert.Equal(1, source.Saves);
        Assert.Equal("replacement", source.Connection!.Credential);
        Assert.Null(source.Saved);
    }

    [Fact]
    public async Task InteractiveSignInSavesAfterApprovalAgainstItsOriginalVersion()
    {
        var source = new Source();
        var auth = new Authenticator();
        await using var service = new LlmProviderService(source, [], [auth]);
        await service.SignInAsync("id", new Prompt(code => Assert.Equal(auth.Code, code)));
        Assert.Equal(1, auth.Starts);
        Assert.Equal(1, auth.Polls);
        Assert.Equal("version", source.ExpectedVersion);
        Assert.Equal("access-token", LlmCredential.Parse(source.Saved)!.OAuth!.AccessToken);
    }

    [Fact]
    public async Task ExpiredDeviceAuthorizationNeverPollsOrSaves()
    {
        var source = new Source();
        var auth = new Authenticator { Code = Code() with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) } };
        await using var service = new LlmProviderService(source, [], [auth]);
        LlmProviderException failure = await Assert.ThrowsAsync<LlmProviderException>(() => service.SignInAsync("id", new Prompt(_ => { })));
        Assert.Equal(LlmTransport.XaiResponses, failure.Transport);
        Assert.Equal("expired_token", failure.ProviderCode);
        Assert.Equal(0, auth.Polls);
        Assert.Equal(0, source.Saves);
    }

    [Fact]
    public async Task CallerCancellationStopsAnInteractiveWaitBeforePolling()
    {
        using var cancellation = new CancellationTokenSource();
        var source = new Source();
        var auth = new Authenticator();
        await using var service = new LlmProviderService(source, [], [auth]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SignInAsync("id",
            new Prompt(_ => cancellation.Cancel()), cancellation.Token));
        Assert.Equal(0, auth.Polls);
        Assert.Equal(0, source.Saves);
    }

    [Theory]
    [InlineData("start", false)]
    [InlineData("start", true)]
    [InlineData("resolve", false)]
    [InlineData("resolve", true)]
    [InlineData("poll", false)]
    [InlineData("poll", true)]
    [InlineData("save", false)]
    [InlineData("save", true)]
    public async Task RetirementDrainsUncooperativeSignInAndRejectsLateResults(string boundary, bool callerCancellation)
    {
        var gate = new Gate();
        var source = new Source { ResolveGate = boundary == "resolve" ? gate : null, SaveGate = boundary == "save" ? gate : null };
        var auth = new Authenticator
        {
            StartGate = boundary == "start" ? gate : null,
            PollGate = boundary == "poll" ? gate : null
        };
        using var cancellation = new CancellationTokenSource();
        await using var service = new LlmProviderService(source, [], [auth]);
        Task operation = boundary == "start" ? service.StartXaiLoginAsync(cancellation.Token) : CompleteAsync(service, auth.Code, cancellation.Token);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (callerCancellation) cancellation.Cancel();
        Task retirement = service.DisposeAsync().AsTask();
        Assert.False(retirement.IsCompleted);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.StartXaiLoginAsync());
        gate.Release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        await retirement.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(boundary == "save" ? 1 : 0, source.Saves);
        Assert.Equal(boundary == "start" || boundary == "resolve" ? 0 : 1, auth.Polls);
        Assert.Equal(boundary == "save" ? "access-token" : null, LlmCredential.Parse(source.Saved)?.OAuth?.AccessToken);
        Assert.False(auth.Disposed);
    }

    [Fact]
    public async Task RetirementDrainsAnUncooperativeInteractivePromptWithoutPolling()
    {
        var gate = new Gate();
        var source = new Source();
        var auth = new Authenticator();
        await using var service = new LlmProviderService(source, [], [auth]);
        Task operation = service.SignInAsync("id", new Prompt(_ => { }, gate));
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task retirement = service.DisposeAsync().AsTask();
        Assert.False(retirement.IsCompleted);
        gate.Release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        await retirement.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, auth.Polls);
        Assert.Equal(0, source.Saves);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("resolve")]
    [InlineData("poll")]
    [InlineData("save")]
    [InlineData("prompt")]
    [InlineData("interactive-poll")]
    [InlineData("interactive-save")]
    public async Task OwnerRetirementRejectsLateSignInWhileLinkedCancellationPropagationIsBlocked(string boundary)
    {
        var anchorGate = new Gate();
        var targetGate = new Gate();
        var source = new Source
        {
            ResolveGate = boundary == "resolve" ? targetGate : null,
            SaveGate = boundary is "save" or "interactive-save" ? targetGate : null
        };
        var auth = new Authenticator
        {
            StartGate = anchorGate,
            PollGate = boundary is "poll" or "interactive-poll" ? targetGate : null
        };
        using var caller = new CancellationTokenSource();
        using var unblockCancellation = new ManualResetEventSlim();
        var cancellationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = new LlmProviderService(source, [], [auth]);
        Task anchor = service.StartXaiLoginAsync();
        await anchorGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        CancellationToken ownerStop = auth.LastStartToken;
        auth.StartGate = boundary == "start" ? targetGate : null;
        Task operation = boundary switch
        {
            "start" => service.StartXaiLoginAsync(caller.Token),
            "prompt" or "interactive-poll" or "interactive-save" => service.SignInAsync("id",
                new Prompt(_ => { }, boundary == "prompt" ? targetGate : null), caller.Token),
            _ => CompleteAsync(service, auth.Code, caller.Token)
        };
        Task? retirement = null;
        CancellationTokenRegistration blocker = default;
        try
        {
            await targetGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            CancellationToken linked = boundary == "start" ? auth.LastStartToken : source.LastResolveToken;
            // Cancellation callbacks run newest first. Hold the owner callback before the linked lease callback.
            blocker = ownerStop.Register(() =>
            {
                cancellationEntered.TrySetResult();
                if (!unblockCancellation.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("The controlled owner cancellation callback was not released.");
            });
            retirement = service.DisposeAsync().AsTask();
            await cancellationEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(ownerStop.IsCancellationRequested);
            Assert.False(linked.IsCancellationRequested);
            Assert.False(retirement.IsCompleted);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => service.StartXaiLoginAsync());
            targetGate.Release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.False(linked.IsCancellationRequested);
            Assert.False(retirement.IsCompleted);
            bool saveEntered = boundary is "save" or "interactive-save";
            Assert.Equal(saveEntered ? 1 : 0, source.Saves);
            Assert.Equal(saveEntered ? "access-token" : null, LlmCredential.Parse(source.Saved)?.OAuth?.AccessToken);
            Assert.False(auth.Disposed);
        }
        finally
        {
            unblockCancellation.Set();
            targetGate.Release.TrySetResult();
            anchorGate.Release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => anchor.WaitAsync(TimeSpan.FromSeconds(10)));
            await (retirement ?? service.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(10));
            blocker.Dispose();
        }
    }

    [Theory]
    [InlineData("start")]
    [InlineData("resolve")]
    [InlineData("poll")]
    [InlineData("save")]
    public async Task CallerCancellationRejectsLateResultsWithoutRetiringTheFacade(string boundary)
    {
        var gate = new Gate();
        var source = new Source { ResolveGate = boundary == "resolve" ? gate : null, SaveGate = boundary == "save" ? gate : null };
        var auth = new Authenticator
        { StartGate = boundary == "start" ? gate : null, PollGate = boundary == "poll" ? gate : null };
        using var cancellation = new CancellationTokenSource();
        await using var service = new LlmProviderService(source, [], [auth]);
        Task operation = boundary == "start" ? service.StartXaiLoginAsync(cancellation.Token) : CompleteAsync(service, auth.Code, cancellation.Token);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        gate.Release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal(boundary == "save" ? 1 : 0, source.Saves);
        Assert.Equal(auth.Code, await service.StartXaiLoginAsync());
    }

    [Fact]
    public async Task MissingAuthenticatorIsAnExplicitUnavailablePath()
    {
        await using var service = new LlmProviderService(new Source(), []);
        await Assert.ThrowsAsync<NotSupportedException>(() => service.StartXaiLoginAsync());
    }

    [Fact]
    public void DeviceCodeDiagnosticsContainNoProtocolSecrets()
    {
        Assert.Equal("LlmDeviceCode { Redacted }", Code().ToString());
    }

    private static Task CompleteAsync(LlmProviderService service, LlmDeviceCode code, CancellationToken token = default) =>
        service.CompleteXaiLoginAsync("id", code.DeviceAuthId, code.UserCode, "version", token);
    private static LlmDeviceCode Code() => new("https://auth.x.ai/authorize", "user-code", "device-secret", 1)
    { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5) };

    private sealed class Gate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task WaitAsync()
        { Entered.TrySetResult(); return Release.Task; }
    }
    private sealed class Source : ILlmConnectionSource
    {
        public LlmConnection? Connection { get; set; } = new()
        { Version = "version", Transport = LlmTransport.XaiResponses, Endpoint = "https://api.x.ai/v1", Xai = new() };
        public Gate? SaveGate { get; init; }
        public Gate? ResolveGate { get; init; }
        public int Saves { get; private set; }
        public string? ExpectedVersion { get; private set; }
        public string? Saved { get; private set; }
        public CancellationToken LastResolveToken { get; private set; }
        public async Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
        { LastResolveToken = cancellationToken; Assert.Equal("id", connectionId); if (ResolveGate is { } gate) await gate.WaitAsync(); return Connection; }
        public async Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default)
        {
            Assert.Equal("id", connectionId);
            Saves++;
            if (SaveGate is { } gate) await gate.WaitAsync();
            if (Connection?.Version != expectedVersion) throw new InvalidOperationException("Connection changed.");
            ExpectedVersion = expectedVersion;
            Saved = credentialJson;
            Connection = Connection with { Credential = credentialJson, Version = "saved" };
        }
    }
    private sealed class Authenticator : IXaiDeviceLogin, IDisposable
    {
        public LlmTransport Transport => LlmTransport.XaiResponses;
        public LlmDeviceCode Code { get; init; } = XaiSignInTests.Code();
        public LlmCredential Credential { get; init; } = new() { OAuth = new() { AccessToken = "access-token" } };
        public Gate? StartGate { get; set; }
        public Gate? PollGate { get; init; }
        public LlmAuthPendingException? Pending { get; init; }
        public int Starts { get; private set; }
        public int Polls { get; private set; }
        public bool Disposed { get; private set; }
        public CancellationToken LastStartToken { get; private set; }
        public async Task<LlmDeviceCode> StartAsync(CancellationToken cancellationToken = default)
        { LastStartToken = cancellationToken; Starts++; if (StartGate is { } gate) await gate.WaitAsync(); return Code; }
        public async Task<LlmCredential> CompleteAsync(string deviceAuthId, string userCode, CancellationToken cancellationToken = default)
        {
            Assert.Equal(Code.DeviceAuthId, deviceAuthId);
            Assert.Equal(Code.UserCode, userCode);
            Polls++;
            if (PollGate is { } gate) await gate.WaitAsync();
            if (Pending is { } pending) throw pending;
            return Credential;
        }
        public Task<LlmCredential> EnsureFreshAsync(LlmCredential credential, CancellationToken cancellationToken = default) => Task.FromResult(credential);
        public void Dispose() => Disposed = true;
    }
    private sealed class Prompt(Action<LlmDeviceCode> shown, Gate? gate = null) : ILlmAuthPrompt
    {
        public int Calls { get; private set; }
        public async Task ShowDeviceCodeAsync(LlmDeviceCode code, CancellationToken cancellationToken = default)
        { Calls++; shown(code); if (gate is not null) await gate.WaitAsync(); }
    }
}
