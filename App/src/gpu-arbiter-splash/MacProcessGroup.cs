using System.Collections;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GpuArbiter.Splash;

public interface ISplashProcess : IAsyncDisposable
{
    int Pid { get; }
    Task<int> Exit { get; }
    bool GroupAlive { get; }
    Task StopAsync(TimeSpan timeout, CancellationToken token);
}

public interface ISplashProcessFactory
{
    ISplashProcess Start(SplashOptions options, SplashModelOptions model);
}

public sealed class MacProcessFactory : ISplashProcessFactory
{
    public ISplashProcess Start(SplashOptions options, SplashModelOptions model)
    {
        Directory.CreateDirectory(options.ScratchDir);
        return MacProcessGroup.Start(options.Python, options.Arguments(model), new Dictionary<string, string>
        { ["PYTHONDONTWRITEBYTECODE"] = "1", ["TMPDIR"] = options.ScratchDir });
    }
}

public sealed class MacProcessGroup : ISplashProcess
{
    private const string Lib = "/usr/lib/libSystem.B.dylib";
    private readonly Task _output;
    private readonly Task _error;
    private bool _released;
    public int Pid { get; }
    public Task<int> Exit { get; }
    public bool GroupAlive
    {
        get
        {
            if (_released) return false;
            if (kill(-Pid, 0) == 0) return true;
            var error = Marshal.GetLastPInvokeError();
            if (error == 3) return false; // ESRCH: no member remains in the owned group.
            throw new InvalidOperationException($"Cannot inspect owned process group; errno {error}.");
        }
    }

    private MacProcessGroup(int pid, int stdout, int stderr)
    {
        Pid = pid;
        _output = DrainAsync(stdout);
        _error = DrainAsync(stderr);
        Exit = Task.Run(() =>
        {
            int result, status;
            do { result = waitpid(pid, out status, 0); } while (result < 0 && Marshal.GetLastPInvokeError() == 4);
            if (result != pid) throw new InvalidOperationException("Cannot reap owned Splash server.");
            return (status & 0x7f) == 0 ? (status >> 8) & 0xff : 128 + (status & 0x7f);
        });
    }

