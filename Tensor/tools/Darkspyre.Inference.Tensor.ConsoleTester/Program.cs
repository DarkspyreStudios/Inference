using System.Diagnostics;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Tensor.Common;
using Darkspyre.Inference.Tensor.LLM.Qwen;

namespace Darkspyre.Inference.Tensor.ConsoleTester;

/// <summary>
/// Drives a Tensor engine outside any host: resolves a model, installs it into a pack store, loads it and
/// runs test calls. It uses the same resolver, pack manager and engine that a host's settings use.
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            ConsoleOptions.WriteHelp();
            return args.Length == 0 ? 2 : 0;
        }

        ConsoleOptions options;
        try { options = ConsoleOptions.Parse(args); }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine($"Error: {exception.Message}");
            ConsoleOptions.WriteHelp();
            return 2;
        }

        if (options.TraceExceptions)
        {
            // Shows exceptions a library handles and reports generically, such as the managed agent lane's.
            AppDomain.CurrentDomain.FirstChanceException += (_, raised) =>
                Console.Error.WriteLine($"[exception] {raised.Exception.GetType().Name}: {raised.Exception.Message.Split('\n')[0]}");
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
        try
        {
            return await RunQwenAsync(options, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Console.Error.WriteLine("Cancelled.");
            return 130;
        }
        catch (Exception exception) when (exception is Darkspyre.Inference.Common.InferenceException or TensorModelPackException)
        {
            // A failed load reports the engine's error; its inner exception names the native cause.
            Console.Error.WriteLine($"Failed: {exception.Message}");
            for (Exception? inner = exception.InnerException; inner is not null; inner = inner.InnerException)
            { Console.Error.WriteLine($"  {inner.GetType().Name}: {inner.Message.Split('\n')[0]}"); }
            return 1;
        }
    }

    private static async Task<int> RunQwenAsync(ConsoleOptions options, CancellationToken cancellationToken)
    {
        string store = options.Store!;
        Console.WriteLine($"store   : {store}");
        // The library follows redirects itself so a credential never crosses to another origin.
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        TensorSourceAccess access = HuggingFaceToken;
        using var source = new HuggingFaceModelSource(http, access);
        var resolver = new QwenModelResolver(source, new TensorArtifactReader(source, http, access));
        using var packs = new TensorModelPackManager(new TensorModelCatalog([]), store, http, access);

        Console.WriteLine($"resolve : {options.Describe()}");
        TensorModelResolution resolution = await resolver.ResolveAsync(options.Selection(source.Id), cancellationToken);
        foreach (TensorCompatibilityEvidence evidence in resolution.Evaluation.Evidence)
        {
            Console.WriteLine($"  {evidence.Check,-16} {evidence.Detail}");
        }
        if (resolution.Evaluation.State != TensorCompatibilityState.Supported || resolution.Definition is not { } definition)
        {
            Console.Error.WriteLine($"The model is {resolution.Evaluation.State}.");
            foreach (TensorSourceIssue issue in resolution.Evaluation.Issues) { Console.Error.WriteLine($"  {issue.Code}: {issue.Message}"); }
            return 1;
        }

        await packs.RegisterAsync(definition, cancellationToken);
        TensorModelPackStatus status = await packs.GetStatusAsync(definition.Key, cancellationToken);
        long bytes = definition.Artifacts.Sum(artifact => artifact.Length);
        Console.WriteLine($"pack    : {definition.Key} ({Size(bytes)}, {status.State})");
        if (status.State != TensorModelPackState.Installed)
        {
            if (!options.Install)
            {
                Console.Error.WriteLine("The pack is not installed. Pass --install to download it.");
                return 1;
            }

            if (!HasRoomFor(store, bytes)) { return 1; }
            await InstallAsync(packs, definition.Key, cancellationToken);
        }

        await using var engine = new QwenLlmInference(packs, options.Backend);
        await AwaitStateAsync(engine, () => engine.StartAsync(cancellationToken), TensorEngineState.Started);
        Stopwatch load = Stopwatch.StartNew();
        await AwaitStateAsync(engine, () => engine.LoadAsync(definition.Key, cancellationToken), TensorEngineState.Loaded);
        TensorModelObservation observation = await engine.GetObservationAsync(cancellationToken);
        LlmModelInfo model = (await engine.ListModelsAsync(cancellationToken)).Single(item => item.Id == definition.Key.ModelId);
        Console.WriteLine($"loaded  : {observation.Backend} in {load.Elapsed.TotalSeconds:F1}s; usable context {model.MaxContextTokens:N0} tokens");

        var suite = new QwenSuite(engine, model.Id);
        return await suite.RunAsync(options, cancellationToken) ? 0 : 1;
    }

    private static async Task InstallAsync(TensorModelPackManager packs, TensorModelKey key, CancellationToken cancellationToken)
    {
        // Report each stage once and whole percentages; a byte-level callback floods a non-interactive console.
        TensorModelInstallStage? stage = null;
        long percent = -1;
        var progress = new Progress<TensorModelInstallProgress>(value =>
        {
            if (value.Stage != stage) { stage = value.Stage; Console.WriteLine($"  {value.Stage} {value.ArtifactRole}"); }
            if (value.OverallTotalBytes is > 0 && value.OverallBytesTransferred is { } done)
            {
                long current = done * 100 / value.OverallTotalBytes.Value;
                if (current / 5 != percent / 5) { percent = current; Console.WriteLine($"  {current}% of {Size(value.OverallTotalBytes.Value)}"); }
            }
        });
        Stopwatch clock = Stopwatch.StartNew();
        await packs.InstallAsync(key, progress, cancellationToken);
        Console.WriteLine($"  installed in {clock.Elapsed.TotalMinutes:F1} min");
    }

    /// <summary>Starts a lifecycle request and waits for the engine to report the target state.</summary>
    private static async Task AwaitStateAsync(QwenLlmInference engine, Func<Task> accept, TensorEngineState target)
    {
        var outcome = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnStatus(object? sender, TensorEngineStatus status)
        {
            if (status.State == target) { outcome.TrySetResult(); }
            else if (status.State == TensorEngineState.Failed) { outcome.TrySetException(status.Error ?? new InvalidOperationException("The engine failed.")); }
        }

        engine.EngineStatusChanged += OnStatus;
        try
        {
            await accept();
            await outcome.Task;
        }
        finally { engine.EngineStatusChanged -= OnStatus; }
    }

    // A pack larger than the store's free space would fill the volume before the download fails.
    private static bool HasRoomFor(string store, long bytes)
    {
        Directory.CreateDirectory(store);
        long free = new DriveInfo(Path.GetFullPath(store)).AvailableFreeSpace;
        const long margin = 2L * 1024 * 1024 * 1024;
        if (free >= bytes + margin) { return true; }
        Console.Error.WriteLine($"The store has {Size(free)} free; the pack needs {Size(bytes)} plus a 2 GiB margin. Pass --store on a larger volume.");
        return false;
    }

    // A Hugging Face token for gated repositories, sent only to the Hugging Face origin.
    private static Task<TensorSourceCredential?> HuggingFaceToken(Uri origin, CancellationToken cancellationToken) =>
        Task.FromResult(Environment.GetEnvironmentVariable("HF_TOKEN") is { Length: > 0 } token && origin.Host == "huggingface.co"
            ? TensorSourceCredential.Bearer(token) : null);

    internal static string Size(long bytes) => $"{bytes / 1073741824.0:F2} GiB";
}
