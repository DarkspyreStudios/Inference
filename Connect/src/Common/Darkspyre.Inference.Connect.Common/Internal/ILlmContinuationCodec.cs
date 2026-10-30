using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Owned by a protocol adapter. The session runtime never interprets native payloads.</summary>
internal interface ILlmContinuationCodec
{
    LlmContinuationState Encode(LlmConnection connection, string modelId, JsonArray content);
    bool IsBound(LlmContinuationState state, LlmConnection connection, string modelId);
    JsonArray DecodeMessage(LlmChatMessage message, LlmConnection connection, string modelId);
}

/// <summary>A client whose continuation state its own codec encodes and replays.</summary>
internal interface ILlmContinuationCodecSource
{
    /// <summary>The client's codec, or null when it keeps the shared static rules.</summary>
    ILlmContinuationCodec? ContinuationCodec { get; }
}

/// <summary>Marks a client whose engine decides which continuation state it can replay; the service passes state
/// through unprojected.</summary>
internal interface IEngineOwnedContinuation;

// The Gemini API, OpenAI-compatible Chat and Google Vertex AI use this; they are out of the build
// (Providers/GeminiApiLlmClient*.cs and ChatCompletionsLlmClientBase.cs, under Compile Remove;
// Connect/src/LLM/Darkspyre.Inference.Connect.LLM.Vertex, outside the solution). It moves into their own libraries when
// they return to the build.
/// <summary>Resolves short-lived adapter identity before replay validation. Does not persist tokens.</summary>
internal interface ILlmConnectionPreparation
{
    Task<LlmConnection> PrepareAsync(LlmConnection connection, CancellationToken cancellationToken);
}
