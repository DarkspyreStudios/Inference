using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AppleFoundationModelsPrototype;

internal sealed record NativeReply(int Status, string Content, bool CancellationRequested = false);

internal sealed class NativeOperation : IDisposable
{
    private static readonly ConcurrentDictionary<nint, NativeOperation> Active = new();
    private static long _nextId;
    private static int _unboundCallbacks;
    private readonly bool _streaming;
    private readonly TaskCompletionSource<NativeReply> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _firstSnapshot = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<string> _snapshots = new();
    private int _terminalCallbacks;
    private readonly object _completionGate = new();
    private bool _cancellationRequested;

    internal NativeOperation(bool streaming = false)
    {
        _streaming = streaming;
        Id = (nint)Interlocked.Increment(ref _nextId);
        Active[Id] = this;
    }

    internal nint Id { get; }
    internal Task<NativeReply> Completion => _completion.Task;
    internal Task FirstSnapshot => _firstSnapshot.Task;
    internal string[] Snapshots => [.. _snapshots];
    internal int TerminalCallbacks => Volatile.Read(ref _terminalCallbacks);
    internal static int UnboundCallbacks => Volatile.Read(ref _unboundCallbacks);
    internal static unsafe nint TextCallback => (nint)(delegate* unmanaged[Cdecl]<int, nint, nuint, nint, void>)&OnText;
    internal static unsafe nint StructuredCallback => (nint)(delegate* unmanaged[Cdecl]<int, nint, nint, void>)&OnStructured;
    internal static unsafe nint TokenCallback => (nint)(delegate* unmanaged[Cdecl]<int, int, nint, nint, void>)&OnTokenCount;

    internal async Task<NativeReply> WaitAsync() => await Completion.WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);

    internal bool RequestCancellation()
    {
        lock (_completionGate)
        {
            if (Completion.IsCompleted) return false;
            _cancellationRequested = true;
            return true;
        }
    }

    public void Dispose()
    {
        // A deliberately hung experiment keeps its context alive until its isolated worker exits.
        // Numeric context IDs are never dereferenced as managed pointers, even for a late callback.
        if (Completion.IsCompleted) Active.TryRemove(Id, out _);
    }

    private static NativeOperation? Find(nint id)
    {
        if (Active.TryGetValue(id, out NativeOperation? operation)) return operation;
        Interlocked.Increment(ref _unboundCallbacks);
        return null;
    }

    private void Finish(NativeReply result)
    {
        lock (_completionGate)
        {
            Interlocked.Increment(ref _terminalCallbacks);
            _completion.TrySetResult(result with { CancellationRequested = _cancellationRequested });
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    [SuppressMessage("Design", "CA1031", Justification = "Managed exceptions must not cross a native callback boundary.")]
    private static void OnText(int status, nint content, nuint length, nint context)
    {
        NativeOperation? operation = Find(context);
        if (operation is null) return;
        try
        {
            string text = content == 0 ? string.Empty : Marshal.PtrToStringUTF8(content, checked((int)length)) ?? string.Empty;
            if (operation._streaming && status == 0 && content != 0)
            {
                operation._snapshots.Enqueue(text);
                operation._firstSnapshot.TrySetResult();
            }
            else operation.Finish(new(status, text));
        }
        catch (Exception error) { operation._completion.TrySetException(error); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    [SuppressMessage("Design", "CA1031", Justification = "Managed exceptions must not cross a native callback boundary.")]
    private static void OnStructured(int status, nint content, nint context)
    {
        NativeOperation? operation = Find(context);
        try
        {
            string json = content == 0 ? string.Empty : Native.TakeString(Native.FMGeneratedContentGetJSONString(content));
            operation?.Finish(new(status, json));
        }
        catch (Exception error) { operation?._completion.TrySetException(error); }
        finally { if (content != 0) Native.FMRelease(content); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    [SuppressMessage("Design", "CA1031", Justification = "Managed exceptions must not cross a native callback boundary.")]
    private static void OnTokenCount(int status, int count, nint error, nint context)
    {
        NativeOperation? operation = Find(context);
        try
        {
            operation?.Finish(new(status, status == 0 ? count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : Marshal.PtrToStringUTF8(error) ?? string.Empty));
        }
        catch (Exception exception) { operation?._completion.TrySetException(exception); }
    }
}
