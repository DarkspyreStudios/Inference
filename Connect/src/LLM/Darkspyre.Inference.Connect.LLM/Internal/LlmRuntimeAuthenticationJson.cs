using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Darkspyre.Inference.Connect.LLM.Internal;

internal static class LlmRuntimeAuthenticationJson
{
    internal static JsonTypeInfo? RuntimeMetadata(Type type, JsonSerializerOptions options)
    {
        if (type == typeof(LlmAgentRuntimeLaunchInputs)) return JsonMetadataServices.CreateValueInfo<LlmAgentRuntimeLaunchInputs>(options, new CallerAuthorityConverter<LlmAgentRuntimeLaunchInputs>());
        if (type == typeof(LlmAgentRuntimeLoginServices)) return JsonMetadataServices.CreateValueInfo<LlmAgentRuntimeLoginServices>(options, new CallerAuthorityConverter<LlmAgentRuntimeLoginServices>());
        if (type == typeof(ILlmAgentRuntimeLoginTerminalFactory)) return JsonMetadataServices.CreateValueInfo<ILlmAgentRuntimeLoginTerminalFactory>(options, new CallerAuthorityConverter<ILlmAgentRuntimeLoginTerminalFactory>());
        if (type == typeof(ILlmAgentRuntimeAuthentication)) return JsonMetadataServices.CreateValueInfo<ILlmAgentRuntimeAuthentication>(options, new CallerAuthorityConverter<ILlmAgentRuntimeAuthentication>());
        if (type == typeof(LlmAgentRuntimeLoginTerminalRequest)) return JsonMetadataServices.CreateValueInfo<LlmAgentRuntimeLoginTerminalRequest>(options, new CallerAuthorityConverter<LlmAgentRuntimeLoginTerminalRequest>());
        if (type == typeof(ILlmAuthPrompt)) return JsonMetadataServices.CreateValueInfo<ILlmAuthPrompt>(options, new CallerAuthorityConverter<ILlmAuthPrompt>());
        return null;
    }
}
