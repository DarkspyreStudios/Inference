using System.Text;
using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.ConsoleTester;

internal sealed class ConsoleAuthPrompt : ILlmAuthPrompt
{
    public Task ShowDeviceCodeAsync(
        LlmDeviceCode code,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Console.WriteLine("Open this URL in a browser:");
        Console.WriteLine(code.VerificationUrl);
        Console.WriteLine();
        Console.WriteLine($"Enter code: {code.UserCode}");
        Console.WriteLine();
        Console.WriteLine("Waiting for browser approval…");
        return Task.CompletedTask;
    }

    public Task<LlmRedirectResult> GetAuthorizationCodeAsync(
        Uri authorizeUrl,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Console.WriteLine("Open this URL in a browser and authorize the connection:");
        Console.WriteLine(authorizeUrl);
        Console.WriteLine();
        Console.Write("Paste the complete callback response: ");
        string callback = ReadSecretLine();
        Console.WriteLine();
        return Task.FromResult(LlmRedirectResult.ParsePastedCode(callback));
    }

    private static string ReadSecretLine()
    {
        if (Console.IsInputRedirected)
        {
            return Console.ReadLine() ?? string.Empty;
        }

        var value = new StringBuilder();
        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                return value.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (value.Length > 0)
                {
                    value.Length--;
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                value.Append(key.KeyChar);
            }
        }
    }
}
