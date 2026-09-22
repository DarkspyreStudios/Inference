using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.ConsoleTester;

internal sealed record ConsoleInvocation(
    string Command,
    ConsoleTesterProvider Provider,
    string[] CommandArguments,
    string? EndpointOverride)
{
    public static ConsoleInvocation Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0)
        {
            throw new ArgumentException("A command is required.", nameof(args));
        }

        string? providerId = null;
        string? harnessArn = null;
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
            else
            {
                commandArguments.Add(argument);
            }
        }

        if (string.IsNullOrWhiteSpace(providerId))
        {
            throw new ArgumentException(
                $"{args[0]} requires --provider <{string.Join('|', ConsoleTesterProviders.All.Select(provider => provider.Id))}>.",
                nameof(args));
        }

        ConsoleTesterProvider provider = ConsoleTesterProviders.Resolve(providerId);
        if (provider.Transport == LlmTransport.AwsBedrockAgentCore
            && string.IsNullOrWhiteSpace(harnessArn))
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

        return new ConsoleInvocation(
            args[0],
            provider,
            [.. commandArguments],
            harnessArn);
    }
}
