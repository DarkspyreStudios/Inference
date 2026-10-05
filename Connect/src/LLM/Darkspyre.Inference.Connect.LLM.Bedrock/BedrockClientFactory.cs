using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using Amazon;
using Amazon.Bedrock;
using Amazon.BedrockAgentCore;
using Amazon.BedrockRuntime;
using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;
using Amazon.Runtime.Credentials;
using Amazon.Runtime.Credentials.Internal;
using Amazon.Runtime.Identity;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Bedrock;

internal sealed class BedrockClientFactory : IDisposable, IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly LlmOperationLifetime _lifetime = new();
    private readonly Dictionary<string, Lazy<AmazonBedrockRuntimeClient>> _runtime = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Lazy<AmazonBedrockClient>> _control = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Lazy<AmazonBedrockAgentCoreClient>> _agentCore = new(StringComparer.Ordinal);
    private readonly List<IDisposable> _credentials = [];
    private readonly List<Exception> _cleanupFailures = [];
    private readonly List<IDisposable> _failedResources = [];
    private readonly IdentityResolvers _identities = new();
    private readonly BedrockHttpClientFactory _httpClients = new();
    private Func<LlmAwsCredentials, AmazonBedrockRuntimeClient>? _createRuntime;
    private Func<LlmAwsCredentials, AmazonBedrockClient>? _createControl;
    private Func<LlmAwsCredentials, AmazonBedrockAgentCoreClient>? _createAgentCore;

    public BedrockClientFactory()
    {
        _createRuntime = aws => Create<AmazonBedrockRuntimeConfig, AmazonBedrockRuntimeClient>(aws,
            (credentials, config) => credentials is null ? new(config) : new(credentials, config), generation: true);
        _createControl = aws => Create<AmazonBedrockConfig, AmazonBedrockClient>(aws,
            (credentials, config) => credentials is null ? new(config) : new(credentials, config), generation: false);
        _createAgentCore = aws => Create<AmazonBedrockAgentCoreConfig, AmazonBedrockAgentCoreClient>(aws,
            (credentials, config) => credentials is null ? new(config) : new(credentials, config), generation: true);
    }

    internal BedrockClientFactory(Func<LlmAwsCredentials, AmazonBedrockRuntimeClient> runtime,
        Func<LlmAwsCredentials, AmazonBedrockClient> control,
        Func<LlmAwsCredentials, AmazonBedrockAgentCoreClient> agentCore)
    {
        _createRuntime = runtime;
        _createControl = control;
        _createAgentCore = agentCore;
    }

    public Lease<AmazonBedrockRuntimeClient> Runtime(LlmAwsCredentials aws, CancellationToken cancellationToken = default) =>
        Acquire(_runtime, aws, _createRuntime!, cancellationToken);

    public Lease<AmazonBedrockClient> Control(LlmAwsCredentials aws, CancellationToken cancellationToken = default) =>
        Acquire(_control, aws, _createControl!, cancellationToken);

    public Lease<AmazonBedrockAgentCoreClient> AgentCore(LlmAwsCredentials aws, CancellationToken cancellationToken = default) =>
        Acquire(_agentCore, aws, _createAgentCore!, cancellationToken);

    internal IAsyncEnumerable<T> StreamAsync<T>(Func<CancellationToken, IAsyncEnumerable<T>> source,
        CancellationToken cancellationToken) => _lifetime.StreamAsync(source, cancellationToken);

    private Lease<T> Acquire<T>(Dictionary<string, Lazy<T>> clients, LlmAwsCredentials aws,
        Func<LlmAwsCredentials, T> create, CancellationToken caller) where T : class, IDisposable
    {
        LlmOperationLifetime.Lease operation = _lifetime.Enter(caller);
        bool transferred = false;
        try
        {
            string key = ScopeKey(aws);
            Lazy<T> entry;
            lock (_gate)
            {
                if (!clients.TryGetValue(key, out entry!))
                {
                    entry = new(() => create(aws), LazyThreadSafetyMode.ExecutionAndPublication);
                    clients.Add(key, entry);
                }
            }
            ThrowIfOperationCanceled(operation.Token);
            T client;
            try { client = entry.Value; }
            catch
            {
                lock (_gate)
                {
                    if (clients.TryGetValue(key, out Lazy<T>? current) && ReferenceEquals(entry, current)) clients.Remove(key);
                }
                throw;
            }
            ThrowIfOperationCanceled(operation.Token);
            var lease = new Lease<T>(client, operation, ThrowIfOperationCanceled, RecordCleanupFailure);
            transferred = true;
            return lease;
        }
        finally { if (!transferred) operation.Dispose(); }
    }

    public static string ScopeKey(LlmAwsCredentials aws)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            writer.WriteStringValue(aws.Region);
            writer.WriteStringValue(aws.Profile);
            writer.WriteStringValue(aws.AccessKeyId);
            writer.WriteStringValue(aws.SecretAccessKey);
            writer.WriteStringValue(aws.SessionToken);
            writer.WriteEndArray();
        }
        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan));
    }

    private TClient Create<TConfig, TClient>(LlmAwsCredentials aws, Func<AWSCredentials?, TConfig, TClient> create,
        bool generation) where TConfig : ClientConfig, new()
    {
        var config = new TConfig
        {
            RegionEndpoint = ResolveRegion(aws),
            IdentityResolverConfiguration = _identities,
            HttpClientFactory = _httpClients
        };
        if (generation) config.Timeout = Timeout.InfiniteTimeSpan;
        AWSCredentials? credentials = ResolveCredentials(aws);
        bool transferred = false;
        try
        {
            TClient client = create(credentials, config);
            if (credentials is IDisposable disposable)
            {
                lock (_gate) { _credentials.Add(disposable); }
            }
            transferred = true;
            return client;
        }
        finally { if (!transferred && credentials is IDisposable disposable) disposable.Dispose(); }
    }

    public ValueTask DisposeAsync() => new(_lifetime.RetireAsync(ReleaseAsync));
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private void ThrowIfOperationCanceled(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { _lifetime.ThrowIfStopping(); }
        catch (ObjectDisposedException) { throw new OperationCanceledException("Bedrock resource ownership is retiring.", token); }
    }

    private void RecordCleanupFailure(IDisposable resource, Exception error)
    {
        lock (_gate) { _cleanupFailures.Add(error); _failedResources.Add(resource); }
    }

    [SuppressMessage("Design", "CA1031", Justification = "Every owned resource is retired before disposal failures are returned together.")]
    private Task ReleaseAsync()
    {
        List<Exception> failures;
        IDisposable[] resources;
        lock (_gate)
        {
            failures = [.. _cleanupFailures];
            _cleanupFailures.Clear();
            resources = [.. _runtime.Values.Where(value => value.IsValueCreated).Select(value => (IDisposable)value.Value),
                .. _control.Values.Where(value => value.IsValueCreated).Select(value => value.Value),
                .. _agentCore.Values.Where(value => value.IsValueCreated).Select(value => value.Value),
                .. _credentials, _identities];
            _runtime.Clear();
            _control.Clear();
            _agentCore.Clear();
            _credentials.Clear();
            _createRuntime = null;
            _createControl = null;
            _createAgentCore = null;
        }
        foreach (IDisposable resource in resources.Distinct<IDisposable>(ReferenceEqualityComparer.Instance))
        {
            try { resource.Dispose(); }
            catch (Exception error)
            {
                failures.Add(error);
                lock (_gate) { _failedResources.Add(resource); }
            }
        }
        if (failures.Count > 0) throw new AggregateException("Bedrock resource retirement failed.", failures);
        return Task.CompletedTask;
    }

    internal sealed class Lease<T>(T client, LlmOperationLifetime.Lease operation,
        Action<CancellationToken> checkLifetime, Action<IDisposable, Exception> recordFailure) : IDisposable where T : class
    {
        private LlmOperationLifetime.Lease? _operation = operation;
        private T? _client = client;
        private Action<IDisposable, Exception>? _recordFailure = recordFailure;
        private Action<CancellationToken>? _checkLifetime = checkLifetime;
        public T Client => _client ?? throw new ObjectDisposedException(nameof(Lease<T>));
        public CancellationToken Token { get; } = operation.Token;
        public void ThrowIfCanceled()
        {
            ObjectDisposedException.ThrowIf(_operation is null, this);
            _checkLifetime!(Token);
        }
        public IDisposable OwnResponse(IDisposable response) => new ResponseScope(() => DisposeResponse(response));

        public void DisposeResponse(IDisposable response)
        {
            try { response.Dispose(); }
            catch (Exception error)
            {
                Exception cleanup = BedrockLlmClient.CleanupFailure(error, response);
                _recordFailure?.Invoke(response, cleanup);
                ExceptionDispatchInfo.Throw(cleanup);
            }
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _operation, null)?.Dispose();
            _client = null;
            _recordFailure = null;
            _checkLifetime = null;
        }

        private sealed class ResponseScope(Action dispose) : IDisposable
        {
            private Action? _dispose = dispose;
            public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
        }
    }

    private sealed class IdentityResolvers : IIdentityResolverConfiguration, IDisposable
    {
        private readonly DefaultAWSCredentialsIdentityResolver _credentials = new();
        public IIdentityResolver GetIdentityResolver<T>() where T : BaseIdentity =>
            typeof(T) == typeof(AWSCredentials) ? _credentials : DefaultIdentityResolverConfiguration.Instance.GetIdentityResolver<T>();
        public void Dispose() => _credentials.Dispose();
    }

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
