using System.Net;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class XaiSpeechDiagnosticTests
{
    [Fact]
    public void ExistingClassifierSignaturesStayFixed()
    {
        Func<HttpResponseMessage, ProviderAuthorizationHeader?, CancellationToken, Task> publicEntry = XaiErrorClassifier.ThrowIfUnsuccessfulAsync;
        Assert.Equal(3, publicEntry.Method.GetParameters().Length);
        Assert.Single(typeof(XaiErrorClassifier).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static),
            method => method.Name == nameof(XaiErrorClassifier.ThrowIfUnsuccessfulAsync));
        Assert.NotNull(typeof(XaiErrorClassifier).GetMethod(nameof(XaiErrorClassifier.ThrowIfUnsuccessfulAsync),
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
            [typeof(HttpResponseMessage), typeof(LlmCredential), typeof(IEnumerable<string>), typeof(CancellationToken), typeof(bool)]));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "unknown", InferenceFailureCode.AuthenticationRequired)]
    [InlineData(HttpStatusCode.BadRequest, "invalid_token", InferenceFailureCode.AuthenticationRequired)]
    [InlineData(HttpStatusCode.PaymentRequired, "unknown", InferenceFailureCode.AccessDenied)]
    public async Task FixedSpeechDiagnosticPreservesClassificationWithoutExposingBody(HttpStatusCode status, string code, InferenceFailureCode expected)
    {
        const string secret = "controlled-secret-value";
        using var response = new HttpResponseMessage(status) { Content = new StringContent($"{{\"error\":{{\"code\":\"{code}\",\"message\":\"{secret}\",\"url\":\"https://example.test/?secret={secret}\"}}}}") };
        InferenceException error = await Assert.ThrowsAsync<InferenceException>(() => XaiErrorClassifier.ThrowIfUnsuccessfulSpeechAsync(
            response, null, CancellationToken.None));
        Assert.Equal(expected, error.Code);
        var provider = Assert.IsType<LlmProviderException>(error.InnerException); Assert.Equal(status, provider.StatusCode);
        Assert.Equal("Provider rejected the speech request.", provider.SanitizedResponseBody);
        Assert.DoesNotContain(secret, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OrdinaryFailureKeepsStatusAndFilesDefaultDiagnostic()
    {
        using var speech = new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("raw secret payload") };
        var error = await Assert.ThrowsAsync<LlmProviderException>(() => XaiErrorClassifier.ThrowIfUnsuccessfulSpeechAsync(
            speech, null, CancellationToken.None));
        Assert.Equal(HttpStatusCode.InternalServerError, error.StatusCode); Assert.Null(error.ProviderCode);
        Assert.DoesNotContain("raw secret payload", error.ToString(), StringComparison.Ordinal);
        using var files = new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("raw secret payload") };
        error = await Assert.ThrowsAsync<LlmProviderException>(() => XaiErrorClassifier.ThrowIfUnsuccessfulAsync(files, null, [], CancellationToken.None, diagnosticBody: false));
        Assert.Equal("Provider rejected the Files request.", error.SanitizedResponseBody);
    }
}
