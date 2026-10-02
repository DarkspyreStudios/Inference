using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Vertex;

internal sealed partial class VertexLlmClient
{
    void ILlmContextCacheAdapter.ValidateCacheConnection(LlmConnection connection) => ValidateCacheConnection(connection);
    void ILlmContextCacheAdapter.ValidateCacheReference(LlmConnection connection, LlmExplicitCacheReference reference) => ValidateCacheReference(connection, reference);
    void ILlmContextCacheAdapter.ValidateCachedSuffix(LlmChatRequest request) => ValidateCachedSuffix(request);
    void ILlmContextCacheAdapter.ValidateCacheCreate(LlmConnection connection, LlmContextCacheCreate request) => ValidateCacheCreate(connection, request);

    private static void ValidateCacheConnection(LlmConnection connection)
    {
        VertexEndpoints.Validate(connection);
        if (connection.Vertex!.AuthenticationMode == LlmGoogleAuthenticationMode.ApiKey)
            throw new NotSupportedException("Vertex express context cache routes are not implemented.");
    }

    private static void ValidateCacheReference(LlmExplicitCacheReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        _ = CacheIdentity(reference.ResourceName);
        if (VertexEndpoints.ParseModel(reference.ModelId).Publisher != "google")
            throw new NotSupportedException("Vertex explicit caches serve Gemini models only.");
        _ = VertexModelProfiles.Get(reference.ModelId);
    }

    private static void ValidateCacheReference(LlmConnection connection, LlmExplicitCacheReference reference)
    {
        ValidateCacheConnection(connection);
        ValidateCacheReference(reference);
        var identity = CacheIdentity(reference.ResourceName);
        var config = connection.Vertex!;
        if (identity.Location != config.Location || config.ProjectId!.All(char.IsAsciiDigit) && identity.Project != config.ProjectId)
            throw new ArgumentException("The cache does not match the selected Vertex project/location.", nameof(reference));
    }

    private static void ValidateCachedSuffix(LlmChatRequest request)
    {
        if (request.ExplicitCache is not { } reference) return;
        ValidateCacheReference(reference);
        if (request.ModelId != reference.ModelId || !string.IsNullOrWhiteSpace(request.Instructions)
            || request.Messages.Any(message => message.Role == "system") || request.Tools.Count != 0 || request.ToolChoice is not null)
            throw new ArgumentException("Cached Vertex Gemini requests cannot replace model, instructions, tools or tool configuration.", nameof(request));
    }

    async Task<ILlmContextCacheOperation> ILlmContextCacheAdapter.PrepareCacheOperationAsync(LlmConnection connection,
        Action<CancellationToken> fence, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); fence(token); ValidateCacheConnection(connection);
        VertexAccess access = await authentication.ResolveAsync(connection, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested(); fence(token);
        return new CacheOperation(this, connection with
        {
            CacheCredentialEvidence = access.Scope,
            ContinuationCodec = new VertexContinuationCodec(),
            ResolvedContinuationScope = access.Scope
        }, access);
    }

    private Task<VertexAccess> ResolveAccessAsync(LlmConnection connection, CancellationToken token) =>
        connection.CacheOperation is CacheOperation prepared && ReferenceEquals(prepared.Owner, this)
            ? Task.FromResult(prepared.Access) : authentication.ResolveAsync(connection, token);

