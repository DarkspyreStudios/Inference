using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.HttpConfiguration.Consumer;

public static class ConfigurationCaller
{
    public static JsonTypeInfo OptionsMetadata => ConfigurationJsonContext.Default.InferenceHttpPipelineOptions;
    public static JsonTypeInfo LoggingMetadata => ConfigurationJsonContext.Default.InferenceHttpLoggingBuilder;
    public static JsonTypeInfo EnvelopeMetadata => ConfigurationJsonContext.Default.ConfigurationEnvelope;

    public static string Exercise()
    {
        int callbacks = 0;
        var options = new InferenceHttpPipelineOptions();
        InferenceHttpLoggingBuilder? logging = null;
        options.ConfigureLogging("OpenAiResponsesLlmClient", builder =>
        {
            logging = builder;
            builder.AddLogger(_ => { callbacks++; throw new InvalidOperationException("Callback must not execute."); });
            builder.RedactLoggedHeaders(_ => { callbacks++; return true; });
        });
        Refuses(() => JsonSerializer.Serialize(options, ConfigurationJsonContext.Default.InferenceHttpPipelineOptions));
        Refuses(() => JsonSerializer.Serialize(logging, ConfigurationJsonContext.Default.InferenceHttpLoggingBuilder));
        Refuses(() => JsonSerializer.Serialize(new ConfigurationEnvelope(options, logging), ConfigurationJsonContext.Default.ConfigurationEnvelope));
        foreach (string json in new[] { "{}", "[]", "0", "\"runtime\"" })
        {
            Refuses(() => JsonSerializer.Deserialize(json, ConfigurationJsonContext.Default.InferenceHttpPipelineOptions));
            Refuses(() => JsonSerializer.Deserialize(json, ConfigurationJsonContext.Default.InferenceHttpLoggingBuilder));
        }
        Refuses(() => JsonSerializer.Deserialize("{\"Options\":{},\"Logging\":null}", ConfigurationJsonContext.Default.ConfigurationEnvelope));
        Refuses(() => JsonSerializer.Deserialize("{\"Options\":null,\"Logging\":{}}", ConfigurationJsonContext.Default.ConfigurationEnvelope));
        Require(JsonSerializer.Serialize<InferenceHttpPipelineOptions>(null!, ConfigurationJsonContext.Default.InferenceHttpPipelineOptions) == "null");
        Require(JsonSerializer.Serialize<InferenceHttpLoggingBuilder>(null!, ConfigurationJsonContext.Default.InferenceHttpLoggingBuilder) == "null");
        Require(JsonSerializer.Deserialize("null", ConfigurationJsonContext.Default.InferenceHttpPipelineOptions) is null);
        Require(JsonSerializer.Deserialize("null", ConfigurationJsonContext.Default.InferenceHttpLoggingBuilder) is null);
        Require(JsonSerializer.Serialize(new ConfigurationEnvelope(null, null), ConfigurationJsonContext.Default.ConfigurationEnvelope) == "{\"Options\":null,\"Logging\":null}");
        ConfigurationEnvelope? empty = JsonSerializer.Deserialize("{\"Options\":null,\"Logging\":null}", ConfigurationJsonContext.Default.ConfigurationEnvelope);
        Require(empty is { Options: null, Logging: null });
        Require(callbacks == 0);
        return "nonnull-refused|null-preserved|callbacks=0";
    }

    private static void Refuses(Action action)
    {
        try { action(); }
        catch (NotSupportedException) { return; }
        throw new InvalidOperationException("Runtime configuration must refuse nonnull JSON.");
    }
    private static void Require(bool actual)
    {
        if (!actual) throw new InvalidOperationException("The public configuration JSON contract was not preserved.");
    }
}

internal sealed record ConfigurationEnvelope(InferenceHttpPipelineOptions? Options, InferenceHttpLoggingBuilder? Logging);

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(InferenceHttpPipelineOptions))]
[JsonSerializable(typeof(InferenceHttpLoggingBuilder))]
[JsonSerializable(typeof(ConfigurationEnvelope))]
internal sealed partial class ConfigurationJsonContext : JsonSerializerContext;
