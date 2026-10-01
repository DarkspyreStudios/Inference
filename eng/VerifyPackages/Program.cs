using System.IO.Compression;

if (args.Length is < 2 or > 3)
{
    throw new ArgumentException("Usage: VerifyPackages <archive-directory> <family-version> [source-commit]");
}

string directory = Path.GetFullPath(args[0]);
string version = args[1];
DependencyPolicy policy = DependencyPolicy.ReadSourcePolicy();

foreach ((string id, string[] assemblies) in PackageVerification.Owned)
{
    using ZipArchive archive = ZipFile.OpenRead(Path.Combine(directory, $"{id}.{version}.nupkg"));
    PackageVerification.Verify(archive, id, version, args.Length == 3 ? args[2] : null, policy);
    Console.WriteLine($"Verified {id} {version}: {assemblies.Length} owned assemblies; exact family dependency boundary.");
}
