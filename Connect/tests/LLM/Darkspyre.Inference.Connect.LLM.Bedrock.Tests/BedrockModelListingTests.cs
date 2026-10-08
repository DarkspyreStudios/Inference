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
    public async Task FoundationModelCarriesItsLimits()
    {
        var control = new Control([Model("anthropic.claude-opus-4-6-v1", "ON_DEMAND")], [], availability: _ => Subscribed());

        LlmModel model = Assert.Single(await ListModelsAsync(control));
        Assert.Equal((1_000_000L, 128_000L), (model.MaxContextTokens, model.MaxOutputTokens));
    }

    [Fact]
    public async Task ProfilesFromEveryPageAreListed()
    {
        var control = new Control([], [Profile("us." + Sonnet, Sonnet)], _ => Subscribed(),
            secondProfilePage: [Profile("global." + Sonnet, Sonnet)]);

        Assert.Equal(new[] { "global." + Sonnet, "us." + Sonnet }, await ListAsync(control));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentAvailabilityFailuresRemainVisibleWhenDiagnosticLoggingAlsoFails(bool loggingFails)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loggingFailure = new InvalidOperationException("The diagnostic sink failed.");
        var logger = new Logger { NextWarningFailure = loggingFails ? loggingFailure : null };
        int requests = 0;
        var control = new Control([Model(Sonnet, "ON_DEMAND"), Model(Opus, "ON_DEMAND")], [], _ => Subscribed())
        {
            CheckAvailability = async id =>
            {
                if (Interlocked.Increment(ref requests) == 2) entered.TrySetResult();
                await release.Task;
                throw new AmazonBedrockException("Access denied for bedrock:GetFoundationModelAvailability using fixture-secret.", ErrorType.Sender,
                    "AccessDeniedException", "request-" + id, HttpStatusCode.Forbidden);
            }
        };
        Task<IReadOnlyList<LlmModel>> listing = ListModelsAsync(control, logger);
        Exception? failure;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            release.SetResult();
            failure = await Record.ExceptionAsync(() => listing.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            release.TrySetResult();
            _ = await Record.ExceptionAsync(() => listing.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        var causes = Assert.IsType<AggregateException>(failure).Flatten().InnerExceptions;
        LlmProviderException[] providerFailures = [.. causes.OfType<LlmProviderException>()];
        Assert.Equal(2, providerFailures.Length);
        Assert.Equal(new[] { "request-" + Opus, "request-" + Sonnet },
            providerFailures.Select(error => error.RequestId).Order(StringComparer.Ordinal));
        Assert.All(providerFailures, error =>
        {
            Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
            Assert.Equal("AccessDeniedException", error.ProviderCode);
            Assert.Contains("Access denied for bedrock:GetFoundationModelAvailability using [REDACTED].", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("fixture-secret", error.ToString(), StringComparison.Ordinal);
        });
        Assert.Equal(loggingFails ? 3 : 2, causes.Count);
        Assert.Equal(2, logger.Levels.Count(level => level == LogLevel.Warning));
        Assert.All(logger.Messages, message =>
        {
            Assert.Contains("Access denied for bedrock:GetFoundationModelAvailability using [REDACTED].", message, StringComparison.Ordinal);
            Assert.DoesNotContain("fixture-secret", message, StringComparison.Ordinal);
        });
        Assert.Equal(2, requests);
    }

    private static async Task<string[]> ListAsync(Control control, Logger? logger = null) =>
        [.. (await ListModelsAsync(control, logger)).Select(model => model.Id)];

    private static async Task<IReadOnlyList<LlmModel>> ListModelsAsync(Control control, Logger? logger = null)
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
        return models;
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
        Func<string, GetFoundationModelAvailabilityResponse> availability,
        List<InferenceProfileSummary>? secondProfilePage = null)
        : AmazonBedrockClient(new BasicAWSCredentials("fixture-key", "fixture-secret"), RegionEndpoint.USEast1)
    {
        internal Func<string, Task<GetFoundationModelAvailabilityResponse>>? CheckAvailability { get; init; }

        public override Task<ListFoundationModelsResponse> ListFoundationModelsAsync(
            ListFoundationModelsRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ListFoundationModelsResponse { ModelSummaries = models });

        public override Task<ListInferenceProfilesResponse> ListInferenceProfilesAsync(
            ListInferenceProfilesRequest request, CancellationToken cancellationToken = default)
        {
            if (request.NextToken is null)
                return Task.FromResult(new ListInferenceProfilesResponse
                {
                    InferenceProfileSummaries = profiles,
                    NextToken = secondProfilePage is null ? null : "page-two"
                });

            Assert.Equal("page-two", request.NextToken);
            return Task.FromResult(new ListInferenceProfilesResponse { InferenceProfileSummaries = secondProfilePage });
        }

        public override Task<GetFoundationModelAvailabilityResponse> GetFoundationModelAvailabilityAsync(
            GetFoundationModelAvailabilityRequest request, CancellationToken cancellationToken = default) =>
            CheckAvailability?.Invoke(request.ModelId) ?? Task.FromResult(availability(request.ModelId));
    }

    private sealed class Logger : ILogger<BedrockLlmClient>
    {
        internal List<LogLevel> Levels { get; } = [];
        internal List<string> Messages { get; } = [];
        internal Exception? NextWarningFailure { get; set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Levels)
            {
                Levels.Add(logLevel);
                Messages.Add(formatter(state, exception));
                if (logLevel == LogLevel.Warning && NextWarningFailure is { } failure)
                {
                    NextWarningFailure = null;
                    throw failure;
                }
            }
        }
    }
}
