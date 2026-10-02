using System.Text.Json;
using System.Text.Json.Serialization;
using Darkspyre.Inference.Connect.LLM.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Logging;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>Selects SDK-owned HTTP retirement or explicit caller-factory borrowing.</summary>
public enum InferenceHttpCreationMode { SdkGenerationOwned = 0, BorrowHostFactory = 1 }

/// <summary>Configures one SDK DI generation. This runtime configuration is not portable data.</summary>
[JsonConverter(typeof(InferenceHttpRuntimeJsonConverter<InferenceHttpPipelineOptions>))]
public sealed class InferenceHttpPipelineOptions
{
    private readonly List<(string Name, InferenceHttpLoggingBuilder Builder)> _logging = [];
    private InferenceHttpCreationMode _mode;
    private bool _sealed;
    public InferenceHttpCreationMode CreationMode
    {
        get => _mode;
        set { ObjectDisposedException.ThrowIf(_sealed, this); if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value)); _mode = value; }
    }
    public void ConfigureLogging(string name, Action<InferenceHttpLoggingBuilder> configure)
    {
        ObjectDisposedException.ThrowIf(_sealed, this); SdkHttpRegistration.ValidateName(name); ArgumentNullException.ThrowIfNull(configure);
        var builder = new InferenceHttpLoggingBuilder(); configure(builder); _logging.Add((name, builder));
    }
    internal void Apply(IServiceCollection services, SdkHttpConfiguration configuration)
    {
        _sealed = true; configuration.SelectMode(_mode);
        foreach ((string name, InferenceHttpLoggingBuilder logger) in _logging) logger.Apply(services.AddHttpClient(name), configuration);
        _logging.Clear();
    }
    public override string ToString() => nameof(InferenceHttpPipelineOptions);
}

/// <summary>Registers ordered logging through public factory policy and explicit SDK logger adapters.</summary>
[JsonConverter(typeof(InferenceHttpRuntimeJsonConverter<InferenceHttpLoggingBuilder>))]
public sealed class InferenceHttpLoggingBuilder
{
    private readonly List<Action<IHttpClientBuilder, SdkHttpConfiguration>> _actions = [];
    private bool _sealed;
    internal InferenceHttpLoggingBuilder() { }
    public InferenceHttpLoggingBuilder AddLogger(Func<IServiceProvider, IHttpClientLogger> loggerFactory, bool wrapHandlersPipeline = false)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        return Add((builder, configuration) =>
        {
            if (configuration.Mode == InferenceHttpCreationMode.BorrowHostFactory) builder.AddLogger(loggerFactory, wrapHandlersPipeline);
            else configuration.AddLogger(builder, new(loggerFactory, wrapHandlersPipeline));
        });
    }
    public InferenceHttpLoggingBuilder AddLogger<TLogger>(bool wrapHandlersPipeline = false) where TLogger : class, IHttpClientLogger =>
        AddLogger(provider => provider.GetRequiredService<TLogger>(), wrapHandlersPipeline);
    public InferenceHttpLoggingBuilder RemoveAllLoggers() => Add((builder, configuration) => { builder.RemoveAllLoggers(); configuration.RemoveLoggers(builder.Name); });
    public InferenceHttpLoggingBuilder AddDefaultLogger() => Add((builder, _) => builder.AddDefaultLogger());
    public InferenceHttpLoggingBuilder RedactLoggedHeaders(Func<string, bool> shouldRedactHeaderValue)
    {
        ArgumentNullException.ThrowIfNull(shouldRedactHeaderValue);
        return Add((builder, _) => builder.RedactLoggedHeaders(shouldRedactHeaderValue));
    }
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
    public override string ToString() => nameof(InferenceHttpLoggingBuilder);
}

internal sealed class InferenceHttpRuntimeJsonConverter<T> : JsonConverter<T> where T : class
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
