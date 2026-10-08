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

    [Theory]
    [InlineData(null, null, true)]
    [InlineData(null, "AUTHORIZED", true)]
    [InlineData("AVAILABLE", null, true)]
    [InlineData("FUTURE_STATUS", "AUTHORIZED", true)]
    [InlineData("AVAILABLE", "FUTURE_STATUS", true)]
    [InlineData("FUTURE_STATUS", "NOT_AUTHORIZED", false)]
    [InlineData("NOT_AVAILABLE", "FUTURE_STATUS", false)]
    [InlineData("PENDING", "AUTHORIZED", false)]
    [InlineData("ERROR", "AUTHORIZED", false)]
    [InlineData("AVAILABLE", "AUTHORIZED", true)]
    public async Task UnknownAvailabilityRetainsModelsAndProfilesUnlessAnotherFactDeniesAccess(
        string? agreement, string? authorization, bool listed)
    {
        InferenceProfileSummary profile = Profile("us." + Nova, Nova);
        profile.Models.Add(new() { ModelArn = "arn:aws:bedrock:us-east-1::foundation-model/" + Opus });
        var control = new Control(
            [Model(Nova, "ON_DEMAND"), Model(Opus, "ON_DEMAND")], [profile],
            availability: id => id == Opus ? Unsubscribed() : new()
            {
                AgreementAvailability = agreement is null ? null : new() { Status = AgreementStatus.FindValue(agreement) },
                AuthorizationStatus = authorization is null ? null : AuthorizationStatus.FindValue(authorization)
            });

        Assert.Equal(listed ? new[] { Nova, "us." + Nova } : [], await ListAsync(control));
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

    [Theory]
    [InlineData("amazon.nova-micro-v1:0", 128_000L, 5_000L)]
    [InlineData("amazon.nova-lite-v1:0", 300_000L, 5_000L)]
    [InlineData("amazon.nova-pro-v1:0", 300_000L, 5_000L)]
    [InlineData("amazon.nova-2-lite-v1:0", 1_000_000L, null)]
    [InlineData("openai.gpt-oss-20b-1:0", 128_000L, 16_000L)]
    [InlineData("openai.gpt-oss-120b-1:0", 128_000L, 16_000L)]
    public async Task PublishedNonClaudeLimitsReachCatalogWithoutInferringOtherIdentities(
        string modelId, long context, long? output)
    {
        string profileId = (modelId.StartsWith("openai.", StringComparison.Ordinal) ? "us-gov." : "us.") + modelId;
        string arn = "arn:aws:bedrock:us-east-1:123456789012:application-inference-profile/custom-profile";
        string unknown = modelId + "-future";
        var control = new Control([Model(modelId, "ON_DEMAND"), Model(unknown, "ON_DEMAND")],
            [Profile(profileId, modelId), Profile(arn, modelId)], availability: _ => Subscribed());

        IReadOnlyList<LlmModel> models = await ListModelsAsync(control);

        Assert.Equal(4, models.Count);
        foreach (string id in new[] { modelId, profileId })
        {
            LlmModel model = Assert.Single(models, model => model.Id == id);
            Assert.Equal((context, output), (model.MaxContextTokens, model.MaxOutputTokens));
        }
        foreach (string id in new[] { arn, unknown })
        {
            LlmModel model = Assert.Single(models, model => model.Id == id);
            Assert.Null(model.MaxContextTokens);
            Assert.Null(model.MaxOutputTokens);
        }
    }

    [Fact]
    public async Task ProfilesFromEveryPageAreListed()
    {
        var control = new Control([], [Profile("us." + Sonnet, Sonnet)], _ => Subscribed(),
            secondProfilePage: [Profile("global." + Sonnet, Sonnet)]);

        Assert.Equal(new[] { "global." + Sonnet, "us." + Sonnet }, await ListAsync(control));
    }

    [Theory]
    [InlineData("acquisition", "client")]
    [InlineData("profiles", "client")]
    [InlineData("foundations", "client")]
    [InlineData("availability", "client")]
    [InlineData("acquisition", "http")]
    [InlineData("profiles", "http")]
    [InlineData("foundations", "http")]
    [InlineData("availability", "http")]
    [InlineData("acquisition", "service")]
    [InlineData("profiles", "service")]
    [InlineData("foundations", "service")]
    [InlineData("availability", "service")]
    public async Task UnmodeledCatalogFailuresRetainSafeDiagnosticsAndLogOnce(string stage, string kind)
    {
        const string message = "Catalog request failed for fixture-key and fixture-secret.";
        var cause = new IOException("Private catalog graph fixture-secret.");
        Exception original = kind switch
        {
            "client" => new AmazonClientException(message, cause),
            "http" => new HttpRequestException(message, cause, HttpStatusCode.BadGateway),
            _ => new AmazonServiceException(message, cause, ErrorType.Sender,
                "error-fixture-secret", "request-fixture-secret", HttpStatusCode.ServiceUnavailable)
        };
        var logger = new Logger();
        using var control = new Control([Model(Nova, "ON_DEMAND")], [], _ => Subscribed())
        {
            FailureStage = stage,
            Failure = original
        };
        LlmProviderException failure = await Assert.ThrowsAsync<LlmProviderException>(() =>
            ListModelsAsync(control, logger, stage == "acquisition" ? original : null));
        Assert.Contains("Catalog request failed for [REDACTED] and [REDACTED].", failure.Message, StringComparison.Ordinal);
        Assert.Equal(LlmTransport.AwsBedrockConverse, failure.Transport);
        Assert.Equal(kind == "service" ? HttpStatusCode.ServiceUnavailable
            : kind == "http" ? HttpStatusCode.BadGateway : (HttpStatusCode?)null, failure.StatusCode);
        Assert.Equal(kind == "service" ? "error-[REDACTED]" : null, failure.ProviderCode);
        Assert.Equal(kind == "service" ? "request-[REDACTED]" : null, failure.RequestId);
        Assert.Null(failure.RetryAfter);
        Assert.Equal(LogLevel.Warning, Assert.Single(logger.Levels));
        string logged = Assert.Single(logger.Messages);
        Assert.Contains(failure.Message, logged, StringComparison.Ordinal);
        foreach (string secret in new[] { "fixture-key", "fixture-secret", "Private catalog" })
        {
            Assert.DoesNotContain(secret, failure.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(secret, logged, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ConcurrentAvailabilityFailuresRemainVisibleWhenDiagnosticLoggingAlsoFails(bool loggingFails) =>
        CheckConcurrentAvailabilityFailures("service", loggingFails);

    [Theory]
    [InlineData("client", false)]
    [InlineData("http", false)]
    [InlineData("client", true)]
    public Task ConcurrentUnmodeledAvailabilityFailuresRetainEveryObservedError(string kind, bool loggingFails) =>
        CheckConcurrentAvailabilityFailures(kind, loggingFails);

    private static async Task CheckConcurrentAvailabilityFailures(string kind, bool loggingFails)
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
                string message = "Access denied for bedrock:GetFoundationModelAvailability using fixture-secret. Model " + id;
                throw kind switch
                {
                    "client" => new AmazonClientException(message),
                    "http" => new HttpRequestException(message, null, HttpStatusCode.BadGateway),
                    _ => new AmazonBedrockException(message, ErrorType.Sender,
                        "AccessDeniedException", "request-" + id, HttpStatusCode.Forbidden)
                };
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
        if (kind == "service")
            Assert.Equal(new[] { "request-" + Opus, "request-" + Sonnet },
                providerFailures.Select(error => error.RequestId).Order(StringComparer.Ordinal));
        else Assert.All(providerFailures, error => Assert.Null(error.RequestId));
        Assert.Contains(providerFailures, error => error.Message.EndsWith(Opus, StringComparison.Ordinal));
        Assert.Contains(providerFailures, error => error.Message.EndsWith(Sonnet, StringComparison.Ordinal));
        Assert.All(providerFailures, error =>
        {
            Assert.Equal(kind == "service" ? HttpStatusCode.Forbidden
                : kind == "http" ? HttpStatusCode.BadGateway : (HttpStatusCode?)null, error.StatusCode);
            Assert.Equal(kind == "service" ? "AccessDeniedException" : null, error.ProviderCode);
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

    private static async Task<IReadOnlyList<LlmModel>> ListModelsAsync(Control control, Logger? logger = null,
        Exception? acquisitionFailure = null)
    {
        await using var factory = new BedrockClientFactory(
            _ => throw new NotSupportedException(),
            _ => acquisitionFailure is { } failure ? throw failure : control,
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
        internal string? FailureStage { get; init; }
        internal Exception? Failure { get; init; }

        public override Task<ListFoundationModelsResponse> ListFoundationModelsAsync(
            ListFoundationModelsRequest request, CancellationToken cancellationToken = default) =>
            FailureStage == "foundations" ? Task.FromException<ListFoundationModelsResponse>(Failure!)
                : Task.FromResult(new ListFoundationModelsResponse { ModelSummaries = models });

        public override Task<ListInferenceProfilesResponse> ListInferenceProfilesAsync(
            ListInferenceProfilesRequest request, CancellationToken cancellationToken = default)
        {
            if (FailureStage == "profiles") return Task.FromException<ListInferenceProfilesResponse>(Failure!);
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
            CheckAvailability?.Invoke(request.ModelId) ?? (FailureStage == "availability"
                ? Task.FromException<GetFoundationModelAvailabilityResponse>(Failure!) : Task.FromResult(availability(request.ModelId)));
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
