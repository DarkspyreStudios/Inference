using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.Realtime;

internal static class XaiRealtimeToolOutput
{
    internal static LlmToolOutput Snapshot(LlmToolOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        LlmToolOutput snapshot = ManagedAgentContext.CopyOutput(output);
        if (snapshot.StructuredContent is { ValueKind: not JsonValueKind.Object }) throw XaiRealtimeProtocol.Invalid();
        foreach (var part in snapshot.Parts)
            switch (part)
            {
                case LlmToolOutputText { Text: not null }: break;
                case LlmToolOutputBinary binary when Enum.IsDefined(binary.Kind) && !string.IsNullOrWhiteSpace(binary.MediaType): break;
                case LlmToolOutputResource resource when resource.Uri is not null && !(resource.Text is not null && resource.Data is not null): break;
                case LlmToolOutputSessionResource: break;
                default: throw XaiRealtimeProtocol.Invalid();
            }
        return snapshot;
    }
    internal static async Task<string> PrepareAsync(LlmToolOutput snapshot, IInferenceResourceAccess? access,
        long maximumBytes, Action fence, Action<Exception> recordCleanup, CancellationToken token)
    {
        var prepared = new List<LlmToolOutputPart>();
        var reader = LlmResourcePreparation.ForNamedReads();
        var documents = new Dictionary<InferenceResourceReference, LlmToolOutputResource>();
        string? result = null; Exception? failure = null;
        try
        {
            foreach (var part in snapshot.Parts)
            {
                fence(); token.ThrowIfCancellationRequested();
                if (part is not LlmToolOutputSessionResource named) { prepared.Add(part); continue; }
                if (access is null) throw XaiRealtimeProtocol.Invalid();
                if (!documents.TryGetValue(named.Resource, out LlmToolOutputResource? document))
                {
                    var opened = await reader.OpenNamedAsync(access, named.Resource, descriptor =>
                    {
                        if (descriptor.ContentType is null || !LlmToolOutputResource.IsTextMediaType(descriptor.ContentType)) throw XaiRealtimeProtocol.Unsupported();
                        if (descriptor.Bytes > Math.Min(maximumBytes, int.MaxValue)) throw XaiRealtimeProtocol.Capacity();
                    }, fence, token).ConfigureAwait(false);
                    fence();
                    using var bytes = new MemoryStream();
                    var buffer = new byte[8192];
                    while (true)
                    {
                        long remaining = maximumBytes - bytes.Length;
                        int requested = remaining >= buffer.Length ? buffer.Length : checked((int)remaining + 1);
                        int count = await opened.Content.ReadAsync(buffer.AsMemory(0, requested), token).ConfigureAwait(false);
                        fence(); if (count == 0) break;
                        if (bytes.Length + count > maximumBytes || bytes.Length + count > int.MaxValue) throw XaiRealtimeProtocol.Capacity();
                        bytes.Write(buffer, 0, count);
                    }
                    reader.VerifyRead(access, opened.Descriptor, opened.Content, fence, token);
                    string text;
                    try { text = new UTF8Encoding(false, true).GetString(bytes.GetBuffer(), 0, checked((int)bytes.Length)); }
                    catch (DecoderFallbackException) { throw XaiRealtimeProtocol.Invalid(); }
                    document = new LlmToolOutputResource(named.Resource.Id)
                    { Name = opened.Descriptor.FileName, MediaType = opened.Descriptor.ContentType, Text = text };
                    documents.Add(named.Resource, document);
                }
                prepared.Add(document);
            }
            snapshot = snapshot with { Parts = Array.AsReadOnly(prepared.ToArray()) };
            result = JsonSerializer.Serialize(snapshot, InferenceJson.ToolOutput);
            if (Encoding.UTF8.GetByteCount(result) > maximumBytes) throw XaiRealtimeProtocol.Capacity();
            fence();
        }
        catch (Exception error) { failure = error; }
        try { await reader.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error)
        {
            recordCleanup(error); failure = failure is null ? error : new AggregateException(failure, error);
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        fence(); token.ThrowIfCancellationRequested();
        return result!;
    }
}
