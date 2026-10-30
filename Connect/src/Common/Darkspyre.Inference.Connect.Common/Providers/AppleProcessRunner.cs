using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Common;

namespace Darkspyre.Inference.Connect.LLM.Providers;

internal interface IAppleProcessRunner
{
    IAsyncEnumerable<JsonElement> RunAsync(string executable, AppleCommand command, AppleProcessLimits limits,
        CancellationToken cancellationToken = default);
}

// Exception to the operating-system boundaries rule: Connect keeps its macOS, Linux and Windows process handling
// inside this library and picks the OS commands at runtime, not behind a separate OS capability contract. This code is
// battle-tested on all three platforms. Brandon approved this on 2026-10-12.
internal sealed class AppleProcessRunner : IAppleProcessRunner
{
    public async IAsyncEnumerable<JsonElement> RunAsync(string executable, AppleCommand command, AppleProcessLimits limits,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsMacOSVersionAtLeast(26) || RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
            throw new InferenceException(InferenceFailureCode.BackendUnavailable, "Apple Foundation Models requires macOS 26 or later on Apple Silicon.");
        cancellationToken.ThrowIfCancellationRequested();
        string payload = JsonSerializer.Serialize(command, AppleProtocolJsonContext.Default.AppleCommand);
        if (Encoding.UTF8.GetByteCount(payload) > limits.MaximumFrameBytes)
            throw new ArgumentException("The Apple request exceeds the configured frame limit.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false, true)
        };
        start.ArgumentList.Add("--max-frame-bytes");
        start.ArgumentList.Add(limits.MaximumFrameBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        CliProcessTermination.Isolate(start);
        using var process = new Process { StartInfo = start };
        try { if (!process.Start()) throw new Win32Exception(); }
        catch (Win32Exception error)
        {
            throw new InferenceException(InferenceFailureCode.BackendUnavailable,
                "The Apple helper could not start. Install the signed helper and check its executable path.", error);
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(limits.RequestTimeoutSeconds));
        var lines = new AppleFrameReader(process.StandardOutput.BaseStream, limits.MaximumFrameBytes);
        Task<bool> stderr = DrainStderrAsync(process, limits.MaximumStderrBytes);
        try
        {
            var frames = new AppleFrameSequence(command.Id);
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            startup.CancelAfter(TimeSpan.FromSeconds(limits.StartupTimeoutSeconds));
            string? helloLine = await ReadAsync(lines, startup.Token, cancellationToken).ConfigureAwait(false);
            JsonElement hello = frames.Hello(helloLine);
            AppleProtocol.ValidateSampling(hello, command.Request);
            yield return hello;
            await WriteAsync(process, payload, deadline.Token, cancellationToken).ConfigureAwait(false);
            JsonElement? terminal = null;
            using var exitDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            while (await ReadAsync(lines, exitDeadline.Token, cancellationToken).ConfigureAwait(false) is { } line)
            {
                JsonElement frame = frames.Next(line);
                if (AppleProtocol.RequiredString(frame, "type") == "terminal")
                {
                    terminal = frame;
                    exitDeadline.CancelAfter(TimeSpan.FromSeconds(limits.CancellationGraceSeconds));
                }
                else { yield return frame; }
            }
            await WaitAsync(process, exitDeadline.Token, cancellationToken).ConfigureAwait(false);
            if (!await stderr.ConfigureAwait(false)) throw AppleProtocol.Failure("The Apple helper exceeded its stderr limit.");
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new InferenceException(InferenceFailureCode.ExecutionFailed, "The Apple helper exited unsuccessfully.");
            if (terminal is null) throw AppleProtocol.Failure("The Apple helper exited without a terminal result.");
            cancellationToken.ThrowIfCancellationRequested();
            yield return terminal.Value;
        }
        finally
        {
            if (!process.HasExited)
            {
                using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(limits.CancellationGraceSeconds));
                try
                {
                    await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(
                        new AppleCommand("cancel", command.Id), AppleProtocolJsonContext.Default.AppleCommand).AsMemory(), grace.Token).ConfigureAwait(false);
                    await process.StandardInput.FlushAsync(grace.Token).ConfigureAwait(false);
                    await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException)
                { CliProcessTermination.Signal(process); }
                if (!process.HasExited) CliProcessTermination.Signal(process);
            }
            await SettleAsync(process, stderr).ConfigureAwait(false);
        }
    }

    /// <summary>Waits a bounded time for the helper's exit and then for its standard-error reader.</summary>
    private static async Task SettleAsync(Process process, Task stderr)
    {
        try { await CliProcessTermination.WaitForExitAsync(process).ConfigureAwait(false); }
        catch (CliProcessTerminationException error)
        { throw new InferenceException(InferenceFailureCode.ExecutionFailed, "The Apple helper did not exit after it was stopped.", error); }
        // A descendant that left the process group can hold the pipe open. Disposing the process closes it.
        try { await stderr.WaitAsync(CliProcessTermination.ExitBound).ConfigureAwait(false); }
        catch (TimeoutException) { /* Standard error is diagnostic only after the exit. */ }
    }

    private static async Task<string?> ReadAsync(AppleFrameReader reader, CancellationToken deadline, CancellationToken caller)
    {
        try { return await reader.ReadAsync(deadline).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        { throw new InferenceException(InferenceFailureCode.TimedOut, "The Apple helper exceeded its configured deadline."); }
        catch (IOException) when (caller.IsCancellationRequested) { throw new OperationCanceledException(caller); }
        catch (IOException error) { throw new InferenceException(InferenceFailureCode.BackendUnavailable, "The Apple helper pipe failed.", error); }
    }

    private static async Task WriteAsync(Process process, string content, CancellationToken deadline, CancellationToken caller)
    {
        try
        {
            await process.StandardInput.WriteLineAsync(content.AsMemory(), deadline).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(deadline).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        { throw new InferenceException(InferenceFailureCode.TimedOut, "The Apple helper input deadline expired."); }
        catch (IOException) when (caller.IsCancellationRequested) { throw new OperationCanceledException(caller); }
        catch (IOException error) { throw new InferenceException(InferenceFailureCode.BackendUnavailable, "The Apple helper input pipe closed.", error); }
    }

    private static async Task WaitAsync(Process process, CancellationToken deadline, CancellationToken caller)
    {
        try { await process.WaitForExitAsync(deadline).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        { throw new InferenceException(InferenceFailureCode.TimedOut, "The Apple helper failed to exit after its terminal result."); }
    }

    private static async Task<bool> DrainStderrAsync(Process process, int maximum)
    {
        byte[] buffer = new byte[4096];
        int total = 0;
        while (true)
        {
            int count = await process.StandardError.BaseStream.ReadAsync(buffer).ConfigureAwait(false);
            if (count == 0) return true;
            total += count;
            if (total > maximum) { CliProcessTermination.Signal(process); return false; }
        }
    }
}

internal sealed class AppleFrameReader(Stream stream, int maximumBytes)
{
    private readonly byte[] _buffer = new byte[4096];
    private int _offset;
    private int _count;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal async Task<string?> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var frame = new ArrayBufferWriter<byte>();
        while (true)
        {
            if (_offset == _count)
            {
                _count = await stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
                _offset = 0;
                if (_count == 0)
                    return frame.WrittenCount == 0 ? null : throw AppleProtocol.Failure("The Apple helper sent an unterminated frame.");
            }
            int newline = Array.IndexOf(_buffer, (byte)'\n', _offset, _count - _offset);
            int end = newline < 0 ? _count : newline;
            if (frame.WrittenCount + end - _offset > maximumBytes)
                throw AppleProtocol.Failure("The Apple helper exceeded its frame limit.");
            frame.Write(_buffer.AsSpan(_offset, end - _offset));
            _offset = newline < 0 ? end : end + 1;
            if (newline >= 0)
            {
                try { return Utf8.GetString(frame.WrittenSpan); }
                catch (DecoderFallbackException) { throw AppleProtocol.Failure("The Apple helper sent invalid UTF-8."); }
            }
        }
    }
}

internal sealed class AppleFrameSequence(string requestId)
{
    private long _sequence;
    private bool _hello;
    private bool _terminal;

    internal JsonElement Hello(string? line)
    {
        JsonElement hello = Parse(line);
        if (_hello || AppleProtocol.RequiredString(hello, "type") != "hello"
            || AppleProtocol.RequiredString(hello, "protocol") != AppleProtocol.Name
            || AppleProtocol.RequiredInteger(hello, "major") != 1)
            throw AppleProtocol.Failure("The Apple helper protocol version is incompatible.");
        _ = AppleProtocol.RequiredString(hello, "helperVersion");
        _ = AppleProtocol.RequiredString(hello, "osVersion");
        _ = AppleProtocol.RequiredString(hello, "availability");
        if (AppleProtocol.RequiredInteger(hello, "contextSize") <= 0) throw AppleProtocol.Failure("Invalid Apple model context size.");
        _hello = true;
        return hello;
    }

    internal JsonElement Next(string line)
    {
        JsonElement frame = Parse(line);
        if (!_hello || _terminal || AppleProtocol.RequiredString(frame, "id") != requestId
            || AppleProtocol.RequiredInteger(frame, "sequence") != ++_sequence)
            throw AppleProtocol.Failure("The Apple helper returned an out-of-order or duplicate event.");
        string type = AppleProtocol.RequiredString(frame, "type");
        if (type is not ("text" or "structured" or "batch" or "terminal")) throw AppleProtocol.Failure("Unknown Apple helper event.");
        _terminal = type == "terminal";
        return frame;
    }

    private static JsonElement Parse(string? line)
    {
        if (line is null) throw AppleProtocol.Failure("The Apple helper closed its output unexpectedly.");
        try
        {
            using JsonDocument json = JsonDocument.Parse(line, new() { MaxDepth = 128 });
            JsonElement root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Select(static p => p.Name).Distinct(StringComparer.Ordinal).Count()
                != root.EnumerateObject().Count()) throw AppleProtocol.Failure("Invalid Apple helper envelope.");
            return root.Clone();
        }
        catch (JsonException) { throw AppleProtocol.Failure("The Apple helper returned invalid JSON."); }
    }
}
