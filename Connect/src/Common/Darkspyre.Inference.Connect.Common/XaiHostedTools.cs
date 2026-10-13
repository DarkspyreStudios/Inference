using System.Text.Json.Serialization;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM;

public sealed record XaiXSearchTool(
    IReadOnlyList<string>? AllowedHandles = null, IReadOnlyList<string>? ExcludedHandles = null,
    DateOnly? FromDate = null, DateOnly? ToDate = null,
    bool IncludeImageUnderstanding = false, bool IncludeVideoUnderstanding = false) : LlmHostedTool
{
    public IReadOnlyList<string>? AllowedHandles { get; init; } = AllowedHandles is null ? null : Array.AsReadOnly(AllowedHandles.ToArray());
    public IReadOnlyList<string>? ExcludedHandles { get; init; } = ExcludedHandles is null ? null : Array.AsReadOnly(ExcludedHandles.ToArray());
    [JsonIgnore] public override LlmHostedToolKind Kind => LlmHostedToolKind.SocialSearch;
}
