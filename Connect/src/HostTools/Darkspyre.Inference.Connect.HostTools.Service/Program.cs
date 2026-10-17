using Darkspyre.Inference.Connect.HostTools;
using Darkspyre.LocalServices;
using Darkspyre.LocalServices.Contracts;

// LocalServices launches the service with eight named arguments.
if (args.Length != 16) return 2;
try
{
    var values = Enumerable.Range(0, 8).ToDictionary(i => args[i * 2], i => args[(i * 2) + 1], StringComparer.Ordinal);
    var start = new ServiceStart(Enum.Parse<ServiceFamily>(values["--family"]), Guid.Parse(values["--store-id"]), values["--layout-id"],
        values["--runtime-path"], values["--store-path"], values["--package-cache-path"], values["--build-digest"], Guid.Parse(values["--attempt-id"]));
    return await HostToolsBackend.RunAsync(start);
}
catch (Exception error) when (error is ArgumentException or FormatException or KeyNotFoundException)
{
    System.Diagnostics.Trace.TraceError("The host-tools service launch arguments are invalid. {0}", error);
    return 2;
}
