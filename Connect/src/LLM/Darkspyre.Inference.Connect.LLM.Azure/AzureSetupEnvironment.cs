using Darkspyre.Inference.Common.Setup;

namespace Darkspyre.Inference.Connect.LLM.Azure;

internal sealed class AzureSetupEnvironment(LlmTransport transport) : ILlmSetupEnvironment
{
    public LlmTransport Transport => transport;
    public Task<IReadOnlyList<SetupChoice>> GetProfilesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SetupChoice>>([]);
    public Task<IReadOnlyList<SetupChoice>> GetRegionsAsync(string? profile, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SetupChoice>>([]);
    public SetupValidation Validate(LlmConnection connection)
    {
        try { _ = AzureEndpoints.Validate(connection); return new([]); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or System.Text.Json.JsonException)
        {
            return new([new("azure-configuration-invalid", "Only OpenAI v1 with an exclusive protected API key is available. Supply an HTTPS resource authority and exact deployment name; Entra and versioned routes are unavailable.")]);
        }
    }
}
