using System.IO.Compression;
using System.Xml.Linq;

if (args.Length is < 2 or > 3)
{
    throw new ArgumentException("Usage: VerifyPackages <archive-directory> <family-version> [source-commit]");
}

string directory = Path.GetFullPath(args[0]);
string version = args[1];
const string common = "Darkspyre.Inference.Common";
Dictionary<string, string[]> owned = new(StringComparer.Ordinal)
{
    [common] = [common],
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
        "Darkspyre.Inference.Tensor.STT", "Darkspyre.Inference.Tensor.STT.Whisper"
    ],
    ["Darkspyre.Inference.UI.Settings"] = ["Darkspyre.Inference.UI.Settings"]
};

foreach ((string id, string[] assemblies) in owned)
{
    using ZipArchive archive = ZipFile.OpenRead(Path.Combine(directory, $"{id}.{version}.nupkg"));
    string[] entries = archive.Entries.Select(entry => entry.FullName).ToArray();
    string[] dlls = entries.Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).ToArray();
    string[] expected = assemblies.Select(name => $"lib/net11.0/{name}.dll").Order(StringComparer.Ordinal).ToArray();
    Require(dlls.SequenceEqual(expected, StringComparer.Ordinal), $"{id}: unexpected, missing, or embedded foreign DLLs: {string.Join(", ", dlls)}");
    Require(!entries.Any(path => path.EndsWith(".so", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".a", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".metallib", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".metal", StringComparison.OrdinalIgnoreCase)), $"{id}: third-party native binaries and shaders must come from dependency packages");
    foreach (string assembly in assemblies)
    {
        Require(entries.Contains($"lib/net11.0/{assembly}.xml", StringComparer.Ordinal), $"{id}: missing XML documentation for {assembly}");
    }
    Require(entries.Contains("README.md", StringComparer.Ordinal) && entries.Contains("LICENSE.md", StringComparer.Ordinal), $"{id}: missing README or license");
    ZipArchiveEntry manifest = archive.GetEntry(id + ".nuspec") ?? throw new InvalidDataException($"{id}: missing manifest");
    using Stream stream = manifest.Open();
    XElement root = XDocument.Load(stream).Root ?? throw new InvalidDataException($"{id}: empty manifest");
    XNamespace ns = root.Name.Namespace;
    XElement metadata = root.Element(ns + "metadata") ?? throw new InvalidDataException($"{id}: missing metadata");
    Require(metadata.Element(ns + "id")?.Value == id && metadata.Element(ns + "version")?.Value == version, $"{id}: wrong identity/version");
    if (args.Length == 3)
    {
        Require((string?)metadata.Element(ns + "repository")?.Attribute("commit") == args[2], $"{id}: source commit differs from release source");
    }
    XElement[] dependencies = metadata.Descendants(ns + "dependency").ToArray();
    XElement[] familyDependencies = dependencies.Where(dependency => ((string?)dependency.Attribute("id"))?.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal) == true).ToArray();
    Require(id == common ? familyDependencies.Length == 0 : familyDependencies.Length == 1
        && (string?)familyDependencies[0].Attribute("id") == common
        && (string?)familyDependencies[0].Attribute("version") == $"[{version}]", $"{id}: expected only the exact synchronized Common NuGet dependency");
    Require(dependencies.All(dependency => !((string?)dependency.Attribute("version") ?? string.Empty).Contains("local", StringComparison.OrdinalIgnoreCase)), $"{id}: local-only dependency leaked into release");
    Require(metadata.Descendants(ns + "group").All(group => (string?)group.Attribute("targetFramework") == "net11.0"), $"{id}: unexpected target framework");
    if (id == "Darkspyre.Inference.UI.Settings")
    {
        Require(entries.Contains("THIRD-PARTY-NOTICES.md", StringComparer.Ordinal), $"{id}: missing numeric-control attribution");
        Require(entries.Any(path => path.StartsWith("staticwebassets/", StringComparison.Ordinal) && path.EndsWith(".css", StringComparison.Ordinal)), $"{id}: missing scoped CSS");
        Require(entries.Contains("buildTransitive/" + id + ".targets", StringComparer.Ordinal), $"{id}: missing static asset import");
    }
    if (id == "Darkspyre.Inference.Tensor")
    {
        XElement runtime = dependencies.Single(item => (string?)item.Attribute("id") == "Whisper.net.Runtime");
        Require((string?)runtime.Attribute("include") == "All", $"{id}: Whisper native build assets must reach the consuming application through NuGet");
        foreach (string dependency in new[] { "Whisper.net", "Whisper.net.Runtime", "NAudio.Core", "Darkspyre.TensorSharp.Models", "Darkspyre.TensorSharp.Chat" })
        {
            Require(dependencies.Any(item => (string?)item.Attribute("id") == dependency), $"{id}: missing declared dependency {dependency}");
        }
    }
    Console.WriteLine($"Verified {id} {version}: {assemblies.Length} owned assemblies; exact family dependency boundary.");
}

static void Require(bool condition, string message)
{
    if (!condition) { throw new InvalidDataException(message); }
}
