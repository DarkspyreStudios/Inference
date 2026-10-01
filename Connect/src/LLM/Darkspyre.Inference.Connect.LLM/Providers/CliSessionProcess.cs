using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Text;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>Owns a session process, its active pipe operations and its bounded stderr reader.</summary>
internal sealed class CliSessionProcess : IAsyncDisposable
{
    private static readonly TimeSpan GracefulExit = TimeSpan.FromSeconds(2);
    internal const int MaximumLineCharacters = 16 * 1024 * 1024;
    private readonly Process _process;
    private readonly CliLineReader _standardOutput;
    private readonly string _name;
    private readonly int _maximumErrorCharacters;
    private readonly bool _truncateError;
    private readonly LlmOperationLifetime _lifetime = new();
    private readonly CancellationTokenSource _errorRead = new();
    private readonly CancellationTokenSource _errorFailure = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _readLock = new(1, 1);
    private readonly Task<string> _standardError;
    private bool _inputClosed;

    internal CliSessionProcess(Process process, string name, int maximumErrorCharacters, bool truncateError)
    {
        _process = process;
        _name = name;
        _standardOutput = new(process.StandardOutput, MaximumLineCharacters,
            $"{name} output exceeded the {MaximumLineCharacters}-character line limit.");
        _maximumErrorCharacters = maximumErrorCharacters;
        _truncateError = truncateError;
        _standardError = CaptureErrorAsync();
    }

