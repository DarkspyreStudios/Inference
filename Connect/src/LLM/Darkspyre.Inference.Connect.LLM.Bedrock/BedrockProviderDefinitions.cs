namespace Darkspyre.Inference.Connect.LLM.Bedrock;

internal static class BedrockProviderDefinitions
{
    public static LlmProviderDefinition AwsProfile { get; } = new()
    {
        Id = "bedrock-aws-profile",
        DisplayName = "Bedrock AWS Profile",
        Description = "Use Amazon Bedrock with a named profile from the standard AWS credential chain.",
        Transport = LlmTransport.AwsBedrockConverse,
        DefaultEndpoint = "https://bedrock-runtime.us-east-1.amazonaws.com",
        DisplayOrder = 50,
        SetupSteps =
        [
            new LlmSetupStep
            {
                Id = "configure-profile",
                Title = "Choose an AWS profile and region",
                Instructions = "Configure a named AWS profile outside the application, then enter its profile name and AWS region. The profile must be permitted to list and invoke the Bedrock models you intend to use.",
                Action = LlmSetupAction.ConfigureAwsProfile,
                Inputs =
                [
                    new LlmSetupInput
                    {
                        Id = "aws-profile",
                        DisplayName = "AWS profile",
                        HelpText = "The name used in the standard AWS shared configuration and credential files.",
                        IsRequired = true
                    },
                    new LlmSetupInput
                    {
                        Id = "aws-region",
                        DisplayName = "AWS region",
                        HelpText = "The region where the selected Bedrock models are available.",
                        IsRequired = true
                    }
                ],
                CompletionRequirement = "The AWS SDK must resolve the named profile and list Bedrock models in the selected region.",
                Troubleshooting = "Verify the profile with the AWS CLI and confirm it has Bedrock list and invoke permissions in the selected region.",
                WindowsInstructions = "In PowerShell, run aws configure --profile PROFILE_NAME, then verify it with aws sts get-caller-identity --profile PROFILE_NAME.",
                MacOsInstructions = "In Terminal, run aws configure --profile PROFILE_NAME, then verify it with aws sts get-caller-identity --profile PROFILE_NAME.",
                LinuxInstructions = "In a terminal, run aws configure --profile PROFILE_NAME, then verify it with aws sts get-caller-identity --profile PROFILE_NAME."
            },
            new LlmSetupStep
            {
                Id = "select-model",
                Title = "Choose a model",
                Instructions = "Load the Bedrock models available to the selected profile and region, then choose the model the application should use.",
                Action = LlmSetupAction.SelectModel,
                Inputs =
                [
                    new LlmSetupInput
                    {
                        Id = "model-id",
                        DisplayName = "Model",
                        IsRequired = true
                    }
                ],
                CompletionRequirement = "A Bedrock model available to this profile and region must be selected.",
                Troubleshooting = "Confirm the model is available in the selected region and that the AWS account has access to invoke it."
            },
            new LlmSetupStep
            {
                Id = "test-connection",
                Title = "Test and finish",
                Instructions = "Test the selected model. Finish setup only after AWS accepts the connection.",
                Action = LlmSetupAction.TestConnection,
                CompletionRequirement = "Bedrock must return a completed response from the selected model.",
                Troubleshooting = "Check the profile's Bedrock permissions, region, model access, and any organization service-control policies."
            }
        ]
    };

    public static LlmProviderDefinition AgentCoreHarness { get; } = new()
    {
        Id = "bedrock-agentcore-harness",
        DisplayName = "Bedrock AgentCore Harness",
        Description = "Use an AWS-hosted AgentCore Harness through its session-oriented agent API.",
        Transport = LlmTransport.AwsBedrockAgentCore,
        DefaultEndpoint = "arn:aws:bedrock-agentcore:REGION:ACCOUNT_ID:harness/HARNESS_ID",
        DefaultModelId = BedrockAgentCoreLlmClient.DefaultModelId,
        DisplayOrder = 60,
        SetupSteps =
        [
            new LlmSetupStep
            {
                Id = "configure-agentcore",
                Title = "Choose an AWS profile, region, and Harness",
                Instructions = "Configure a named AWS profile outside the application, then enter its profile name, region, and deployed AgentCore Harness ARN.",
                Action = LlmSetupAction.ConfigureAwsProfile,
                Inputs =
                [
                    new LlmSetupInput
                    {
                        Id = "aws-profile",
                        DisplayName = "AWS profile",
                        HelpText = "The name used in the standard AWS shared configuration and credential files.",
                        IsRequired = true
                    },
                    new LlmSetupInput
                    {
                        Id = "aws-region",
                        DisplayName = "AWS region",
                        HelpText = "The region containing the deployed Harness.",
                        IsRequired = true
                    },
                    new LlmSetupInput
                    {
                        Id = "harness-arn",
                        DisplayName = "Harness ARN",
                        HelpText = "The ARN returned when the AgentCore Harness was deployed.",
                        IsRequired = true
                    }
                ],
                CompletionRequirement = "The AWS SDK must resolve the profile and the profile must be allowed to invoke the Harness.",
                Troubleshooting = "Verify the profile with the AWS CLI, confirm the ARN and region match, and grant bedrock-agentcore:InvokeHarness.",
                WindowsInstructions = "In PowerShell, verify the profile with aws sts get-caller-identity --profile PROFILE_NAME.",
                MacOsInstructions = "In Terminal, verify the profile with aws sts get-caller-identity --profile PROFILE_NAME.",
                LinuxInstructions = "In a terminal, verify the profile with aws sts get-caller-identity --profile PROFILE_NAME."
            },
            new LlmSetupStep
            {
                Id = "select-model",
                Title = "Choose a model override",
                Instructions = "Use 'default' to keep the Harness model, or enter a Bedrock model ID to override it for each invocation.",
                Action = LlmSetupAction.SelectModel,
                Inputs =
                [
                    new LlmSetupInput
                    {
                        Id = "model-id",
                        DisplayName = "Model",
                        HelpText = "Use 'default' unless this connection should override the Harness's configured Bedrock model.",
                        IsRequired = true
                    }
                ],
                CompletionRequirement = "A model selection must be recorded; 'default' uses the model deployed with the Harness.",
                Troubleshooting = "If an override fails, confirm the Harness role can invoke that Bedrock model or switch back to 'default'."
            },
            new LlmSetupStep
            {
                Id = "test-connection",
                Title = "Test and finish",
                Instructions = "Start an AgentCore session and send a short prompt. Finish setup only after the Harness returns a completed turn.",
                Action = LlmSetupAction.TestConnection,
                CompletionRequirement = "AgentCore must accept the Harness invocation and return a completed response.",
                Troubleshooting = "Check the profile, region, Harness ARN, InvokeHarness permission, Harness execution role, and model access."
            }
        ]
    };
}
