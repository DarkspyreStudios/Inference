namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class LoopbackRedirectListenerTests
{
    [Fact]
    public async Task StartAsyncOpensALoopbackListenerThatReceivesTheRedirect()
    {
        using LoopbackRedirectListener listener = await LoopbackRedirectListener.StartAsync();
        var redirect = new Uri(listener.RedirectUri);
        Assert.Equal("localhost", redirect.Host);
        Assert.Equal("/callback", redirect.AbsolutePath);

        Task<LlmRedirectResult> waiting = listener.WaitForCodeAsync();
        using var http = new HttpClient();
        using HttpResponseMessage response = await http.GetAsync(listener.RedirectUri + "?code=abc&state=xyz");

        LlmRedirectResult result = await waiting;
        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("abc", result.Code);
        Assert.Equal("xyz", result.State);
    }

    [Fact]
    public async Task StartAsyncHonoursAnAlreadyCancelledToken()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => LoopbackRedirectListener.StartAsync(cancelled.Token));
    }
}
