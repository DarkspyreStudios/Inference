using System.Net;
using System.Text;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class XaiAuthorizationTests
{
    public static TheoryData<string> InvalidCredentials => new()
    {
        "api-key", "secret-header", "aws", "google", "azure-secret", "azure-certificate",
        "copilot", "copilot-runtime", "empty-token", "whitespace-token", "newline-token", "nul-token"
    };

    [Theory]
    [MemberData(nameof(InvalidCredentials))]
    public async Task FamilyRefusesKnownInvalidCredentialBeforeRefreshOrSave(string kind)
    {
        var source = new Source { Connection = Connection(Invalid(kind)) };
        var authenticator = new Authenticator();
        await using var access = new LlmConnectionAccess(source, [authenticator]);
        var binding = new XaiFamilyBinding(new("connection"), access);
        Exception? error = await Record.ExceptionAsync(() => binding.AcquireAsync());
        Assert.Equal(0, authenticator.Calls);
        Assert.Equal(0, source.Writes);
        AssertRefusal(error, kind);
    }

    [Theory]
    [MemberData(nameof(InvalidCredentials))]
    public async Task FamilyRefusesIncompatibleRefreshedCredentialBeforeSave(string kind)
    {
        var source = new Source { Connection = Connection(OAuth()) };
        var authenticator = new Authenticator { Result = Invalid(kind) };
        await using var access = new LlmConnectionAccess(source, [authenticator]);
        var binding = new XaiFamilyBinding(new("connection"), access);
        Exception? error = await Record.ExceptionAsync(() => binding.Authorization.GetAsync().AsTask());
        Assert.Equal(1, authenticator.Calls);
        Assert.Equal(0, source.Writes);
        AssertRefusal(error, kind);
    }

    [Theory]
    [MemberData(nameof(InvalidCredentials))]
    public async Task ResponsesRefusesSameInvalidCredentialBeforeHttp(string kind)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        Exception? error = await Record.ExceptionAsync(async () =>
        {
            await foreach (LlmStreamEvent _ in new XaiResponsesLlmClient(http).StreamAsync(
                Connection(Invalid(kind)), new() { ModelId = "grok-4", Messages = [new("user", "hello")] })) { }
        });
        Assert.Equal(0, handler.Calls);
        AssertRefusal(error, kind);
    }

    [Fact]
    public async Task ValidFamilyRefreshKeepsOneSourceAndOriginalExpectedVersion()
    {
        var source = new Source { Connection = Connection(OAuth()) };
        var authenticator = new Authenticator { Result = OAuth() with { OAuth = new() { AccessToken = "fresh-token" } } };
        await using var access = new LlmConnectionAccess(source, [authenticator]);
        var binding = new XaiFamilyBinding(new("connection"), access);
        ProviderAuthorizationHeader authorization = await binding.Authorization.GetAsync();
        Assert.Equal("Bearer", authorization.Scheme);
        Assert.Equal("fresh-token", authorization.Value);
        Assert.Equal(1, source.Reads);
        Assert.Equal(1, authenticator.Calls);
        Assert.Equal(1, source.Writes);
        Assert.Equal("version", source.ExpectedVersion);
    }

    private static void AssertRefusal(Exception? error, string kind)
    {
        if (kind is "copilot" or "copilot-runtime")
            Assert.IsType<NotSupportedException>(error);
        else
            Assert.Equal(InferenceFailureCode.AuthenticationRequired, Assert.IsType<InferenceException>(error).Code);
        Assert.DoesNotContain("original-token", error!.ToString());
        Assert.DoesNotContain("other-secret", error.ToString());
    }

    private static LlmCredential OAuth() => new() { OAuth = new() { AccessToken = "original-token" } };

    private static LlmCredential Invalid(string kind) => kind switch
    {
        "api-key" => OAuth() with { ApiKey = "other-secret" },
        "secret-header" => OAuth() with { SecretHeaders = new Dictionary<string, string> { ["X-Key"] = "other-secret" } },
        "aws" => OAuth() with { Aws = new() { SecretAccessKey = "other-secret" } },
        "google" => OAuth() with { Google = new() { ServiceAccountJson = "other-secret" } },
        "azure-secret" => OAuth() with { Azure = new LlmAzureSecret.ClientSecret("other-secret") },
        "azure-certificate" => OAuth() with { Azure = new LlmAzureSecret.CertificatePkcs12("other-secret", "other-secret") },
        "copilot" => OAuth() with { Copilot = new() { AccessToken = "other-secret" } },
        "copilot-runtime" => OAuth() with { CopilotRuntime = new() { ConnectionToken = "other-secret" } },
        "empty-token" => OAuth() with { OAuth = new() { AccessToken = "" } },
        "whitespace-token" => OAuth() with { OAuth = new() { AccessToken = "bad token" } },
        "newline-token" => OAuth() with { OAuth = new() { AccessToken = "token\r\nnew" } },
        "nul-token" => OAuth() with { OAuth = new() { AccessToken = "token\0new" } },
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static LlmConnection Connection(LlmCredential credential) => new()
    {
        Transport = LlmTransport.XaiResponses,
        Endpoint = "https://api.x.ai/v1",
        Version = "version",
        Credential = credential.Serialize()
    };

    private sealed class Source : ILlmConnectionSource
    {
        public required LlmConnection Connection { get; init; }
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public string? ExpectedVersion { get; private set; }
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
        {
            Reads++;
            return Task.FromResult<LlmConnection?>(Connection);
        }
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion,
            CancellationToken cancellationToken = default)
        {
            Writes++;
            ExpectedVersion = expectedVersion;
            return Task.CompletedTask;
        }
    }

    private sealed class Authenticator : ILlmAuthenticator
    {
        public LlmTransport Transport => LlmTransport.XaiResponses;
        public LlmCredential Result { get; init; } = OAuth();
        public int Calls { get; private set; }
        public Task<LlmCredential> EnsureFreshAsync(LlmCredential credential, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Result);
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
        }
    }
}
