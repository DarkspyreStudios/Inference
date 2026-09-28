using System.Diagnostics;
using System.Globalization;
using Darkspyre.Inference.Common;

namespace Darkspyre.Inference.Connect.ConsoleTester;

internal sealed record AppleProbeOptions(string Model, string Case, int Repeat, int TimeoutSeconds)
{
    public static AppleProbeOptions Parse(string[] args, string defaultModel)
    {
        string model = defaultModel, selected = "all";
        int repeat = 1, timeout = 180;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i++)
        {
            string option = args[i];
            if (!seen.Add(option) || ++i == args.Length || string.IsNullOrWhiteSpace(args[i]))
                throw new ArgumentException("Each probe option requires exactly one value.");
            string value = args[i];
            switch (option)
            {
                case "--model": model = value; break;
                case "--case": selected = value; break;
                case "--repeat": repeat = Number(value, 20, option); break;
                case "--timeout-seconds": timeout = Number(value, 600, option); break;
                default: throw new ArgumentException($"Unknown probe option '{option}'.");
            }
        }
        if (selected is not ("all" or "text" or "structured" or "tools" or "agent" or "cancel"))
            throw new ArgumentException("--case must be all, text, structured, tools, agent or cancel.");
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("A model is required.");
        return new(model, selected, repeat, timeout);
    }

    private static int Number(string value, int maximum, string option) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number >= 1 && number <= maximum
            ? number : throw new ArgumentException($"{option} must be between 1 and {maximum}.");
}

internal sealed record ProbeCase(string Name, Func<CancellationToken, Task<string>> Run);

// Only assertion text owned by this harness may reach its report. Provider exceptions/content do not.
internal sealed class ProbeFailure(string message) : Exception(message);

internal static class ProbeRunner
{
    public static async Task<int> RunAsync(IReadOnlyList<ProbeCase> cases, AppleProbeOptions options,
        bool ready, TextWriter output, CancellationToken token)
    {
        int passed = 0, failed = 0, skipped = 0;
        var selected = cases.Where(test => options.Case == "all" || test.Name == options.Case).ToArray();
        if (selected.Length == 0) throw new ArgumentException("No matching probe cases.");
        for (int iteration = 1; iteration <= options.Repeat; iteration++)
        {
            foreach (var test in selected)
            {
                if (token.IsCancellationRequested)
                {
                    output.WriteLine($"CANCELLED: passed={passed} failed={failed} skipped={skipped}; remaining cases not run.");
                    return 130;
                }
                if (!ready)
                {
                    skipped++;
                    output.WriteLine($"SKIP {iteration}/{options.Repeat} {test.Name}: prerequisites unavailable");
                    continue;
                }
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
                var clock = Stopwatch.StartNew();
                string state, detail;
                try
                {
                    detail = await test.Run(deadline.Token);
                    deadline.Token.ThrowIfCancellationRequested();
                    state = "PASS";
                    passed++;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    output.WriteLine($"CANCELLED {iteration}/{options.Repeat} {test.Name}; passed={passed} failed={failed} skipped={skipped}");
                    return 130;
                }
                catch (Exception error)
                {
                    state = "FAIL";
                    failed++;
                    detail = error switch
                    {
                        OperationCanceledException when deadline.IsCancellationRequested => "case deadline exceeded",
                        ProbeFailure assertion => assertion.Message,
                        InferenceException inference => $"inference failure: {inference.Code}",
                        _ => $"{error.GetType().Name} (provider details omitted)"
                    };
                }
                output.WriteLine($"{state} {iteration}/{options.Repeat} {test.Name} ({clock.Elapsed.TotalSeconds:F2}s): {detail}");
                output.Flush();
            }
        }
        output.WriteLine($"Summary: passed={passed} failed={failed} skipped={skipped}");
        return failed > 0 ? 1 : skipped > 0 ? 3 : 0;
    }
}
