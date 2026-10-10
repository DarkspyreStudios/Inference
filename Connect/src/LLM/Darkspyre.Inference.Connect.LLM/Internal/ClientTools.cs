using Darkspyre.Inference.Common;
using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Internal;

internal static class ClientTools
{
    internal static LlmAgentCapabilities ManagedCapabilities(LlmProviderCapabilities capabilities) => capabilities.Agent with
    {
        SupportsShellTools = LlmProviderCapabilities.Intersect(capabilities.SupportsShellTools, capabilities.Agent.SupportsShellTools)
    };

    internal static void ValidateCallerServices(LlmAgentCallerServices? services, bool supportsResources = false)
    {
        if (services is null) return;
        if (services.Resources is not null && !supportsResources)
            throw new NotSupportedException("This agent lane does not bind caller-owned resource access.");
    }

    internal static void ValidateManagedOpening(LlmAgentSessionOptions options, LlmProviderCapabilities capabilities, bool supportsResources = false)
    {
        LlmAgentCapabilities managed = ManagedCapabilities(capabilities);
        ValidateCallerServices(options.CallerServices, supportsResources);
        if (options.Tools.Any(tool => tool is LlmAgentShellTool))
        {
            if ((managed.SupportsShellTools == InferenceSupport.Unsupported))
                throw new NotSupportedException("This route does not implement shell tools.");
            if (options.CallerServices?.ShellExecutor is null)
                throw new ArgumentException("Managed shell tools require a borrowed caller shell executor.");
        }
        if (options.Tools.Any(tool => tool is LlmAgentToolDefinition) && options.ToolHandler is null)
            throw new ArgumentException("Managed function tools require a host tool handler.");
    }

    internal static LlmClientToolCall Snapshot(LlmClientToolCall call) => call switch
    {
        LlmToolCall function => function,
        LlmShellCall shell => shell with { Commands = shell.Commands },
        _ => throw new NotSupportedException("Unknown client tool call.")
    };

    internal static LlmShellCallResult Snapshot(LlmShellCallResult result) => result with { Output = result.Output };

    internal static LlmAgentSessionOptions Snapshot(LlmAgentSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options with
        {
            Tools = Snapshot(options.Tools),
            HostedTools = HostedTools.Snapshot(options.HostedTools),
            Context = options.Context is null ? null : ManagedAgentContext.Copy(options.Context, includeAttachmentData: true)
        };
    }

    internal static LlmAgentResumeOptions Snapshot(LlmAgentResumeOptions options) => options with
    {
        Tools = Snapshot(options.Tools),
        HostedTools = HostedTools.Snapshot(options.HostedTools)
    };

    internal static LlmAgentTurnRequest Snapshot(LlmAgentTurnRequest request) => request with
    {
        Input = Array.AsReadOnly(request.Input.ToArray()),
        Attachments = Array.AsReadOnly(request.Attachments.Select(LlmAttachments.Copy).ToArray())
    };

    private static ReadOnlyCollection<LlmAgentClientToolDefinition> Snapshot(IReadOnlyList<LlmAgentClientToolDefinition> tools) =>
        Array.AsReadOnly(tools.Select(tool => tool switch
        {
            LlmAgentToolDefinition function => (LlmAgentClientToolDefinition)(function with { InputSchema = function.InputSchema.Clone() }),
            LlmAgentShellTool shell => shell with { Tool = shell.Tool with { Skills = shell.Tool.Skills } },
            _ => throw new NotSupportedException("Unknown agent tool definition.")
        }).ToArray());

    internal static LlmChatRequest Snapshot(LlmChatRequest request) => request with
    {
        StopSequences = Array.AsReadOnly(request.StopSequences.ToArray()),
        ProviderOptions = new ReadOnlyDictionary<string, JsonElement>(request.ProviderOptions.ToDictionary(
            option => option.Key, option => option.Value.Clone(), StringComparer.Ordinal)),
        StructuredOutput = request.StructuredOutput is { } output ? output with { Schema = output.Schema.Clone() } : null,
        Tools = Array.AsReadOnly(request.Tools.Select(tool => tool switch
        {
            LlmToolDefinition function => (LlmClientToolDefinition)(function with { InputSchema = function.InputSchema.Clone() }),
            LlmShellTool shell => shell with { Skills = shell.Skills },
            _ => throw new NotSupportedException("Unknown client tool definition.")
        }).ToArray()),
        Messages = Array.AsReadOnly(request.Messages.Select(message => message with
        {
            Attachments = Array.AsReadOnly(message.Attachments.Select(LlmAttachments.Copy).ToArray()),
            OmittedAttachments = Array.AsReadOnly(message.OmittedAttachments.ToArray()),
            ToolCalls = Array.AsReadOnly(message.ToolCalls.Select(Snapshot).ToArray()),
            ToolResults = Array.AsReadOnly(message.ToolResults.Select(result => result switch
            {
                LlmToolResult function => (LlmClientToolResult)(function with { Output = ManagedAgentContext.CopyOutput(function.Output) }),
                LlmShellCallResult shell => Snapshot(shell),
                _ => throw new NotSupportedException("Unknown client tool result.")
            }).ToArray())
        }).ToArray())
    };

