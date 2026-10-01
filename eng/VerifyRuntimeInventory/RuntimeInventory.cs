using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;

internal sealed record RuntimeInventory(
    int SchemaVersion, string SourceCommit, string AssetsSha256, IReadOnlyList<string> RequiredRids,
    IReadOnlyList<RuntimePackage> Packages, IReadOnlyList<RuntimeAsset> NativeAssets,
    IReadOnlyList<RuntimeCoverage> Coverage, bool HasTransitiveGgmlNatives, string RuntimeQualification)
{
    internal static readonly string[] SupportedRids = ["osx-arm64", "linux-x64", "linux-arm64", "win-x64", "win-arm64"];

    internal static RuntimeInventory Read(string assetsPath, string sourceCommit)
    {
        if (sourceCommit.Length != 40 || sourceCommit.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("The source commit must be a full lowercase Git object ID.", nameof(sourceCommit));
        }
        using FileStream input = File.OpenRead(assetsPath);
        string assetsHash = Convert.ToHexStringLower(SHA256.HashData(input));
        input.Position = 0;
        using JsonDocument document = JsonDocument.Parse(input);
        JsonElement root = document.RootElement;
        string[] packageRoots = root.GetProperty("packageFolders").EnumerateObject().Select(item => item.Name).ToArray();
        JsonProperty[] targets = root.GetProperty("targets").EnumerateObject().ToArray();
        var packages = new List<RuntimePackage>();
        var native = new List<RuntimeAsset>();
        foreach (JsonProperty library in root.GetProperty("libraries").EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            if (library.Value.GetProperty("type").GetString() != "package") continue;
            int split = library.Name.LastIndexOf('/');
            string id = library.Name[..split];
            string version = library.Name[(split + 1)..];
            string packagePath = library.Value.GetProperty("path").GetString()!;
            string directory = packageRoots.Select(path => Resolve(path, packagePath)).FirstOrDefault(Directory.Exists)
                ?? throw new InvalidDataException($"{library.Name}: restored package directory is absent.");
            string[] files = library.Value.GetProperty("files").EnumerateArray().Select(item => item.GetString()!).Order(StringComparer.Ordinal).ToArray();
            string nuspec = files.Single(file => file.EndsWith(".nuspec", StringComparison.Ordinal));
            XElement metadata = XDocument.Load(Resolve(directory, nuspec)).Descendants().Single(element => element.Name.LocalName == "metadata");
            Require(metadata.Elements().Single(element => element.Name.LocalName == "id").Value.Equals(id, StringComparison.OrdinalIgnoreCase)
                && metadata.Elements().Single(element => element.Name.LocalName == "version").Value == version,
                $"{library.Name}: restore graph and installed manifest identity differ.");
            var managed = new SortedSet<string>(StringComparer.Ordinal);
            var dependencies = new SortedSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty target in targets)
            {
                if (!target.Value.TryGetProperty(library.Name, out JsonElement node)) continue;
                foreach (string group in new[] { "compile", "runtime" })
                {
                    if (node.TryGetProperty(group, out JsonElement assets))
                        foreach (JsonProperty asset in assets.EnumerateObject())
                            if (asset.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) managed.Add(asset.Name);
                }
                if (node.TryGetProperty("dependencies", out JsonElement edges))
                    foreach (JsonProperty edge in edges.EnumerateObject()) dependencies.Add(edge.Name + ":" + edge.Value.GetString());
            }
            foreach (string file in files)
            {
                (string? rid, string? kind) = Classify(file);
                if (kind is null) continue;
                string fullPath = Resolve(directory, file);
                using FileStream asset = File.OpenRead(fullPath);
                string hash = Convert.ToHexStringLower(SHA256.HashData(asset));
                asset.Position = 0;
                string? architecture = rid?.StartsWith("win-", StringComparison.Ordinal) == true && kind == "NativeLibrary"
                    ? ReadPeArchitecture(asset) : null;
                string architectureValidation = architecture is null ? "NotInspected"
                    : rid == "win-" + architecture ? "HeaderMatchesRid" : "HeaderMismatch";
                native.Add(new(id, version, file, rid, kind, asset.Length, hash, architecture, architectureValidation, "NotRun"));
            }
            packages.Add(new(id, version, library.Value.TryGetProperty("sha512", out JsonElement packageHash) ? packageHash.GetString() : null,
                nuspec, managed.ToArray(), dependencies.ToArray()));
        }
        RuntimeCoverage[] coverage = BuildCoverage(native);
        bool ggmlNatives = native.Any(asset => IsGgml(asset.PackageId) && asset.Kind == "NativeLibrary");
        return new(1, sourceCommit, assetsHash, SupportedRids, packages, native, coverage, ggmlNatives, "NotRun");
    }

    private static RuntimeCoverage[] BuildCoverage(List<RuntimeAsset> assets)
    {
        var coverage = new List<RuntimeCoverage>();
        foreach (string rid in SupportedRids)
        {
            Add("OnnxRuntime", rid, asset => asset.PackageId == "Microsoft.ML.OnnxRuntime", "onnxruntime", "Laya CPU; provider availability is checked separately.");
            Add("Whisper", rid, asset => asset.PackageId == "Whisper.net.Runtime", "whisper", "Whisper runtime uses build-target assets, not NuGet runtimeTargets.");
            Add("Magick", rid, asset => asset.PackageId.StartsWith("Magick.NET-Q8-", StringComparison.Ordinal), "Magick.Native", "AnyCPU includes native payloads; file presence does not prove image execution.");
            Add("OpenCv", rid, asset => asset.PackageId.Contains("OpenCvSharp", StringComparison.Ordinal), "OpenCvSharpExtern", "OpenCV-dependent paths require this vendor runtime; no other architecture substitutes.");
            Add("Ggml", rid, asset => IsGgml(asset.PackageId), "GgmlOps", "Standalone consumers explicitly supply a compatible native package/plan after managed-only GGML adoption.");
        }
        return coverage.ToArray();

        void Add(string family, string rid, Func<RuntimeAsset, bool> package, string libraryName, string detail)
        {
            RuntimeAsset[] matching = assets.Where(asset => asset.Rid == rid && asset.Kind == "NativeLibrary" && package(asset)).ToArray();
            bool present = matching.Any(asset => family switch
            {
                "Whisper" => Path.GetFileName(asset.Path) is "whisper.dll" or "libwhisper.so" or "libwhisper.dylib",
                "OnnxRuntime" => Path.GetFileName(asset.Path) is "onnxruntime.dll" or "libonnxruntime.so" or "libonnxruntime.dylib",
                _ => Path.GetFileName(asset.Path).Contains(libraryName, StringComparison.OrdinalIgnoreCase)
            });
            string state = !present ? "AssetsMissing" : matching.Any(asset => asset.ArchitectureValidation == "HeaderMismatch")
                ? "AssetsPresentWithArchitectureMismatch" : "AssetsPresent";
            coverage.Add(new(family, rid, state, matching.Select(asset => asset.PackageId + "/" + asset.Version + ":" + asset.Path).ToArray(), detail, "NotRun"));
        }
    }

    private static bool IsGgml(string id) => id.StartsWith("Darkspyre.TensorSharp.Backends.GGML", StringComparison.Ordinal);

    private static (string? Rid, string? Kind) Classify(string path)
    {
        string[] parts = path.Split('/');
        if (parts.Length >= 4 && parts[0] == "runtimes" && parts[2] == "native")
            return (parts[1], IsLibrary(path) ? "NativeLibrary" : "NativeAuxiliary");
        if (parts.Length >= 3 && parts[0] == "build" && IsLibrary(path))
            return (parts[1] switch { "macos-arm64" => "osx-arm64", "macos-x64" => "osx-x64", _ => parts[1] }, "NativeLibrary");
        if (path.EndsWith(".metal", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".metallib", StringComparison.OrdinalIgnoreCase)) return ("osx-arm64", "GpuCode");
        if (path.EndsWith(".ptx", StringComparison.OrdinalIgnoreCase)) return (null, "GpuCode");
        return (null, null);
    }

    private static bool IsLibrary(string path) => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".so", StringComparison.OrdinalIgnoreCase)
        || Path.GetFileName(path).Contains(".so.", StringComparison.Ordinal);

    private static string ReadPeArchitecture(Stream stream)
    {
        using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
        return pe.PEHeaders.CoffHeader.Machine switch
        {
            Machine.Amd64 => "x64",
            Machine.Arm64 => "arm64",
            Machine.I386 => "x86",
            _ => pe.PEHeaders.CoffHeader.Machine.ToString()
        };
    }

    private static string Resolve(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains('\\') || relative.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException("Package metadata contains a nonportable path.");
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!full.StartsWith(fullRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("Package metadata path escapes its root.");
        return full;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}

internal sealed record RuntimePackage(string Id, string Version, string? PackageSha512, string ManifestPath,
    IReadOnlyList<string> ManagedAssemblies, IReadOnlyList<string> Dependencies);
internal sealed record RuntimeAsset(string PackageId, string Version, string Path, string? Rid, string Kind,
    long Length, string Sha256, string? HeaderArchitecture, string ArchitectureValidation, string RuntimeQualification);
internal sealed record RuntimeCoverage(string Family, string Rid, string State, IReadOnlyList<string> Assets,
    string Detail, string RuntimeQualification);

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(RuntimeInventory))]
internal sealed partial class InventoryJsonContext : JsonSerializerContext;
