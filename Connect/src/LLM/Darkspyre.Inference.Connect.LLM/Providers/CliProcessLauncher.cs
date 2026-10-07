using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Darkspyre.Inference.Connect.LLM.Providers;

internal static partial class CliProcessLauncher
{
    internal const int MaximumBatchCommandCharacters = 8191;
    private static readonly Encoding Utf8WithoutPreamble = new UTF8Encoding(false, true);

    internal static ProcessStartInfo CreateStartInfo(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        var start = new ProcessStartInfo
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8WithoutPreamble,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        CliProcessTermination.Isolate(start);
        if (OperatingSystem.IsWindows())
        {
            ConfigureWindows(start, executable, arguments);
        }
        else
        {
            ConfigureUnix(start, executable, arguments, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }
        return start;
    }

    internal static void ConfigureUnix(ProcessStartInfo start, string executable, IReadOnlyList<string> arguments,
        string userDirectory)
    {
        if (!executable.Contains('/'))
        {
            _ = start.Environment.TryGetValue("PATH", out string? path);
            IEnumerable<string> directories = (path ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Append(Path.Combine(userDirectory, ".local", "bin"));
            foreach (string directory in directories)
            {
                if (!Path.IsPathFullyQualified(directory)) continue;
                string candidate = Path.Combine(directory, executable);
                if (File.Exists(candidate) && UnixAccess(candidate, 1) == 0)
                {
                    executable = candidate;
                    break;
                }
            }
        }
        AddExecutableArguments(start, executable, arguments);
    }

    // POSIX X_OK checks filesystem execute permission, not just a file-mode bit.
    [LibraryImport("libc", EntryPoint = "access", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int UnixAccess(string path, int mode);

    internal static void ConfigureWindows(ProcessStartInfo start, string executable, IReadOnlyList<string> arguments)
    {
        executable = ResolveWindowsExecutable(start, executable);
        if (IsBatchFile(executable))
        {
            if (TryResolveNpmNodeShim(start, executable, out string? node, out string? entry))
            {
                if (start.Environment.TryGetValue("PATHEXT", out string? extensions) && extensions is not null)
                {
                    start.Environment["PATHEXT"] = extensions.Replace(";.JS;", ";", StringComparison.OrdinalIgnoreCase);
                }
                start.ArgumentList.Add(entry);
                AddExecutableArguments(start, node, arguments);
                return;
            }

            start.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            // cmd consumes its own command grammar, not the CRT quoting applied by ArgumentList.
            start.Arguments = BuildBatchArguments(start.FileName, executable, arguments);
            return;
        }
        string extension = Path.GetExtension(executable);
        if (extension.Equals(".js", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".cjs", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mjs", StringComparison.OrdinalIgnoreCase))
        {
            string entry = Path.GetFullPath(executable);
            if (!File.Exists(entry) || !TryResolveNode(start, Path.GetDirectoryName(entry)!, out string? node))
            {
                throw new ArgumentException("The JavaScript CLI entry point and an installed Node executable are required.");
            }
            start.ArgumentList.Add(entry);
            AddExecutableArguments(start, node, arguments);
            return;
        }
        AddExecutableArguments(start, executable, arguments);
    }

    internal static string BuildBatchArguments(string interpreter, string executable, IReadOnlyList<string> arguments)
    {
        var command = new StringBuilder("/d /q /v:off /s /c \"");
        AppendBatchArgument(command, executable);
        foreach (string argument in arguments)
        {
            command.Append(' ');
            AppendBatchArgument(command, argument);
        }
        command.Append('"');
        // Include the quoted interpreter and separator in cmd's complete command-line bound.
        if ((long)interpreter.Length + 3 + command.Length > MaximumBatchCommandCharacters)
        {
            throw new ArgumentException(
                "The Windows CLI batch command exceeds the 8191-character limit. Configure the native executable or Node entry point.");
        }
        return command.ToString();
    }

    private static void AppendBatchArgument(StringBuilder command, string value)
    {
        if (value.IndexOfAny(['"', '%', '!', '\r', '\n', '\0']) >= 0 || value.EndsWith('\\'))
        {
            throw new ArgumentException(
                "Windows CLI batch arguments cannot contain quotes, percent signs, exclamation marks, line breaks, NUL, "
                + "or a trailing backslash. Configure the native executable or Node entry point.");
        }
        command.Append('"').Append(value).Append('"');
    }

    private static void AddExecutableArguments(ProcessStartInfo start, string executable, IReadOnlyList<string> arguments)
    {
        if (executable.Contains('\0') || arguments.Any(value => value.Contains('\0')))
        {
            throw new ArgumentException("CLI executable paths and arguments cannot contain NUL.");
        }
        start.FileName = executable;
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
    }

    private static bool IsBatchFile(string executable) =>
        Path.GetExtension(executable).Equals(".cmd", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(executable).Equals(".bat", StringComparison.OrdinalIgnoreCase);

    private static string ResolveWindowsExecutable(ProcessStartInfo start, string executable)
    {
        bool hasExtension = Path.HasExtension(executable);
        string[] extensions = hasExtension ? [string.Empty] : [".exe", ".com", ".cmd", ".bat", string.Empty];
        if (executable.IndexOfAny(['/', '\\']) >= 0)
        {
            foreach (string extension in extensions)
            {
                string candidate = executable + extension;
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            return executable;
        }

        _ = start.Environment.TryGetValue("PATH", out string? path);
        foreach (string directory in (path ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string root = directory.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(root)) continue;
            foreach (string extension in extensions)
            {
                string candidate = Path.Combine(root, executable + extension);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return executable;
    }

    private static bool TryResolveNpmNodeShim(
        ProcessStartInfo start,
        string executable,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? node,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? entry)
    {
        node = entry = null;
        string content;
        if (!File.Exists(executable)) return false;
        try
        {
            using var reader = new StreamReader(executable, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[8192];
            int count = reader.ReadBlock(buffer, 0, buffer.Length);
            if (count == buffer.Length) return false;
            content = new string(buffer, 0, count).Replace("\r\n", "\n", StringComparison.Ordinal);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        Match match = NpmNodeShim().Match(content);
        if (!match.Success) return false;
        string directory = Path.GetDirectoryName(Path.GetFullPath(executable))!;
        string relative = match.Groups["entry"].Value.Replace('\\', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(relative) || relative.Contains(':')) return false;
        string script = Path.GetFullPath(Path.Combine(directory, relative));
        if (!File.Exists(script) || !TryResolveNode(start, directory, out node)) return false;
        entry = script;
        return true;
    }

    private static bool TryResolveNode(
        ProcessStartInfo start,
        string directory,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? node)
    {
        string localNode = Path.Combine(directory, "node.exe");
        string candidate = File.Exists(localNode) ? localNode : ResolveWindowsExecutable(start, "node.exe");
        node = File.Exists(candidate) ? Path.GetFullPath(candidate) : null;
        return node is not null;
    }

    // Recognize the complete argless Node shim from npm/cmd-shim, never arbitrary batch instructions.
    [GeneratedRegex("""
        \A@ECHO off
        GOTO start
        :find_dp0
        SET dp0=%~dp0
        EXIT /b
        :start
        SETLOCAL
        CALL :find_dp0

        IF EXIST "%dp0%\\node\.exe" \(
          SET "_prog=%dp0%\\node\.exe"
        \) ELSE \(
          SET "_prog=node"
        \)

        endLocal & goto #_undefined_# 2>NUL \|\| title %COMSPEC% & set PATHEXT=%PATHEXT:;\.JS;=;% & "%_prog%"  "%dp0%\\(?<entry>[^"%!\r\n\0]+)" %\*
        \z
        """, RegexOptions.CultureInvariant)]
    private static partial Regex NpmNodeShim();
}
