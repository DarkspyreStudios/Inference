using System.Text;
using System.Text.Json;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime.EventStreams;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Bedrock;

/// <summary>One assistant content block of a Converse reply, in contentBlockIndex order.</summary>
internal abstract record BedrockBlock;

internal sealed record BedrockTextBlock(string Text) : BedrockBlock;

internal sealed record BedrockReasoningBlock(string Text, string? Signature) : BedrockBlock;

internal sealed record BedrockRedactedReasoningBlock(byte[] Data) : BedrockBlock;

internal sealed record BedrockToolUseBlock(string Id, string Name, string ArgumentsJson, bool ValidArguments) : BedrockBlock;

/// <summary>A complete Converse reply, from ConverseStream or Converse.</summary>
internal sealed record BedrockReply(IReadOnlyList<BedrockBlock> Blocks, string? StopReason, TokenUsage? Usage, string? RequestId);

/// <summary>Turns ConverseStream events into Connect events and accumulates the reply.</summary>
internal sealed class BedrockStreamAccumulator
{
    private readonly SortedDictionary<int, Block> _blocks = [];
    private int _toolBlocks;
    private bool _stopped;
    private string? _stopReason;
    private TokenUsage? _usage;

    public IReadOnlyList<LlmStreamEvent> Observe(IEventStreamEvent streamEvent)
    {
        switch (streamEvent)
        {
            case ContentBlockStartEvent { Start.ToolUse: { } toolUse } start:
                if (_blocks.ContainsKey(start.ContentBlockIndex ?? 0))
                {
                    throw MixedContent();
                }
                var tool = new ToolBlock(toolUse.ToolUseId ?? string.Empty, toolUse.Name ?? string.Empty, _toolBlocks++);
                _blocks[start.ContentBlockIndex ?? 0] = tool;
                return [new LlmToolCallStartedEvent(tool.Id, tool.Name, tool.Ordinal)];
            case ContentBlockDeltaEvent { Delta: { } delta } deltaEvent:
                return ObserveDelta(deltaEvent.ContentBlockIndex ?? 0, delta);
            case ContentBlockStopEvent stop
                when _blocks.TryGetValue(stop.ContentBlockIndex ?? 0, out Block? block) && block is ToolBlock stopped:
                return stopped.Stop() is { } call ? [new LlmToolCallCompletedEvent(call)] : [];
            case MessageStopEvent messageStop:
                _stopped = true;
                _stopReason = messageStop.StopReason?.Value;
                return [];
            case ConverseStreamMetadataEvent metadata:
                _usage = metadata.Usage;
                return [];
            default:
                // Message start, image and tool-result starts, and other block stops carry nothing Connect maps.
                return [];
        }
    }

    public BedrockReply Complete(string? requestId)
    {
        if (!_stopped)
        {
            throw new LlmProviderException(
                "Bedrock ended the stream without a message-stop event.",
                LlmTransport.AwsBedrockConverse);
        }

        return new BedrockReply([.. _blocks.Values.Select(block => block.ToReplyBlock())], _stopReason, _usage, requestId);
    }

    private IReadOnlyList<LlmStreamEvent> ObserveDelta(int index, ContentBlockDelta delta)
    {
        if (delta.Text is { } text)
        {
            BlockAt<TextBlock>(index, () => new TextBlock()).Text.Append(text);
            return text.Length > 0 ? [new LlmTextDeltaEvent(text)] : [];
        }

        if (delta.ToolUse is { } toolUse)
        {
            if (!_blocks.TryGetValue(index, out Block? existing))
            {
                throw new LlmProviderException(
                    "Bedrock streamed tool input before the tool's start event.",
                    LlmTransport.AwsBedrockConverse);
            }
            if (existing is not ToolBlock tool)
            {
                throw MixedContent();
            }
            string input = toolUse.Input ?? string.Empty;
            tool.Arguments.Append(input);
            return input.Length > 0 ? [new LlmToolCallArgumentsDeltaEvent(tool.Id, input)] : [];
        }

        if (delta.ReasoningContent is { } reasoning)
        {
            if (reasoning.RedactedContent is { } redacted)
            {
                redacted.Position = 0;
                redacted.CopyTo(BlockAt<RedactedBlock>(index, () => new RedactedBlock()).Data);
                return [];
            }

            ReasoningBlock block = BlockAt<ReasoningBlock>(index, () => new ReasoningBlock());
            if (reasoning.Signature is { } signature)
            {
                (block.Signature ??= new StringBuilder()).Append(signature);
            }
            if (reasoning.Text is { } reasoningText)
            {
                block.Text.Append(reasoningText);
                return reasoningText.Length > 0 ? [new LlmReasoningDeltaEvent(reasoningText, IsSummary: false)] : [];
            }
            return [];
        }

        // Citation, image and tool-result deltas carry nothing Connect maps.
        return [];
    }

    private T BlockAt<T>(int index, Func<T> create) where T : Block
    {
        if (!_blocks.TryGetValue(index, out Block? block))
        {
            T created = create();
            _blocks[index] = created;
            return created;
        }

        return block as T ?? throw MixedContent();
    }

    private static LlmProviderException MixedContent() => new(
        "Bedrock streamed mixed content in one block.",
        LlmTransport.AwsBedrockConverse);

    private abstract class Block
    {
        public abstract BedrockBlock ToReplyBlock();
    }

    private sealed class TextBlock : Block
    {
        public StringBuilder Text { get; } = new();

        public override BedrockBlock ToReplyBlock() => new BedrockTextBlock(Text.ToString());
    }

    private sealed class ReasoningBlock : Block
    {
        public StringBuilder Text { get; } = new();

        public StringBuilder? Signature { get; set; }

        public override BedrockBlock ToReplyBlock() => new BedrockReasoningBlock(Text.ToString(), Signature?.ToString());
    }

    private sealed class RedactedBlock : Block
    {
        public MemoryStream Data { get; } = new();

        public override BedrockBlock ToReplyBlock() => new BedrockRedactedReasoningBlock(Data.ToArray());
    }

    private sealed class ToolBlock(string id, string name, int ordinal) : Block
    {
        private bool _valid;

        public string Id { get; } = id;

        public string Name { get; } = name;

        public int Ordinal { get; } = ordinal;

        public StringBuilder Arguments { get; } = new();

        /// <summary>
        /// Parses the arguments. Empty arguments mean an empty object. Anything other than a JSON object marks the
        /// block invalid and returns null.
        /// </summary>
        public LlmToolCall? Stop()
        {
            string arguments = Arguments.Length == 0 ? "{}" : Arguments.ToString();
            try
            {
                using JsonDocument document = JsonDocument.Parse(arguments);
                _valid = document.RootElement.ValueKind == JsonValueKind.Object;
            }
            catch (JsonException)
            {
                _valid = false;
            }

            return _valid ? new LlmToolCall(Id, Name, arguments) : null;
        }

        public override BedrockBlock ToReplyBlock() => new BedrockToolUseBlock(
            Id,
            Name,
            Arguments.Length == 0 ? "{}" : Arguments.ToString(),
            _valid);
    }
}
