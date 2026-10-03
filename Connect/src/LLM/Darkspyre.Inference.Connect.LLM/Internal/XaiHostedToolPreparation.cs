using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>One admitted request frame. Authorization and resource access remain borrowed.</summary>
internal sealed class XaiHostedToolPreparation : IDisposable
{
    private Action? _fence;
    private IInferenceResourceAccess? _resources;
    private readonly List<string> _secrets = [];
    private readonly CancellationToken _token;

    private XaiHostedToolPreparation(LlmChatRequest request, IInferenceResourceAccess? resources, InferenceResourceSelectionVersion? selection,
        Action fence, CancellationToken token)
    {
        _resources = resources;
        Selection = selection;
        _fence = fence;
        _token = token;
        foreach (LlmRemoteMcpTool tool in request.HostedTools.OfType<LlmRemoteMcpTool>())
            if (tool.Headers is { } headers) _secrets.AddRange(headers.Values);
    }

    internal IReadOnlyList<string> Secrets => _secrets;
    internal IInferenceResourceAccess? Resources => _resources;
    internal InferenceResourceSelectionVersion? Selection { get; }

    internal void Fence()
    {
        _token.ThrowIfCancellationRequested();
        (_fence ?? throw new ObjectDisposedException(nameof(XaiHostedToolPreparation)))();
        _token.ThrowIfCancellationRequested();
    }

    internal static async Task<XaiHostedToolPreparation> PrepareAsync(JsonObject payload, LlmChatRequest request,
        IInferenceResourceAccess? resources, InferenceResourceSelectionVersion? selection, Action fence, CancellationToken token)
    {
        var frame = new XaiHostedToolPreparation(request, resources, selection, fence, token);
        try
        {
            var resolved = new Dictionary<IProviderAuthorization, string>(ReferenceEqualityComparer.Instance);
            for (int index = 0; index < request.HostedTools.Count; index++)
            {
                if (request.HostedTools[index] is not LlmRemoteMcpTool { Authorization: { } authority }) continue;
                frame.Fence();
                if (!resolved.TryGetValue(authority, out string? authorization))
                {
                    ProviderAuthorizationHeader header = await authority.GetAsync(token).ConfigureAwait(false);
                    frame.Fence();
                    if (header is null) throw new InvalidOperationException("MCP authorization returned no header.");
                    try { authorization = new AuthenticationHeaderValue(header.Scheme, header.Value).ToString(); }
                    catch (Exception error) when (error is ArgumentException or FormatException)
                    { throw new InvalidOperationException("MCP authorization returned an invalid header."); }
                    if (string.IsNullOrEmpty(header.Value) || header.Value.Contains('\r') || header.Value.Contains('\n'))
                        throw new InvalidOperationException("MCP authorization returned an invalid header.");
                    frame._secrets.Add(header.Value);
                    frame._secrets.Add(authorization);
                    resolved.Add(authority, authorization);
                }
                ((JsonObject)((JsonArray)payload["tools"]!)[request.Tools.Count + index]!)["authorization"] = authorization;
            }
            frame.Fence();
            return frame;
        }
        catch { frame.Dispose(); throw; }
    }

    public void Dispose()
    {
        _fence = null;
        _resources = null;
        _secrets.Clear();
    }
}
