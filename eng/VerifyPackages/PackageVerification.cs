using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NuGet.Versioning;

[assembly: InternalsVisibleTo("VerifyRuntimeInventory")]

internal sealed class DependencyPolicy
{
    internal const string Ggml = "TensorSharp.Backends.GGML";
    private const string TensorSharpChat = "TensorSharp.Chat";
    internal static readonly string[] TensorDependencies =
    [
        "Darkspyre.ML.Tokenizers", "TensorSharp.Backends.Cuda", Ggml, "TensorSharp.Backends.MLX", TensorSharpChat,
        "TensorSharp.Models", "TensorSharp.Runtime", "StbImageSharp", "Microsoft.ML.OnnxRuntime", "Microsoft.ML.OnnxRuntime.EP.WebGpu",
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
        PackageVerification.Require(document.Root?.Name.LocalName.Equals("Project", StringComparison.OrdinalIgnoreCase) == true, "Expected central package Project XML.");
        PackageVerification.Require(!document.Descendants().Any(element => element.Name.LocalName.ToUpperInvariant() is "IMPORT" or "CHOOSE" or "TARGET" or "USINGTASK"),
            "Imported, conditional or executable central package inputs are unsupported.");
        // A version may name one unconditional property of the same file, as TensorSharpVersion does.
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (XElement property in document.Descendants().Where(element =>
            element.Parent?.Name.LocalName.Equals("PropertyGroup", StringComparison.OrdinalIgnoreCase) == true
            && !element.AncestorsAndSelf().Any(ancestor => PackageVerification.ReadAttribute(ancestor, "Condition") is not null)))
            properties[property.Name.LocalName] = property.Value;
        var versions = new Dictionary<string, NuGetVersion>(StringComparer.OrdinalIgnoreCase);
        foreach (XElement item in document.Descendants().Where(element => element.Name.LocalName.Equals("PackageVersion", StringComparison.OrdinalIgnoreCase)))
        {
            string? id = PackageVerification.ReadAttribute(item, "Include");
            string? value = PackageVerification.ReadAttribute(item, "Version");
            if (value is ['$', '(', .., ')'] && properties.TryGetValue(value[2..^1], out string? named)) value = named;
            PackageVerification.Require(!string.IsNullOrWhiteSpace(id)
                && id.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_') && !item.HasElements
                && PackageVerification.ReadAttribute(item, "Update") is null && PackageVerification.ReadAttribute(item, "Remove") is null
                && !item.AncestorsAndSelf().Any(element => PackageVerification.ReadAttribute(element, "Condition") is not null)
                && value is not null && NuGetVersion.TryParse(value, out _),
                "Central PackageVersion inputs must be unconditional literal identities and versions.");
            PackageVerification.Require(versions.TryAdd(id!, NuGetVersion.Parse(value!)), $"Ambiguous central package version: {id}.");
        }
        foreach (string id in TensorDependencies)
            PackageVerification.Require(versions.ContainsKey(id), $"Missing central package version: {id}.");
        NuGetVersion tensorSharp = versions[TensorSharpChat];
        PackageVerification.Require(versions.Where(item => IsTensorSharp(item.Key)).All(item => item.Value == tensorSharp),
            "Central TensorSharp package versions are not synchronized.");
        return new(versions);
    }

    private static bool IsTensorSharp(string id) => id.StartsWith("TensorSharp.", StringComparison.OrdinalIgnoreCase);

