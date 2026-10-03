using System.Text.Json;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Internal;

internal static class HostedTools
{
    internal static void Validate(LlmChatRequest request, LlmProviderCapabilities capabilities) =>
        Validate(request.HostedTools, request.MaxHostedTurns, capabilities.HostedTools);

    internal static void Validate(IReadOnlyList<LlmHostedTool> tools, int? maxTurns, IReadOnlySet<LlmHostedToolKind> supported)
    {
        ArgumentNullException.ThrowIfNull(tools);
        if (maxTurns is <= 0) { throw new ArgumentOutOfRangeException(nameof(maxTurns), "Hosted turn limits must be positive."); }
        foreach (var tool in tools)
        {
            ArgumentNullException.ThrowIfNull(tool);
            switch (tool)
            {
                case LlmWebSearchTool web:
                    Strings(web.AllowedDomains); Strings(web.ExcludedDomains);
                    break;
                case LlmCodeExecutionTool:
                    break;
                case LlmImageGenerationTool image when Enum.IsDefined(image.Action):
                    break;
                case LlmDocumentSearchTool document:
                    ArgumentNullException.ThrowIfNull(document.CollectionIds);
                    Strings(document.CollectionIds);
                    if (document.MaxResults is <= 0) { throw new ArgumentOutOfRangeException(nameof(tools), "Document result limits must be positive."); }
                    break;
                case LlmRemoteMcpTool mcp:
                    ArgumentException.ThrowIfNullOrWhiteSpace(mcp.ServerUrl);
                    ArgumentException.ThrowIfNullOrWhiteSpace(mcp.ServerLabel);
                    Strings(mcp.AllowedTools);
                    if (mcp.Headers?.Any(pair => pair.Key is null || pair.Value is null) is true) { throw new ArgumentException("MCP headers cannot contain null values."); }
                    break;
                case XaiXSearchTool search:
                    Strings(search.AllowedHandles); Strings(search.ExcludedHandles);
                    break;
                default:
                    throw new NotSupportedException("This hosted-tool definition is not a supported contract variant.");
            }
            if (!supported.Contains(tool.Kind)) { throw new NotSupportedException("This route does not implement the requested hosted tool."); }
        }
        if (maxTurns is not null && supported.Count == 0) { throw new NotSupportedException("This route does not implement hosted turn limits."); }
    }

    internal static IReadOnlyList<LlmHostedTool> Snapshot(IReadOnlyList<LlmHostedTool> tools) =>
        Array.AsReadOnly(tools.Select<LlmHostedTool, LlmHostedTool>(tool => tool switch
        {
            LlmWebSearchTool web => web with { AllowedDomains = Copy(web.AllowedDomains), ExcludedDomains = Copy(web.ExcludedDomains), Location = web.Location is { } location ? location with { } : null },
            LlmCodeExecutionTool code => code,
            LlmImageGenerationTool image => image,
            LlmDocumentSearchTool document => document with { CollectionIds = Copy(document.CollectionIds)! },
            LlmRemoteMcpTool mcp => mcp with
            {
                AllowedTools = Copy(mcp.AllowedTools),
                Headers = mcp.Headers is null ? null : new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(new Dictionary<string, string>(mcp.Headers, StringComparer.Ordinal))
            },
            XaiXSearchTool search => search with { AllowedHandles = Copy(search.AllowedHandles), ExcludedHandles = Copy(search.ExcludedHandles) },
            _ => throw new NotSupportedException("This hosted-tool definition is not a supported contract variant.")
        }).ToArray());

    internal static LlmHostedToolCall Snapshot(LlmHostedToolCall call)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(call.Name);
        if (!Enum.IsDefined(call.Kind)) { throw new ArgumentOutOfRangeException(nameof(call), "The hosted kind is undefined."); }
        LlmHostedToolOutput? output = call.Output switch
        {
            null => null,
            LlmHostedSearchOutput search when call.Kind is LlmHostedToolKind.WebSearch or LlmHostedToolKind.SocialSearch => search with { Sources = Copy(search.Sources) },
            LlmHostedCodeOutput code when call.Kind == LlmHostedToolKind.CodeExecution => code with { Logs = Copy(code.Logs), ImageUrls = Copy(code.ImageUrls) },
            LlmHostedDocumentSearchOutput document when call.Kind == LlmHostedToolKind.DocumentSearch => document with { Results = Copy(document.Results) },
            LlmHostedMcpOutput mcp when call.Kind == LlmHostedToolKind.RemoteMcp => mcp with { Arguments = Clone(mcp.Arguments), Output = Clone(mcp.Output), Error = Clone(mcp.Error) },
            LlmHostedImageOutput image when call.Kind == LlmHostedToolKind.ImageGeneration => image with { Data = image.Data.ToArray() },
            _ => throw new ArgumentException("Hosted output is incompatible with the observed tool kind.")
        };
        if (output is not null) { output = output with { Resources = Copy(output.Resources) }; }
        return call with { Input = Clone(call.Input), Output = output };
    }

    private static JsonElement? Clone(JsonElement? value) => value?.Clone();
    private static System.Collections.ObjectModel.ReadOnlyCollection<T>? Copy<T>(IReadOnlyList<T>? values)
    {
        if (values is null) { return null; }
        T[] snapshot = values.ToArray();
        if (snapshot.Any(value => value is null)) { throw new ArgumentException("Hosted lists cannot contain null entries.", nameof(values)); }
        return Array.AsReadOnly(snapshot);
    }
    private static void Strings(IReadOnlyList<string>? values)
    {
        if (values?.Any(value => value is null) is true) { throw new ArgumentException("Hosted filters cannot contain null entries."); }
    }
}
