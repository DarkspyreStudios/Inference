using System.Collections.Concurrent;
using Amazon;
using Amazon.Bedrock;
using Amazon.BedrockAgentCore;
using Amazon.BedrockRuntime;
using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;

namespace Darkspyre.Inference.Connect.LLM.Bedrock;

internal sealed class BedrockClientFactory
{
    private readonly ConcurrentDictionary<string, AmazonBedrockRuntimeClient> _runtime = new();
    private readonly ConcurrentDictionary<string, AmazonBedrockClient> _control = new();
    private readonly ConcurrentDictionary<string, AmazonBedrockAgentCoreClient> _agentCore = new();

    public AmazonBedrockRuntimeClient Runtime(LlmAwsCredentials aws) =>
        _runtime.GetOrAdd(ScopeKey(aws), _ =>
        {
            RegionEndpoint region = ResolveRegion(aws);
            AWSCredentials? credentials = ResolveCredentials(aws);
            return credentials is null
                ? new AmazonBedrockRuntimeClient(region)
                : new AmazonBedrockRuntimeClient(credentials, region);
        });

    public AmazonBedrockClient Control(LlmAwsCredentials aws) =>
        _control.GetOrAdd(ScopeKey(aws), _ =>
        {
            RegionEndpoint region = ResolveRegion(aws);
            AWSCredentials? credentials = ResolveCredentials(aws);
            return credentials is null
                ? new AmazonBedrockClient(region)
                : new AmazonBedrockClient(credentials, region);
        });

    public AmazonBedrockAgentCoreClient AgentCore(LlmAwsCredentials aws) =>
        _agentCore.GetOrAdd(ScopeKey(aws), _ =>
        {
            RegionEndpoint region = ResolveRegion(aws);
            AWSCredentials? credentials = ResolveCredentials(aws);
            return credentials is null
                ? new AmazonBedrockAgentCoreClient(region)
                : new AmazonBedrockAgentCoreClient(credentials, region);
        });

    public static string ScopeKey(LlmAwsCredentials aws) =>
        $"{aws.Region}|{aws.Profile}|{aws.AccessKeyId}";

    private static RegionEndpoint ResolveRegion(LlmAwsCredentials aws)
    {
        if (string.IsNullOrWhiteSpace(aws.Region))
        {
            throw new InvalidOperationException("Bedrock requires an AWS region.");
        }

        return RegionEndpoint.GetBySystemName(aws.Region);
    }

    private static AWSCredentials? ResolveCredentials(LlmAwsCredentials aws)
    {
        if (!string.IsNullOrWhiteSpace(aws.AccessKeyId)
            && !string.IsNullOrWhiteSpace(aws.SecretAccessKey))
        {
            return string.IsNullOrWhiteSpace(aws.SessionToken)
                ? new BasicAWSCredentials(aws.AccessKeyId, aws.SecretAccessKey)
                : new SessionAWSCredentials(
                    aws.AccessKeyId,
                    aws.SecretAccessKey,
                    aws.SessionToken);
        }

        if (!string.IsNullOrWhiteSpace(aws.Profile))
        {
            var chain = new CredentialProfileStoreChain();
            if (chain.TryGetAWSCredentials(aws.Profile, out AWSCredentials? credentials))
            {
                return credentials;
            }

            throw new InvalidOperationException($"AWS profile '{aws.Profile}' was not found.");
        }

        return null;
    }
}
