using System.Net;
using Darkspyre.Inference.Connect.LLM.Internal;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.LLM.Azure.Tests;

public sealed class AzureBoundaryTests
{
    [Fact]
    public void TransportDiscriminatorsPreserveExistingIdsAndReservedGrokSlot()
    {
        (LlmTransport Transport, int Id)[] existing =
        [
            (LlmTransport.OpenAiResponses, 0), (LlmTransport.OpenAiCompatibleChat, 1),
            (LlmTransport.AnthropicMessages, 2), (LlmTransport.OpenAiCodex, 3),
            (LlmTransport.AwsBedrockConverse, 4), (LlmTransport.GoogleAntigravityCli, 6),
            (LlmTransport.AnthropicClaudeCli, 7), (LlmTransport.AwsBedrockAgentCore, 8),
            (LlmTransport.SelfHost, 9), (LlmTransport.AppleFoundationModels, 10),
            (LlmTransport.GoogleVertexAi, 11), (LlmTransport.GoogleGeminiApi, 12)
        ];
        foreach (var item in existing) Assert.Equal(item.Id, (int)item.Transport);
        Assert.Equal(14, (int)LlmTransport.AzureOpenAiResponses);
        Assert.Equal(15, (int)LlmTransport.AzureOpenAiChat);
        Assert.NotEqual(13, (int)LlmTransport.AzureOpenAiResponses);
        Assert.Equal(Enum.GetValues<LlmTransport>().Length, Enum.GetValues<LlmTransport>().Distinct().Count());
    }

    [Theory]
    [InlineData("http://resource.openai.azure.com")]
    [InlineData("https://resource.openai.azure.com/openai/v1")]
    [InlineData("https://resource.openai.azure.com/openai/openai/v1")]
    [InlineData("https://resource.openai.azure.com/openai/deployments/name")]
    [InlineData("https://resource.openai.azure.com/?api-version=v1")]
    [InlineData("https://resource.openai.azure.com/#fragment")]
    [InlineData("https://user:secret@resource.openai.azure.com")]
    [InlineData("https://resource.openai.azure.com/../")]
    [InlineData("https://resource.openai.azure.com/%2e%2e/")]
    public async Task InvalidAuthoritiesFailBeforeDispatch(string endpoint)
    {
        foreach (bool responses in new[] { true, false })
        {
            using var handler = new AzureProtocolTests.Handler(responses, false);
            using var http = new HttpClient(handler);
            await Assert.ThrowsAsync<ArgumentException>(() => AzureProtocolTests.Complete(AzureProtocolTests.Client(http, responses),
                AzureProtocolTests.Connection(responses, false) with { Endpoint = endpoint }, AzureProtocolTests.Request));
            Assert.Equal(0, handler.Calls);
        }
    }