    internal async ValueTask WriteAsync(string text, bool newline, CancellationToken cancellationToken)
    {
        using LlmOperationLifetime.Lease operation = _lifetime.Enter(cancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(operation.Token, _errorFailure.Token);
        try
        {
            await _writeLock.WaitAsync(cancellation.Token).ConfigureAwait(false);
            try
            {
                if (_inputClosed) throw new IOException("The CLI input pipe is closed.");
                if (newline) await _process.StandardInput.WriteLineAsync(text.AsMemory(), cancellation.Token).ConfigureAwait(false);
                else await _process.StandardInput.WriteAsync(text.AsMemory(), cancellation.Token).ConfigureAwait(false);
                await _process.StandardInput.FlushAsync(cancellation.Token).ConfigureAwait(false);
                cancellation.Token.ThrowIfCancellationRequested();
            }
            finally { _writeLock.Release(); }
        }
        catch (OperationCanceledException) when (!operation.Token.IsCancellationRequested && _errorFailure.IsCancellationRequested)
        {
            await _standardError.ConfigureAwait(false);
            throw;
        }
    }

    internal void CloseInput()
    {
        using LlmOperationLifetime.Lease operation = _lifetime.Enter(CancellationToken.None);
        if (!_writeLock.Wait(0)) throw new InvalidOperationException("The CLI input cannot close during an active write.");
        try { CloseInputCore(); }
        finally { _writeLock.Release(); }
    }

    internal async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        using LlmOperationLifetime.Lease operation = _lifetime.Enter(cancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(operation.Token, _errorFailure.Token);
        try
        {
            await _readLock.WaitAsync(cancellation.Token).ConfigureAwait(false);
            try
            {
                string? line = await _standardOutput.ReadLineAsync(cancellation.Token).ConfigureAwait(false);
                cancellation.Token.ThrowIfCancellationRequested();
                return line;
            }
            finally { _readLock.Release(); }
        }
        catch (OperationCanceledException) when (!operation.Token.IsCancellationRequested && _errorFailure.IsCancellationRequested)
        {
            await _standardError.ConfigureAwait(false);
            throw;
        }
    }

    internal async Task<(int ExitCode, string StandardError)> WaitForExitAsync(CancellationToken cancellationToken)
    {
        using LlmOperationLifetime.Lease operation = _lifetime.Enter(cancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(operation.Token, _errorFailure.Token);
        try
        {
            await _process.WaitForExitAsync(cancellation.Token).ConfigureAwait(false);
            string error;
            try { error = await _standardError.WaitAsync(CliProcessTermination.ExitBound, cancellation.Token).ConfigureAwait(false); }
            catch (TimeoutException) { throw new CliProcessDrainException(_process.Id); }
            cancellation.Token.ThrowIfCancellationRequested();
            return (_process.ExitCode, error);
        }
        catch (OperationCanceledException) when (!operation.Token.IsCancellationRequested && _errorFailure.IsCancellationRequested)
        {
            await _standardError.ConfigureAwait(false);
            throw;
        }
    }

    internal async Task KillAsync()
    {
        using LlmOperationLifetime.Lease operation = _lifetime.Enter(CancellationToken.None);
        await CliProcessTermination.KillAsync(_process).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => new(_lifetime.RetireAsync(RetireProcessAsync));

    private async Task<string> CaptureErrorAsync()
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        try
        {
            int count;
            while ((count = await _process.StandardError.ReadAsync(buffer.AsMemory(), _errorRead.Token).ConfigureAwait(false)) != 0)
            {
                int remaining = _maximumErrorCharacters - text.Length;
                if (!_truncateError && count > remaining)
                    throw new InvalidOperationException($"{_name} StandardError exceeded the {_maximumErrorCharacters}-character capture limit.");
                if (remaining > 0) text.Append(buffer, 0, Math.Min(remaining, count));
            }
            return text.ToString();
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or DecoderFallbackException)
        {
            await _errorFailure.CancelAsync().ConfigureAwait(false);
            throw;
        }
    }

    [SuppressMessage("Design", "CA1031", Justification = "Retirement observes every cleanup failure and reports them together to all disposal callers.")]
    private async Task RetireProcessAsync()
    {
        var failures = new List<Exception>();
        try
        {
            // A cancelled write can leave StreamWriter's buffer full. Close the native pipe first
            // so retirement never flushes that abandoned request into a child that stopped reading.
            try { CloseInputCore(); }
            catch (Exception error) { failures.Add(error); }
            if (!_process.HasExited)
            {
                using var grace = new CancellationTokenSource(GracefulExit);
                try
                {
                    if (_standardError.IsFaulted) CliProcessTermination.Signal(_process);
                    else await _process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (grace.IsCancellationRequested) { CliProcessTermination.Signal(_process); }
            }
            // Both exit and pipe EOF must settle in the same post-signal bound.
            using var bound = new CancellationTokenSource(CliProcessTermination.ExitBound);
            try { await _process.WaitForExitAsync(bound.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (bound.IsCancellationRequested) { throw new CliProcessTerminationException(_process.Id); }
            try { await _standardError.WaitAsync(bound.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (bound.IsCancellationRequested) { throw new CliProcessDrainException(_process.Id); }
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            try { await _errorRead.CancelAsync().ConfigureAwait(false); }
            catch (Exception error) { failures.Add(error); }
            try { await _standardError.WaitAsync(CliProcessTermination.ExitBound).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_errorRead.IsCancellationRequested) { }
            catch (Exception error)
            {
                if (!failures.Contains(error)) failures.Add(error);
                _ = _standardError.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            _writeLock.Dispose();
            _readLock.Dispose();
            _standardOutput.ReleaseBufferedText();
            try { _process.Dispose(); }
            catch (Exception error) { failures.Add(error); }
            _errorRead.Dispose();
            _errorFailure.Dispose();
        }
        if (failures.Count == 1) ExceptionDispatchInfo.Throw(failures[0]);
        if (failures.Count > 1) throw new AggregateException("The CLI session did not retire cleanly.", failures);
    }

    private void CloseInputCore()
    {
        if (_inputClosed) return;
        _inputClosed = true;
        try { _process.StandardInput.BaseStream.Dispose(); }
        finally
        {
            try { _process.StandardInput.Close(); }
            catch (IOException) { }
            catch (InvalidOperationException) { }
        }
    }
}

internal sealed class CliProcessDrainException(int processId)
    : IOException($"The CLI process {processId} exited, but its standard-error pipe did not drain within {CliProcessTermination.ExitBound.TotalSeconds:0} seconds.");
