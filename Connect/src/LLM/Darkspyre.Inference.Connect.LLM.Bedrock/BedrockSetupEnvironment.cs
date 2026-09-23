using Amazon;
using Amazon.Runtime.CredentialManagement;
using Darkspyre.Inference.Common.Setup;

namespace Darkspyre.Inference.Connect.LLM.Bedrock;

internal sealed class BedrockSetupEnvironment(LlmTransport transport) : ILlmSetupEnvironment
{
    public LlmTransport Transport { get; } = transport;

    public Task<IReadOnlyList<SetupChoice>> GetProfilesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<SetupChoice>>(new CredentialProfileStoreChain().ListProfiles()
            .Select(profile => new SetupChoice(profile.Name, profile.Name)).ToArray());
    }

    public Task<IReadOnlyList<SetupChoice>> GetRegionsAsync(string? profile, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<SetupChoice>>(RegionEndpoint.EnumerableAllRegions
            .Select(region => new SetupChoice(region.SystemName, region.DisplayName)).ToArray());
    }

    public SetupValidation Validate(LlmConnection connection)
    {
        var issues = new List<SetupIssue>();
        LlmAwsCredentials? aws = LlmCredential.Parse(connection.Credential)?.Aws;
        if (string.IsNullOrWhiteSpace(aws?.Profile)) { issues.Add(new("profile-required", "Select an AWS profile.", "aws-profile")); }
        if (string.IsNullOrWhiteSpace(aws?.Region)) { issues.Add(new("region-required", "Select an AWS region.", "aws-region")); }
        if (Transport == LlmTransport.AwsBedrockAgentCore)
        {
            string[] parts = connection.Endpoint.Split(':', 6);
            if (parts.Length != 6 || parts[0] != "arn" || parts[2] != "bedrock-agentcore"
                || parts[3] != aws?.Region || parts[4].Length != 12 || !parts[4].All(char.IsAsciiDigit)
                || !parts[5].StartsWith("harness/", StringComparison.Ordinal) || parts[5].Length <= 8)
            {
                issues.Add(new("harness-invalid", "Enter a Harness ARN matching the selected AWS region.", "harness-arn"));
            }
        }
        return new(issues.AsReadOnly());
    }
}