    [Theory]
    [InlineData("Authorization", false)]
    [InlineData("aUtHoRiZaTiOn", true)]
    [InlineData("api-key", false)]
    [InlineData("API-KEY", true)]
    public async Task BothAuthHeaderBagsAreReservedCaseInsensitively(string header, bool secret)
    {
        foreach (bool responses in new[] { true, false })
        {
            using var handler = new AzureProtocolTests.Handler(responses, false);
            using var http = new HttpClient(handler);
            LlmConnection connection = AzureProtocolTests.Connection(responses, false);
            connection = secret ? connection with
            { Credential = new LlmCredential { ApiKey = "protected-key", SecretHeaders = new Dictionary<string, string> { [header] = "injected" } }.Serialize() }
                : connection with { AdditionalHeaders = new Dictionary<string, string> { [header] = "injected" } };
            await Assert.ThrowsAsync<ArgumentException>(() => AzureProtocolTests.Complete(AzureProtocolTests.Client(http, responses), connection, AzureProtocolTests.Request));
            Assert.Equal(0, handler.Calls);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ForeignCredentialsAndMissingKeysNeverDispatch(bool responses)
    {
        using var handler = new AzureProtocolTests.Handler(responses, false);
        using var http = new HttpClient(handler);
        foreach (LlmCredential credential in new LlmCredential[]
        {
            new(),
            new() { ApiKey = "key", OAuth = new() { AccessToken = "token" } },
            new() { ApiKey = "key", Aws = new() },
            new() { ApiKey = "key", Google = new() { ServiceAccountJson = "{}" } },
            new() { ApiKey = "key", Azure = new LlmAzureSecret.ClientSecret("protected-secret") },
            new() { ApiKey = "key", Azure = new LlmAzureSecret.CertificatePkcs12("protected-certificate") },
            new() { ApiKey = "key\ninvalid" }
        })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => AzureProtocolTests.Complete(AzureProtocolTests.Client(http, responses),
                AzureProtocolTests.Connection(responses, false) with { Credential = credential.Serialize() }, AzureProtocolTests.Request));
        }
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnimplementedConfigurationCannotUseTheExistingV1Route(bool responses)
    {
        using var handler = new AzureProtocolTests.Handler(responses, false);
        using var http = new HttpClient(handler);
        LlmConnection connection = AzureProtocolTests.Connection(responses, false);
        foreach (LlmAzureConfiguration configuration in new[]
        {
            connection.Azure! with { ApiVersion = "2025-01-01" },
            connection.Azure! with { Entra = new(LlmAzureCloud.Public, LlmAzureTokenAudience.Foundry,
                new LlmAzureIdentitySelection.SystemAssignedManagedIdentity()) }
        })
            await Assert.ThrowsAsync<NotSupportedException>(() => AzureProtocolTests.Complete(
                AzureProtocolTests.Client(http, responses), connection with { Azure = configuration }, AzureProtocolTests.Request));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ForeignConfigurationAndUnknownModesNeverDispatch(bool responses)
    {
        using var handler = new AzureProtocolTests.Handler(responses, false);
        using var http = new HttpClient(handler);
        LlmConnection connection = AzureProtocolTests.Connection(responses, false);
        foreach (LlmConnection invalid in new[]
        {
            connection with { Azure = null },
            connection with { Azure = connection.Azure! with { ApiMode = (LlmAzureApiMode)999 } },
            connection with { Azure = connection.Azure! with { AuthenticationMode = (LlmAzureAuthenticationMode)999 } },
            connection with { Azure = connection.Azure! with { DeploymentId = " " } }
        })
            await Assert.ThrowsAsync<ArgumentException>(() => AzureProtocolTests.Complete(AzureProtocolTests.Client(http, responses), invalid, AzureProtocolTests.Request));
        await Assert.ThrowsAsync<NotSupportedException>(() => AzureProtocolTests.Complete(AzureProtocolTests.Client(http, responses),
            connection with { Vertex = new() }, AzureProtocolTests.Request));
        if (responses)
            await Assert.ThrowsAsync<NotSupportedException>(() => AzureProtocolTests.Complete(AzureProtocolTests.Client(http, responses),
                connection with { ChatCompletions = new() }, AzureProtocolTests.Request));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public void OptionalRegistrationConfiguresTheActualDefaultHandlersWithoutRedirects()
    {
        var services = new ServiceCollection();
        services.AddLlmConnectAzure();
        using ServiceProvider container = services.BuildServiceProvider();
        var factory = container.GetRequiredService<IHttpMessageHandlerFactory>();
        foreach (string name in new[] { nameof(AzureResponsesLlmClient), nameof(AzureChatLlmClient) })
        {
            HttpMessageHandler handler = factory.CreateHandler(name);
            while (handler is DelegatingHandler delegating) handler = delegating.InnerHandler!;
            Assert.False(Assert.IsType<HttpClientHandler>(handler).AllowAutoRedirect);
        }
        Assert.Equal(2, container.GetServices<ILlmClient>().Count());
        Assert.Equal(2, container.GetServices<ILlmSetupEnvironment>().Count());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OptionalClientsAreSelectedThroughTheExistingFacade(bool responses)
    {
        using var handler = new AzureProtocolTests.Handler(responses, false);
        var source = new Source(AzureProtocolTests.Connection(responses, false));
        var services = new ServiceCollection();
        services.AddLlmConnect().AddLlmConnectAzure();
        services.AddSingleton<ILlmConnectionSource>(source);
        services.AddHttpClient(responses ? nameof(AzureResponsesLlmClient) : nameof(AzureChatLlmClient))
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        await using ServiceProvider container = services.BuildServiceProvider();
        await using AsyncServiceScope scope = container.CreateAsyncScope();
        var facade = scope.ServiceProvider.GetRequiredService<LlmProviderService>();
        LlmChatResult? result = null;
        await foreach (var item in facade.StreamAsync("azure", AzureProtocolTests.Request))
            if (item is LlmChatCompletionEvent completion) result = completion.Result;
        Assert.Equal("ok", result!.Text);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(0, source.Writes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RedirectResponsesAreRefusedWithoutAdapterRetry(bool responses)
    {
        using var handler = new FailureHandler(HttpStatusCode.Redirect);
        using var http = new HttpClient(handler);
        LlmProviderException error = await Assert.ThrowsAsync<LlmProviderException>(() => AzureProtocolTests.Complete(
            AzureProtocolTests.Client(http, responses), AzureProtocolTests.Connection(responses, false), AzureProtocolTests.Request));
        Assert.Equal(HttpStatusCode.Redirect, error.StatusCode);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProviderErrorsRedactTheKeyAndPreserveSafeRequestMetadata(bool responses)
    {
        using var handler = new FailureHandler(HttpStatusCode.Unauthorized);
        using var http = new HttpClient(handler);
        LlmProviderException error = await Assert.ThrowsAsync<LlmProviderException>(() => AzureProtocolTests.Complete(
            AzureProtocolTests.Client(http, responses), AzureProtocolTests.Connection(responses, false), AzureProtocolTests.Request));
        Assert.DoesNotContain("protected-key", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("protected-key", error.SanitizedResponseBody ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal("azure-request", error.RequestId);
        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
    }

    [Fact]
    public void NonAzureScopeBytesRemainUnchanged()
    {
        var identity = new ConnectContinuationScopeIdentity(LlmTransport.OpenAiResponses, "https://endpoint.test", "key", null, [], []);
        string json = System.Text.Json.JsonSerializer.Serialize(identity, ConnectLlmJsonContext.Default.ConnectContinuationScopeIdentity);
        Assert.Equal("{\"Transport\":0,\"Endpoint\":\"https://endpoint.test\",\"ApiKey\":\"key\",\"OAuthIdentity\":null,\"Headers\":[],\"SecretHeaders\":[]}", json);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AzureScopeUsesCanonicalAuthorityAndIgnoresTheConcurrencyVersion(bool responses)
    {
        LlmConnection connection = AzureProtocolTests.Connection(responses, false);
        string scope = ProviderContinuationStateCodec.Scope(connection);
        Assert.Equal(scope, ProviderContinuationStateCodec.Scope(connection with { Endpoint = "https://RESOURCE.OPENAI.AZURE.COM/", Version = "v2" }));
        Assert.NotEqual(scope, ProviderContinuationStateCodec.Scope(connection with { Azure = connection.Azure! with { DeploymentId = "other" } }));
        Assert.NotEqual(scope, ProviderContinuationStateCodec.Scope(connection with { Transport = responses ? LlmTransport.AzureOpenAiChat : LlmTransport.AzureOpenAiResponses }));
    }

    private sealed class FailureHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var response = new HttpResponseMessage(status) { Content = new StringContent("{\"message\":\"protected-key\"}") };
            response.Headers.Add("apim-request-id", "azure-request");
            response.Headers.Location = new Uri("https://other.test/credentials");
            return Task.FromResult(response);
        }
    }

    private sealed class Source(LlmConnection connection) : ILlmConnectionSource
    {
        public int Writes { get; private set; }
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) => Task.FromResult<LlmConnection?>(connection);
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default)
        {
            Writes++;
            throw new InvalidOperationException("API-key generation must not persist credentials.");
        }
    }
}
