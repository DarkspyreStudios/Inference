using System.Text.Json;

if (args.Length is < 3 or > 4 || args.Length == 4 && args[3] != "--require-managed-ggml")
{
    throw new ArgumentException("Usage: VerifyRuntimeInventory <project.assets.json> <output.json> <source-commit> [--require-managed-ggml]");
}

RuntimeInventory inventory = RuntimeInventory.Read(args[0], args[2]);
string output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllText(output, JsonSerializer.Serialize(inventory, InventoryJsonContext.Default.RuntimeInventory) + Environment.NewLine);
Console.WriteLine($"Inventoried {inventory.Packages.Count} dependency packages and {inventory.NativeAssets.Count} native/GPU assets. Runtime execution qualification: NotRun.");
foreach (RuntimeCoverage coverage in inventory.Coverage.Where(item => item.State != "AssetsPresent"))
{
    Console.WriteLine($"{coverage.Family} {coverage.Rid}: {coverage.State}. {coverage.Detail}");
}
if (args.Length == 4 && inventory.HasTransitiveGgmlNatives)
{
    Console.Error.WriteLine("The Tensor restore graph still contains GGML native payloads. Managed-only closure is not satisfied.");
    Environment.ExitCode = 1;
}
