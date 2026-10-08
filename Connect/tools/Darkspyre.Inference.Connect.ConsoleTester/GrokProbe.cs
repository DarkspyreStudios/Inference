using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.Resources;
using Darkspyre.Inference.Connect.TTS;

namespace Darkspyre.Inference.Connect.ConsoleTester;

/// <summary>Opt-in public Grok checks with synthetic inputs and no retries.</summary>
internal static class GrokProbe
{
    private const string ArgumentMarker = "CONNECT_STRICT_ARGUMENT_OK";
    private const string ToolName = "connect_strict_marker";

    internal static async Task<int> RunAsync(LlmProviderService llms, LlmAgentProviderService agents,
        ILlmConnectionSource connections, LlmConnectionAccess access, TtsProviderService speech,
        ILlmBatchClient batches, ConsoleInvocation invocation, CancellationToken token)
    {
        ProviderProbeOptions options = ProviderProbeOptions.Parse(invocation.CommandArguments,
            "grok-4.20-0309-non-reasoning");
        bool ready = await llms.IsAuthenticatedAsync(invocation.Provider.ConnectionId, token);
        ProbeCase[] cases =
        [
            new("strict", cancellation => StrictAsync(agents, invocation.Provider.ConnectionId, options.Model,
                Console.Out, cancellation)),
            new("files", cancellation => FilesAsync(llms, invocation.Provider.ConnectionId,
                options.TimeoutSeconds, Console.Out, cancellation)),
            new("tts", cancellation => SpeechAsync(llms, connections, access, speech,
                invocation.Provider.ConnectionId, options.TimeoutSeconds, cancellation)),
            new("batch", cancellation => BatchAsync(batches, invocation.Provider.ConnectionId,
                options.Model, options.TimeoutSeconds, Console.Out, cancellation)),
            new("batch-list", cancellation => BatchListAsync(batches, invocation.Provider.ConnectionId, cancellation)),
            new("deferred", cancellation => DeferredAsync(batches, invocation.Provider.ConnectionId,
                options.Model, Console.Out, cancellation))
        ];
        return await ProbeRunner.RunAsync(cases, options, ready, Console.Out, token);
    }

    private static async Task<string> SpeechAsync(LlmProviderService llms, ILlmConnectionSource connections,
        LlmConnectionAccess access, TtsProviderService speech, string connectionId, int timeoutSeconds,
        CancellationToken token)
    {
        LlmConnection selected = await connections.GetConnectionAsync(connectionId, token)
            ?? throw new ProbeUnavailable("The selected Grok connection is unavailable.");
        var binding = new XaiFamilyBinding(new(connectionId), access, llms);
        await using ITtsClient client = speech.Bind(new()
        {
            Transport = TtsTransport.XaiTts,
            Endpoint = new Uri(selected.Endpoint),
            RequestTimeout = TimeSpan.FromSeconds(timeoutSeconds),
            Xai = binding.Selection
        }, binding);
        IReadOnlyList<TtsVoice> voices = await client.ListVoicesAsync(token);
        if (voices.Count == 0) throw new ProbeUnavailable("Grok returned no voices; speech generation is not qualified.");
        TtsResult result = await client.SpeakAsync(new()
        {
            Text = "Hello from Connect.",
            VoiceId = voices[0].Id,
            Language = "en",
            Format = TtsAudioFormat.Wav
        }, token);
        ValidateSpeech(result);
        return $"voice catalog and one short REST utterance returned {result.Audio.Length} bytes ({result.ContentType}); no playback or persisted audio";
    }

