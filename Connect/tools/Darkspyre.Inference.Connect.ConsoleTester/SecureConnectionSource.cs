using System.Security.Cryptography;
using System.Text;
using Darkspyre.Inference.Connect.LLM;
using SecureStore;

namespace Darkspyre.Inference.Connect.ConsoleTester;

internal sealed class SecureConnectionSource(
    ICredentialStore credentials,
    ConsoleTesterProviders providers,
    ConsoleInvocation? invocation = null) : ILlmConnectionSource
{
    private const string CredentialPrefix = "provider-credential-";
    private const string CredentialMutexName =
        "Darkspyre.Inference.Connect.ConsoleTester.CredentialCompareAndSave";

    public async Task<LlmConnection?> GetConnectionAsync(
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        ConsoleTesterProvider? provider = providers.FindByConnectionId(connectionId);
        if (provider is null)
        {
            return null;
        }

        string? credential = UsesHostCredential(provider)
            ? await credentials.GetAsync(CredentialName(provider), cancellationToken)
            : null;
        return BuildConnection(provider, credential);
    }

    public Task SaveCredentialAsync(
        string connectionId,
        string credentialJson,
        string expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ConsoleTesterProvider? provider = providers.FindByConnectionId(connectionId);
        if (provider is null)
        {
            throw new InvalidOperationException($"No LLM connection found for '{connectionId}'.");
        }
        if (!UsesHostCredential(provider))
        {
            throw new NotSupportedException(
                $"{provider.DisplayName} does not use a host-stored credential.");
        }

        return Task.Run(
            () => SaveCredential(
                provider,
                credentialJson,
                expectedVersion,
                cancellationToken),
            cancellationToken);
    }

    private void SaveCredential(
        ConsoleTesterProvider provider,
        string credentialJson,
        string expectedVersion,
        CancellationToken cancellationToken)
    {
        using var processMutex = new Mutex(false, CredentialMutexName);
        bool acquired = false;
        try
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                while (!processMutex.WaitOne(TimeSpan.FromMilliseconds(100)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                acquired = true;
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            string? currentCredential = credentials.GetAsync(
                    CredentialName(provider),
                    cancellationToken)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            LlmConnection current = BuildConnection(provider, currentCredential);
            if (!string.Equals(current.Version, expectedVersion, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The test connection changed during authentication or credential refresh.");
            }

            credentials.SetAsync(
                    CredentialName(provider),
                    credentialJson,
                    cancellationToken)
                .AsTask()
                .GetAwaiter()
                .GetResult();
        }
        finally
        {
            if (acquired)
            {
                processMutex.ReleaseMutex();
            }
        }
    }

    private LlmConnection BuildConnection(
        ConsoleTesterProvider provider,
        string? credential)
    {
        string endpoint = invocation is not null
            && invocation.Provider.ConnectionId == provider.ConnectionId
            && invocation.EndpointOverride is not null
                ? invocation.EndpointOverride
                : provider.Endpoint;
        return provider.CreateConnection(
            ComputeVersion(provider, endpoint + invocation?.Vertex?.ProjectId + "/" + invocation?.Vertex?.Location, credential), credential) with
        { Endpoint = endpoint, Vertex = invocation?.Vertex };
    }

    private static string ComputeVersion(
        ConsoleTesterProvider provider,
        string endpoint,
        string? credential)
    {
        string state = string.Join(
            '\0',
            provider.ConnectionId,
            provider.Transport.ToString(),
            endpoint,
            credential ?? string.Empty);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state)));
    }

    private static string CredentialName(ConsoleTesterProvider provider) =>
        $"{CredentialPrefix}{provider.Id}";

    private bool UsesHostCredential(ConsoleTesterProvider provider) =>
        StoresHostCredential(provider.Transport) &&
        // An explicit local test endpoint is anonymous. A credential stored for the
        // default endpoint must not be read or sent to this different destination.
        !(provider.Transport == LlmTransport.OpenAiCompatibleChat &&
          invocation?.Provider.ConnectionId == provider.ConnectionId && invocation.EndpointOverride is not null);

    private static bool StoresHostCredential(LlmTransport transport) => transport is
        LlmTransport.OpenAiCodex
        or LlmTransport.OpenAiResponses
        or LlmTransport.OpenAiCompatibleChat
        or LlmTransport.AnthropicMessages
        or LlmTransport.AwsBedrockConverse
        or LlmTransport.AwsBedrockAgentCore
        or LlmTransport.GoogleGeminiApi;
}
