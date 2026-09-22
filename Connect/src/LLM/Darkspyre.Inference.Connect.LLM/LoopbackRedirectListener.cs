using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>
/// Optional PKCE redirect helper for a CLI or desktop host running on the user's own machine. Hosted
/// applications must use <see cref="LlmProviderService.ClaudeManualRedirectUri"/> and a pasted code
/// because localhost resolves on the user's device, not the application server.
/// </summary>
public sealed class LoopbackRedirectListener : IDisposable
{
    private readonly HttpListener _listener;

    public LoopbackRedirectListener()
    {
        int port = GetFreePort();
        RedirectUri = $"http://localhost:{port}/callback";
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Start();
    }

    public string RedirectUri { get; }

    public async Task<LlmRedirectResult> WaitForCodeAsync(
        CancellationToken cancellationToken = default)
    {
        HttpListenerContext context = await _listener
            .GetContextAsync()
            .WaitAsync(cancellationToken);
        string? code = context.Request.QueryString["code"];
        string? state = context.Request.QueryString["state"];

        byte[] bytes = Encoding.UTF8.GetBytes(
            "<html><body>Sign-in complete. You can close this tab.</body></html>");
        context.Response.ContentType = "text/html";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes, cancellationToken);
        context.Response.Close();

        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
        {
            throw new InvalidOperationException(
                "OAuth redirect did not include a code and state.");
        }

        return new LlmRedirectResult(code, state);
    }

    public void Dispose() => _listener.Close();

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
