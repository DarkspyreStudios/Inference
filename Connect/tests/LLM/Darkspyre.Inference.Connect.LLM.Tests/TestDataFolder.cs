using System.Security.AccessControl;
using System.Security.Principal;

namespace Darkspyre.Inference.Connect.LLM.Tests;

/// <summary>The host data folder of a test: its own temporary folder, deleted with it.</summary>
internal sealed class TestDataFolder : ILlmDataFolder, IDisposable
{
    internal string Path { get; } = Directory.CreateTempSubdirectory("connect-data-").FullName;

    public ValueTask<string> ReadAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(Path);

    public void Dispose()
    {
        // Installed service packages seal their directories. Restore this test's delete rights before removing them.
        foreach (string directory in Directory.EnumerateDirectories(Path, "*", SearchOption.AllDirectories).Prepend(Path))
        {
            if (OperatingSystem.IsWindows())
            {
                var info = new DirectoryInfo(directory);
                DirectorySecurity access = info.GetAccessControl(AccessControlSections.Access);
                using WindowsIdentity user = WindowsIdentity.GetCurrent();
                access.AddAccessRule(new FileSystemAccessRule(user.User!, FileSystemRights.FullControl, AccessControlType.Allow));
                info.SetAccessControl(access);
            }
            else File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        Directory.Delete(Path, recursive: true);
    }
}