    internal void ValidateDeclarations(IReadOnlyDictionary<string, XElement> dependencies, bool completeTensor = true)
    {
        foreach (string id in completeTensor ? TensorDependencies : [])
            PackageVerification.Require(dependencies.ContainsKey(id), $"Tensor: missing declared dependency {id}.");
        foreach ((string id, XElement dependency) in dependencies)
        {
            NuGetVersion? expected = IsTensorSharp(id) ? _versions[TensorSharpChat] : _versions.GetValueOrDefault(id);
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
        foreach (string id in TensorDependencies)
            PackageVerification.Require(restored.Any(package => package.Id.Equals(id, StringComparison.OrdinalIgnoreCase)),
                $"The restore graph is missing {id}.");
        foreach ((string id, string version) in restored)
        {
            NuGetVersion? expected = IsTensorSharp(id) ? _versions[TensorSharpChat] : _versions.GetValueOrDefault(id);
            if (expected is null) continue;
            PackageVerification.Require(NuGetVersion.TryParse(version, out NuGetVersion? actual) && actual == expected,
                $"The restored {id} version {version} differs from the central version {expected}.");
        }
    }
}

internal static class PackageVerification
{
    internal const string Common = "Darkspyre.Inference.Common";
    // HostTools carries Darkspyre.Common's unpublished LocalServices libraries and depends on no other Inference package.
    internal const string HostTools = "Darkspyre.Inference.Connect.HostTools";
    private static readonly string[] NativeOrGpuExtensions = [".dylib", ".a", ".metallib", ".metal", ".ptx", ".cubin"];
    internal static readonly IReadOnlyDictionary<string, string[]> Owned = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        [Common] = [Common],
        ["Darkspyre.Inference.Connect"] =
        [
            "Darkspyre.Inference.Connect.Image", "Darkspyre.Inference.Connect.TTS", "Darkspyre.Inference.Connect.Audio"
        ],
        ["Darkspyre.Inference.Connect.Common"] = ["Darkspyre.Inference.Connect.Common"],
        ["Darkspyre.Inference.Connect.Codex"] = ["Darkspyre.Inference.Connect.Codex"],
        ["Darkspyre.Inference.Connect.Claude"] = ["Darkspyre.Inference.Connect.Claude"],
        ["Darkspyre.Inference.Connect.Antigravity"] = ["Darkspyre.Inference.Connect.Antigravity"],
        [HostTools] =
        [
            HostTools, "Darkspyre.Inference.Connect.HostTools.Contracts", "Darkspyre.LocalServices.Contracts", "Darkspyre.LocalServices",
            "Darkspyre.LocalServices.Platform.MacOS", "Darkspyre.LocalServices.Platform.Linux", "Darkspyre.LocalServices.Platform.Windows"
        ],
        ["Darkspyre.Inference.Connect.Grok"] = ["Darkspyre.Inference.Connect.Grok"],
        ["Darkspyre.Inference.Connect.OpenAiApi"] = ["Darkspyre.Inference.Connect.OpenAiApi"],
        ["Darkspyre.Inference.Connect.SelfHost"] = ["Darkspyre.Inference.Connect.SelfHost"],
        ["Darkspyre.Inference.Tensor.Common"] = ["Darkspyre.Inference.Tensor.Common"],
        ["Darkspyre.Inference.Tensor.Decision.Laya"] = ["Darkspyre.Inference.Tensor.Decision.Laya"],
        ["Darkspyre.Inference.Tensor.Embedding"] = ["Darkspyre.Inference.Tensor.Embedding"],
        ["Darkspyre.Inference.Tensor.Embedding.Gemma"] = ["Darkspyre.Inference.Tensor.Embedding.Gemma"],
        ["Darkspyre.Inference.Tensor.Ggml"] = ["Darkspyre.Inference.Tensor.Ggml"],
        ["Darkspyre.Inference.Tensor.Image.Supra"] = ["Darkspyre.Inference.Tensor.Image.Supra"],
        ["Darkspyre.Inference.Tensor.LLM.Qwen"] = ["Darkspyre.Inference.Tensor.LLM.Qwen"],
        ["Darkspyre.Inference.Tensor.Registry"] = ["Darkspyre.Inference.Tensor.Registry"],
        ["Darkspyre.Inference.Tensor.STT.Whisper"] = ["Darkspyre.Inference.Tensor.STT.Whisper"],
        ["Darkspyre.Inference.UI.Settings.Core"] = ["Darkspyre.Inference.UI.Settings.Core"],
        ["Darkspyre.Inference.UI.Settings.Avalonia"] = ["Darkspyre.Inference.UI.Settings.Avalonia"],
        ["Darkspyre.Inference.UI.Settings.Blazor"] = ["Darkspyre.Inference.UI.Settings.Blazor"]
    };

    private static readonly Regex AcquisitionCommand = new(
        @"(?:^|[\s;&|""'])(?:[^\s;&|""']*[/\\])?(?:npm(?:\.cmd)?\s+(?:install|i|ci|update)\b|pnpm\s+(?:add|install|update)\b|yarn\s+(?:add|install)\b|npx(?:\.cmd)?\b|dotnet\s+tool\s+(?:install|update)\b|curl(?:\.exe)?\b|wget(?:\.exe)?\b|Invoke-WebRequest\b|Invoke-RestMethod\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CliName = new(@"claude|codex|copilot|antigravity", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AcquisitionName = new(@"install|acquire|download|bootstrap", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> CliAcquisitionPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "@anthropic-ai/claude-code", "@openai/codex", "@github/copilot", "antigravity"
    };