    internal static void ValidateSpeech(TtsResult result)
    {
        if (result.Audio.IsEmpty || !result.ContentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            throw new ProbeFailure("Speech must return nonempty audio with an audio media type.");
    }

    private static async Task<string> BatchListAsync(ILlmBatchClient batches, string connectionId, CancellationToken token)
    {
        LlmBatchPage page = await batches.ListAsync(connectionId, new(Limit: 1), token);
        if (page.Items.Count == 0)
            throw new ProbeUnavailable("The single batch page is empty; receipt timestamp parsing is unqualified.");
        if (page.Items.Count != 1 || page.Items[0].CreateTime is null)
            throw new ProbeFailure("The single batch page must contain one receipt with its observed creation time.");
        return "one native batch receipt and its creation time parsed; one read-only page, no generation, mutation, cleanup or retry";
    }

    internal sealed class BatchShapeHandler(TextWriter output) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            HttpResponseMessage response = await base.SendAsync(request, token);
            if (request.Method != HttpMethod.Get || request.RequestUri?.AbsolutePath != "/v1/batches"
                || !response.IsSuccessStatusCode) return response;
            try
            {
                byte[] body = await response.Content.ReadAsByteArrayAsync(token);
                try
                {
                    using JsonDocument document = JsonDocument.Parse(body);
                    JsonElement root = document.RootElement;
                    output.WriteLine($"Batch shape: root={root.ValueKind}.");
                    if (root.ValueKind != JsonValueKind.Object) return response;
                    if (!root.TryGetProperty("batches", out JsonElement batches))
                    { output.WriteLine("Batch shape: batches=absent."); return response; }
                    output.WriteLine($"Batch shape: batches={batches.ValueKind}.");
                    if (batches.ValueKind != JsonValueKind.Array || batches.GetArrayLength() == 0) return response;
                    JsonElement first = batches[0];
                    output.WriteLine($"Batch shape: first={first.ValueKind}.");
                    if (first.ValueKind != JsonValueKind.Object) return response;
                    foreach (string name in new[] { "batch_id", "name", "create_time", "expire_time", "cancel_time", "cancel_by_xai_message", "create_api_key_id", "state" })
                        WriteKind(first, name);
                    if (first.TryGetProperty("state", out JsonElement state) && state.ValueKind == JsonValueKind.Object)
                        foreach (string name in new[] { "num_requests", "num_pending", "num_success", "num_error", "num_cancelled" })
                            WriteKind(state, name);
                }
                catch (JsonException) { output.WriteLine("Batch shape: invalid JSON; values are withheld."); }
                return response;
            }
            catch { response.Dispose(); throw; }
        }

