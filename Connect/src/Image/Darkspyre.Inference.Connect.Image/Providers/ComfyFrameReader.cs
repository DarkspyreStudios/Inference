using System.Buffers.Binary;
using System.Net.WebSockets;
using Darkspyre.Inference.Common;

namespace Darkspyre.Inference.Connect.Image.Providers;

internal sealed record ComfyFrame(WebSocketMessageType Type, byte[] Data)
{
    public override string ToString() => "ComfyFrame [message]";
}

// Boundary policy: provider bytes stay opaque because mutation corrupts image/protocol data.
// Size, frame headers and PNG signatures are validated here. Raw data never becomes markup, paths, commands or log text.
internal static class ComfyFrameReader
{
    internal const int MaximumBytes = 64 * 1024 * 1024;

    internal static async Task<ComfyFrame?> ReadAsync(WebSocket socket, CancellationToken token)
    {
        byte[] buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        WebSocketMessageType? type = null;
        while (true)
        {
            ValueWebSocketReceiveResult result = await socket.ReceiveAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            type ??= result.MessageType;
            if (result.MessageType != type || result.Count > MaximumBytes - message.Length)
                throw new InferenceException(InferenceFailureCode.ProtocolFailure, "The inline image message is invalid or exceeds the supported size.");
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return new(type.Value, message.ToArray());
        }
    }

    internal static byte[] ReadFinalPng(ReadOnlySpan<byte> message)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (message.Length < 16 || BinaryPrimitives.ReadUInt32BigEndian(message) != 1 ||
            BinaryPrimitives.ReadUInt32BigEndian(message[4..]) != 2 || !message.Slice(8, 8).SequenceEqual(signature))
            throw new InferenceException(InferenceFailureCode.ProtocolFailure, "The inline image output is not a valid PNG frame.");
        return message[8..].ToArray();
    }
}
