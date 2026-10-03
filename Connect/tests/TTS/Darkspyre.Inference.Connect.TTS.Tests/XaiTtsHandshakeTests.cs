using System.Net;
using System.Reflection;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.TTS.Tests;

public sealed class XaiTtsHandshakeTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    private static readonly InferenceStreamLimits Limits = new(4, 2048, 2048);

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, InferenceFailureCode.AuthenticationRequired)]
    [InlineData(HttpStatusCode.PaymentRequired, InferenceFailureCode.AccessDenied)]
    [InlineData(HttpStatusCode.Forbidden, null)]
    [InlineData(HttpStatusCode.BadRequest, null)]
    public async Task ActualBclFailedUpgradeUsesOnlyObservedStatus(HttpStatusCode status, InferenceFailureCode? expected)
    {
        using var access = new LlmConnectionAccess(new XaiTtsTests.Source(), []);
        using var http = new HttpClient(new XaiTtsTests.Handler(request =>
        {
            Assert.Equal("controlled-token", request.Headers.Authorization!.Parameter);
            var response = new HttpResponseMessage(status) { Content = new StringContent("{\"error\":{\"code\":\"invalid_token\",\"message\":\"raw-secret-body\"}}") };
            response.Headers.TryAddWithoutValidation("X-Private-Diagnostic", "raw-secret-header");
            return Task.FromResult(response);
        }));
        await using ITtsClient client = new XaiTtsProvider(http).Bind(XaiTtsTests.Connection, new(new("selected"), access));
        Exception error = (await Record.ExceptionAsync(() => Assert.IsAssignableFrom<ITtsStreamingClient>(client).OpenStreamAsync(new(Language: "en"), Limits)))!;
        LlmProviderException provider;
        if (expected is { } code)
        {
            var classified = Assert.IsType<InferenceException>(error); Assert.Equal(code, classified.Code);
            provider = Assert.IsType<LlmProviderException>(classified.InnerException);
        }
        else { provider = Assert.IsType<LlmProviderException>(error); Assert.Null(provider.ProviderCode); }
        Assert.Equal(status, provider.StatusCode);
        Assert.DoesNotContain("raw-secret", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("controlled-token", error.ToString(), StringComparison.Ordinal);
        Assert.Equal("Provider rejected the speech request.", provider.SanitizedResponseBody);
    }

    [Fact]
    public async Task MissingObservedStatusKeepsActualInternalCauseWithoutRawDiagnostics()
    {
        var original = new InvalidOperationException("raw-secret-transport");
        using var access = new LlmConnectionAccess(new XaiTtsTests.Source(), []);
        using var http = new HttpClient(new XaiTtsTests.Handler(_ => throw original));
        await using ITtsClient client = new XaiTtsProvider(http).Bind(XaiTtsTests.Connection, new(new("selected"), access));
        var error = Assert.IsType<InferenceException>(await Record.ExceptionAsync(() => Assert.IsAssignableFrom<ITtsStreamingClient>(client).OpenStreamAsync(new(Language: "en"), Limits)));
        Assert.Equal(InferenceFailureCode.ExecutionFailed, error.Code);
        Assert.DoesNotContain("raw-secret-transport", error.ToString(), StringComparison.Ordinal);
        object evidence = error.InnerException!;
        PropertyInfo property = evidence.GetType().GetProperty("Original", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.NotNull(property);
        Exception retained = Assert.IsAssignableFrom<Exception>(property.GetValue(evidence));
        while (retained.InnerException is { } inner) retained = inner;
        Assert.Same(original, retained); Assert.Null(evidence.GetType().GetProperty("Original"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IgnoringBclUpgradeIsDrainedAndCancellationIsNotAuthentication(bool retire)
    {
        using var access = new LlmConnectionAccess(new XaiTtsTests.Source(), []);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new XaiTtsTests.Handler(async _ => { entered.TrySetResult(); await release.Task; return new(HttpStatusCode.Unauthorized); }));
        ITtsClient client = new XaiTtsProvider(http).Bind(XaiTtsTests.Connection, new(new("selected"), access));
        using var caller = new CancellationTokenSource();
        Task<ITtsStreamingSession> opening = Assert.IsAssignableFrom<ITtsStreamingClient>(client).OpenStreamAsync(new(Language: "en"), Limits, caller.Token);
        Task? retirement = null;
        try
        {
            await entered.Task.WaitAsync(Bound);
            if (retire) { retirement = client.DisposeAsync().AsTask(); Assert.False(retirement.IsCompleted); }
            else await caller.CancelAsync();
            release.TrySetResult(); Assert.IsAssignableFrom<OperationCanceledException>(await Record.ExceptionAsync(() => opening));
            retirement ??= client.DisposeAsync().AsTask(); await retirement.WaitAsync(Bound);
        }
        finally
        {
            retirement ??= client.DisposeAsync().AsTask(); release.TrySetResult();
            try { _ = await Record.ExceptionAsync(() => opening.WaitAsync(Bound)); }
            finally { _ = await Record.ExceptionAsync(() => retirement.WaitAsync(Bound)); }
        }
    }

    [Fact]
    public async Task SynchronousOpeningFencePrecedesUpgradeClassificationWhenCancellationPropagationIsHeld()
    {
        using var access = new LlmConnectionAccess(new XaiTtsTests.Source(), []);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var callbackRelease = new ManualResetEventSlim();
        using var http = new HttpClient(new XaiTtsTests.Handler(async _ =>
        {
            entered.TrySetResult(); await release.Task; return new(HttpStatusCode.Unauthorized);
        }));
        ITtsClient client = new XaiTtsProvider(http).Bind(XaiTtsTests.Connection, new(new("selected"), access));
        Task<ITtsStreamingSession> opening = Assert.IsAssignableFrom<ITtsStreamingClient>(client).OpenStreamAsync(new(Language: "en"), Limits);
        Task? retirement = null;
        object lifetime = client.GetType().GetField("_lifetime", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;
        var stop = (CancellationTokenSource)lifetime.GetType().GetField("_stop", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(lifetime)!;
        using CancellationTokenRegistration callback = stop.Token.Register(() => { callbackEntered.TrySetResult(); callbackRelease.Wait(Bound); });
        try
        {
            await entered.Task.WaitAsync(Bound);
            retirement = client.DisposeAsync().AsTask(); await callbackEntered.Task.WaitAsync(Bound);
            Assert.False(retirement.IsCompleted);
            release.TrySetResult();
            Assert.IsAssignableFrom<OperationCanceledException>(await Record.ExceptionAsync(() => opening.WaitAsync(Bound)));
        }
        finally
        {
            retirement ??= client.DisposeAsync().AsTask(); callbackRelease.Set(); release.TrySetResult();
            try { _ = await Record.ExceptionAsync(() => opening.WaitAsync(Bound)); }
            finally { _ = await Record.ExceptionAsync(() => retirement.WaitAsync(Bound)); }
        }
    }
}