    internal static MacProcessGroup Start(string executable, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> overrides)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Splash requires Apple Silicon macOS.");
        nint attributes = 0, actions = 0;
        int[] stdout = [-1, -1], stderr = [-1, -1];
        var transferred = false;
        try
        {
            Check(posix_spawnattr_init(ref attributes));
            Check(posix_spawnattr_setflags(ref attributes, 0x0002));
            Check(posix_spawnattr_setpgroup(ref attributes, 0));
            Check(posix_spawn_file_actions_init(ref actions));
            if (pipe(stdout) != 0 || pipe(stderr) != 0) throw new InvalidOperationException("Cannot create Splash output pipes.");
            foreach (var fd in stdout.Concat(stderr))
                if (fcntl(fd, 2, 1) < 0) throw new InvalidOperationException("Cannot protect Splash output descriptor.");
            Check(posix_spawn_file_actions_addopen(ref actions, 0, "/dev/null", 0, 0));
            Check(posix_spawn_file_actions_adddup2(ref actions, stdout[1], 1));
            Check(posix_spawn_file_actions_adddup2(ref actions, stderr[1], 2));
            foreach (var fd in stdout.Concat(stderr)) Check(posix_spawn_file_actions_addclose(ref actions, fd));
            var environment = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
                .ToDictionary(e => (string)e.Key, e => (string)e.Value!, StringComparer.Ordinal);
            foreach (var pair in overrides) environment[pair.Key] = pair.Value;
            // Ambient runtime authentication must not change the private loopback endpoint.
            environment.Remove("SPLASH_API_KEY");
            using var argv = new NativeStrings(new[] { executable }.Concat(arguments));
            using var env = new NativeStrings(environment.Select(e => $"{e.Key}={e.Value}"));
            Check(posix_spawn(out var pid, executable, ref actions, ref attributes, argv.Pointer, env.Pointer));
            close(stdout[1]); stdout[1] = -1;
            close(stderr[1]); stderr[1] = -1;
            var process = new MacProcessGroup(pid, stdout[0], stderr[0]);
            transferred = true;
            return process;
        }
        finally
        {
            if (attributes != 0) posix_spawnattr_destroy(ref attributes);
            if (actions != 0) posix_spawn_file_actions_destroy(ref actions);
            foreach (var fd in transferred ? new[] { stdout[1], stderr[1] } : stdout.Concat(stderr))
                if (fd >= 0) close(fd);
        }
    }

    private static async Task DrainAsync(int fd)
    {
        using var handle = new SafeFileHandle((nint)fd, ownsHandle: true);
        await using var stream = new FileStream(handle, FileAccess.Read);
        // Child output is untrusted provider text. Drain bytes without retaining or logging them.
        var buffer = new byte[8192];
        while (await stream.ReadAsync(buffer).ConfigureAwait(false) != 0) { }
    }

    public async Task StopAsync(TimeSpan timeout, CancellationToken token)
    {
        if (_released) return;
        Signal(15);
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (GroupAlive && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(50, token).ConfigureAwait(false);
        if (GroupAlive)
        {
            Signal(9);
            deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (GroupAlive && DateTimeOffset.UtcNow < deadline)
                await Task.Delay(50, token).ConfigureAwait(false);
        }
        if (GroupAlive) throw new InvalidOperationException("Owned Splash process group survives termination.");
        await Exit.WaitAsync(token).ConfigureAwait(false);
        await Task.WhenAll(_output, _error).WaitAsync(token).ConfigureAwait(false);
        _released = true;
    }

    private void Signal(int signal)
    {
        if (kill(-Pid, signal) != 0 && Marshal.GetLastPInvokeError() != 3)
            throw new InvalidOperationException("Cannot signal owned Splash process group.");
    }

    public async ValueTask DisposeAsync() => await StopAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
    private static void Check(int error) { if (error != 0) throw new InvalidOperationException($"Mac process operation failed; errno {error}."); }

    private sealed class NativeStrings : IDisposable
    {
        private readonly nint[] _strings;
        public nint Pointer { get; }
        public NativeStrings(IEnumerable<string> values)
        {
            _strings = values.Select(Marshal.StringToCoTaskMemUTF8).ToArray();
            Pointer = Marshal.AllocHGlobal((_strings.Length + 1) * nint.Size);
            for (var i = 0; i < _strings.Length; i++) Marshal.WriteIntPtr(Pointer, i * nint.Size, _strings[i]);
            Marshal.WriteIntPtr(Pointer, _strings.Length * nint.Size, 0);
        }
        public void Dispose()
        {
            foreach (var value in _strings) Marshal.FreeCoTaskMem(value);
            Marshal.FreeHGlobal(Pointer);
        }
    }

    [DllImport(Lib)] private static extern int posix_spawnattr_init(ref nint attr);
    [DllImport(Lib)] private static extern int posix_spawnattr_destroy(ref nint attr);
    [DllImport(Lib)] private static extern int posix_spawnattr_setflags(ref nint attr, short flags);
    [DllImport(Lib)] private static extern int posix_spawnattr_setpgroup(ref nint attr, int group);
    [DllImport(Lib)] private static extern int posix_spawn_file_actions_init(ref nint actions);
    [DllImport(Lib)] private static extern int posix_spawn_file_actions_destroy(ref nint actions);
    [DllImport(Lib)] private static extern int posix_spawn_file_actions_adddup2(ref nint actions, int fd, int target);
    [DllImport(Lib)] private static extern int posix_spawn_file_actions_addclose(ref nint actions, int fd);
    [DllImport(Lib)] private static extern int posix_spawn_file_actions_addopen(ref nint actions, int fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, int mode);
    [DllImport(Lib)] private static extern int posix_spawn(out int pid, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, ref nint actions, ref nint attr, nint argv, nint env);
    [DllImport(Lib, SetLastError = true)] private static extern int pipe([Out] int[] descriptors);
    [DllImport(Lib, SetLastError = true)] private static extern int fcntl(int fd, int command, int flags);
    [DllImport(Lib)] private static extern int close(int fd);
    [DllImport(Lib, SetLastError = true)] private static extern int kill(int pid, int signal);
    [DllImport(Lib, SetLastError = true)] private static extern int waitpid(int pid, out int status, int options);
}
