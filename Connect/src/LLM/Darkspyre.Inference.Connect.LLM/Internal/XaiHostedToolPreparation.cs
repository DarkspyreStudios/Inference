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
    private Action<Exception>? _recordCleanup;
    private IInferenceResourceAccess? _resources;
    private AuthorizationMemo? _authorization;
    private readonly bool _ownsAuthorization;
    private readonly CancellationToken _token;

    private XaiHostedToolPreparation(IInferenceResourceAccess? resources, InferenceResourceSelectionVersion? selection,
        Action fence, Action<Exception>? recordCleanup, CancellationToken token, AuthorizationMemo authorization,
        bool ownsAuthorization)
    {
        _resources = resources;
        Selection = selection;
        _fence = fence;
        _recordCleanup = recordCleanup;
        _token = token;
        _authorization = authorization;
        _ownsAuthorization = ownsAuthorization;
    }

    internal IReadOnlyList<string> Secrets => _authorization?.Secrets ?? [];
    internal IInferenceResourceAccess? Resources => _resources;
    internal InferenceResourceSelectionVersion? Selection { get; }

    internal void RecordCleanup(Exception error) => _recordCleanup?.Invoke(error);

    internal void Fence()
    {
        _token.ThrowIfCancellationRequested();
        (_fence ?? throw new ObjectDisposedException(nameof(XaiHostedToolPreparation)))();
        _token.ThrowIfCancellationRequested();
    }

    internal static async Task<XaiHostedToolPreparation> PrepareAsync(JsonObject payload, LlmChatRequest request,
        IInferenceResourceAccess? resources, InferenceResourceSelectionVersion? selection, Action fence,
        Action<Exception>? recordCleanup, CancellationToken token, AuthorizationMemo? authorization = null)
    {
        AuthorizationMemo memo = authorization ?? new AuthorizationMemo(fence, token);
        var frame = new XaiHostedToolPreparation(resources, selection, fence, recordCleanup, token, memo,
            ownsAuthorization: authorization is null);
        try
        {
            memo.AddStatic(request);
            for (int index = 0; index < request.HostedTools.Count; index++)
            {
                if (request.HostedTools[index] is not LlmRemoteMcpTool { Authorization: { } authority }) continue;
                string resolved = await memo.GetAsync(authority).ConfigureAwait(false);
                frame.Fence();
                ((JsonObject)((JsonArray)payload["tools"]!)[request.Tools.Count + index]!)["authorization"] = resolved;
            }
            frame.Fence();
            return frame;
        }
        catch { frame.Dispose(); throw; }
    }

    public void Dispose()
    {
        _fence = null;
        _recordCleanup = null;
        _resources = null;
        AuthorizationMemo? authorization = _authorization;
        _authorization = null;
        if (_ownsAuthorization) authorization?.Dispose();
    }

    /// <summary>One serial operation's borrowed authorization identities and diagnostic secrets.</summary>
    internal sealed class AuthorizationMemo(Action fence, CancellationToken token) : IDisposable
    {
        private Action? _fence = fence;
        private readonly Dictionary<IProviderAuthorization, string> _resolved = new(ReferenceEqualityComparer.Instance);
        private readonly List<string> _secrets = [];

        internal IReadOnlyList<string> Secrets => _secrets;

        internal void AddStatic(LlmChatRequest request)
        {
            Fence();
            foreach (LlmRemoteMcpTool tool in request.HostedTools.OfType<LlmRemoteMcpTool>())
                if (tool.Headers is { } headers) _secrets.AddRange(headers.Values);
            Fence();
        }

        internal async Task<string> GetAsync(IProviderAuthorization authority)
        {
            Fence();
            if (!_resolved.TryGetValue(authority, out string? authorization))
            {
                ProviderAuthorizationHeader header = await authority.GetAsync(token).ConfigureAwait(false);
                Fence();
                if (header is null) throw new InvalidOperationException("MCP authorization returned no header.");
                try { authorization = new AuthenticationHeaderValue(header.Scheme, header.Value).ToString(); }
                catch (Exception error) when (error is ArgumentException or FormatException)
                { throw new InvalidOperationException("MCP authorization returned an invalid header."); }
                if (string.IsNullOrEmpty(header.Value) || header.Value.Contains('\r') || header.Value.Contains('\n'))
                    throw new InvalidOperationException("MCP authorization returned an invalid header.");
                _secrets.Add(header.Value);
                _secrets.Add(authorization);
                _resolved.Add(authority, authorization);
            }
            Fence();
            return authorization;
        }

        private void Fence()
        {
            token.ThrowIfCancellationRequested();
            (_fence ?? throw new ObjectDisposedException(nameof(AuthorizationMemo)))();
            token.ThrowIfCancellationRequested();
        }

        public void Dispose()
        {
            _fence = null;
            _resolved.Clear();
            _secrets.Clear();
        }
    }
}
