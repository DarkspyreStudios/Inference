using System.Text.RegularExpressions;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Vertex;

internal static partial class VertexEndpoints
{
    public const string BaseEndpoint = "https://aiplatform.googleapis.com";

    public static void Validate(LlmConnection connection)
    {
        if (connection.Transport != LlmTransport.GoogleVertexAi || connection.Vertex is not { } config
            || !Enum.IsDefined(config.AuthenticationMode))
        {
            throw new ArgumentException("Vertex requires a project, location and supported authentication mode.");
        }
        if (connection.Endpoint.TrimEnd('/') != BaseEndpoint || connection.AdditionalHeaders.Count != 0
            || connection.ChatCompletions is not null)
        {
            throw new NotSupportedException("Vertex owns endpoint routing, headers and request fields.");
        }
        LlmCredential? credential = LlmCredential.Parse(connection.Credential);
        if (config.AuthenticationMode == LlmGoogleAuthenticationMode.ApiKey)
        {
            if (config.ProjectId is not null || config.Location is not null
                || string.IsNullOrWhiteSpace(credential?.ApiKey) || credential.Google is not null
                || credential.OAuth is not null || credential.Aws is not null || credential.SecretHeaders.Count != 0)
                throw new ArgumentException("Vertex express mode requires an API-key-only credential and no project or location.");
            return;
        }
        if (config.ProjectId is null || config.Location is null
            || !Segment().IsMatch(config.ProjectId) || !Segment().IsMatch(config.Location))
            throw new ArgumentException("Vertex requires a project and an explicit location.");
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

    /// <summary>The generation URL: the streaming operation when <paramref name="stream"/> is true, else the complete one.</summary>
    public static Uri Generation(LlmConnection connection, string modelId, bool stream)
    {
        Validate(connection);
        var (publisher, model) = ParseModel(modelId);
        ValidatePublisher(connection, publisher);
        string operation = (publisher, stream) switch
        {
            ("google", true) => "streamGenerateContent?alt=sse",
            ("google", false) => "generateContent",
            (_, true) => "streamRawPredict",
            _ => "rawPredict"
        };
        return ModelOperation(connection, publisher, model, operation);
    }

    internal static Uri CountTokens(LlmConnection connection, string modelId)
    {
        Validate(connection);
        var (publisher, model) = ParseModel(modelId);
        ValidatePublisher(connection, publisher);
        _ = VertexModelProfiles.Get(modelId);
        if (publisher == "anthropic")
        {
            if (connection.Vertex!.Location is not ("global" or "us" or "eu" or "asia-southeast1"))
                throw new NotSupportedException("Claude token counting is not supported in the selected Vertex location. The request is not rerouted.");
            return ModelOperation(connection, publisher, "count-tokens", "rawPredict");
        }
        return ModelOperation(connection, publisher, model, "countTokens");
    }

    private static Uri ModelOperation(LlmConnection connection, string publisher, string model, string operation)
    {
        var config = connection.Vertex!;
        if (config.AuthenticationMode == LlmGoogleAuthenticationMode.ApiKey)
            return new($"{BaseEndpoint}/v1/publishers/{publisher}/models/{model}:{operation}");
        string host = config.Location == "global" ? "aiplatform.googleapis.com" : $"{config.Location}-aiplatform.googleapis.com";
        return new($"https://{host}/v1/projects/{config.ProjectId}/locations/{config.Location}/publishers/{publisher}/models/{model}:{operation}");
    }

    private static void ValidatePublisher(LlmConnection connection, string publisher)
    {
        if (connection.Vertex!.AuthenticationMode == LlmGoogleAuthenticationMode.ApiKey && publisher != "google")
            throw new NotSupportedException("Vertex express mode serves Gemini models only.");
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex Segment();

    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9._@-]{0,199}$", RegexOptions.CultureInvariant)]
    private static partial Regex ModelSegment();
}
