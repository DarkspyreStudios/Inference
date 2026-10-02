using System.Text.Json;

namespace GpuArbiter.Splash;

public sealed class SplashOptions : CoordinatorOptions
{
    public string InferenceUrl { get; set; } = "http://127.0.0.1:8084";
    public string ComfyProxyUrl { get; set; } = "http://127.0.0.1:8191";
    public int Port { get; set; } = 8085;
    public string Url => $"http://127.0.0.1:{Port}";
    public string Python { get; set; } = "/opt/homebrew/Cellar/splash/1.1.0/libexec/python/bin/python3";
    public string Server { get; set; } = "/opt/homebrew/Cellar/splash/1.1.0/libexec/server/server.py";
    public string Binary { get; set; } = "/opt/homebrew/Cellar/splash/1.1.0/libexec/engine/splash";
    public string LogDir { get; set; } = "/Volumes/Data/Models/gpu-arbiter-splash/logs";
    public string ScratchDir { get; set; } = "/Volumes/Data/Models/gpu-arbiter-splash/scratch";
    public int KeepaliveSeconds { get; set; } = 5;
    public int MaximumContext { get; set; } = 16384;
    public long MaximumMemoryBytes { get; set; }
    public long MaximumRequestBytes { get; set; } = 128 * 1024 * 1024;
    public double StopTimeoutSeconds { get; set; } = 30;
    public double CleanupTimeoutSeconds { get; set; } = 30;
    public List<SplashModelOptions> Models { get; set; } = [];

    public void Validate(bool checkAssets = true)
    {
        if (!Uri.TryCreate(InferenceUrl, UriKind.Absolute, out var inference) || inference.Scheme != "http" ||
            !Uri.TryCreate(ComfyProxyUrl, UriKind.Absolute, out var proxy) || proxy.Scheme != "http" ||
            !Uri.TryCreate(Comfy.Url, UriKind.Absolute, out var comfy) || comfy.Scheme != "http" || !comfy.IsLoopback ||
            Port is < 1 or > 65535 || inference.Port == proxy.Port || inference.Port == Port || proxy.Port == Port ||
            inference.Port == comfy.Port || proxy.Port == comfy.Port || Port == comfy.Port ||
            MaximumContext is < 1 or > 262144 || MaximumMemoryBytes < 0 || KeepaliveSeconds <= 0 ||
            MaximumRequestBytes is < 1 or > 134217728 || Models.Count == 0 ||
            !Positive(ModelLoadTimeoutSeconds) || !Positive(ReleaseTimeoutSeconds) || !Positive(Comfy.PollSeconds) ||
            !Positive(StopTimeoutSeconds) || !Positive(CleanupTimeoutSeconds))
            throw new InvalidOperationException("Invalid Splash arbiter ports, models or limits.");
        foreach (var path in new[] { Python, Server, Binary, LogDir, ScratchDir })
            if (!Path.IsPathFullyQualified(path) || path.Any(char.IsControl))
                throw new InvalidOperationException("Splash runtime, log and scratch paths must be absolute.");
        var aliases = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in Models)
        {
            if (string.IsNullOrWhiteSpace(model.Id) || model.Id.Length > 256 || model.Id.Any(char.IsControl) ||
                !aliases.Add(model.Id) || !ValidModelId(model.Id) || !Path.IsPathFullyQualified(model.Pack) || model.Pack.Any(char.IsControl))
                throw new InvalidOperationException("Invalid Splash model ID or installed pack path.");
            if (checkAssets) ValidatePack(model.Pack);
        }
        if (checkAssets)
            foreach (var path in new[] { Python, Server, Binary })
                if (!File.Exists(path)) throw new InvalidOperationException($"Installed Splash runtime file is missing: {path}");
    }

    private static bool ValidModelId(string id)
    {
        var parts = id.Split('/');
        return parts.Length == 2 && parts.All(part => part.Length is >= 1 and <= 96 &&
            char.IsAsciiLetterOrDigit(part[0]) && char.IsAsciiLetterOrDigit(part[^1]) &&
            !part.Contains("..", StringComparison.Ordinal) && !part.Contains("--", StringComparison.Ordinal) &&
            part.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'));
    }

    private static bool Positive(double value) => double.IsFinite(value) && value > 0;

    private static void ValidatePack(string pack)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(pack, "manifest.json")));
        if (manifest.RootElement.GetProperty("schema_version").GetInt32() != 3 ||
            manifest.RootElement.GetProperty("format").GetProperty("name").GetString() != "splash-packed-q4")
            throw new InvalidOperationException("The model must be an installed Splash q4 pack.");
        foreach (var directory in new[] { "target", "draft", "tokenizer" })
            if (!Directory.Exists(Path.Combine(pack, directory))) throw new InvalidOperationException($"Incomplete Splash pack: {directory}");
        foreach (var artifact in manifest.RootElement.GetProperty("artifacts").EnumerateArray())
        {
            var relative = artifact.GetProperty("path").GetString() ?? throw new InvalidOperationException("Invalid pack artifact.");
            var path = Path.GetFullPath(Path.Combine(pack, relative));
            if (Path.IsPathFullyQualified(relative) || !path.StartsWith(Path.TrimEndingDirectorySeparator(pack) + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                !File.Exists(path) || new FileInfo(path).Length != artifact.GetProperty("size").GetInt64())
                throw new InvalidOperationException("Missing, invalid or truncated Splash pack artifact.");
        }
    }

    public IReadOnlyList<string> Arguments(SplashModelOptions model) =>
    [ "-u", Server, Path.Combine(model.Pack, "target"), Path.Combine(model.Pack, "draft"),
      "--tokenizer", Path.Combine(model.Pack, "tokenizer"), "--model", model.Id, "--binary", Binary,
      "--host", "127.0.0.1", "--port", Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
      "--max-context", MaximumContext.ToString(System.Globalization.CultureInfo.InvariantCulture), "--kv-format", "int8",
      "--max-memory", MaximumMemoryBytes == 0 ? "auto" : MaximumMemoryBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
      "--max-request-size", MaximumRequestBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
      "--default-reasoning-effort", "none", "--no-webui" ];
}

public sealed class SplashModelOptions
{
    public string Id { get; set; } = "incoai/Qwen3.8-27B-Splash";
    public string Pack { get; set; } = "/Volumes/Data/Models/Qwen3.8-27B-Splash";
}
