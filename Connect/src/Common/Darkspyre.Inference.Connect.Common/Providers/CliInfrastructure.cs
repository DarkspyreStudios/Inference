namespace Darkspyre.Inference.Connect.LLM.Providers;

internal static class CliInfrastructure
{
    public static string ResolveExecutable(
        string endpoint,
        string command,
        string localEndpoint,
        string errorMessage,
        params string[] legacyEndpoints)
    {
        string value = endpoint.Trim();
        if (value.Length == 0
            || string.Equals(value, localEndpoint, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, command, StringComparison.OrdinalIgnoreCase)
            || legacyEndpoints.Any(candidate =>
                string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            return command;
        }

        if (string.Equals(value, $"{command}.exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, $"{command}.cmd", StringComparison.OrdinalIgnoreCase)
            || Path.IsPathFullyQualified(value))
        {
            return value;
        }

        throw new ArgumentException(errorMessage, nameof(endpoint));
    }

    /// <summary>Creates a new call folder for <paramref name="provider"/> under <paramref name="root"/>, creating the root when missing.</summary>
    public static string CreateWorkingDirectory(string root, string provider)
    {
        string path = Path.Combine(
            root,
            $"darkspyre-inference-connect-{provider}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    public static void DeleteWorkingDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort cleanup after the child process releases its files.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort cleanup; the operating system may still hold a transient handle.
        }
    }

    public static string NormalizeWorkingDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A working directory is required.", nameof(path));
        }

        string fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException(
                $"The agent working directory '{fullPath}' does not exist.");
        }

        return fullPath;
    }

    public static void ValidateThreadId(string threadId)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            throw new ArgumentException("A thread id is required.", nameof(threadId));
        }
    }
}
