using Darkspyre.Inference.Common.Setup;

namespace Darkspyre.Inference.Connect.LLM.Vertex;

internal sealed class VertexSetupEnvironment : ILlmSetupEnvironment
{
    public LlmTransport Transport => LlmTransport.GoogleVertexAi;
    public Task<IReadOnlyList<SetupChoice>> GetProfilesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SetupChoice>>([]);
    public Task<IReadOnlyList<SetupChoice>> GetRegionsAsync(string? profile, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SetupChoice>>([]);
    public SetupValidation Validate(LlmConnection connection)
    {
        try
        {
            VertexEndpoints.Validate(connection);
            if (connection.Vertex!.AuthenticationMode == LlmGoogleAuthenticationMode.ServiceAccount)
            {
                string? json = LlmCredential.Parse(connection.Credential)?.Google?.ServiceAccountJson;
                if (string.IsNullOrWhiteSpace(json)) { throw new ArgumentException("A service-account key is required."); }
                VertexAuthentication.ValidateServiceAccount(json);
            }
            return new([]);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException)
        {
            string message = connection.Vertex?.AuthenticationMode == LlmGoogleAuthenticationMode.ApiKey
                ? "Enter a Google Cloud API key without a project, location or other credential. Express mode serves Gemini only."
                : "Enter a valid Google project, explicit location and matching authentication configuration.";
            return new([new("vertex-configuration-invalid", message)]);
        }
    }
}
