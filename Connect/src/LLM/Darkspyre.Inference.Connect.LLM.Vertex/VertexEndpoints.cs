using System.Text.RegularExpressions;

namespace Darkspyre.Inference.Connect.LLM.Vertex;

internal static partial class VertexEndpoints
{
    public const string BaseEndpoint = "https://aiplatform.googleapis.com";

    public static void Validate(LlmConnection connection)
    {
        if (connection.Transport != LlmTransport.GoogleVertexAi || connection.Vertex is not { } config
            || !Segment().IsMatch(config.ProjectId) || !Segment().IsMatch(config.Location)
            || !Enum.IsDefined(config.AuthenticationMode))
        {
            throw new ArgumentException("Vertex requires a project, location and supported authentication mode.");
        }
        if (connection.Endpoint.TrimEnd('/') != BaseEndpoint || connection.AdditionalHeaders.Count != 0
            || connection.ProviderOptions.Count != 0)
        {
            throw new NotSupportedException("Vertex owns endpoint routing, headers and request fields.");
        }
        LlmCredential? credential = LlmCredential.Parse(connection.Credential);
        if (credential is { } c && (c.ApiKey is not null || c.OAuth is not null || c.Aws is not null || c.SecretHeaders.Count != 0)
            || (config.AuthenticationMode == LlmGoogleAuthenticationMode.ApplicationDefault && credential?.Google is not null))
        {
            throw new ArgumentException("The credential does not match the selected Google authentication mode.");
        }
    }

    public static (string Publisher, string Model) ParseModel(string modelId)
    {
        string[] parts = modelId.Split('/');
        if (parts.Length != 2 || parts[0] is not ("google" or "anthropic") || !ModelSegment().IsMatch(parts[1]))
        {
            throw new ArgumentException("Use a publisher-qualified Vertex model ID: google/model or anthropic/model.");
        }
        return (parts[0], parts[1]);
    }

    public static Uri Generation(LlmConnection connection, string modelId)
    {
        Validate(connection);
        var (publisher, model) = ParseModel(modelId);
        var config = connection.Vertex!;
        string host = config.Location == "global" ? "aiplatform.googleapis.com" : $"{config.Location}-aiplatform.googleapis.com";
        string operation = publisher == "google" ? "streamGenerateContent?alt=sse" : "streamRawPredict";
        return new($"https://{host}/v1/projects/{config.ProjectId}/locations/{config.Location}/publishers/{publisher}/models/{model}:{operation}");
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex Segment();

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9._@-]{0,199}$", RegexOptions.CultureInvariant)]
    private static partial Regex ModelSegment();
}