    internal static IReadOnlyList<LlmAgentToolDefinition> Functions(IReadOnlyList<LlmAgentClientToolDefinition> tools) =>
        Array.AsReadOnly(tools.Select(tool => tool is LlmAgentToolDefinition function ? function
            : throw new NotSupportedException("This runtime implements function tools only.")).ToArray());

    internal static IReadOnlyDictionary<string, LlmShellCall> Validate(LlmChatRequest request, bool supportsShellTools)
    {
        ArgumentNullException.ThrowIfNull(request.Tools);
        ArgumentNullException.ThrowIfNull(request.Messages);
        bool shell = request.Tools.Any(tool => tool is LlmShellTool)
            || request.Messages.Any(message => message.ToolCalls.Any(call => call is LlmShellCall)
                || message.ToolResults.Any(result => result is LlmShellCallResult));
        if (shell && !supportsShellTools)
            throw new NotSupportedException("This route does not implement shell tools or shell history.");
        if (!shell) return FrozenDictionary<string, LlmShellCall>.Empty;

        var calls = new Dictionary<string, LlmClientToolCall>(StringComparer.Ordinal);
        var shellIds = new HashSet<string>(StringComparer.Ordinal);
        var settled = new HashSet<string>(StringComparer.Ordinal);
        foreach (LlmChatMessage message in request.Messages)
        {
            if (shellIds.Any(id => !settled.Contains(id)) && message.ToolResults.Count == 0)
                throw new ArgumentException("Unresolved shell calls require ordered results before another message.");
            foreach (LlmClientToolCall call in message.ToolCalls)
            {
                ArgumentNullException.ThrowIfNull(call);
                if (call is LlmShellCall)
                {
                    if (!shellIds.Add(call.Id) || calls.ContainsKey(call.Id))
                        throw new ArgumentException("Shell call identities must be unique.");
                    calls.Add(call.Id, call);
                }
                else if (call is LlmToolCall)
                {
                    if (shellIds.Contains(call.Id))
                        throw new ArgumentException("A shell call identity cannot identify a function call.");
                    calls[call.Id] = call;
                }
                else throw new NotSupportedException("Unknown client tool call.");
            }
            foreach (LlmClientToolResult result in message.ToolResults)
            {
                ArgumentNullException.ThrowIfNull(result);
                if (result is LlmShellCallResult)
                {
                    if (!calls.TryGetValue(result.ToolCallId, out LlmClientToolCall? call)
                        || call is not LlmShellCall || !settled.Add(result.ToolCallId))
                        throw new ArgumentException("Each shell result requires one preceding unsettled shell call.");
                }
                else if (result is LlmToolResult)
                {
                    if (shellIds.Contains(result.ToolCallId))
                        throw new ArgumentException("A function result cannot settle a shell call.");
                }
                else throw new NotSupportedException("Unknown client tool result.");
            }
        }
        if (shellIds.Any(id => !settled.Contains(id)))
            throw new ArgumentException("Runnable shell history requires every shell call to be settled.");
        return shellIds.ToFrozenDictionary(id => id, id => (LlmShellCall)calls[id], StringComparer.Ordinal);
    }

    internal static IReadOnlyList<LlmToolDefinition> Functions(IReadOnlyList<LlmClientToolDefinition> tools) =>
        Array.AsReadOnly(tools.Select(tool => tool is LlmToolDefinition function ? function
            : throw new NotSupportedException("This projection implements function tools only.")).ToArray());

    internal static IReadOnlyList<LlmToolCall> Functions(IReadOnlyList<LlmClientToolCall> calls) =>
        Array.AsReadOnly(calls.Select(call => call is LlmToolCall function ? function
            : throw new NotSupportedException("This projection implements function calls only.")).ToArray());

    internal static IReadOnlyList<LlmToolResult> Functions(IReadOnlyList<LlmClientToolResult> results) =>
        Array.AsReadOnly(results.Select(result => result is LlmToolResult function ? function
            : throw new NotSupportedException("This projection implements function results only.")).ToArray());
}
