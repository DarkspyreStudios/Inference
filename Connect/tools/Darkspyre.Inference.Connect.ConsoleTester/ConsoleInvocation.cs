using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.ConsoleTester;

internal sealed record ConsoleInvocation(
    string Command,
    ConsoleTesterProvider Provider,
    string[] CommandArguments,
    string? EndpointOverride)
{
    public LlmVertexConfiguration? Vertex { get; init; }

    public bool NonStreaming { get; init; }

    public static ConsoleInvocation Parse(string[] args, ConsoleTesterProviders providers)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0)
        {
            throw new ArgumentException("A command is required.", nameof(args));
        }

        string? providerId = null;
        string? harnessArn = null;
        string? helper = null;
        string? endpoint = null;
        string? googleProject = null;
        string? googleLocation = null;
        LlmGoogleAuthenticationMode? googleAuthentication = null;
        bool nonStreaming = false;
        var commandArguments = new List<string>();
        for (int index = 1; index < args.Length; index++)
        {
            string argument = args[index];
            if (argument is "--provider" or "-p")
            {
                if (providerId is not null)
                {
                    throw new ArgumentException("Provider can be specified only once.", nameof(args));
                }

                if (++index >= args.Length)
                {
                    throw new ArgumentException($"{argument} requires a value.", nameof(args));
                }

                providerId = args[index];
            }
            else if (argument == "--harness-arn")
            {
                if (harnessArn is not null)
                {
                    throw new ArgumentException(
                        "Harness ARN can be specified only once.",
                        nameof(args));
                }
                if (++index >= args.Length)
                {
                    throw new ArgumentException(
                        "--harness-arn requires a value.",
                        nameof(args));
                }
                harnessArn = args[index];
            }
            else if (argument == "--endpoint")
            {
                if (endpoint is not null || ++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
                    throw new ArgumentException("--endpoint requires one endpoint URI.", nameof(args));
                endpoint = args[index];
            }
            else if (argument == "--google-authentication")
            {
                if (googleAuthentication is not null || ++index >= args.Length
                    || !Enum.TryParse(args[index], out LlmGoogleAuthenticationMode mode) || !Enum.IsDefined(mode))
                    throw new ArgumentException("--google-authentication requires one supported Google authentication mode.", nameof(args));
                googleAuthentication = mode;
            }
            else if (argument is "--google-project" or "--google-location")
            {
                if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
                    throw new ArgumentException("Google project/location options require a value.", nameof(args));
                if (argument == "--google-project") { googleProject = args[index]; }
                else { googleLocation = args[index]; }
            }
            else if (argument == "--non-streaming")
            {
                nonStreaming = true;
            }
            else if (argument == "--helper")
            {
                if (helper is not null || ++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
                    throw new ArgumentException("--helper requires one executable name or absolute path.", nameof(args));
                helper = args[index];
                if (helper.IndexOfAny(['\r', '\n', '\0']) >= 0
                    || !Path.IsPathFullyQualified(helper) && (helper.Contains('/') || helper.Contains('\\')))
                    throw new ArgumentException("--helper requires an executable name or absolute path.", nameof(args));
            }
            else
            {
                commandArguments.Add(argument);
            }
        }

        if (string.IsNullOrWhiteSpace(providerId))
        {
            throw new ArgumentException(
                $"{args[0]} requires --provider <{string.Join('|', providers.All.Select(provider => provider.Id))}>.",
                nameof(args));
        }

        ConsoleTesterProvider provider = providers.Resolve(providerId);
        if (nonStreaming && args[0] != "prompt")
            throw new ArgumentException("--non-streaming is valid only for prompt.", nameof(args));
        if (provider.Transport == LlmTransport.AwsBedrockAgentCore
            && args[0] != "setup" && string.IsNullOrWhiteSpace(harnessArn))
        {
            throw new ArgumentException(
                $"{args[0]} requires --harness-arn <arn> for provider agentcore.",
                nameof(args));
        }
        if (provider.Transport != LlmTransport.AwsBedrockAgentCore && harnessArn is not null)
        {
            throw new ArgumentException(
                "--harness-arn is valid only for provider agentcore.",
                nameof(args));
        }

        if (endpoint is not null && provider.Transport != LlmTransport.OpenAiCompatibleChat)
            throw new ArgumentException("--endpoint is valid only for the OpenAI-compatible provider.", nameof(args));

        if (helper is not null && provider.Transport != LlmTransport.AppleFoundationModels)
            throw new ArgumentException("--helper is valid only for provider apple.", nameof(args));
        if (args[0] == "apple-probe" && provider.Transport != LlmTransport.AppleFoundationModels)
            throw new ArgumentException("apple-probe requires --provider apple.", nameof(args));

        if (provider.Transport != LlmTransport.GoogleVertexAi && (googleProject is not null || googleLocation is not null || googleAuthentication is not null))
            throw new ArgumentException("Google configuration options require the Vertex provider.", nameof(args));
        if (googleAuthentication == LlmGoogleAuthenticationMode.ApiKey && (googleProject is not null || googleLocation is not null))
            throw new ArgumentException("Vertex express mode accepts no project or location.", nameof(args));
        if (provider.Transport == LlmTransport.GoogleVertexAi && args[0] != "setup" && googleAuthentication != LlmGoogleAuthenticationMode.ApiKey
            && (googleProject is null || googleLocation is null))
            throw new ArgumentException("Vertex requires --google-project and --google-location. Configure ADC outside the harness.", nameof(args));
        return new ConsoleInvocation(
            args[0],
            provider,
            [.. commandArguments],
            endpoint ?? helper ?? harnessArn)
        {
            NonStreaming = nonStreaming,
            Vertex = provider.Transport == LlmTransport.GoogleVertexAi ? new()
            { ProjectId = googleProject, Location = googleLocation, AuthenticationMode = googleAuthentication ?? LlmGoogleAuthenticationMode.ApplicationDefault } : null
        };
    }
}
