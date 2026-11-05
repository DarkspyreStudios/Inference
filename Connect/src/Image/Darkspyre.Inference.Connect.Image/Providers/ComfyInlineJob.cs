using System.Net.WebSockets;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.Image.Providers;

internal sealed class ComfyInlineJob(
    string id,
    string outputNode,
    InferenceResourceScope scope,
    ClientWebSocket socket,
    int referenceBytes,
    SdkHttpConsumption.Operation admission)
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(admission.Token);
    private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Guid _captureId = Guid.NewGuid();
    private readonly List<byte[]> _outputs = [];
    private Task _pump = Task.CompletedTask;
    private Task? _release;
    private string? _activeNode;
    private int _retained = referenceBytes;
    private ImageTransportJobState _state = ImageTransportJobState.Queued;
    private bool _terminalProof;
    private bool _noDispatch = true;
    private bool _released;

    internal SemaphoreSlim CleanupGate { get; } = new(1, 1);
    internal string Id => id;
    internal Guid CaptureId => _captureId;
    internal InferenceResourceScope Scope => scope;
    internal bool TerminalProof { get { lock (_gate) return _terminalProof; } }
    internal bool NoDispatch { get { lock (_gate) return _noDispatch; } }
    internal CancellationToken Token => _stop.Token;

    internal Task StartAsync(Uri endpoint)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_released, this);
            _pump = PumpAsync(endpoint);
            return _connected.Task;
        }
    }

    internal void BeginDispatch()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_released, this);
            admission.Fence();
            _noDispatch = false;
        }
    }

    internal void ConfirmValidationRejection()
    {
        lock (_gate) _noDispatch = true;
    }

    internal void ConfirmTerminalProof()
    {
        lock (_gate) _terminalProof = true;
    }

    internal ImageTransportJobSnapshot Snapshot()
    {
        lock (_gate)
        {
            if (_released) return FailedSnapshot();
            admission.Fence();
            if (_state == ImageTransportJobState.Failed) return FailedSnapshot();
            return _state == ImageTransportJobState.Completed
                ? new(_state, _outputs.Select((_, index) => new ImageTransportOutput(null, scope,
                    JsonSerializer.SerializeToElement(new ComfyInlineOutput(_captureId, index), ComfyInlineJsonContext.Default.ComfyInlineOutput),
                    ContentType: "image/png")).ToArray())
                : new(_state);
        }
    }

    internal ImageTransportDownload Download(ComfyInlineOutput output)
    {
        lock (_gate)
        {
            if (_released) throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "The live image output has been released.");
            admission.Fence();
            if (_state != ImageTransportJobState.Completed || output.CaptureId != _captureId ||
                output.Index < 0 || output.Index >= _outputs.Count)
                throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "The live image output is no longer owned by this connection.");
            return new("image/png", _outputs[output.Index]);
        }
    }

    internal Task ReleaseLocalAsync()
    {
        lock (_gate)
        {
            if (_release is not null) return _release;
            _released = true;
            _outputs.Clear();
            _retained = 0;
            _activeNode = null;
            socket.Abort();
            return _release = DrainAsync();
        }
    }

    private async Task DrainAsync()
    {
        try { await _stop.CancelAsync().ConfigureAwait(false); }
        finally
        {
            try { await _pump.ConfigureAwait(false); }
            finally
            {
                socket.Dispose();
                _stop.Dispose();
                admission.Dispose();
            }
        }
    }

    private async Task PumpAsync(Uri endpoint)
    {
        try
        {
            await socket.ConnectAsync(endpoint, _stop.Token).ConfigureAwait(false);
            _connected.TrySetResult();
            while (await ComfyFrameReader.ReadAsync(socket, _stop.Token).ConfigureAwait(false) is { } frame)
            {
                lock (_gate)
                {
                    if (_released) return;
                    admission.Fence();
                    if (frame.Type == WebSocketMessageType.Text) ReadText(frame.Data);
                    else if (frame.Type == WebSocketMessageType.Binary && _state != ImageTransportJobState.Completed &&
                        _state != ImageTransportJobState.Failed && _activeNode == outputNode)
                    {
                        byte[] png = ComfyFrameReader.ReadFinalPng(frame.Data);
                        if (png.Length > ComfyFrameReader.MaximumBytes - _retained)
                            throw new InferenceException(InferenceFailureCode.ProtocolFailure, "The inline image payload exceeds the supported size.");
                        _retained += png.Length;
                        _outputs.Add(png);
                    }
                }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _connected.TrySetException(new InferenceException(InferenceFailureCode.ExecutionFailed, "The inline image stream could not be opened."));
        }
        finally
        {
            lock (_gate)
            {
                if (_state != ImageTransportJobState.Completed) _state = ImageTransportJobState.Failed;
                _activeNode = null;
            }
        }
    }

    private void ReadText(byte[] bytes)
    {
        using JsonDocument message = JsonDocument.Parse(bytes);
        JsonElement root = message.RootElement;
        if (!root.TryGetProperty("type", out JsonElement type) ||
            !root.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Object)
            return;
        string? prompt = data.TryGetProperty("prompt_id", out JsonElement promptId) && promptId.ValueKind == JsonValueKind.String
            ? promptId.GetString() : null;
        string? kind = type.ValueKind == JsonValueKind.String ? type.GetString() : null;
        if (kind == "executing" && prompt != id) _activeNode = null;
        if (prompt != id) return;
        if (kind == "executing" && data.TryGetProperty("node", out JsonElement node) && node.ValueKind == JsonValueKind.Null)
        {
            _terminalProof = true;
            _activeNode = null;
            return;
        }
        if (_state is ImageTransportJobState.Completed or ImageTransportJobState.Failed) return;
        switch (kind)
        {
            case "execution_start":
                _state = ImageTransportJobState.Generating;
                break;
            case "executing":
                _state = ImageTransportJobState.Generating;
                _activeNode = data.TryGetProperty("node", out JsonElement active) && active.ValueKind == JsonValueKind.String
                    ? active.GetString() : null;
                break;
            case "execution_success":
                _state = _outputs.Count > 0 ? ImageTransportJobState.Completed : ImageTransportJobState.Failed;
                _activeNode = null;
                break;
            case "execution_error":
            case "execution_interrupted":
                _state = ImageTransportJobState.Failed;
                _activeNode = null;
                break;
        }
    }

    internal static ImageTransportJobSnapshot FailedSnapshot() => new(ImageTransportJobState.Failed,
        Error: new InferenceResourceFailure(InferenceFailureCode.ExecutionFailed, null, "The live image execution is unavailable."));
}
