using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class LlmProviderServiceTests
{
    [Fact]
    public void ProviderCatalogRejectsDuplicateProviderIds()
    {
        LlmProviderDefinition first = Definition(
            "duplicate",
            LlmTransport.OpenAiResponses);
        LlmProviderDefinition second = Definition(
            "duplicate",
            LlmTransport.AnthropicMessages);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => new LlmProviderCatalog([first, second]));

        Assert.Contains("duplicate", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderCatalogRejectsDuplicateTransports()
    {
        LlmProviderDefinition first = Definition(
            "first",
            LlmTransport.OpenAiResponses);
        LlmProviderDefinition second = Definition(
            "second",
            LlmTransport.OpenAiResponses);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => new LlmProviderCatalog([first, second]));

        Assert.Contains(nameof(LlmTransport.OpenAiResponses), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServiceDispatchesOnlyToTheConfiguredTransport()
    {
        var responses = new StubClient(LlmTransport.OpenAiResponses);
        var compatible = new StubClient(LlmTransport.OpenAiCompatibleChat);
        var source = new StubConnectionSource(new LlmConnection
        {
            Version = "v1",
            Transport = LlmTransport.OpenAiCompatibleChat,
            Endpoint = "https://provider.test/v1"
        });
        var service = new LlmProviderService(source, [responses, compatible]);

        LlmChatResult result = await service.CompleteAsync(
            "configured-provider",
            new LlmChatRequest { ModelId = "model" });

        Assert.Equal("OpenAiCompatibleChat", result.Text);
        Assert.Equal(0, responses.StreamCalls);
        Assert.Equal(1, compatible.StreamCalls);
    }

    [Fact]
    public async Task ServiceDoesNotFallBackWhenConfiguredTransportFails()
    {
        var responses = new StubClient(LlmTransport.OpenAiResponses);
        var compatible = new StubClient(LlmTransport.OpenAiCompatibleChat) { Failure = new TestException() };
        var source = new StubConnectionSource(new LlmConnection
        {
            Version = "v1",
            Transport = LlmTransport.OpenAiCompatibleChat,
            Endpoint = "https://provider.test/v1"
        });
        var service = new LlmProviderService(source, [responses, compatible]);

        await Assert.ThrowsAsync<TestException>(() => service.CompleteAsync(
            "configured-provider",
            new LlmChatRequest { ModelId = "model" }));

        Assert.Equal(0, responses.StreamCalls);
        Assert.Equal(1, compatible.StreamCalls);
    }

    [Fact]
    public void ServiceRejectsDuplicateTransportRegistrations()
    {
        var source = new StubConnectionSource(new LlmConnection
        {
            Version = "v1",
            Transport = LlmTransport.OpenAiResponses,
            Endpoint = "https://provider.test/v1"
        });

        Assert.Throws<InvalidOperationException>(() => new LlmProviderService(
            source,
            [new StubClient(LlmTransport.OpenAiResponses), new StubClient(LlmTransport.OpenAiResponses)]));
    }

    [Fact]
    public async Task ServiceRedactsCredentialValuesAndSensitiveDiagnosticFieldsAtItsBoundary()
    {
        const string apiKey = "provider-service-api-key";
        const string headerSecret = "provider-service-header-secret";
        var client = new StubClient(LlmTransport.OpenAiResponses)
        {
            Diagnostics = new LlmProviderDiagnostics(
                200,
                $"request-{apiKey}",
                JsonSerializer.Serialize(new
                {
                    authorization = $"Bearer {apiKey}",
                    nested = new { value = headerSecret }
                }))
        };
        var source = new StubConnectionSource(new LlmConnection
        {
            Version = "v1",
            Transport = LlmTransport.OpenAiResponses,
            Endpoint = "https://provider.test/v1",
            Credential = new LlmCredential
            {
                ApiKey = apiKey,
                SecretHeaders = new Dictionary<string, string> { ["x-private"] = headerSecret }
            }.Serialize()
        });
        var service = new LlmProviderService(source, [client]);

        LlmChatResult result = await service.CompleteAsync(
            "configured-provider",
            new LlmChatRequest { ModelId = "model" });

        string diagnostics = $"{result.Diagnostics.RequestId}|{result.Diagnostics.RawFinalResponseJson}";
        Assert.DoesNotContain(apiKey, diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain(headerSecret, diagnostics, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshCannotOverwriteAConcurrentCredentialChange()
    {
        var source = new MutableConnectionSource(Connection(
            "v1",
            new LlmCredential { OAuth = Tokens("expired") }.Serialize(),
            LlmTransport.AnthropicMessages));
        var authenticator = new BlockingAuthenticator(LlmTransport.AnthropicMessages);
        var client = new StubClient(LlmTransport.AnthropicMessages);
        var service = new LlmProviderService(source, [client], [authenticator]);

        Task<IReadOnlyList<LlmModel>> pending = service.ListModelsAsync("configured-provider");
        await authenticator.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        source.Replace(Connection(
            "v2",
            new LlmCredential { OAuth = Tokens("replacement") }.Serialize(),
            LlmTransport.AnthropicMessages));
        authenticator.Release.TrySetResult();

        await Assert.ThrowsAsync<ConnectionChangedException>(() => pending);
        Assert.Equal(0, client.ListCalls);
        Assert.Equal("replacement", LlmCredential.Parse(source.Connection.Credential)?.OAuth?.AccessToken);
    }

    [Fact]
    public async Task StreamingCanBypassRefreshWhenClientDoesNotUseStoredOAuthToken()
    {
        var source = new StubConnectionSource(Connection(
            "v1",
            new LlmCredential { OAuth = Tokens("expired") }.Serialize(),
            LlmTransport.AnthropicMessages));
        var client = new StubClient(LlmTransport.AnthropicMessages)
        {
            RequiresFreshOAuthForStreaming = false
        };
        var service = new LlmProviderService(
            source,
            [client],
            [new ThrowingRefreshAuthenticator()]);

        LlmChatResult result = await service.CompleteAsync(
            "configured-provider",
            new LlmChatRequest { ModelId = "claude-test" });

        Assert.Equal("AnthropicMessages", result.Text);
        Assert.Equal(1, client.StreamCalls);
    }

    [Fact]
    public async Task AccountUsageDispatchesWithoutRefreshingCliOwnedCredential()
    {
        var source = new StubConnectionSource(Connection(
            "v1",
            new LlmCredential { OAuth = Tokens("expired") }.Serialize(),
            LlmTransport.AnthropicMessages));
        var client = new StubClient(LlmTransport.AnthropicMessages)
        {
            SupportsAccountUsage = true
        };
        var service = new LlmProviderService(
            source,
            [client],
            [new ThrowingRefreshAuthenticator()]);

        LlmAccountUsageSnapshot usage = await service.GetAccountUsageAsync("configured-provider");

        Assert.Equal("test", usage.Plan);
        Assert.Equal(1, client.AccountUsageCalls);
    }

    [Fact]
    public async Task AccountUsageRejectsAdaptersThatDoNotAdvertiseSupport()
    {
        var source = new StubConnectionSource(Connection("v1"));
        var service = new LlmProviderService(
            source,
            [new StubClient(LlmTransport.OpenAiCodex)]);

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            service.GetAccountUsageAsync("configured-provider"));
    }

    [Fact]
    public async Task InteractiveSignInCannotSaveAfterTheConnectionChanges()
    {
        var source = new MutableConnectionSource(Connection("v1"));
        var authenticator = new BlockingAuthenticator(LlmTransport.OpenAiCodex);
        var service = new LlmProviderService(
            source,
            [new StubClient(LlmTransport.OpenAiCodex)],
            [authenticator]);

        Task pending = service.SignInAsync(
            "configured-provider",
            new StubAuthPrompt());
        await authenticator.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        source.Replace(Connection("v2"));
        authenticator.Release.TrySetResult();

        await Assert.ThrowsAsync<ConnectionChangedException>(() => pending);
        Assert.Null(source.Connection.Credential);
    }

    [Fact]
    public async Task CredentialSettersRejectAStaleVersionAndSaveAtTheExpectedOne()
    {
        var source = new MutableConnectionSource(Connection("v2", null, LlmTransport.OpenAiResponses));
        var service = new LlmProviderService(source, [new StubClient(LlmTransport.OpenAiResponses)], []);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetApiKeyAsync("configured-provider", "key", "v1"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetBedrockCredentialsAsync(
            "configured-provider", new LlmAwsCredentials { Profile = "profile", Region = "us-east-1" }, "v1"));
        Assert.Null(source.Connection.Credential);

        await service.SetApiKeyAsync("configured-provider", "key", "v2");
        Assert.Equal("key", LlmCredential.Parse(source.Connection.Credential)?.ApiKey);
    }

    [Fact]
    public async Task ExplicitCompletionRejectsAStaleVersionBeforeTokenExchange()
    {
        var source = new MutableConnectionSource(Connection("v2"));
        var authenticator = new BlockingAuthenticator(LlmTransport.OpenAiCodex);
        var service = new LlmProviderService(
            source,
            [new StubClient(LlmTransport.OpenAiCodex)],
            [authenticator]);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CompleteCodexLoginAsync(
                "configured-provider",
                "device-secret",
                "user-code",
                "v1"));

        Assert.Contains("changed during authentication", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, authenticator.CompleteCalls);
    }

    private static LlmProviderDefinition Definition(string id, LlmTransport transport) => new()
    {
        Id = id,
        DisplayName = id,
        Description = "Test provider",
        Transport = transport,
        DefaultEndpoint = "https://example.test",
        SetupSteps = []
    };

    private static LlmConnection Connection(
        string version,
        string? credential = null,
        LlmTransport transport = LlmTransport.OpenAiCodex) => new()
        {
            Version = version,
            Transport = transport,
            Endpoint = "https://provider.test/v1",
            Credential = credential
        };

    private static LlmOAuthTokens Tokens(string accessToken) => new()
    {
        AccessToken = accessToken,
        RefreshToken = "refresh-token",
        ExpiresAtUnixSeconds = 0
    };

    private sealed class StubConnectionSource(LlmConnection? connection) : ILlmConnectionSource
    {
        public Task<LlmConnection?> GetConnectionAsync(
            string connectionId,
            CancellationToken cancellationToken = default) => Task.FromResult<LlmConnection?>(connection);

        public Task SaveCredentialAsync(
            string connectionId,
            string credentialJson,
            string expectedVersion,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class MutableConnectionSource(LlmConnection connection) : ILlmConnectionSource
    {
        private readonly Lock _gate = new();

        public LlmConnection Connection { get; private set; } = connection;

        public Task<LlmConnection?> GetConnectionAsync(
            string connectionId,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                return Task.FromResult<LlmConnection?>(Connection);
            }
        }

        public Task SaveCredentialAsync(
            string connectionId,
            string credentialJson,
            string expectedVersion,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (!string.Equals(Connection.Version, expectedVersion, StringComparison.Ordinal))
                {
                    throw new ConnectionChangedException();
                }

                Connection = Connection with
                {
                    Version = $"{Connection.Version}-saved",
                    Credential = credentialJson
                };
                return Task.CompletedTask;
            }
        }

        public void Replace(LlmConnection replacement)
        {
            lock (_gate)
            {
                Connection = replacement;
            }
        }
    }

    private sealed class BlockingAuthenticator(LlmTransport transport) : ICodexDeviceLogin
    {
        public TaskCompletionSource Entered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int CompleteCalls { get; private set; }

        public LlmTransport Transport => transport;

        public async Task<LlmCredential> EnsureFreshAsync(
            LlmCredential credential,
            CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new LlmCredential { OAuth = Tokens("fresh") };
        }

        public Task<LlmDeviceCode> StartAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new LlmDeviceCode(
                "https://login.test",
                "user-code",
                "device-secret",
                0));

        public async Task<LlmCredential> CompleteAsync(
            string deviceAuthId,
            string userCode,
            CancellationToken cancellationToken = default)
        {
            CompleteCalls++;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new LlmCredential { OAuth = Tokens("signed-in") };
        }
    }

    private sealed class StubAuthPrompt : ILlmAuthPrompt
    {
        public Task ShowDeviceCodeAsync(
            LlmDeviceCode code,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StubClient(LlmTransport transport)
        : ILlmClient, ILlmAccountUsageClient, IStreamingCredentialRefreshPolicy
    {
        public Exception? Failure { get; init; }

        public int StreamCalls { get; private set; }

        public int ListCalls { get; private set; }

        public int AccountUsageCalls { get; private set; }

        public bool SupportsAccountUsage { get; init; }

        public bool RequiresFreshOAuthForStreaming { get; init; } = true;

        public LlmProviderDiagnostics Diagnostics { get; init; } = new(200, null, null);

        public LlmTransport Transport => transport;

        public bool RequiresFreshOAuthCredentialForStreaming(LlmConnection connection) =>
            RequiresFreshOAuthForStreaming;

        public LlmProviderDefinition ProviderDefinition => new()
        {
            Id = transport.ToString(),
            DisplayName = transport.ToString(),
            Description = "Test provider",
            Transport = transport,
            DefaultEndpoint = "https://example.test/v1",
            SetupSteps = []
        };

        public LlmProviderCapabilities GetCapabilities(LlmConnection connection, string? modelId = null) => new()
        {
            SupportsStreaming = true,
            SupportsAccountUsage = SupportsAccountUsage
        };

        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            LlmConnection connection,
            LlmChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            StreamCalls++;
            if (Failure is not null)
            {
                throw Failure;
            }

            yield return new LlmChatCompletionEvent(new LlmChatResult
            {
                Text = Transport.ToString(),
                Status = LlmCompletionStatus.Completed,
                Diagnostics = Diagnostics
            });
            await Task.CompletedTask;
        }

        public Task<IReadOnlyList<LlmModel>> ListModelsAsync(
            LlmConnection connection,
            CancellationToken cancellationToken = default)
        {
            ListCalls++;
            return Task.FromResult<IReadOnlyList<LlmModel>>([]);
        }

        public Task<LlmAccountUsageSnapshot> GetAccountUsageAsync(
            LlmConnection connection,
            CancellationToken cancellationToken = default)
        {
            AccountUsageCalls++;
            return Task.FromResult(new LlmAccountUsageSnapshot
            {
                RetrievedAt = DateTimeOffset.UtcNow,
                Plan = "test"
            });
        }
    }

    private sealed class ThrowingRefreshAuthenticator : ILlmAuthenticator
    {
        public LlmTransport Transport => LlmTransport.AnthropicMessages;

        public Task<LlmCredential> EnsureFreshAsync(
            LlmCredential credential,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Streaming attempted to refresh the catalog credential.");
    }

    private sealed class TestException : Exception;

    private sealed class ConnectionChangedException : Exception;
}
