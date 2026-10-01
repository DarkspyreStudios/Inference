using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace VerifyPackages.Tests;

public sealed class PackageVerificationTests
{
    private const string Version = "11.15.0-rc.1";
    private const string Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Tensor = "Darkspyre.Inference.Tensor";
    private const string Ui = "Darkspyre.Inference.UI.Settings";
    private static readonly IReadOnlyDictionary<string, string> Versions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Darkspyre.ML.Tokenizers"] = "2.0.0.1",
        ["Darkspyre.TensorSharp.Backends.Cuda"] = "9.1.0",
        ["Darkspyre.TensorSharp.Chat"] = "9.1.0",
        ["Darkspyre.TensorSharp.Models"] = "9.1.0",
        ["Darkspyre.TensorSharp.Runtime"] = "9.1.0",
        ["Microsoft.ML.OnnxRuntime"] = "1.30.0",
        ["Whisper.net"] = "1.9.1",
        ["Whisper.net.Runtime"] = "1.9.1",
        ["NAudio.Core"] = "2.2.1"
    };

    [Theory]
    [InlineData(PackageVerification.Common)]
    [InlineData("Darkspyre.Inference.Connect")]
    [InlineData(Tensor)]
    [InlineData(Ui)]
    public void FourArchiveBoundariesAndGeneratedUiImportsRemainValid(string id) => Verify(id);

    [Theory]
    [InlineData("native/libforeign.so")]
    [InlineData("native/libforeign.so.1")]
    [InlineData("native/libforeign.SO.1.2")]
    [InlineData("native/libforeign.dylib")]
    [InlineData("native/libforeign.a")]
    [InlineData("native/shader.metal")]
    [InlineData("native/shader.metallib")]
    [InlineData("native/shader.ptx")]
    [InlineData("native/shader.cubin")]
    [InlineData("native/foreign.dll")]
    public void ForeignNativeAndGpuEntriesAreRejected(string path) =>
        Assert.Throws<InvalidDataException>(() => Verify(PackageVerification.Common, files => files.Add(path, "fixture")));

    [Theory]
    [InlineData("[11.14.0-rc.1]")]
    [InlineData("11.15.0-rc.1")]
    [InlineData("[11.15.0-rc.1,)")]
    public void CommonMustHaveAnExactSynchronizedDeclaration(string version) =>
        Assert.Throws<InvalidDataException>(() => Verify(Tensor, manifest: metadata => Dependency(metadata, PackageVerification.Common).SetAttributeValue("version", version)));

    [Theory]
    [InlineData("Darkspyre.TensorSharp.Backends.Cuda")]
    [InlineData("Darkspyre.TensorSharp.Chat")]
    [InlineData("Darkspyre.TensorSharp.Models")]
    [InlineData("Darkspyre.TensorSharp.Runtime")]
    [InlineData("Microsoft.ML.OnnxRuntime")]
    [InlineData("Whisper.net")]
    [InlineData("Whisper.net.Runtime")]
    [InlineData("NAudio.Core")]
    public void RequiredTensorDependenciesCannotDisappear(string id) =>
        Assert.Throws<InvalidDataException>(() => Verify(Tensor, manifest: metadata => Dependency(metadata, id).Remove()));

    [Theory]
    [InlineData("Darkspyre.TensorSharp.Chat", "9.2.0")]
    [InlineData("Darkspyre.TensorSharp.Runtime", "[9.0.0,)")]
    [InlineData("Microsoft.ML.OnnxRuntime", "1.29.0")]
    [InlineData("Whisper.net", "1.9.0")]
    [InlineData("Whisper.net.Runtime", "1.9.2")]
    [InlineData("NAudio.Core", "2.2.0")]
    [InlineData("Darkspyre.TensorSharp.Models", "(9.1.0,)")]
    [InlineData("Darkspyre.TensorSharp.Models", "[9.1.0,9.0.0]")]
    [InlineData("Darkspyre.TensorSharp.Models", "not-a-range")]
    public void DeclaredMinimumMustEqualTheAuthoritativeInput(string id, string value) =>
        Assert.Throws<InvalidDataException>(() => Verify(Tensor, manifest: metadata => Dependency(metadata, id).SetAttributeValue("version", value)));

    [Theory]
    [InlineData("9.1.0")]
    [InlineData("[9.1.0]")]
    [InlineData("[9.1.0,)")]
    public void NuGetDeclarationFormsDoNotPretendToPinTheResolvedVersion(string range) =>
        Verify(Tensor, manifest: metadata => Dependency(metadata, "Darkspyre.TensorSharp.Models").SetAttributeValue("version", range));

    [Theory]
    [InlineData(DependencyPolicy.NativeGgml)]
    [InlineData(DependencyPolicy.NativeGgml + ".win-arm64")]
    [InlineData(DependencyPolicy.NativeGgml + ".linux-arm64.cuda13")]
    [InlineData("darkspyre.tensorsharp.backends.ggml.native.win-x64.vulkan")]
    public void NativeDependencyIdsAreRejectedWithoutArchivePayload(string id) =>
        Assert.Throws<InvalidDataException>(() => Verify(Tensor, manifest: metadata => Dependencies(metadata).Add(new XElement("dependency",
            new XAttribute("id", id), new XAttribute("version", "9.1.0")))));

    [Fact]
    public void OrdinaryManagedGgmlDependencyIsAllowed() => Verify(Tensor, manifest: metadata => Dependencies(metadata).Add(
        new XElement("dependency", new XAttribute("id", DependencyPolicy.Ggml), new XAttribute("version", "9.1.0"))));

    [Theory]
    [InlineData("None")]
    [InlineData("Runtime, Compile")]
    public void WhisperRuntimeBuildAssetsStillFlowToConsumers(string include) =>
        Assert.Throws<InvalidDataException>(() => Verify(Tensor, manifest: metadata => Dependency(metadata, "Whisper.net.Runtime").SetAttributeValue("include", include)));

    [Theory]
    [InlineData("missing-dll")]
    [InlineData("missing-xml")]
    [InlineData("missing-readme")]
    [InlineData("missing-license")]
    [InlineData("wrong-version")]
    [InlineData("wrong-commit")]
    [InlineData("wrong-framework")]
    [InlineData("local-dependency")]
    [InlineData("duplicate-dependency")]
    public void ExistingArchiveInvariantsRemainEnforced(string failure)
    {
        Assert.Throws<InvalidDataException>(() => Verify(Tensor, files =>
        {
            if (failure == "missing-dll") files.Remove("lib/net11.0/" + PackageVerification.Owned[Tensor][0] + ".dll");
            if (failure == "missing-xml") files.Remove("lib/net11.0/" + PackageVerification.Owned[Tensor][0] + ".xml");
            if (failure == "missing-readme") files.Remove("README.md");
            if (failure == "missing-license") files.Remove("LICENSE.md");
        }, metadata =>
        {
            if (failure == "wrong-version") metadata.Element("version")!.Value = "11.14.0-rc.1";
            if (failure == "wrong-commit") metadata.Element("repository")!.SetAttributeValue("commit", new string('b', 40));
            if (failure == "wrong-framework") Dependencies(metadata).SetAttributeValue("targetFramework", "net10.0");
            if (failure == "local-dependency") Dependency(metadata, "NAudio.Core").SetAttributeValue("version", "2.2.1-local");
            if (failure == "duplicate-dependency") Dependencies(metadata).Add(new XElement(Dependency(metadata, "NAudio.Core")));
        }));
    }

    [Theory]
    [InlineData("<DownloadFile SourceUrl='https://example.invalid/cli' DestinationFile='cli' />")]
    [InlineData("<downloadfile SourceUrl='https://example.invalid/cli' DestinationFile='cli' />")]
    [InlineData("<Target Name='Run'><exec Command='npx @github/copilot' /></Target>")]
    [InlineData("<Target Name='Run'><Exec command='npx @github/copilot' /></Target>")]
    [InlineData("<ItemGroup><packagedownload Include='@anthropic-ai/claude-code' /></ItemGroup>")]
    [InlineData("<Target Name='InstallClaudeCli'><Message Text='acquire' /></Target>")]
    [InlineData("<Target Name='AcquireCodex' />")]
    [InlineData("<Target Name='DownloadCopilot' />")]
    [InlineData("<Target Name='BootstrapAntigravity' />")]
    [InlineData("<ItemGroup><PackageDownload Include='@anthropic-ai/claude-code' /></ItemGroup>")]
    [InlineData("<ItemGroup><PackageReference Include='@openai/codex' /></ItemGroup>")]
    [InlineData("<ItemGroup><PackageDownload Include='@github/copilot' /></ItemGroup>")]
    [InlineData("<ItemGroup><PackageDownload Include='antigravity' /></ItemGroup>")]
    [InlineData("<Import Project='https://example.invalid/acquire.targets' />")]
    [InlineData("<PropertyGroup><Acquire>npm install @github/copilot</Acquire></PropertyGroup><Target Name='Run'><Exec Command='$(Acquire)' /></Target>")]
    [InlineData("<UsingTask TaskName='Acquire' TaskFactory='RoslynCodeTaskFactory'><Task><Code>Invoke-WebRequest https://example.invalid/cli</Code></Task></UsingTask>")]
    public void AcquisitionBuildAssetsFailArchiveVerification(string xml) =>
        Assert.Throws<InvalidDataException>(() => Verify(PackageVerification.Common,
            files => files.Add("buildTransitive/acquisition.targets", "<Project>" + xml + "</Project>")));

    [Theory]
    [InlineData("npm install @anthropic-ai/claude-code")]
    [InlineData("npm.cmd i @openai/codex")]
    [InlineData("pnpm add @github/copilot")]
    [InlineData("yarn install")]
    [InlineData("npx @github/copilot")]
    [InlineData("dotnet tool install Cli")]
    [InlineData("dotnet tool update Cli")]
    [InlineData("/usr/bin/curl -o cli https://example.invalid/cli")]
    [InlineData("wget https://example.invalid/cli")]
    [InlineData("powershell -Command Invoke-WebRequest https://example.invalid/cli")]
    public void KnownAcquisitionExecPatternsFailRegardlessOfTargetName(string command) =>
        Assert.Throws<InvalidDataException>(() => PackageVerification.VerifyBuildAssets(new XDocument(new XElement("Project",
            new XElement("Target", new XAttribute("Name", "Prepare"), new XElement("Exec", new XAttribute("Command", command))))), "fixture.targets"));

    [Fact]
    public void LocalManagedImportsPropertiesAndCopyTargetsRemainUsable()
    {
        PackageVerification.VerifyBuildAssets(XDocument.Parse("""
            <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <Import Project="../build/managed.targets" />
              <PropertyGroup><Output>$(MSBuildThisFileDirectory)assets</Output></PropertyGroup>
              <ItemGroup><PackageReference Include="GitHub.Copilot.SDK" Version="1.0.0" /></ItemGroup>
              <ItemGroup><None Include="$(Output)/fixture.txt" CopyToOutputDirectory="PreserveNewest" /></ItemGroup>
              <Target Name="CopyAssets"><Copy SourceFiles="@(None)" DestinationFolder="$(OutDir)" /><Message Text="CLI is configured externally" /></Target>
            </Project>
            """), "managed.targets");
    }

    [Fact]
    public async Task CurrentUiGeneratedBuildInputsPassWithoutCreatingAnArchive()
    {
        DirectoryInfo? repository = new(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "Directory.Packages.props"))) repository = repository.Parent;
        Assert.NotNull(repository);
        string scratch = Path.Combine(repository.FullName, "tmp");
        Directory.CreateDirectory(scratch);
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repository.FullName,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.Environment["TMPDIR"] = scratch;
        start.ArgumentList.Add("msbuild");
        start.ArgumentList.Add("UI/src/Settings/Darkspyre.Inference.UI.Settings/Darkspyre.Inference.UI.Settings.csproj");
        start.ArgumentList.Add("-target:ResolveStaticWebAssetsConfiguration,GenerateStaticWebAssetsPackFiles");
        start.ArgumentList.Add("-getItem:StaticWebAssetPackageFile");
        start.ArgumentList.Add("-nologo");
