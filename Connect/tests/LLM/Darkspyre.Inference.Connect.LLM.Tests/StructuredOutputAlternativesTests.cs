using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Tests;

/// <summary>A schema with alternatives passes or fails as a whole, whatever its other alternatives report.</summary>
public sealed class StructuredOutputAlternativesTests
{
    private const LlmStructuredOutputEnforcement Mode = LlmStructuredOutputEnforcement.LocalValidationOnly;

    [Fact]
    public void AValueMatchingOneAlternativeIsValid()
    {
        LlmStructuredOutputRequest selection = new()
        {
            Name = "selection",
            Schema = JsonElement.Parse("""
                {"type":"object","properties":{"selection":{"oneOf":[
                  {"type":"object","properties":{"kind":{"const":"names"},"names":{"type":"array","items":{"type":"string"}}},"required":["kind","names"]},
                  {"type":"object","properties":{"kind":{"const":"filter"},"value":{"type":"string"}},"required":["kind","value"]},
                  {"type":"object","properties":{"kind":{"const":"all"}},"required":["kind"],"additionalProperties":false}]}},
                 "required":["selection"]}
                """)
        };

        LlmStructuredOutputValidation result = StructuredOutput.Validate("""{"selection":{"kind":"filter","value":"tests"}}""", selection, Mode);

        Assert.True(result.IsValid, string.Join("\n", result.Errors));
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void AValueMatchingTwoOneOfAlternativesIsInvalid()
    {
        LlmStructuredOutputRequest both = new()
        {
            Name = "both",
            Schema = JsonElement.Parse("""{"oneOf":[{"type":"object"},{"type":"object"}]}""")
        };

        Assert.False(StructuredOutput.Validate("{}", both, Mode).IsValid);
    }
}
