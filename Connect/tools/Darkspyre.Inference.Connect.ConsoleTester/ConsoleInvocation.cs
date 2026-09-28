using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.ConsoleTester;

internal sealed record ConsoleInvocation(
    string Command,
    ConsoleTesterProvider Provider,
    string[] CommandArguments,
    string? EndpointOverride)
{
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

        if (helper is not null && provider.Transport != LlmTransport.AppleFoundationModels)
            throw new ArgumentException("--helper is valid only for provider apple.", nameof(args));
        if (args[0] == "apple-probe" && provider.Transport != LlmTransport.AppleFoundationModels)
            throw new ArgumentException("apple-probe requires --provider apple.", nameof(args));

        return new ConsoleInvocation(
            args[0],
            provider,
            [.. commandArguments],
            helper ?? harnessArn);
    }
}
