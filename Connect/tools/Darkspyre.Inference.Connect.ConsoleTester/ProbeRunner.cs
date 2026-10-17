using System.Diagnostics;
using System.Globalization;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.ConsoleTester;

internal sealed record ProviderProbeOptions(string Model, string Case, int Repeat, int TimeoutSeconds)
{
    public static ProviderProbeOptions Parse(string[] args, string defaultModel, int defaultTimeoutSeconds = 180)
    {
        string model = defaultModel, selected = "all";
        int repeat = 1, timeout = defaultTimeoutSeconds;
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
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("A model is required.");
        return new(model, selected, repeat, timeout);
    }

    private static int Number(string value, int maximum, string option) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number >= 1 && number <= maximum
            ? number : throw new ArgumentException($"{option} must be between 1 and {maximum}.");
}

internal sealed record ProbeCase(string Name, Func<CancellationToken, Task<string>> Run);

// Reports include owned assertions and public typed diagnostics, never arbitrary exception text or payloads.
internal sealed class ProbeFailure(string message) : Exception(message);
internal sealed class ProbeUnavailable(string message) : Exception(message);

internal static class ProbeRunner
{
    public static async Task<int> RunAsync(IReadOnlyList<ProbeCase> cases, ProviderProbeOptions options,
        bool ready, TextWriter output, CancellationToken token)
    {
        int passed = 0, failed = 0, skipped = 0;
        var selected = cases.Where(test => options.Case == "all" || test.Name == options.Case).ToArray();
        if (selected.Length == 0)
            throw new ArgumentException($"--case must be all or one of: {string.Join(", ", cases.Select(test => test.Name))}.");
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
                catch (ProbeUnavailable error)
                {
                    state = "SKIP";
                    skipped++;
                    detail = error.Message;
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
                    detail = error is OperationCanceledException && deadline.IsCancellationRequested
                        ? "case deadline exceeded" : FailureDetail(error);
                }
                output.WriteLine($"{state} {iteration}/{options.Repeat} {test.Name} ({clock.Elapsed.TotalSeconds:F2}s): {detail}");
                output.Flush();
            }
        }
        output.WriteLine($"Summary: passed={passed} failed={failed} skipped={skipped}");
        return failed > 0 ? 1 : skipped > 0 ? 3 : 0;
    }

    internal static string FailureDetail(Exception error) => error switch
    {
        AggregateException aggregate => string.Join("; ", aggregate.InnerExceptions.Select(FailureDetail)),
        ProbeFailure assertion => assertion.Message,
        ProbeUnavailable unavailable => unavailable.Message,
        InferenceException inference => Program.FormatInferenceFailure(inference),
        LlmProviderException provider => Program.FormatProviderFailure(provider),
        _ => $"{error.GetType().Name} (provider details omitted)"
    };
}
