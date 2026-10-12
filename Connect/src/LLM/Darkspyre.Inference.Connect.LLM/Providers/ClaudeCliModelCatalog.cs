using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

internal static class ClaudeCliModelCatalog
{
    [SuppressMessage("Design", "CA1031", Justification = "The owned process preserves independent operation and cleanup failures before publication.")]
    internal static async Task<IReadOnlyList<LlmModel>> ReadAsync(IClaudeCliSessionProcessFactory factory,
        string executable, string? accountToken, Action fence, CancellationToken token)
    {
        fence();
        string directory = CliInfrastructure.CreateWorkingDirectory("claude");
        IClaudeCliSessionProcess? process = null;
        IReadOnlyList<LlmModel>? models = null;
        Exception? failure = null;
        bool released = false;
        try
        {
            fence();
            process = await factory.StartAsync(new(executable,
                ["--verbose", "--input-format", "stream-json", "--output-format", "stream-json", "--tools", "",
                    "--strict-mcp-config", "--disable-slash-commands", "--permission-prompts", "none", "--no-session-persistence"],
                directory) { AccountToken = accountToken }, token).ConfigureAwait(false);
            fence();
            if (process is null) throw ProtocolFailure();
            string requestId = Guid.NewGuid().ToString("N");
            JsonObject request = ClaudeCliControlProtocol.Request(requestId, new JsonObject { ["subtype"] = "initialize" });
            await process.WriteLineAsync(request.ToJsonString(), token).ConfigureAwait(false);
            fence();
            while (models is null)
            {
                string? line = await process.ReadLineAsync(token).ConfigureAwait(false);
                fence();
                if (line is null)
                {
                    ClaudeCliProcessResult exit = await process.WaitForExitAsync(token).ConfigureAwait(false);
                    fence();
                    throw new LlmProviderException(exit.ExitCode == 0
                        ? "Claude CLI ended before model initialization completed."
                        : "Claude CLI failed before model initialization completed.", LlmTransport.AnthropicClaudeCli,
                        providerCode: "claude_cli_model_initialization_failed");
                }
                if (string.IsNullOrWhiteSpace(line)) continue;
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) throw ProtocolFailure();
                if (LlmJson.GetString(root, "type") != "control_response") continue;
                if (!ClaudeCliControlProtocol.TryReadResponse(root, out string observedId, out JsonElement response))
                    throw ProtocolFailure();
                if (observedId != requestId) continue;
                if (ClaudeCliControlProtocol.IsRejected(response)) throw ProtocolFailure();
                models = Project(ClaudeCliControlProtocol.Payload(response));
            }
        }
        catch (ClaudeCliUnavailableException error) { failure = ClaudeCliConfiguration.Unavailable(error); }
        catch (JsonException error) { failure = ProtocolFailure(new CatalogFailureEvidence(error)); }
        catch (Exception error)
        {
            failure = error is LlmProviderException ? error : SafeFailure(error);
        }
        if (process is not null)
            try { await process.DisposeAsync().ConfigureAwait(false); released = true; }
            catch (Exception cleanup)
            {
                Exception retained = LlmResourcePreparation.AsCleanupFailure(SafeFailure(cleanup), refusedOwner: process);
                failure = failure is null ? retained : new AggregateException(failure, retained);
            }
        else released = true;
        if (released) CliInfrastructure.DeleteWorkingDirectory(directory);
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        fence();
        return models!;
    }

    internal static ReadOnlyCollection<LlmModel> Project(JsonElement initialization)
    {
        if (initialization.ValueKind != JsonValueKind.Object
            || !initialization.TryGetProperty("models", out JsonElement models) || models.ValueKind != JsonValueKind.Array)
            throw ProtocolFailure();
        var result = new List<LlmModel>();
        foreach (JsonElement model in models.EnumerateArray())
        {
            if (model.ValueKind != JsonValueKind.Object || !model.TryGetProperty("value", out JsonElement value)
                || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())
                || !model.TryGetProperty("displayName", out JsonElement name) || name.ValueKind != JsonValueKind.String)
                throw ProtocolFailure();
            // The CLI reports no window; the published limits of the model an alias resolves to supply it.
            string resolved = model.TryGetProperty("resolvedModel", out JsonElement target) && target.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(target.GetString()) ? target.GetString()! : value.GetString()!;
            ClaudeModelLimit limit = ClaudeModelLimits.Get(resolved);
            result.Add(new(value.GetString()!, name.GetString()!)
            {
                CatalogEntry = model.Clone(), MaxContextTokens = limit.ContextTokens, MaxOutputTokens = limit.OutputTokens,
            });
        }
        return Array.AsReadOnly(result.ToArray());
    }

    private static LlmProviderException ProtocolFailure(Exception? evidence = null) => new("Claude CLI returned invalid model initialization.",
        LlmTransport.AnthropicClaudeCli, providerCode: "claude_cli_model_initialization_failed", innerException: evidence);

    private static Exception SafeFailure(Exception original)
    {
        var evidence = new CatalogFailureEvidence(original);
        return original is OperationCanceledException canceled
            ? new OperationCanceledException("Claude CLI model discovery was canceled.", evidence, canceled.CancellationToken)
            : evidence;
    }

    private sealed class CatalogFailureEvidence(Exception original) : Exception("Claude CLI model discovery reported an operation failure.")
    {
        internal Exception Original { get; } = original;
    }
}
