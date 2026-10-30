using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Http.Logging;

namespace Darkspyre.Inference.Connect.LLM.Internal;

internal static class SdkHttpRegistration
{
    internal static SdkHttpConfiguration Register(IServiceCollection services)
    {
        services.AddHttpClient();
        SdkHttpConfiguration? configuration = services.SingleOrDefault(descriptor => descriptor.ServiceType == typeof(SdkHttpConfiguration))?.ImplementationInstance as SdkHttpConfiguration;
        if (configuration is null) { configuration = new(); services.AddSingleton(configuration); }
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<SdkHttpGenerationPool>();
        return configuration;
    }
    internal static IHttpClientBuilder AddInferenceHttpClient<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(this IServiceCollection services, Action<HttpClient>? configure = null) where T : class
    {
        string name = typeof(T).Name; Register(services).RecordPipeline(name);
        IHttpClientBuilder builder = services.AddHttpClient(name);
        if (configure is not null) builder.ConfigureHttpClient(configure);
        services.TryAddTransient<T>(provider => provider.GetRequiredService<ITypedHttpClientFactory<T>>()
            .CreateClient(provider.GetRequiredService<SdkHttpGenerationPool>().CreateClient(name)));
        return builder;
    }
}

internal sealed record SdkHttpLoggerDefinition(Func<IServiceProvider, IHttpClientLogger> Factory, bool Wrap);

internal sealed class SdkHttpConfiguration
{
    private readonly Dictionary<string, List<SdkHttpLoggerDefinition>> _loggers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pipelines = new(StringComparer.Ordinal);
    private readonly List<string> _configured = [];
    private bool _selected;
    private bool _frozen;
    internal InferenceHttpCreationMode Mode { get; private set; }
    internal void SelectMode(InferenceHttpCreationMode mode)
    {
        ObjectDisposedException.ThrowIf(_frozen, this);
        if (_selected && Mode != mode) throw new InvalidOperationException("An SDK generation cannot select conflicting HTTP creation modes.");
        Mode = mode; _selected = true;
    }
    internal void Freeze() => _frozen = true;
    /// <summary>Records a pipeline a Connect library registered. Throws ObjectDisposedException once frozen.</summary>
    internal void RecordPipeline(string name) { ObjectDisposedException.ThrowIf(_frozen, this); _pipelines.Add(name); }
    /// <summary>Records a pipeline name the host configured. Throws ObjectDisposedException once frozen.</summary>
    internal void RecordConfigured(string name) { ObjectDisposedException.ThrowIf(_frozen, this); _configured.Add(name); }
    /// <summary>Throws ArgumentException when <paramref name="name"/> is not a recorded pipeline.</summary>
    internal void ValidateName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!_pipelines.Contains(name))
            throw new ArgumentException($"The HTTP pipeline name '{name}' is not registered by a Connect library.", nameof(name));
    }
    internal void AddLogger(IHttpClientBuilder builder, SdkHttpLoggerDefinition logger)
    {
        ObjectDisposedException.ThrowIf(_frozen, this);
        if (!_loggers.TryGetValue(builder.Name, out List<SdkHttpLoggerDefinition>? loggers))
        {
            _loggers.Add(builder.Name, loggers = []);
            builder.Services.Configure<HttpClientFactoryOptions>(builder.Name, options => options.HttpMessageHandlerBuilderActions.Add(handler =>
            {
                if (handler is SdkHttpMessageHandlerBuilder owned) owned.Loggers = owned.Configuration.GetLoggers(builder.Name);
            }));
        }
        loggers.Add(logger);
    }
    internal void RemoveLoggers(string name) { ObjectDisposedException.ThrowIf(_frozen, this); if (_loggers.TryGetValue(name, out var loggers)) loggers.Clear(); }
    internal SdkHttpConfiguration Snapshot()
    {
        Freeze();
        // Every registration has run when the container first creates the pool, so a configured name no library
        // registered is refused here, whatever order the host configured and registered in.
        foreach (string configured in _configured) ValidateName(configured);
        var snapshot = new SdkHttpConfiguration { Mode = Mode, _selected = _selected, _frozen = true };
        foreach ((string name, List<SdkHttpLoggerDefinition> loggers) in _loggers) snapshot._loggers.Add(name, [.. loggers]);
        snapshot._pipelines.UnionWith(_pipelines);
        return snapshot;
    }
    internal SdkHttpLoggerDefinition[] GetLoggers(string name) => _loggers[name].ToArray();
    internal void Detach() => _loggers.Clear();
}
