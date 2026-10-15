using System.Xml.Linq;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Tensor.Embedding.Gemma;
using Darkspyre.Inference.Tensor.LLM.Qwen;
using Darkspyre.Inference.Tensor.STT.Whisper;

namespace Darkspyre.Inference.Tensor.Common.Tests;

public sealed class TensorEngineIconTests
{
    /// <summary>Qwen, Gemma and Whisper list under "Run on this computer" with their own logo in the subset the host's icon draws.</summary>
    [Fact]
    public void EnginesHaveAGroupAndTheirIcon()
    {
        SetupIcon openAi = SetupIcons.FromResource(typeof(SetupIcons).Assembly, SetupIcons.OpenAiResource);
        (InferenceSetupDefinition Definition, bool OpenAiMark)[] engines =
            [(QwenSetup.Definition, false), (GemmaEmbeddingSetup.Definition, false), (WhisperSetup.Definition, true)];
        foreach ((InferenceSetupDefinition definition, bool openAiMark) in engines)
        {
            Assert.Equal(SetupProviderGroup.ThisComputer, definition.Group);
            SetupIcon icon = Assert.IsType<SetupIcon>(definition.Icon);
            Assert.Equal("0 0 24 24", icon.ViewBox);
            XElement[] elements = [.. XElement.Parse("<svg>" + icon.Markup + "</svg>").Elements()];
            Assert.NotEmpty(elements);
            Assert.All(elements, element =>
            {
                Assert.True(element.Name.LocalName is "path" or "rect" or "circle", element.Name.LocalName);
                Assert.Empty(element.Elements());
                Assert.Null(element.Attribute("transform"));
                Assert.Matches("^#[0-9A-F]{6}$", (string?)element.Attribute("fill") ?? string.Empty);
            });
            Assert.Equal(openAiMark, icon == openAi);
        }
        Assert.NotEqual(QwenSetup.Definition.Icon, GemmaEmbeddingSetup.Definition.Icon);
    }
}