    private static (string Project, string Location, string Id) CacheIdentity(string name)
    {
        string[] parts = name.Split('/');
        if (parts.Length != 6 || parts[0] != "projects" || parts[2] != "locations" || parts[4] != "cachedContents"
            || parts[1].Length == 0 || parts[1].Any(character => !char.IsAsciiDigit(character)))
            throw new ArgumentException("Use a project-qualified Vertex cache resource name.", nameof(name));
        _ = GeminiCacheReads.ResourceId("cachedContents/" + parts[5]);
        if (parts[3].Length == 0 || parts[3].Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '-')))
            throw new ArgumentException("Use a valid Vertex cache location.", nameof(name));
        return (parts[1], parts[3], parts[5]);
    }

    private sealed partial class CacheOperation(VertexLlmClient owner, LlmConnection connection, VertexAccess access) : ILlmContextCacheOperation
    {
        internal VertexLlmClient Owner { get; } = owner;
        internal VertexAccess Access { get; } = access;
        public LlmConnection Connection { get; } = connection;

        private HttpRequestMessage Message(HttpMethod method, string suffix = "")
        {
            var config = Connection.Vertex!;
            string host = config.Location == "global" ? "aiplatform.googleapis.com" : config.Location + "-aiplatform.googleapis.com";
            var message = new HttpRequestMessage(method,
                $"https://{host}/v1/projects/{config.ProjectId}/locations/{config.Location}/cachedContents{suffix}");
            try { Access.Authorize(message); }
            catch { message.Dispose(); throw; }
            return message;
        }

        private string SelectId(LlmExplicitCacheReference reference)
        {
            ValidateCacheReference(Connection, reference);
            var identity = CacheIdentity(reference.ResourceName);
            return Uri.EscapeDataString(identity.Id);
        }

        private LlmContextCache Parse(JsonElement root, GeminiCacheBindings.Admission admission,
            string? expectedName, string? expectedModel, Action<CancellationToken> fence, CancellationToken token, bool observe = true)
        {
            string? numericProject = null;
            var config = Connection.Vertex!;
            LlmContextCache receipt = GoogleCacheData.Receipt(root, admission.Scope, name =>
            {
                var identity = CacheIdentity(name);
                if (identity.Location != config.Location || expectedName is not null && name != expectedName
                    || config.ProjectId!.All(char.IsAsciiDigit) && config.ProjectId != identity.Project) throw new JsonException();
                numericProject = identity.Project;
            }, model =>
            {
                string[] parts = model.Split('/');
                string canonical;
                if (parts.Length == 2 && parts[0] == "models") canonical = "google/" + parts[1];
                else if (parts.Length == 8 && parts[0] == "projects" && parts[2] == "locations"
                    && parts[4] == "publishers" && parts[5] == "google" && parts[6] == "models"
                    && (parts[1] == config.ProjectId || parts[1] == numericProject) && parts[3] == config.Location)
                    canonical = "google/" + parts[7];
                else throw new JsonException();
                _ = VertexModelProfiles.Get(canonical);
                if (expectedModel is not null && canonical != expectedModel) throw new JsonException();
                return canonical;
            }, vertex: true);
            token.ThrowIfCancellationRequested(); fence(token);
            if (observe) admission.Owner.ObserveNativeProject(admission, numericProject!);
            return receipt;
        }

        public async Task<LlmContextCache> GetAsync(LlmExplicitCacheReference reference, GeminiCacheBindings.Admission admission,
            Action<CancellationToken> fence, CancellationToken token)
        {
            using var message = Message(HttpMethod.Get, "/" + SelectId(reference));
            try
            {
                return await GoogleCacheHttp.GetAsync(Owner.Http, message, Owner.Transport,
                    root => Parse(root, admission, reference.ResourceName, reference.ModelId, fence, token), fence, token).ConfigureAwait(false);
            }
            catch (LlmProviderException error) when (error.StatusCode == HttpStatusCode.NotFound)
            { throw new LlmContextCacheUnavailableException(LlmContextCacheUnavailableReason.Missing, reference, error.StatusCode, error.RequestId, error); }
        }

        public async Task<LlmContextCachePage> ListAsync(LlmContextCacheList request, GeminiCacheBindings.Admission admission,
            Action<CancellationToken> fence, CancellationToken token)
        {
            GoogleCacheData.ValidatePage(request);
            string query = "?pageSize=" + request.PageSize.ToString(CultureInfo.InvariantCulture)
                + (request.PageToken is null ? "" : "&pageToken=" + Uri.EscapeDataString(request.PageToken));
            using var message = Message(HttpMethod.Get, query);
            return await GoogleCacheHttp.ListAsync(Owner.Http, message, Owner.Transport, root =>
            {
                LlmContextCachePage page = GoogleCacheData.Page(root, item => Parse(item, admission, null, null, fence, token, observe: false));
                string? project = null;
                foreach (LlmContextCache receipt in page.Items)
                {
                    string actual = CacheIdentity(receipt.Reference.ResourceName).Project;
                    if (project is not null && project != actual) throw new JsonException();
                    project = actual;
                }
                fence(token);
                if (project is not null) admission.Owner.ObserveNativeProject(admission, project);
                return page;
            }, fence, token).ConfigureAwait(false);
        }

        public async Task<LlmContextCache> UpdateExpiryAsync(LlmExplicitCacheReference reference, DateTimeOffset expiry,
            GeminiCacheBindings.Admission admission, Action<CancellationToken> fence, CancellationToken token)
        {
            using var message = Message(HttpMethod.Patch, "/" + SelectId(reference) + "?updateMask=expireTime");
            message.Content = JsonContent.Create(new JsonObject { ["expireTime"] = GoogleCacheData.Timestamp(expiry) });
            return await GoogleCacheHttp.UpdateExpiryAsync(Owner.Http, message, Owner.Transport, reference,
                root => Parse(root, admission, reference.ResourceName, reference.ModelId, fence, token), fence, token).ConfigureAwait(false);
        }

        public async Task<LlmContextCacheDeleteResult> DeleteAsync(LlmExplicitCacheReference reference,
            Func<LlmExplicitCacheReference, bool> authorizeAlreadyAbsent, GeminiCacheBindings.Admission admission,
            Action<CancellationToken> fence, CancellationToken token)
        {
            using var message = Message(HttpMethod.Delete, "/" + SelectId(reference));
            return await GoogleCacheHttp.DeleteAsync(Owner.Http, message, Owner.Transport, reference, authorizeAlreadyAbsent, fence, token).ConfigureAwait(false);
        }

        public Task<LlmContextCache> CreateAsync(LlmContextCacheCreate request, GeminiCacheBindings.Admission admission,
            Action<CancellationToken> fence, CancellationToken token) => CreateCoreAsync(request, admission, fence, token);
    }
}
