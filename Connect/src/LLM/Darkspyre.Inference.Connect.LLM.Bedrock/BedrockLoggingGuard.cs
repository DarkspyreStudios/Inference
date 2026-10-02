using System.Collections.Concurrent;
using Amazon.Bedrock;
using Amazon.Bedrock.Model;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Bedrock;

internal sealed class BedrockLoggingGuard : IDisposable, IAsyncDisposable
{
    private readonly LlmOperationLifetime _lifetime = new();
    internal static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _verifiedAt = new(
        StringComparer.OrdinalIgnoreCase);

    public Task EnsureLoggingDisabledAsync(
        BedrockClientFactory.Lease<AmazonBedrockClient> control,
        LlmAwsCredentials aws,
        CancellationToken cancellationToken = default) =>
        EnsureLoggingDisabledAsync(
            BedrockClientFactory.ScopeKey(aws),
            aws.Region!,
            async ct =>
            {
                control.ThrowIfCanceled();
                GetModelInvocationLoggingConfigurationResponse response = await control.Client.GetModelInvocationLoggingConfigurationAsync(
                    new GetModelInvocationLoggingConfigurationRequest(), ct);
                control.ThrowIfCanceled();
                return response.LoggingConfig;
            },
            cancellationToken);

    internal async Task EnsureLoggingDisabledAsync(
        string scopeKey,
        string region,
        Func<CancellationToken, Task<LoggingConfig?>> fetchLoggingConfig,
        CancellationToken cancellationToken = default)
    {
        using LlmOperationLifetime.Lease operation = _lifetime.Enter(cancellationToken);
        cancellationToken = operation.Token;
        ThrowIfOperationCanceled(cancellationToken);
        if (_verifiedAt.TryGetValue(scopeKey, out DateTimeOffset verified)
            && DateTimeOffset.UtcNow - verified < CacheTtl)
        {
            return;
        }

        LoggingConfig? config;
        try
        {
            config = await fetchLoggingConfig(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "Refusing to send to Bedrock because model invocation logging could not be verified "
                + $"as disabled in {region}. Grant bedrock:GetModelInvocationLoggingConfiguration "
                + "or use another connection.",
                exception);
        }

        ThrowIfOperationCanceled(cancellationToken);
        if (DescribeEnabledLogging(config) is { } detail)
        {
            throw new InvalidOperationException(
                $"Refusing to send to Bedrock because model invocation logging is enabled in "
                + $"{region} ({detail}).");
        }

        _verifiedAt[scopeKey] = DateTimeOffset.UtcNow;
    }

    public ValueTask DisposeAsync() => new(_lifetime.RetireAsync(() =>
    {
        _verifiedAt.Clear();
        return Task.CompletedTask;
    }));

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private void ThrowIfOperationCanceled(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { _lifetime.ThrowIfStopping(); }
        catch (ObjectDisposedException) { throw new OperationCanceledException("Bedrock logging verification is retiring.", token); }
    }

    internal static string? DescribeEnabledLogging(LoggingConfig? config)
    {
        if (config is null)
        {
            return null;
        }

        var reasons = new List<string>();
        if (config.TextDataDeliveryEnabled == true) reasons.Add("text delivery");
        if (config.ImageDataDeliveryEnabled == true) reasons.Add("image delivery");
        if (config.EmbeddingDataDeliveryEnabled == true) reasons.Add("embedding delivery");
        if (config.VideoDataDeliveryEnabled == true) reasons.Add("video delivery");
        if (config.AudioDataDeliveryEnabled == true) reasons.Add("audio delivery");
        if (config.CloudWatchConfig is not null) reasons.Add("CloudWatch destination");
        if (config.S3Config is not null) reasons.Add("S3 destination");
        return reasons.Count == 0 ? null : string.Join(", ", reasons);
    }
}
