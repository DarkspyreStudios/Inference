using Darkspyre.Inference.Connect.LLM.Vertex;
using Google.Apis.Auth.OAuth2;

namespace Darkspyre.Inference.Connect.LLM.Vertex.Tests;

public sealed class VertexAuthenticationTests
{
    private static LlmConnection Adc => new()
    {
        Transport = LlmTransport.GoogleVertexAi,
        Version = "1",
        Endpoint = "https://aiplatform.googleapis.com",
        Vertex = new() { ProjectId = "project-a", Location = "global" }
    };

    [Fact]
    public async Task AdcReplacementChangesScopeWhileConcurrentReadsShareCredentialOwnership()
    {
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, """{"type":"authorized_user","account":"first"}""");
            int created = 0;
            await using var authentication = new VertexAuthentication((_, _) =>
            { Interlocked.Increment(ref created); return GoogleCredential.FromAccessToken("fixture-token"); }, () => (path, true));
            var first = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => authentication.ResolveAsync(Adc, default)));
            Assert.Single(first.Select(access => access.Scope).Distinct(StringComparer.Ordinal));
            Assert.Equal(1, created);
            var versionOnly = await authentication.ResolveAsync(Adc with { Version = "new-host-version" }, default);
            Assert.Equal(first[0].Scope, versionOnly.Scope);
            await File.WriteAllTextAsync(path, """{"type":"authorized_user","account":"second"}""");
            var changed = await authentication.ResolveAsync(Adc, default);
            Assert.NotEqual(first[0].Scope, changed.Scope);
            Assert.Equal(2, created);
            var project = await authentication.ResolveAsync(Adc with { Vertex = Adc.Vertex! with { ProjectId = "project-b" } }, default);
            Assert.NotEqual(changed.Scope, project.Scope);
            var location = await authentication.ResolveAsync(Adc with { Vertex = Adc.Vertex! with { Location = "us-east5" } }, default);
            Assert.NotEqual(changed.Scope, location.Scope);
            Assert.Equal(2, created);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task CredentialErrorsNeverExposeSdkDiagnosticsOrTheKey()
    {
        await using var authentication = new VertexAuthentication((_, _) => throw new InvalidOperationException("secret-sdk-diagnostic"),
            () => ("unused", true));
        var connection = Adc with
        {
            Vertex = Adc.Vertex! with { AuthenticationMode = LlmGoogleAuthenticationMode.ServiceAccount },
            Credential = new LlmCredential { Google = new() { ServiceAccountJson = """{"type":"service_account","client_email":"fixture@example.invalid","private_key":"secret-fixture-key","token_uri":"https://oauth2.googleapis.com/token"}""" } }.Serialize()
        };
        var error = await Assert.ThrowsAsync<LlmProviderException>(() => authentication.ResolveAsync(connection, default));
        Assert.DoesNotContain("secret", error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
        Assert.Throws<ArgumentException>(() => VertexAuthentication.ValidateServiceAccount("""{"type":"service_account","token_uri":"https://example.invalid/token"}"""));
    }

    [Fact]
    public async Task CancellationBeforeFileAcquisitionPropagates()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await using var authentication = new VertexAuthentication((_, _) => throw new InvalidOperationException("must not create"), () => ("unread", true));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => authentication.ResolveAsync(Adc, cancelled.Token));
    }
}
