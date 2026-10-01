using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NuGet.Versioning;

[assembly: InternalsVisibleTo("VerifyPackages.Tests")]
[assembly: InternalsVisibleTo("VerifyRuntimeInventory")]
[assembly: InternalsVisibleTo("VerifyRuntimeInventory.Tests")]

internal sealed class DependencyPolicy
{
    internal const string Ggml = "Darkspyre.TensorSharp.Backends.GGML";
    internal const string NativeGgml = Ggml + ".Native";
    internal static readonly string[] TensorDependencies =
    [
        "Darkspyre.ML.Tokenizers", "Darkspyre.TensorSharp.Backends.Cuda", "Darkspyre.TensorSharp.Chat",
        "Darkspyre.TensorSharp.Models", "Darkspyre.TensorSharp.Runtime", "Microsoft.ML.OnnxRuntime",
        "Whisper.net", "Whisper.net.Runtime", "NAudio.Core"
    ];
    private readonly Dictionary<string, NuGetVersion> _versions;

    private DependencyPolicy(Dictionary<string, NuGetVersion> versions) => _versions = versions;

    internal static DependencyPolicy ReadSourcePolicy()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string path = Path.Combine(directory.FullName, "Directory.Packages.props");
            if (File.Exists(path)) return Read(XDocument.Load(path));
            directory = directory.Parent;
        }
        throw new InvalidDataException("The verifier's source context has no authoritative Directory.Packages.props.");
    }

    internal static DependencyPolicy Read(XDocument document)
    {
        PackageVerification.Require(document.Root?.Name.LocalName == "Project", "Expected central package Project XML.");
        PackageVerification.Require(!document.Descendants().Any(element => element.Name.LocalName is "Import" or "Choose" or "Target" or "UsingTask"),
            "Imported, conditional or executable central package inputs are unsupported.");
        var versions = new Dictionary<string, NuGetVersion>(StringComparer.OrdinalIgnoreCase);
        foreach (XElement item in document.Descendants().Where(element => element.Name.LocalName == "PackageVersion"))
        {
            string? id = (string?)item.Attribute("Include");
            string? value = (string?)item.Attribute("Version");
            PackageVerification.Require(!string.IsNullOrWhiteSpace(id) && !item.HasElements
                && item.Attribute("Update") is null && item.Attribute("Remove") is null
                && !item.AncestorsAndSelf().Any(element => element.Attribute("Condition") is not null)
                && value is not null && NuGetVersion.TryParse(value, out _),
                "Central PackageVersion inputs must be unconditional literal identities and versions.");
            PackageVerification.Require(versions.TryAdd(id!, NuGetVersion.Parse(value!)), $"Ambiguous central package version: {id}.");
        }
        foreach (string id in TensorDependencies)
            PackageVerification.Require(versions.ContainsKey(id), $"Missing central package version: {id}.");
        NuGetVersion tensorSharp = versions["Darkspyre.TensorSharp.Chat"];
        PackageVerification.Require(versions.Where(item => IsTensorSharp(item.Key)).All(item => item.Value == tensorSharp),
            "Central TensorSharp package versions are not synchronized.");
        return new(versions);
    }

    internal static bool IsNativeGgml(string id) => id.Equals(NativeGgml, StringComparison.OrdinalIgnoreCase)
        || id.StartsWith(NativeGgml + ".", StringComparison.OrdinalIgnoreCase);

    private static bool IsTensorSharp(string id) => id.StartsWith("Darkspyre.TensorSharp.", StringComparison.OrdinalIgnoreCase);

    internal void ValidateDeclarations(IReadOnlyDictionary<string, XElement> dependencies)
    {
        foreach (string id in TensorDependencies)
            PackageVerification.Require(dependencies.ContainsKey(id), $"Tensor: missing declared dependency {id}.");
        foreach ((string id, XElement dependency) in dependencies)
        {
            NuGetVersion? expected = IsTensorSharp(id) ? _versions["Darkspyre.TensorSharp.Chat"] : _versions.GetValueOrDefault(id);
            if (expected is null) continue;
            string? declared = (string?)dependency.Attribute("version");
            PackageVerification.Require(declared is not null && VersionRange.TryParse(declared, out VersionRange? range)
                && range.MinVersion == expected && range.IsMinInclusive && range.Satisfies(expected),
                $"Tensor: {id} must declare the central minimum version {expected}; found {declared}.");
        }
    }

    internal void ValidateRestored(IEnumerable<(string Id, string Version)> packages)
    {
        (string Id, string Version)[] restored = packages.ToArray();
        PackageVerification.Require(!restored.Any(package => IsNativeGgml(package.Id)),
            "The restore graph contains a GGML native package, even if it has no native payload.");
        foreach (string id in TensorDependencies.Append(Ggml))
            PackageVerification.Require(restored.Any(package => package.Id.Equals(id, StringComparison.OrdinalIgnoreCase)),
                $"The restore graph is missing {id}.");
        foreach ((string id, string version) in restored)
        {
            NuGetVersion? expected = IsTensorSharp(id) ? _versions["Darkspyre.TensorSharp.Chat"] : _versions.GetValueOrDefault(id);
            if (expected is null) continue;
            PackageVerification.Require(NuGetVersion.TryParse(version, out NuGetVersion? actual) && actual == expected,
                $"The restored {id} version {version} differs from the central version {expected}.");
        }
    }
}

