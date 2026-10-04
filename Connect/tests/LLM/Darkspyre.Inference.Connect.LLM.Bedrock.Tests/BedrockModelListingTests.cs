using System.Net;
using Amazon;
using Amazon.Bedrock;
using Amazon.Bedrock.Model;
using Amazon.Runtime;
using Microsoft.Extensions.Logging;

namespace Darkspyre.Inference.Connect.LLM.Bedrock.Tests;

public sealed class BedrockModelListingTests
{
    private const string Sonnet = "anthropic.claude-sonnet-4-5";
    private const string Opus = "anthropic.claude-opus-5-5";
    private const string Nova = "amazon.nova-lite-v1:0";

    [Fact]
    public async Task UnsubscribedModelIsNotListed()
    {
        var control = new Control(
            [Model(Nova, "ON_DEMAND"), Model(Opus, "ON_DEMAND")],
            [],
            availability: id => id == Opus ? Unsubscribed() : Subscribed());

        Assert.Equal(new[] { Nova }, await ListAsync(control));
    }

    [Fact]
    public async Task ProfileOnlyModelAppearsOnlyThroughItsProfiles()
    {
        var control = new Control(
            [Model(Sonnet, "INFERENCE_PROFILE"), Model(Opus, "INFERENCE_PROFILE")],
            [Profile("us." + Sonnet, Sonnet), Profile("global." + Sonnet, Sonnet), Profile("us." + Opus, Opus)],
            availability: id => id == Opus ? Unsubscribed() : Subscribed());

        Assert.Equal(new[] { "global." + Sonnet, "us." + Sonnet }, await ListAsync(control));
    }

    [Fact]
    public async Task DeniedAvailabilityFailsTheListing()
    {
        var control = new Control(
            [Model(Nova, "ON_DEMAND"), Model(Opus, "INFERENCE_PROFILE")],
            [Profile("us." + Opus, Opus)],
            availability: _ => throw new AccessDeniedException("not authorized") { StatusCode = HttpStatusCode.Forbidden });

        var logger = new Logger();

        var failure = await Assert.ThrowsAsync<LlmProviderException>(() => ListAsync(control, logger));
        Assert.Equal(HttpStatusCode.Forbidden, failure.StatusCode);
        Assert.Equal([LogLevel.Warning], logger.Levels);
    }

    private static async Task<string[]> ListAsync(Control control, Logger? logger = null)
    {
        await using var factory = new BedrockClientFactory(
            _ => throw new NotSupportedException(),
            _ => control,
            _ => throw new NotSupportedException());
        await using var guard = new BedrockLoggingGuard();
        var client = new BedrockLlmClient(factory, guard, new StubConverseInvoker(), logger ?? new Logger());
        IReadOnlyList<LlmModel> models = await client.ListModelsAsync(new()
        {
            Version = "v1",
            Transport = LlmTransport.AwsBedrockConverse,
            Endpoint = "aws://bedrock",
            Credential = new LlmCredential
            {
                Aws = new() { Region = "us-east-1", AccessKeyId = "fixture-key", SecretAccessKey = "fixture-secret" }
            }.Serialize()
        });
        return [.. models.Select(model => model.Id)];
    }

    private static FoundationModelSummary Model(string id, params string[] inferenceTypes) =>
        new() { ModelId = id, ModelName = id, InferenceTypesSupported = [.. inferenceTypes] };

    private static InferenceProfileSummary Profile(string id, string modelId) => new()
    {
        InferenceProfileId = id,
        InferenceProfileName = id,
        Models = [new() { ModelArn = "arn:aws:bedrock:us-east-1::foundation-model/" + modelId }]
    };

    private static GetFoundationModelAvailabilityResponse Subscribed() => new()
    {
        AgreementAvailability = new() { Status = AgreementStatus.AVAILABLE },
        AuthorizationStatus = AuthorizationStatus.AUTHORIZED
    };

    private static GetFoundationModelAvailabilityResponse Unsubscribed() => new()
    {
        AgreementAvailability = new() { Status = AgreementStatus.NOT_AVAILABLE },
        AuthorizationStatus = AuthorizationStatus.AUTHORIZED
    };

    private sealed class Control(
        List<FoundationModelSummary> models,
        List<InferenceProfileSummary> profiles,
        Func<string, GetFoundationModelAvailabilityResponse> availability)
        : AmazonBedrockClient(new BasicAWSCredentials("fixture-key", "fixture-secret"), RegionEndpoint.USEast1)
    {
        public override Task<ListFoundationModelsResponse> ListFoundationModelsAsync(
            ListFoundationModelsRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ListFoundationModelsResponse { ModelSummaries = models });

        public override Task<ListInferenceProfilesResponse> ListInferenceProfilesAsync(
            ListInferenceProfilesRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ListInferenceProfilesResponse { InferenceProfileSummaries = profiles });

        public override Task<GetFoundationModelAvailabilityResponse> GetFoundationModelAvailabilityAsync(
            GetFoundationModelAvailabilityRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(availability(request.ModelId));
    }

    private sealed class Logger : ILogger<BedrockLlmClient>
    {
        internal List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Levels) Levels.Add(logLevel);
        }
    }
}