        private void WriteKind(JsonElement owner, string name)
        {
            if (!owner.TryGetProperty(name, out JsonElement value))
            { output.WriteLine($"Batch shape: {name}=absent."); return; }
            string length = value.ValueKind == JsonValueKind.String ? $"; length={value.GetString()!.Length}" : "";
            output.WriteLine($"Batch shape: {name}={value.ValueKind}{length}.");
        }
    }

    internal static async Task<string> BatchAsync(ILlmBatchClient batches, string connectionId, string model,
        int cleanupTimeoutSeconds, TextWriter output, CancellationToken token)
    {
        string marker = $"CONNECT_BATCH_{Guid.NewGuid():N}";
        string customId = $"connect-{Guid.NewGuid():N}";
        LlmBatch? batch = null;
        Exception? failure = null;
        try
        {
            batch = await batches.CreateFromRequestsAsync(connectionId, customId,
                [new(customId, new LlmBatchChatOperation(MarkerRequest(model, marker)))], cancellationToken: token);
            output.WriteLine($"Synthetic batch receipt: {batch.Id}; one request: {customId}.");
            batch = await batches.GetAsync(connectionId, batch, token);
            LlmBatchRequestPage requests = await batches.ListRequestsAsync(connectionId, batch, new(Limit: 1), token);
            if (requests.Items.Count != 1)
                throw new ProbeFailure("The single batch metadata page must contain one request.");
            // A single marker result needs no media; retain at most one MiB of the response page.
            LlmBatchResultPage results = await batches.GetResultsAsync(connectionId, batch, new(Limit: 1),
                new(1_048_576, 1_048_576), cancellationToken: token);
            if (results.Items.Count == 0)
                throw new ProbeUnavailable("Batch submission/get/list succeeded; the one result page is empty. Completion is unqualified; no poll or retry follows.");
            if (results.Items.Count == 1 && results.Items[0] is LlmBatchFailure failed)
                throw new InferenceException(failed.Error.Code, $"Batch {failed.Error.ProviderCode ?? "unknown"}: {failed.Error.Message}");
            if (results.Items.Count != 1 || results.Items[0] is not LlmBatchChatResult chat || chat.CustomId != customId)
                throw new ProbeFailure("The batch page must return exactly the submitted chat result with its CustomId.");
            ValidateMarker(chat.Result, marker);
        }
        catch (Exception error)
        {
            failure = error;
            batch ??= error switch
            {
                LlmResourceOperationException resource => resource.Observation.ObservedBatch,
                LlmResourceOperationCanceledException resource => resource.Observation.ObservedBatch,
                _ => null
            };
        }
        if (batch is not null && batch.Counts.Pending != 0)
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(cleanupTimeoutSeconds));
            try
            {
                LlmBatch cancelled = await batches.CancelAsync(connectionId, batch, cleanup.Token);
                if (cancelled.Id != batch.Id)
                    throw new ProbeFailure("Cancellation does not acknowledge the exact synthetic batch.");
                output.WriteLine($"Synthetic batch cancellation acknowledged: {batch.Id}; terminal cleanup remains unqualified.");
            }
            catch (Exception error)
            {
                output.WriteLine($"Synthetic batch cleanup failed: {ProbeRunner.FailureDetail(error)}");
                if (failure is not null) throw new AggregateException(failure, error);
                throw;
            }
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        return "one batch row and its actual marker result verified; no retry or unrelated batch cancellation";
    }

    private static async Task<string> DeferredAsync(ILlmBatchClient batches, string connectionId, string model,
        TextWriter output, CancellationToken token)
    {
        string marker = $"CONNECT_DEFERRED_{Guid.NewGuid():N}";
        LlmDeferredCompletion receipt = await batches.SubmitDeferredAsync(connectionId, MarkerRequest(model, marker),
            cancellationToken: token);
        output.WriteLine($"Synthetic deferred receipt: {receipt.RequestId}; exactly one retrieval follows.");
        LlmDeferredResult result = await batches.GetDeferredAsync(connectionId, receipt, cancellationToken: token);
        if (result is LlmDeferredPending)
            throw new ProbeUnavailable("Deferred submission/get succeeded; the provider reports Pending. Completion is unqualified; no consuming retry follows.");
        if (result is not LlmDeferredCompleted completed) throw new ProbeFailure("Unknown deferred result kind.");
        ValidateMarker(completed.Result, marker);
        return "one deferred request completed with its actual marker; exactly one consuming GET";
    }

    private static LlmChatRequest MarkerRequest(string model, string marker) => new()
    {
        ModelId = model,
        Messages = [new("user", $"Reply only with {marker}. Use no tools.")],
        // One marker needs only a short answer, not an unrestricted generation.
        MaxOutputTokens = 64
    };

    private static void ValidateMarker(LlmChatResult result, string marker)
    {
        if (result.Status != LlmCompletionStatus.Completed || result.Text.Trim() != marker)
            throw new ProbeFailure("The synthetic request must complete with its exact marker.");
    }

    internal static async Task<string> StrictAsync(LlmAgentProviderService agents, string connectionId,
        string model, TextWriter output, CancellationToken token)
    {
        string replayMarker = $"CONNECT_STRICT_REPLAY_{Guid.NewGuid():N}";
        int callbacks = 0, successfulResults = 0;
        var options = new LlmAgentSessionOptions
        {
            ModelId = model,
            // One tool-request step and one final-answer step; no additional model iteration.
            ExecutionOptions = new() { MaxModelIterations = 2 },
            Tools = [CreateStrictTool()],
            ToolHandler = (call, cancellation) =>
            {
                cancellation.ThrowIfCancellationRequested();
                if (Interlocked.Increment(ref callbacks) != 1 || call.Name != ToolName)
                    throw new ProbeFailure("Expected exactly one invocation of the declared strict function.");
                ValidateStrictArguments(call.Arguments);
                return Task.FromResult(LlmToolOutput.FromText(replayMarker));
            }
        };
        await using ILlmAgentSession session = await agents.StartSessionAsync(connectionId, options, token);
        var answer = new StringBuilder();
        LlmAgentTurnCompletedEvent? completed = null;
        await foreach (LlmAgentEvent item in session.RunTurnAsync(new()
        {
            Input = [new LlmAgentTextContent($"Call {ToolName} exactly once with marker {ArgumentMarker}. "
                + "Use no other tools. Reply only with the exact verification word returned by the tool.")]
        }, token))
        {
            Program.WriteAgentEvent(item, output, output);
            if (item is LlmAgentTextDeltaEvent text) answer.Append(text.Delta);
            if (item is LlmAgentToolEvent { Output.IsError: false } tool && tool.Tool == ToolName)
                successfulResults++;
            if (item is LlmAgentTurnCompletedEvent terminal) completed = terminal;
        }
        if (completed?.Status != LlmAgentTurnStatus.Completed || callbacks != 1 || successfulResults != 1
            || answer.ToString().Trim() != replayMarker)
            throw new ProbeFailure("Strict function must validate its original arguments, return one successful tool result and complete with that result's exact word.");
        return "original closed-schema arguments validated; one successful callback; exact result replay; Completed";
    }

    internal static LlmAgentToolDefinition CreateStrictTool() => new()
    {
        Name = ToolName,
        Description = "Returns a verification word. Call once when requested.",
        InputSchema = JsonElement.Parse(
            """{"type":"object","properties":{"marker":{"type":"string","enum":["CONNECT_STRICT_ARGUMENT_OK"]}},"required":["marker"],"additionalProperties":false}"""),
        Strict = true
    };

    internal static void ValidateStrictArguments(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object || arguments.EnumerateObject().Count() != 1
            || !arguments.TryGetProperty("marker", out JsonElement marker)
            || marker.ValueKind != JsonValueKind.String || marker.GetString() != ArgumentMarker)
            throw new ProbeFailure("Strict function arguments do not satisfy the original required marker enum and closed object schema.");
    }

    internal static async Task<string> FilesAsync(ILlmFileClient files, string connectionId,
        int cleanupTimeoutSeconds, TextWriter output, CancellationToken token)
    {
        string fileName = $"connect-files-{Guid.NewGuid():N}.txt";
        byte[] bytes = Encoding.UTF8.GetBytes($"Synthetic Connect Files verification: {fileName}\n");
        output.WriteLine($"Synthetic file: {fileName}; requested expiry: one hour; cleanup names only its upload receipt.");
        LlmProviderFile? uploaded = null;
        Exception? failure = null;
        try
        {
            using var source = new MemoryStream(bytes, writable: false);
            // One hour is the public contract's minimum expiry, not an automatic-expiry guarantee.
            uploaded = await files.UploadAsync(connectionId, source, fileName,
                new(TimeSpan.FromHours(1)), token);
            if (uploaded.FileName != fileName || uploaded.Bytes != bytes.Length)
                throw new ProbeFailure("Upload receipt does not describe the synthetic file.");
            LlmProviderFile fetched = await files.GetAsync(connectionId, uploaded.Id, token);
            if (fetched.Id != uploaded.Id || fetched.FileName != fileName || fetched.Bytes != bytes.Length)
                throw new ProbeFailure("Get does not return the uploaded file's actual identity and size.");
            LlmFilePage page = await files.ListAsync(connectionId,
                new() { Limit = 100, Sort = LlmFileSort.CreatedAt, Order = LlmFileOrder.Descending }, token);
            if (!page.Items.Any(file => file.Id == uploaded.Id))
                throw new ProbeFailure("The single newest-files page does not contain the synthetic upload; no retry or extra page is requested.");
            using var destination = new MemoryStream();
            ResourceDownloadReceipt receipt = await files.DownloadAsync(connectionId, uploaded.Id, destination,
                new(LlmFileContentFormat.Original), token);
            if (receipt.BytesWritten != bytes.Length || !destination.ToArray().AsSpan().SequenceEqual(bytes))
                throw new ProbeFailure("Downloaded original bytes do not match the synthetic upload.");
        }
        catch (Exception error) { failure = error; }

        if (uploaded is not null)
        {
            // Explicit cleanup gets the same caller-selected bound even after case cancellation.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(cleanupTimeoutSeconds));
            try
            {
                LlmFileDeletion deletion = await files.DeleteAsync(connectionId, uploaded.Id, cleanup.Token);
                if (deletion.Id != uploaded.Id || !deletion.Deleted)
                    throw new ProbeFailure("Deletion does not confirm removal of the exact synthetic upload.");
                output.WriteLine("Synthetic file deletion confirmed.");
            }
            catch (Exception error)
            {
                output.WriteLine($"Synthetic file cleanup failed: {ProbeRunner.FailureDetail(error)}");
                if (failure is not null) throw new AggregateException(failure, error);
                throw;
            }
        }
        else
        {
            output.WriteLine("No upload receipt is available; remote creation is unknown and no file is deleted.");
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        return $"upload/get/list/original download verified ({bytes.Length} bytes); exact synthetic file deleted";
    }
}