internal static class PackageVerification
{
    internal const string Common = "Darkspyre.Inference.Common";
    private static readonly string[] NativeOrGpuExtensions = [".dylib", ".a", ".metallib", ".metal", ".ptx", ".cubin"];
    internal static readonly IReadOnlyDictionary<string, string[]> Owned = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        [Common] = [Common],
        ["Darkspyre.Inference.Connect"] =
        [
            "Darkspyre.Inference.Connect.LLM", "Darkspyre.Inference.Connect.LLM.Bedrock", "Darkspyre.Inference.Connect.LLM.Vertex",
            "Darkspyre.Inference.Connect.Image", "Darkspyre.Inference.Connect.TTS", "Darkspyre.Inference.Connect.Audio"
        ],
        ["Darkspyre.Inference.Tensor"] =
        [
            "Darkspyre.Inference.Tensor.Common", "Darkspyre.Inference.Tensor.LLM", "Darkspyre.Inference.Tensor.LLM.Qwen",
            "Darkspyre.Inference.Tensor.Image", "Darkspyre.Inference.Tensor.Image.Supra",
            "Darkspyre.Inference.Tensor.Decision", "Darkspyre.Inference.Tensor.Decision.Laya",
            "Darkspyre.Inference.Tensor.STT", "Darkspyre.Inference.Tensor.STT.Whisper", "Darkspyre.Inference.Tensor.Registry"
        ],
        ["Darkspyre.Inference.UI.Settings"] = ["Darkspyre.Inference.UI.Settings"]
    };

    private static readonly Regex AcquisitionCommand = new(
        @"(?:^|[\s;&|""'])(?:[^\s;&|""']*[/\\])?(?:npm(?:\.cmd)?\s+(?:install|i|ci|update)\b|pnpm\s+(?:add|install|update)\b|yarn\s+(?:add|install)\b|npx(?:\.cmd)?\b|dotnet\s+tool\s+(?:install|update)\b|curl(?:\.exe)?\b|wget(?:\.exe)?\b|Invoke-WebRequest\b|Invoke-RestMethod\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CliName = new(@"claude|codex|copilot|antigravity", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AcquisitionName = new(@"install|acquire|download|bootstrap", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static void Verify(ZipArchive archive, string id, string version, string? sourceCommit, DependencyPolicy policy)
    {
        string[] assemblies = Owned[id];
        string[] entries = archive.Entries.Select(entry => entry.FullName).ToArray();
        string[] dlls = entries.Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).ToArray();
        string[] expected = assemblies.Select(name => $"lib/net11.0/{name}.dll").Order(StringComparer.Ordinal).ToArray();
        Require(dlls.SequenceEqual(expected, StringComparer.Ordinal), $"{id}: unexpected, missing, or embedded foreign DLLs: {string.Join(", ", dlls)}");
        Require(!entries.Any(IsNativeOrGpu), $"{id}: third-party native binaries and shaders must come from dependency packages");
        foreach (string assembly in assemblies)
            Require(entries.Contains($"lib/net11.0/{assembly}.xml", StringComparer.Ordinal), $"{id}: missing XML documentation for {assembly}");
        Require(entries.Contains("README.md", StringComparer.Ordinal) && entries.Contains("LICENSE.md", StringComparer.Ordinal), $"{id}: missing README or license");
        ZipArchiveEntry manifest = archive.GetEntry(id + ".nuspec") ?? throw new InvalidDataException($"{id}: missing manifest");
        using Stream stream = manifest.Open();
        XElement root = XDocument.Load(stream).Root ?? throw new InvalidDataException($"{id}: empty manifest");
        XNamespace ns = root.Name.Namespace;
        XElement metadata = root.Element(ns + "metadata") ?? throw new InvalidDataException($"{id}: missing metadata");
        Require(metadata.Element(ns + "id")?.Value == id && metadata.Element(ns + "version")?.Value == version, $"{id}: wrong identity/version");
        if (sourceCommit is not null)
            Require((string?)metadata.Element(ns + "repository")?.Attribute("commit") == sourceCommit, $"{id}: source commit differs from release source");
        XElement[] declarations = metadata.Descendants(ns + "dependency").ToArray();
        XElement[] family = declarations.Where(dependency => ((string?)dependency.Attribute("id"))?.StartsWith("Darkspyre.Inference.", StringComparison.OrdinalIgnoreCase) == true).ToArray();
        Require(id == Common ? family.Length == 0 : family.Length == 1
            && (string?)family[0].Attribute("id") == Common
            && (string?)family[0].Attribute("version") == $"[{version}]", $"{id}: expected only the exact synchronized Common NuGet dependency");
        var dependencies = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
        foreach (XElement dependency in declarations)
        {
            string? dependencyId = (string?)dependency.Attribute("id");
            Require(!string.IsNullOrWhiteSpace(dependencyId) && dependencies.TryAdd(dependencyId!, dependency), $"{id}: missing or duplicate dependency identity");
            Require(!DependencyPolicy.IsNativeGgml(dependencyId!), $"{id}: GGML native packages must be selected explicitly by the consumer");
            Require(!((string?)dependency.Attribute("version") ?? string.Empty).Contains("local", StringComparison.OrdinalIgnoreCase), $"{id}: local-only dependency leaked into release");
        }
        Require(metadata.Descendants(ns + "group").All(group => (string?)group.Attribute("targetFramework") == "net11.0"), $"{id}: unexpected target framework");
        foreach (ZipArchiveEntry entry in archive.Entries.Where(entry => entry.FullName.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
            || entry.FullName.EndsWith(".targets", StringComparison.OrdinalIgnoreCase)))
        {
            using Stream build = entry.Open();
            VerifyBuildAssets(XDocument.Load(build), entry.FullName);
        }
        if (id == "Darkspyre.Inference.UI.Settings")
        {
            Require(entries.Contains("THIRD-PARTY-NOTICES.md", StringComparer.Ordinal), $"{id}: missing numeric-control attribution");
            Require(entries.Any(path => path.StartsWith("staticwebassets/", StringComparison.Ordinal) && path.EndsWith(".css", StringComparison.Ordinal)), $"{id}: missing scoped CSS");
            Require(entries.Contains("buildTransitive/" + id + ".targets", StringComparer.Ordinal), $"{id}: missing static asset import");
        }
        if (id == "Darkspyre.Inference.Tensor")
        {
            policy.ValidateDeclarations(dependencies);
            Require((string?)dependencies["Whisper.net.Runtime"].Attribute("include") == "All", $"{id}: Whisper native build assets must reach the consuming application through NuGet");
        }
    }

    private static bool IsNativeOrGpu(string path) => path.EndsWith(".so", StringComparison.OrdinalIgnoreCase)
        || Path.GetFileName(path).Contains(".so.", StringComparison.OrdinalIgnoreCase)
        || NativeOrGpuExtensions.Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    internal static void VerifyBuildAssets(XDocument document, string path)
    {
        Require(document.Root?.Name.LocalName == "Project", $"{path}: expected MSBuild Project XML");
        foreach (XElement element in document.Descendants())
        {
            string name = element.Name.LocalName;
            string? targetName = (string?)element.Attribute("Name");
            Require(!name.Equals("DownloadFile", StringComparison.OrdinalIgnoreCase), $"{path}: build-time artifact acquisition is not allowed");
            Require(name != "Target" || targetName is null || !CliName.IsMatch(targetName) || !AcquisitionName.IsMatch(targetName),
                $"{path}: CLI acquisition target is not allowed");
            Require(name is not ("PackageDownload" or "PackageReference") || !CliName.IsMatch((string?)element.Attribute("Include") ?? string.Empty),
                $"{path}: CLI acquisition package item is not allowed");
            if (name == "Import" && Uri.TryCreate((string?)element.Attribute("Project"), UriKind.Absolute, out Uri? import))
                Require(import.Scheme is not ("http" or "https"), $"{path}: remote build import is not allowed");
            if (name == "Exec")
                Require(!AcquisitionCommand.IsMatch((string?)element.Attribute("Command") ?? string.Empty), $"{path}: acquisition command is not allowed");
            if (name == "Code" || element.Parent?.Name.LocalName == "PropertyGroup")
                Require(!AcquisitionCommand.IsMatch(element.Value), $"{path}: indirect acquisition command is not allowed");
        }
    }

    internal static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
