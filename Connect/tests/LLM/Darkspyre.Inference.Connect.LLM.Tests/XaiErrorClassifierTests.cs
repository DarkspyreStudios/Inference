using System.Net;
using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class XaiErrorClassifierTests
{
    // These are controlled protocol fixtures, not live-qualified provider recordings.
    [Theory]
    [InlineData(401, "{}")]
    [InlineData(403, "{\"error\":{\"message\":\"The OAuth2 access token could not be validated\"}}")]
    [InlineData(403, "{\"message\":\"The OAuth2 access token could not be validated\"}")]
    [InlineData(403, "The OAuth2 access token could not be validated")]
    [InlineData(400, "{\"error\":\"invalid_grant\"}")]
    [InlineData(400, "{\"error\":\"invalid_token\"}")]
    public async Task InvalidAuthorizationHasOneNamedFailure(int status, string body)
    {
        using var response = Response(status, body);
        InferenceException error = await Assert.ThrowsAsync<InferenceException>(() => XaiErrorClassifier.ThrowIfUnsuccessfulAsync(response));
        Assert.Equal(InferenceFailureCode.AuthenticationRequired, error.Code);
        LlmProviderException provider = Assert.IsType<LlmProviderException>(error.InnerException);
        Assert.Equal("xai_authentication_required", provider.ProviderCode);
        Assert.Equal((HttpStatusCode)status, provider.StatusCode);
        Assert.Equal(LlmTransport.XaiResponses, provider.Transport);
    }

    [Fact]
    public async Task PaymentFailureUsesProviderSafeMessageAndNamedEntitlementCode()
    {
        using var response = Response(402, "{\"message\":\"Subscription or credits required\"}");
        InferenceException error = await Assert.ThrowsAsync<InferenceException>(() => XaiErrorClassifier.ThrowIfUnsuccessfulAsync(response));
        Assert.Equal(InferenceFailureCode.AccessDenied, error.Code);
        Assert.Contains("Subscription or credits required", error.Message);
        Assert.Equal("xai_entitlement_required", Assert.IsType<LlmProviderException>(error.InnerException).ProviderCode);
    }

    [Theory]
    [InlineData(403, "{\"message\":\"Team permission denied\"}")]
    [InlineData(403, "{\"message\":\"Grok subscription required\"}")]
    [InlineData(403, "{\"hint\":\"The OAuth2 access token could not be validated\",\"message\":\"Other failure\"}")]
    [InlineData(403, "{\"message\":\"Prefix The OAuth2 access token could not be validated suffix\"}")]
    [InlineData(429, "{\"error\":\"rate_limit\"}")]
    [InlineData(500, "unavailable")]
    public async Task UnqualifiedOrUnrelatedErrorsKeepSafeProviderFailure(int status, string body)
    {
        using var response = Response(status, body);
        LlmProviderException error = await Assert.ThrowsAsync<LlmProviderException>(() => XaiErrorClassifier.ThrowIfUnsuccessfulAsync(response));
        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.Null(error.ProviderCode);
        Assert.NotEmpty(error.SanitizedResponseBody!);
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("json")]
    [InlineData("array")]
    public async Task FamilyAuthorizationRedactsEscapedReflectionsBeforeSerialization(string shape)
    {
        const string secret = "secret\"with\\escapes";
        string body = shape == "plain" ? "reflected " + secret
            : shape == "json" ? JsonSerializer.Serialize(new { message = "reflected " + secret, access_token = "unknown-token", device_code = "unknown-device", user_code = "unknown-code" })
            : JsonSerializer.Serialize(new[] { "reflected " + secret });
        using var response = Response(403, body);
        LlmProviderException error = await Assert.ThrowsAsync<LlmProviderException>(() => XaiErrorClassifier.ThrowIfUnsuccessfulAsync(
            response, new ProviderAuthorizationHeader("Bearer", secret)));
        string safe = error.ToString() + error.SanitizedResponseBody;
        Assert.DoesNotContain(secret, safe);
        Assert.DoesNotContain("with", safe);
        Assert.DoesNotContain("unknown-token", safe);
        Assert.DoesNotContain("unknown-device", safe);
        Assert.DoesNotContain("unknown-code", safe);
        Assert.Contains("[REDACTED]", safe);
    }

    [Fact]
    public async Task HtmlAndLongFailuresAreBoundedAndSafe()
    {
        using var html = Response(502, "<html>secret</html>");
        LlmProviderException error = await Assert.ThrowsAsync<LlmProviderException>(() => XaiErrorClassifier.ThrowIfUnsuccessfulAsync(html));
        Assert.DoesNotContain("secret", error.ToString());
        using var longBody = Response(500, new string('x', 5000));
        error = await Assert.ThrowsAsync<LlmProviderException>(() => XaiErrorClassifier.ThrowIfUnsuccessfulAsync(longBody));
        Assert.Equal(4096, error.SanitizedResponseBody!.Length);
    }

    [Fact]
    public async Task SuccessDoesNotReadBodyAndCancellationStillWins()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        await XaiErrorClassifier.ThrowIfUnsuccessfulAsync(response);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => XaiErrorClassifier.ThrowIfUnsuccessfulAsync(response, cancellationToken: cancellation.Token));
    }

    [Fact]
    public void SharedRedactorPreservesSafeStructureAndAllCredentialKinds()
    {
        var credential = new LlmCredential
        {
            ApiKey = "key-secret",
            SecretHeaders = new Dictionary<string, string> { ["Header"] = "header-secret" },
            OAuth = new() { AccessToken = "access-secret", RefreshToken = "refresh-secret", IdToken = "id-secret" },
            Aws = new() { AccessKeyId = "aws-id", SecretAccessKey = "aws-key", SessionToken = "aws-session" }
        };
        string[] secrets = ["key-secret", "header-secret", "access-secret", "refresh-secret", "id-secret", "aws-id", "aws-key", "aws-session"];
        string safe = SecretRedactor.Sanitize(JsonSerializer.Serialize(new { items = secrets, safe = 42 }), credential);
        Assert.All(secrets, secret => Assert.DoesNotContain(secret, safe));
        using JsonDocument document = JsonDocument.Parse(safe);
        Assert.Equal(42, document.RootElement.GetProperty("safe").GetInt32());
        Assert.All(document.RootElement.GetProperty("items").EnumerateArray(), item => Assert.Equal("[REDACTED]", item.GetString()));
    }

    [Fact]
    public void SharedRedactorMutatesNestedContainersWithoutReparentingThem()
    {
        string body = """
            {"items":[{"message":"secret\"with\\escapes","nested":[{"access_token":"hidden","safe":42},null,true]}],"details":{"message":"safe"}}
            """;
        string safe = SecretRedactor.Sanitize(body, null, ["secret\"with\\escapes"]);
        using JsonDocument document = JsonDocument.Parse(safe);
        JsonElement item = document.RootElement.GetProperty("items")[0];
        Assert.Equal("[REDACTED]", item.GetProperty("message").GetString());
        Assert.Equal("[REDACTED]", item.GetProperty("nested")[0].GetProperty("access_token").GetString());
        Assert.Equal(42, item.GetProperty("nested")[0].GetProperty("safe").GetInt32());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("nested")[1].ValueKind);
        Assert.True(item.GetProperty("nested")[2].GetBoolean());
        Assert.Equal("safe", document.RootElement.GetProperty("details").GetProperty("message").GetString());
    }

    private static HttpResponseMessage Response(int status, string body) =>
        new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
