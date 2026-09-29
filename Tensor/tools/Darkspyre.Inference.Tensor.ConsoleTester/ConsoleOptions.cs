using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Tensor.ConsoleTester;

internal sealed record ConsoleOptions
{
    public static readonly string[] Suites = ["chat", "think", "tools", "json", "selfhost"];

    public string? Repository { get; init; }
    public string? File { get; init; }
    public string Revision { get; init; } = "main";
    public string? Path { get; init; }
    public string? License { get; init; }
    public TensorBackendKind Backend { get; init; } = TensorBackendKind.Auto;
    public string? Store { get; init; }
    public bool Install { get; init; }
    public IReadOnlyList<string> Suite { get; init; } = Suites;
    public string? Prompt { get; init; }
    public int MaxTokens { get; init; } = 512;
    public bool TraceExceptions { get; init; }

    public static ConsoleOptions Parse(string[] args)
    {
        if (args[0] != "qwen") { throw new ArgumentException($"Unknown family '{args[0]}'. Supported: qwen, registry."); }
        var options = new ConsoleOptions();
        for (int index = 1; index < args.Length; index++)
        {
            string name = args[index];
            string Value() => ++index < args.Length ? args[index] : throw new ArgumentException($"{name} needs a value.");
            options = name switch
            {
                "--repo" => options with { Repository = Value() },
                "--file" => options with { File = Value() },
                "--revision" => options with { Revision = Value() },
                "--path" => options with { Path = System.IO.Path.GetFullPath(Value()) },
                "--license" => options with { License = Value() },
                "--backend" => options with { Backend = Enum.Parse<TensorBackendKind>(Value(), ignoreCase: true) },
                "--store" => options with { Store = System.IO.Path.GetFullPath(Value()) },
                "--install" => options with { Install = true },
                "--suite" => options with { Suite = ParseSuite(Value()) },
                "--prompt" => options with { Prompt = Value() },
                "--trace-exceptions" => options with { TraceExceptions = true },
                "--max-tokens" => options with { MaxTokens = int.Parse(Value(), System.Globalization.CultureInfo.InvariantCulture) },
                _ => throw new ArgumentException($"Unknown option '{name}'.")
            };
        }

        if ((options.Path is null) == (options.Repository is null))
        { throw new ArgumentException("Pass either --repo with --file, or --path."); }
        if (options.Repository is not null && options.File is null) { throw new ArgumentException("--repo needs --file."); }
        if (options.Path is not null && options.License is null) { throw new ArgumentException("--path needs --license."); }
        if (options.Store is null) { throw new ArgumentException("--store is required: name the pack store directory."); }
        if (options.MaxTokens <= 0) { throw new ArgumentException("--max-tokens must be positive."); }
        return options;
    }

    public TensorModelSelection Selection(string sourceId) => Path is not null
        ? new(new TensorModelLocation.LocalFile(Path), Backend, License)
        : new(new TensorModelLocation.RepositoryArtifact(sourceId, new(Repository, Revision, File!)), Backend);

    public string Describe() => Path ?? $"{Repository}@{Revision}/{File}";

    private static string[] ParseSuite(string value)
    {
        string[] names = value == "all" ? Suites : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string? unknown = names.FirstOrDefault(name => !Suites.Contains(name));
        return unknown is null ? names : throw new ArgumentException($"Unknown suite '{unknown}'. Supported: all, {string.Join(", ", Suites)}.");
    }

    public static void WriteHelp() => Console.WriteLine("""
        Usage: tensor qwen (--repo <owner/name> --file <gguf> [--revision <rev>] | --path <gguf> --license <id>)
                           --store <dir> [--install] [--backend <kind>] [--suite all|chat,think,tools,json,selfhost]
                           [--prompt <text>] [--max-tokens <n>] [--trace-exceptions]
               tensor registry --store <dir>

        Resolves the GGUF from its headers, installs it into the pack store when --install is passed, loads
        the engine and runs the test calls. A repeat run reuses the installed pack. --store names the pack
        store; it is required so a multi-GB download never lands on an unintended volume. HF_TOKEN supplies
        a Hugging Face token for gated models.

        registry lists every engine AddTensorEngines registers: identity, tasks, model store key, offered
        models and the setup actions its provider opens over <dir>/<store key>. It reads metadata only; it
        installs, loads and saves nothing, writes no file and makes no network call.

        Example:
          tensor qwen --repo ukisai/Swift-1.5-Qwen3.8-27B-GGUF --file Swift-1.5-Qwen3.8-27B-Q4_K_M.gguf \
                      --store /Volumes/Data/Models/Tensor --install
        """);
}
