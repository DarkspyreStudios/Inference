using System.Text.Json;
using System.Text.Json.Serialization;
using Darkspyre.Inference.Connect.LLM.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Logging;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>Selects SDK-owned HTTP retirement or explicit caller-factory borrowing.</summary>
public enum InferenceHttpCreationMode
{
    /// <summary>The SDK builds HTTP handlers itself and retires them with its DI generation.</summary>
    SdkGenerationOwned = 0,

    /// <summary>The SDK creates clients from the host's <see cref="IHttpClientFactory"/>.</summary>
    BorrowHostFactory = 1
}

/// <summary>Configures one SDK DI generation. This runtime configuration is not portable data.</summary>
[JsonConverter(typeof(InferenceHttpConfigurationJsonConverterFactory))]
public sealed class InferenceHttpPipelineOptions
{
    private readonly List<(string Name, InferenceHttpLoggingBuilder Builder)> _logging = [];
    private InferenceHttpCreationMode _mode;
    private bool _sealed;
    /// <summary>How the SDK creates HTTP clients. Throws <see cref="ObjectDisposedException"/> once the options are applied.</summary>
    public InferenceHttpCreationMode CreationMode
    {
        get => _mode;
        set { ObjectDisposedException.ThrowIf(_sealed, this); if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value)); _mode = value; }
    }
    /// <summary>Adds logging configuration for the named HTTP client. Throws <see cref="ObjectDisposedException"/> once the options are applied.</summary>
    /// <gotcha>The name is checked against the pipelines Connect libraries registered when the SDK's HTTP pool is first
    /// created, not when this is called.</gotcha>
    public void ConfigureLogging(string name, Action<InferenceHttpLoggingBuilder> configure)
    {
        ObjectDisposedException.ThrowIf(_sealed, this); ArgumentNullException.ThrowIfNull(name); ArgumentNullException.ThrowIfNull(configure);
        var builder = new InferenceHttpLoggingBuilder(); configure(builder); _logging.Add((name, builder));
    }
    internal void Apply(IServiceCollection services, SdkHttpConfiguration configuration)
    {
        _sealed = true; configuration.SelectMode(_mode);
        foreach ((string name, InferenceHttpLoggingBuilder logger) in _logging)
        {
            configuration.RecordConfigured(name);
            logger.Apply(services.AddHttpClient(name), configuration);
        }
        _logging.Clear();
    }
    /// <summary>Returns the type name only.</summary>
    public override string ToString() => nameof(InferenceHttpPipelineOptions);
}

/// <summary>Registers ordered logging through public factory policy and explicit SDK logger adapters.</summary>
[JsonConverter(typeof(InferenceHttpConfigurationJsonConverterFactory))]
public sealed class InferenceHttpLoggingBuilder
{
    private readonly List<Action<IHttpClientBuilder, SdkHttpConfiguration>> _actions = [];
    private bool _sealed;
    internal InferenceHttpLoggingBuilder() { }
    /// <summary>Adds a logger created from the service provider.</summary>
    public InferenceHttpLoggingBuilder AddLogger(Func<IServiceProvider, IHttpClientLogger> loggerFactory, bool wrapHandlersPipeline = false)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        return Add((builder, configuration) =>
        {
            if (configuration.Mode == InferenceHttpCreationMode.BorrowHostFactory) builder.AddLogger(loggerFactory, wrapHandlersPipeline);
            else configuration.AddLogger(builder, new(loggerFactory, wrapHandlersPipeline));
        });
    }
    /// <summary>Adds a logger of type <typeparamref name="TLogger"/> resolved from the service provider.</summary>
    public InferenceHttpLoggingBuilder AddLogger<TLogger>(bool wrapHandlersPipeline = false) where TLogger : class, IHttpClientLogger =>
        AddLogger(provider => provider.GetRequiredService<TLogger>(), wrapHandlersPipeline);
    /// <summary>Removes every logger from the HTTP client, including loggers added earlier.</summary>
    public InferenceHttpLoggingBuilder RemoveAllLoggers() => Add((builder, configuration) => { builder.RemoveAllLoggers(); configuration.RemoveLoggers(builder.Name); });
    /// <summary>Adds the default HTTP client logger.</summary>
    public InferenceHttpLoggingBuilder AddDefaultLogger() => Add((builder, _) => builder.AddDefaultLogger());
    /// <summary>Redacts the values of logged headers that the predicate selects.</summary>
    public InferenceHttpLoggingBuilder RedactLoggedHeaders(Func<string, bool> shouldRedactHeaderValue)
    {
        ArgumentNullException.ThrowIfNull(shouldRedactHeaderValue);
        return Add((builder, _) => builder.RedactLoggedHeaders(shouldRedactHeaderValue));
    }
    /// <summary>Redacts the values of the named logged headers.</summary>
    public InferenceHttpLoggingBuilder RedactLoggedHeaders(IEnumerable<string> redactedHeaderNames)
    {
        ArgumentNullException.ThrowIfNull(redactedHeaderNames); string[] names = redactedHeaderNames.ToArray();
        return Add((builder, _) => builder.RedactLoggedHeaders(names));
    }
    private InferenceHttpLoggingBuilder Add(Action<IHttpClientBuilder, SdkHttpConfiguration> action)
    {
        ObjectDisposedException.ThrowIf(_sealed, this); _actions.Add(action); return this;
    }
    internal void Apply(IHttpClientBuilder builder, SdkHttpConfiguration configuration)
    {
        _sealed = true;
        foreach (Action<IHttpClientBuilder, SdkHttpConfiguration> action in _actions) action(builder, configuration);
        _actions.Clear();
    }
    /// <summary>Returns the type name only.</summary>
    public override string ToString() => nameof(InferenceHttpLoggingBuilder);
}

/// <summary>Refuses transfer of the two HTTP runtime configuration types while preserving null values.</summary>
public sealed class InferenceHttpConfigurationJsonConverterFactory : JsonConverterFactory
{
    /// <summary>Returns true for <see cref="InferenceHttpPipelineOptions"/> and <see cref="InferenceHttpLoggingBuilder"/> only.</summary>
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert == typeof(InferenceHttpPipelineOptions) || typeToConvert == typeof(InferenceHttpLoggingBuilder);

    /// <summary>Returns a converter that reads and writes null and refuses any other value of the requested type.</summary>
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        if (typeToConvert == typeof(InferenceHttpPipelineOptions)) return new RuntimeRefusalConverter<InferenceHttpPipelineOptions>();
        if (typeToConvert == typeof(InferenceHttpLoggingBuilder)) return new RuntimeRefusalConverter<InferenceHttpLoggingBuilder>();
        throw new NotSupportedException("Only the two HTTP runtime configuration types use this converter factory.");
    }

    private sealed class RuntimeRefusalConverter<T> : JsonConverter<T> where T : class
    {
        public override bool HandleNull => true;
        public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.Null ? null : throw new NotSupportedException("HTTP runtime configuration is not portable data.");
        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            if (value is null) writer.WriteNullValue();
            else throw new NotSupportedException("HTTP runtime configuration is not portable data.");
        }
    }
}
