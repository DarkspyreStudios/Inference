using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Realtime;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.Realtime;

/// <summary>Realtime-owned closed data graphs, including fixed Connect provider extensions.</summary>
public static class ConnectRealtimeJson
{
    public static JsonTypeInfo<RealtimeAudioEncoding> RealtimeAudioEncoding => Metadata<RealtimeAudioEncoding>();
    public static JsonTypeInfo<RealtimeAudioTransport> RealtimeAudioTransport => Metadata<RealtimeAudioTransport>();
    public static JsonTypeInfo<RealtimeModality> RealtimeModality => Metadata<RealtimeModality>();
    public static JsonTypeInfo<RealtimeMessageRole> RealtimeMessageRole => Metadata<RealtimeMessageRole>();
    public static JsonTypeInfo<RealtimeAudioConfig> RealtimeAudioConfig => Metadata<RealtimeAudioConfig>();
    public static JsonTypeInfo<RealtimeTurnDetection> RealtimeTurnDetection => Metadata<RealtimeTurnDetection>();
    public static JsonTypeInfo<RealtimeManualTurnDetection> RealtimeManualTurnDetection => Metadata<RealtimeManualTurnDetection>();
    public static JsonTypeInfo<RealtimeServerVad> RealtimeServerVad => Metadata<RealtimeServerVad>();
    public static JsonTypeInfo<RealtimeConversationReference> RealtimeConversationReference => Metadata<RealtimeConversationReference>();
    public static JsonTypeInfo<RealtimeResumptionOptions> RealtimeResumptionOptions => Metadata<RealtimeResumptionOptions>();
    public static JsonTypeInfo<RealtimeProviderOptions> RealtimeProviderOptions => Metadata<RealtimeProviderOptions>();
    public static JsonTypeInfo<RealtimeSessionConfig> RealtimeSessionConfig => Metadata<RealtimeSessionConfig>();
    public static JsonTypeInfo<RealtimeContent> RealtimeContent => Metadata<RealtimeContent>();
    public static JsonTypeInfo<RealtimeTextContent> RealtimeTextContent => Metadata<RealtimeTextContent>();
    public static JsonTypeInfo<RealtimeAudioContent> RealtimeAudioContent => Metadata<RealtimeAudioContent>();
    public static JsonTypeInfo<RealtimeConversationItem> RealtimeConversationItem => Metadata<RealtimeConversationItem>();
    public static JsonTypeInfo<RealtimeMessageItem> RealtimeMessageItem => Metadata<RealtimeMessageItem>();
    public static JsonTypeInfo<RealtimeFunctionCallItem> RealtimeFunctionCallItem => Metadata<RealtimeFunctionCallItem>();
    public static JsonTypeInfo<RealtimeFunctionOutputItem> RealtimeFunctionOutputItem => Metadata<RealtimeFunctionOutputItem>();
    public static JsonTypeInfo<RealtimeResponseOptions> RealtimeResponseOptions => Metadata<RealtimeResponseOptions>();
    public static JsonTypeInfo<RealtimeToolResult> RealtimeToolResult => Metadata<RealtimeToolResult>();
    public static JsonTypeInfo<RealtimeCapabilities> RealtimeCapabilities => Metadata<RealtimeCapabilities>();
    public static JsonTypeInfo<RealtimeEventIdentity> RealtimeEventIdentity => Metadata<RealtimeEventIdentity>();
    public static JsonTypeInfo<RealtimeSessionEvent> RealtimeSessionEvent => Metadata<RealtimeSessionEvent>();
    public static JsonTypeInfo<RealtimeConversationCreatedEvent> RealtimeConversationCreatedEvent => Metadata<RealtimeConversationCreatedEvent>();
    public static JsonTypeInfo<RealtimeInputSpeechEvent> RealtimeInputSpeechEvent => Metadata<RealtimeInputSpeechEvent>();
    public static JsonTypeInfo<RealtimeInputBufferEvent> RealtimeInputBufferEvent => Metadata<RealtimeInputBufferEvent>();
    public static JsonTypeInfo<RealtimeItemEvent> RealtimeItemEvent => Metadata<RealtimeItemEvent>();
    public static JsonTypeInfo<RealtimeContentPartEvent> RealtimeContentPartEvent => Metadata<RealtimeContentPartEvent>();
    public static JsonTypeInfo<RealtimeTranscriptEvent> RealtimeTranscriptEvent => Metadata<RealtimeTranscriptEvent>();
    public static JsonTypeInfo<RealtimeAudioDeltaEvent> RealtimeAudioDeltaEvent => Metadata<RealtimeAudioDeltaEvent>();
    public static JsonTypeInfo<RealtimeAudioDoneEvent> RealtimeAudioDoneEvent => Metadata<RealtimeAudioDoneEvent>();
    public static JsonTypeInfo<RealtimeTextDeltaEvent> RealtimeTextDeltaEvent => Metadata<RealtimeTextDeltaEvent>();
    public static JsonTypeInfo<RealtimeFunctionArgumentsEvent> RealtimeFunctionArgumentsEvent => Metadata<RealtimeFunctionArgumentsEvent>();
    public static JsonTypeInfo<RealtimeHostedStartedEvent> RealtimeHostedStartedEvent => Metadata<RealtimeHostedStartedEvent>();
    public static JsonTypeInfo<RealtimeHostedProgressEvent> RealtimeHostedProgressEvent => Metadata<RealtimeHostedProgressEvent>();
    public static JsonTypeInfo<RealtimeHostedCompletedEvent> RealtimeHostedCompletedEvent => Metadata<RealtimeHostedCompletedEvent>();
    public static JsonTypeInfo<RealtimeResponseEvent> RealtimeResponseEvent => Metadata<RealtimeResponseEvent>();
    public static JsonTypeInfo<RealtimeErrorEvent> RealtimeErrorEvent => Metadata<RealtimeErrorEvent>();
    public static JsonTypeInfo<RealtimeConnection> RealtimeConnection => Metadata<RealtimeConnection>();
    public static JsonTypeInfo<RealtimeTransport> RealtimeTransport => Metadata<RealtimeTransport>();
    public static JsonTypeInfo<XaiRealtimeOptions> XaiRealtimeOptions => Metadata<XaiRealtimeOptions>();
    public static JsonTypeInfo<XaiRealtimeForceMessageItem> XaiRealtimeForceMessageItem => Metadata<XaiRealtimeForceMessageItem>();
    public static JsonTypeInfo<RealtimeSetupConfiguration> RealtimeSetupConfiguration => Metadata<RealtimeSetupConfiguration>();
    public static JsonTypeInfo<RealtimeSetupHeaderChanges> RealtimeSetupHeaderChanges => Metadata<RealtimeSetupHeaderChanges>();
    public static JsonTypeInfo<RealtimeSetupChanges> RealtimeSetupChanges => Metadata<RealtimeSetupChanges>();
    private static readonly JsonSerializerOptions OwnedOptions = CreateOptions();
    private static JsonTypeInfo<T> Metadata<T>() => OwnedOptions.GetTypeInfo<T>();
    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(ConnectRealtimeJsonContext.Default.Options) { TypeInfoResolver = new RealtimeTypeInfoResolver() };
        foreach (Type type in new[] { typeof(IProviderAuthorization), typeof(IInferenceResourceAccess), typeof(IInferenceResourceReadHandle),
            typeof(IInferenceResourceWriteHandle), typeof(LlmHostedToolKind), typeof(LlmHostedToolPhase), typeof(LlmImageGenerationAction), typeof(RealtimeAudioEncoding),
            typeof(RealtimeAudioTransport), typeof(RealtimeModality), typeof(RealtimeMessageRole), typeof(IRealtimeVoiceSession),
            typeof(InferenceResourceOperationPhase), typeof(InferenceResourceCommitOutcome) })
            options.Converters.Add(InferenceJson.RealtimeSessionConfig.Options.Converters.Single(converter => converter.CanConvert(type)));
        options.Converters.Add(ConnectLlmJson.Connection.Options.Converters.Single(converter => converter.CanConvert(typeof(XaiFamilyBinding))));
        options.Converters.Add(new RealtimeTransportJsonConverter());
        options.Converters.Add(new RealtimeRuntimeJsonConverter<IRealtimeProvider>());
        options.Converters.Add(new RealtimeRuntimeJsonConverter<RealtimeProviderService>());
        options.Converters.Add(new RealtimeRuntimeJsonConverter<RealtimeSettings>());
        options.Converters.Add(new RealtimeRuntimeJsonConverter<RealtimeSettingsManager>());
        options.MakeReadOnly();
        return options;
    }

    private sealed class RealtimeTypeInfoResolver : IJsonTypeInfoResolver
    {
        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
        {
            if (type == typeof(XaiFamilyBinding)) return RuntimeValue<XaiFamilyBinding>(options);
            if (type == typeof(IRealtimeVoiceSession)) return RuntimeValue<IRealtimeVoiceSession>(options);
            if (type == typeof(IInferenceResourceAccess)) return RuntimeValue<IInferenceResourceAccess>(options);
            if (type == typeof(IInferenceResourceReadHandle)) return RuntimeValue<IInferenceResourceReadHandle>(options);
            if (type == typeof(IInferenceResourceWriteHandle)) return RuntimeValue<IInferenceResourceWriteHandle>(options);
            if (type == typeof(IRealtimeProvider)) return JsonMetadataServices.CreateValueInfo<IRealtimeProvider>(options, new RealtimeRuntimeJsonConverter<IRealtimeProvider>());
            if (type == typeof(RealtimeProviderService)) return JsonMetadataServices.CreateValueInfo<RealtimeProviderService>(options, new RealtimeRuntimeJsonConverter<RealtimeProviderService>());
            if (type == typeof(RealtimeSettings)) return JsonMetadataServices.CreateValueInfo<RealtimeSettings>(options, new RealtimeRuntimeJsonConverter<RealtimeSettings>());
            if (type == typeof(RealtimeSettingsManager)) return JsonMetadataServices.CreateValueInfo<RealtimeSettingsManager>(options, new RealtimeRuntimeJsonConverter<RealtimeSettingsManager>());
            var metadata = ((IJsonTypeInfoResolver)ConnectRealtimeJsonContext.Default).GetTypeInfo(type, options);
            if (metadata is null) return null;
            if (type == typeof(RealtimeProviderOptions))
                metadata.PolymorphismOptions = new JsonPolymorphismOptions
                {
                    TypeDiscriminatorPropertyName = "type",
                    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization,
                    DerivedTypes = { new JsonDerivedType(typeof(XaiRealtimeOptions), "xai-realtime") }
                };
            if (type == typeof(RealtimeConversationItem))
                metadata.PolymorphismOptions!.DerivedTypes.Add(new JsonDerivedType(typeof(XaiRealtimeForceMessageItem), "xai-force-message"));
            if (type == typeof(LlmHostedTool))
                metadata.PolymorphismOptions!.DerivedTypes.Add(new JsonDerivedType(typeof(XaiXSearchTool), "xai-x-search"));
            if (type == typeof(XaiRealtimeForceMessageItem))
            {
                static void Validate(object value)
                {
                    if (((XaiRealtimeForceMessageItem)value).Id is not null)
                        throw new NotSupportedException("The force-message item does not accept a client ID.");
                }
                metadata.OnSerializing = Validate;
                metadata.OnDeserialized = Validate;
            }
            if (type == typeof(LlmHostedImageOutput))
                metadata.Properties.Single(property => property.Name == "Data").CustomConverter =
                    InferenceJson.LlmHostedImageOutput.Properties.Single(property => property.Name == "Data").CustomConverter;
            if (type == typeof(RealtimeAudioContent) || type == typeof(RealtimeAudioDeltaEvent))
                metadata.Properties.Single(property => property.Name == "Audio").CustomConverter =
                    InferenceJson.RealtimeAudioContent.Properties.Single(property => property.Name == "Audio").CustomConverter;
            return metadata;
        }

        private static JsonTypeInfo<T> RuntimeValue<T>(JsonSerializerOptions options) where T : class =>
            JsonMetadataServices.CreateValueInfo<T>(options, (JsonConverter<T>)options.Converters.Single(converter => converter.CanConvert(typeof(T))));
    }
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(RealtimeAudioEncoding))]
[JsonSerializable(typeof(RealtimeAudioTransport))]
[JsonSerializable(typeof(RealtimeModality))]
[JsonSerializable(typeof(RealtimeMessageRole))]
[JsonSerializable(typeof(RealtimeAudioConfig))]
[JsonSerializable(typeof(RealtimeTurnDetection))]
[JsonSerializable(typeof(RealtimeManualTurnDetection))]
[JsonSerializable(typeof(RealtimeServerVad))]
[JsonSerializable(typeof(RealtimeConversationReference))]
[JsonSerializable(typeof(RealtimeResumptionOptions))]
[JsonSerializable(typeof(RealtimeProviderOptions))]
[JsonSerializable(typeof(RealtimeSessionConfig))]
[JsonSerializable(typeof(RealtimeContent))]
[JsonSerializable(typeof(RealtimeTextContent))]
[JsonSerializable(typeof(RealtimeAudioContent))]
[JsonSerializable(typeof(RealtimeConversationItem))]
[JsonSerializable(typeof(RealtimeMessageItem))]
[JsonSerializable(typeof(RealtimeFunctionCallItem))]
[JsonSerializable(typeof(RealtimeFunctionOutputItem))]
[JsonSerializable(typeof(RealtimeResponseOptions))]
[JsonSerializable(typeof(RealtimeToolResult))]
[JsonSerializable(typeof(RealtimeCapabilities))]
[JsonSerializable(typeof(RealtimeEventIdentity))]
[JsonSerializable(typeof(RealtimeSessionEvent))]
[JsonSerializable(typeof(RealtimeConversationCreatedEvent))]
[JsonSerializable(typeof(RealtimeInputSpeechEvent))]
[JsonSerializable(typeof(RealtimeInputBufferEvent))]
[JsonSerializable(typeof(RealtimeItemEvent))]
[JsonSerializable(typeof(RealtimeContentPartEvent))]
[JsonSerializable(typeof(RealtimeTranscriptEvent))]
[JsonSerializable(typeof(RealtimeAudioDeltaEvent))]
[JsonSerializable(typeof(RealtimeAudioDoneEvent))]
[JsonSerializable(typeof(RealtimeTextDeltaEvent))]
[JsonSerializable(typeof(RealtimeFunctionArgumentsEvent))]
[JsonSerializable(typeof(RealtimeHostedStartedEvent))]
[JsonSerializable(typeof(RealtimeHostedProgressEvent))]
[JsonSerializable(typeof(RealtimeHostedCompletedEvent))]
[JsonSerializable(typeof(RealtimeResponseEvent))]
[JsonSerializable(typeof(RealtimeErrorEvent))]
[JsonSerializable(typeof(RealtimeConnection))]
[JsonSerializable(typeof(RealtimeTransport))]
[JsonSerializable(typeof(XaiRealtimeOptions))]
[JsonSerializable(typeof(XaiRealtimeForceMessageItem))]
[JsonSerializable(typeof(RealtimeSetupConfiguration))]
[JsonSerializable(typeof(RealtimeSetupHeaderChanges))]
[JsonSerializable(typeof(RealtimeSetupChanges))]
[JsonSerializable(typeof(XaiXSearchTool))]
internal sealed partial class ConnectRealtimeJsonContext : JsonSerializerContext;

internal sealed class RealtimeTransportJsonConverter : JsonConverter<RealtimeTransport>
{
    public override RealtimeTransport Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int value) && Enum.IsDefined((RealtimeTransport)value)
            ? (RealtimeTransport)value : throw new JsonException("A defined realtime transport is required.");
    public override void Write(Utf8JsonWriter writer, RealtimeTransport value, JsonSerializerOptions options)
    {
        if (!Enum.IsDefined(value)) throw new JsonException("Unknown realtime transport.");
        writer.WriteNumberValue((int)value);
    }
}

internal sealed class RealtimeRuntimeJsonConverter<T> : JsonConverter<T> where T : class
{
    public override bool HandleNull => true;
    public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Null ? null : throw new NotSupportedException("Live realtime collaborators are not portable data.");
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else throw new NotSupportedException("Live realtime collaborators are not portable data.");
    }
}