#if DEBUG
        start.ArgumentList.Add("-p:Configuration=Debug");
#else
        start.ArgumentList.Add("-p:Configuration=Release");
#endif
        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        Assert.True(process.ExitCode == 0, await error);
        using JsonDocument document = JsonDocument.Parse(await output);
        JsonElement[] targets = document.RootElement.GetProperty("Items").GetProperty("StaticWebAssetPackageFile").EnumerateArray()
            .Where(item => item.GetProperty("Extension").GetString() == ".targets").ToArray();
        Assert.Equal(4, targets.Length);
        foreach (JsonElement target in targets)
            PackageVerification.VerifyBuildAssets(XDocument.Load(target.GetProperty("FullPath").GetString()!), target.GetProperty("PackagePath").GetString()!);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("conditional-item")]
    [InlineData("conditional-group")]
    [InlineData("computed")]
    [InlineData("update")]
    [InlineData("import")]
    [InlineData("mismatched-supplier")]
    [InlineData("child-metadata")]
    [InlineData("executable")]
    [InlineData("computed-id")]
    [InlineData("wildcard-id")]
    public void UnsupportedCentralInputsFailClosed(string failure)
    {
        XDocument document = PolicyDocument();
        XElement item = document.Descendants("PackageVersion").Single(element => (string?)element.Attribute("Include") == "Darkspyre.TensorSharp.Chat");
        if (failure == "missing") item.Remove();
        if (failure == "duplicate") item.Parent!.Add(new XElement(item));
        if (failure == "conditional-item") item.SetAttributeValue("Condition", "'$(Configuration)' == 'Debug'");
        if (failure == "conditional-group") item.Parent!.SetAttributeValue("Condition", "'$(Configuration)' == 'Debug'");
        if (failure == "computed") item.SetAttributeValue("Version", "$(SupplierVersion)");
        if (failure == "update") { item.SetAttributeValue("Update", "Darkspyre.TensorSharp.Chat"); item.Attribute("Include")!.Remove(); }
        if (failure == "import") document.Root!.Add(new XElement("Import", new XAttribute("Project", "supplier.props")));
        if (failure == "mismatched-supplier") item.SetAttributeValue("Version", "9.2.0");
        if (failure == "child-metadata") item.Add(new XElement("Version", "9.2.0"));
        if (failure == "executable") document.Root!.Add(new XElement("Target", new XAttribute("Name", "ChangeVersions")));
        if (failure == "computed-id") item.Parent!.Add(new XElement("PackageVersion", new XAttribute("Include", "$(SupplierPackage)"), new XAttribute("Version", "9.2.0")));
        if (failure == "wildcard-id") item.Parent!.Add(new XElement("PackageVersion", new XAttribute("Include", "Darkspyre.TensorSharp.*"), new XAttribute("Version", "9.2.0")));
        Assert.Throws<InvalidDataException>(() => DependencyPolicy.Read(document));
    }

    [Fact]
    public void SourcePolicyUsesActualCentralInputs() => Assert.NotNull(DependencyPolicy.ReadSourcePolicy());

    [Fact]
    public void ExactResolvedVersionsAreVerifiedSeparatelyFromMinimumDeclarations()
    {
        DependencyPolicy policy = DependencyPolicy.Read(PolicyDocument());
        (string Id, string Version)[] packages = Versions.Select(pair => (pair.Key, pair.Value)).Append((DependencyPolicy.Ggml, "9.1.0")).ToArray();
        policy.ValidateRestored(packages);
        Assert.Throws<InvalidDataException>(() => policy.ValidateRestored(packages.Select(package =>
            package.Id == "Darkspyre.TensorSharp.Models" ? (package.Id, "9.2.0") : package)));
        Assert.Throws<InvalidDataException>(() => policy.ValidateRestored(packages.Where(package => package.Id != DependencyPolicy.Ggml)));
        Assert.Throws<InvalidDataException>(() => policy.ValidateRestored(packages.Append((DependencyPolicy.NativeGgml + ".win-arm64", "9.1.0"))));
    }

    [Theory]
    [InlineData("Darkspyre.ML.Tokenizers")]
    [InlineData("Darkspyre.TensorSharp.Backends.Cuda")]
    [InlineData("Darkspyre.TensorSharp.Chat")]
    [InlineData("Darkspyre.TensorSharp.Models")]
    [InlineData("Darkspyre.TensorSharp.Runtime")]
    [InlineData(DependencyPolicy.Ggml)]
    [InlineData("Microsoft.ML.OnnxRuntime")]
    [InlineData("Whisper.net")]
    [InlineData("Whisper.net.Runtime")]
    [InlineData("NAudio.Core")]
    public void EveryRequiredRestoredDependencyMustBePresentAtItsExactVersion(string id)
    {
        DependencyPolicy policy = DependencyPolicy.Read(PolicyDocument());
        (string Id, string Version)[] packages = Versions.Select(pair => (pair.Key, pair.Value)).Append((DependencyPolicy.Ggml, "9.1.0")).ToArray();
        Assert.Throws<InvalidDataException>(() => policy.ValidateRestored(packages.Where(package => package.Id != id)));
        Assert.Throws<InvalidDataException>(() => policy.ValidateRestored(packages.Select(package =>
            package.Id == id ? (package.Id, "99.0.0") : package)));
    }

    private static XDocument PolicyDocument() => new(new XElement("Project", new XElement("ItemGroup",
        Versions.Select(pair => new XElement("PackageVersion", new XAttribute("Include", pair.Key), new XAttribute("Version", pair.Value))))));

    private static XElement Dependencies(XElement metadata) => metadata.Element("dependencies")!.Element("group")!;
    private static XElement Dependency(XElement metadata, string id) => Dependencies(metadata).Elements("dependency").Single(element => (string?)element.Attribute("id") == id);

    private static void Verify(string id, Action<Dictionary<string, string>>? entries = null, Action<XElement>? manifest = null)
    {
        XElement metadata = new("metadata", new XElement("id", id), new XElement("version", Version), new XElement("repository", new XAttribute("commit", Commit)));
        XElement group = new("group", new XAttribute("targetFramework", "net11.0"));
        metadata.Add(new XElement("dependencies", group));
        if (id != PackageVerification.Common)
            group.Add(new XElement("dependency", new XAttribute("id", PackageVerification.Common), new XAttribute("version", "[" + Version + "]")));
        if (id == Tensor)
            foreach ((string dependency, string version) in Versions)
                group.Add(new XElement("dependency", new XAttribute("id", dependency), new XAttribute("version", version), new XAttribute("include", "All")));
        manifest?.Invoke(metadata);
        var files = new Dictionary<string, string>(StringComparer.Ordinal) { ["README.md"] = "fixture", ["LICENSE.md"] = "fixture" };
        foreach (string assembly in PackageVerification.Owned[id])
        {
            files.Add("lib/net11.0/" + assembly + ".dll", "fixture");
            files.Add("lib/net11.0/" + assembly + ".xml", "<doc />");
        }
        if (id == Ui)
        {
            files.Add("THIRD-PARTY-NOTICES.md", "fixture");
            files.Add("staticwebassets/settings.css", "fixture");
            files.Add("buildTransitive/" + Ui + ".targets", "<Project><Import Project='../buildMultiTargeting/" + Ui + ".targets' /></Project>");
            files.Add("buildMultiTargeting/" + Ui + ".targets", "<Project><Import Project='../build/" + Ui + ".targets' /></Project>");
            files.Add("build/" + Ui + ".targets", "<Project><Import Project='Microsoft.AspNetCore.StaticWebAssets.targets' /></Project>");
            files.Add("build/Microsoft.AspNetCore.StaticWebAssets.targets", "<Project><ItemGroup><StaticWebAssetPackageManifest Include='$(MSBuildThisFileDirectory)assets.json' /></ItemGroup></Project>");
        }
        files.Add(id + ".nuspec", new XDocument(new XElement("package", metadata)).ToString());
        entries?.Invoke(files);
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach ((string path, string content) in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        buffer.Position = 0;
        using var archive = new ZipArchive(buffer, ZipArchiveMode.Read);
        PackageVerification.Verify(archive, id, Version, Commit, DependencyPolicy.Read(PolicyDocument()));
    }
}