    internal static void Verify(ZipArchive archive, string id, string version, string? sourceCommit, DependencyPolicy policy)
    {
        string[] assemblies = Owned[id];
        string[] entries = archive.Entries.Select(entry => entry.FullName).ToArray();
        // The host-tools payloads under hosttools/<rid>/ are programs a consumer copies beside its output, never references.
        string[] dlls = entries.Where(path => path.StartsWith("lib/", StringComparison.Ordinal) && path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal).ToArray();
        string[] expected = assemblies.Select(name => $"lib/net11.0/{name}.dll").Order(StringComparer.Ordinal).ToArray();
        Require(dlls.SequenceEqual(expected, StringComparer.Ordinal), $"{id}: unexpected, missing, or embedded foreign DLLs: {string.Join(", ", dlls)}");
        Require(!entries.Any(IsNativeOrGpu), $"{id}: third-party native binaries and shaders must come from dependency packages");
        foreach (string assembly in assemblies.Where(name => name.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal)))
            Require(entries.Contains($"lib/net11.0/{assembly}.xml", StringComparer.Ordinal), $"{id}: missing XML documentation for {assembly}");
        Require(entries.Contains("README.md", StringComparer.Ordinal) && entries.Contains("LICENSE.md", StringComparer.Ordinal), $"{id}: missing README or license");
        if (id == "Darkspyre.Inference.Tensor.Image.Supra")
            Require(entries.Contains("THIRD-PARTY-NOTICES.md", StringComparer.Ordinal), $"{id}: missing third-party notice for ported TensorSharp code");
        if (id == "Darkspyre.Inference.UI.Settings.Blazor")
            Require(entries.Contains("THIRD-PARTY-NOTICES.md", StringComparer.Ordinal), $"{id}: missing third-party notice for adapted Lumeo code");
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
        Require(id is Common or HostTools ? family.Length == 0 : family.Length > 0
            && family.All(dependency => Owned.ContainsKey((string)dependency.Attribute("id")!)
                && (string?)dependency.Attribute("version") == $"[{version}]"),
            $"{id}: expected exact synchronized library dependencies");
        var dependencies = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
        foreach (XElement dependency in declarations)
        {
            string? dependencyId = (string?)dependency.Attribute("id");
            Require(!string.IsNullOrWhiteSpace(dependencyId) && dependencies.TryAdd(dependencyId!, dependency), $"{id}: missing or duplicate dependency identity");
            Require(!((string?)dependency.Attribute("version") ?? string.Empty).Contains("local", StringComparison.OrdinalIgnoreCase), $"{id}: local-only dependency leaked into release");
        }
        Require(metadata.Descendants(ns + "group").All(group => (string?)group.Attribute("targetFramework") == "net11.0"), $"{id}: unexpected target framework");
        foreach (ZipArchiveEntry entry in archive.Entries.Where(entry => entry.FullName.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
            || entry.FullName.EndsWith(".targets", StringComparison.OrdinalIgnoreCase)))
        {
            using Stream build = entry.Open();
            VerifyBuildAssets(XDocument.Load(build), entry.FullName);
        }
        if (id.StartsWith("Darkspyre.Inference.Tensor.", StringComparison.Ordinal))
        {
            policy.ValidateDeclarations(dependencies, completeTensor: false);
            if (id == "Darkspyre.Inference.Tensor.STT.Whisper") Require((string?)dependencies["Whisper.net.Runtime"].Attribute("include") == "All", $"{id}: Whisper native build assets must reach the consuming application through NuGet");
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
            string name = element.Name.LocalName.ToUpperInvariant();
            string? targetName = ReadAttribute(element, "Name");
            Require(name != "DOWNLOADFILE", $"{path}: build-time artifact acquisition is not allowed");
            Require(name != "TARGET" || targetName is null || !CliName.IsMatch(targetName) || !AcquisitionName.IsMatch(targetName),
                $"{path}: CLI acquisition target is not allowed");
            Require(name is not ("PACKAGEDOWNLOAD" or "PACKAGEREFERENCE") || !CliAcquisitionPackages.Contains(ReadAttribute(element, "Include") ?? string.Empty),
                $"{path}: CLI acquisition package item is not allowed");
            if (name == "IMPORT" && Uri.TryCreate(ReadAttribute(element, "Project"), UriKind.Absolute, out Uri? import))
                Require(import.Scheme is not ("http" or "https"), $"{path}: remote build import is not allowed");
            if (name == "EXEC")
                Require(!AcquisitionCommand.IsMatch(ReadAttribute(element, "Command") ?? string.Empty), $"{path}: acquisition command is not allowed");
            if (name == "CODE" || element.Parent?.Name.LocalName.Equals("PropertyGroup", StringComparison.OrdinalIgnoreCase) == true)
                Require(!AcquisitionCommand.IsMatch(element.Value), $"{path}: indirect acquisition command is not allowed");
        }
    }

    internal static string? ReadAttribute(XElement element, string name)
    {
        XAttribute[] attributes = element.Attributes().Where(attribute => attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        Require(attributes.Length <= 1, $"Ambiguous MSBuild attribute: {name}.");
        return attributes.SingleOrDefault()?.Value;
    }

    internal static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
