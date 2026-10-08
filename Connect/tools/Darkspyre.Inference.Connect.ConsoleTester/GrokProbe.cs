using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.ConsoleTester;

/// <summary>Opt-in public Grok checks with synthetic inputs and no retries.</summary>
internal static class GrokProbe
{
    private const string ArgumentMarker = "CONNECT_STRICT_ARGUMENT_OK";
    private const string ToolName = "connect_strict_marker";

    internal static async Task<int> RunAsync(LlmProviderService llms, LlmAgentProviderService agents,
        ConsoleInvocation invocation, CancellationToken token)
    {
        ProviderProbeOptions options = ProviderProbeOptions.Parse(invocation.CommandArguments,
            "grok-4.20-0309-non-reasoning");
        bool ready = await llms.IsAuthenticatedAsync(invocation.Provider.ConnectionId, token);
        ProbeCase[] cases =
        [
            new("strict", cancellation => StrictAsync(agents, invocation.Provider.ConnectionId, options.Model,
                Console.Out, cancellation)),
            new("files", cancellation => FilesAsync(llms, invocation.Provider.ConnectionId,
                options.TimeoutSeconds, Console.Out, cancellation))
        ];
        return await ProbeRunner.RunAsync(cases, options, ready, Console.Out, token);
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
