using Darkspyre.Inference.Common.Setup;

namespace Darkspyre.Inference.Connect.LLM.Providers;

internal static class SelfHostDefinition
{
    public static LlmProviderDefinition Value { get; } = new()
    {
        Id = "self-host",
        DisplayName = "In-process model",
        Description = "Run a model that this application loads in its own process, such as a Tensor engine.",
        Transport = LlmTransport.SelfHost,
        DefaultEndpoint = string.Empty,
        EndpointKind = LlmProviderEndpointKind.Engine,
        DisplayOrder = 95,
        SetupSteps =
        [
            new SetupStep
            {
                Id = "select-engine",
                Title = "Choose an engine",
                Instructions = "Choose one of the engines this application runs. The application configures and loads each engine.",
                CompletionRequirement = "An engine must be selected.",
                Troubleshooting = "If no engine is listed, add and load one in the application's model settings."
            },
            ProviderDefinitions.SelectModel
        ]
    };
}
