using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.ConsoleTester;

internal static class ConsoleSetup
{
    public static async Task<int> DescribeAsync(LlmProviderService service, ConsoleInvocation invocation,
        TextWriter output, CancellationToken token)
    {
        var (_, settings) = await OpenAsync(service, invocation, token);
        output.WriteLine($"{invocation.Provider.Definition.Id}: {invocation.Provider.DisplayName}");
        output.WriteLine(invocation.Provider.Definition.Description);
        output.WriteLine($"Default endpoint: {invocation.Provider.Endpoint}");
        output.WriteLine($"Default model: {invocation.Provider.Definition.DefaultModelId}");
        foreach (var step in settings.Steps)
        {
            output.WriteLine($"\n{step.Title}: {step.Instructions}");
            if (step.HasPlatformInstructions) output.WriteLine(step.PlatformInstructions);
            if (step.HasCompletionRequirement) output.WriteLine($"Requires: {step.CompletionRequirement}");
            if (step.HasTroubleshooting) output.WriteLine($"Troubleshooting: {step.Troubleshooting}");
            foreach (var field in step.Sections.SelectMany(section => section.Fields).Where(field => field.Description.IsVisible))
                output.WriteLine($"  {field.Description.Id}: {field.Description.DisplayName}"
                    + (field.Description.IsRequired ? " (required)" : "") + $" {field.Description.HelpText}");
        }
        foreach (var action in settings.Actions)
            output.WriteLine($"Action {action.Id}: {action.DisplayName}; effects={action.SideEffects}; enabled={action.IsEnabled}");
        // Opening and rendering settings performs no checks, sign-in, save or generation.
        return 0;
    }

    public static async Task<SetupActionState> CheckAsync(LlmProviderService service, ConsoleInvocation invocation,
        TextWriter output, CancellationToken token)
    {
        var (manager, settings) = await OpenAsync(service, invocation, token);
        var action = settings.Actions.Single(action => action.Purpose == SetupActionPurpose.CheckPrerequisites);
        SetupActionResult result = await manager.RunAsync(settings, action.Id, cancellationToken: token);
        token.ThrowIfCancellationRequested();
        output.WriteLine($"{invocation.Provider.DisplayName}: {result.State}");
        foreach (var issue in result.Issues ?? [])
            output.WriteLine($"{issue.Code}: {issue.Message}");
        if (result.Interaction is SetupExternalPrerequisite prerequisite)
            output.WriteLine(prerequisite.Instructions);
        if (!action.IsEnabled)
            foreach (var issue in settings.Validate().Issues)
                output.WriteLine($"{issue.Code}: {issue.Message}");
        return result.State;
    }

    private static async Task<(LlmSettingsManager Manager, LlmSettings Settings)> OpenAsync(
        LlmProviderService service, ConsoleInvocation invocation, CancellationToken token)
    {
        var manager = service.CreateSettingsManager(new UnsavedTarget());
        var settings = await manager.OpenAsync(invocation.Provider.Transport,
            new(SetupChangeIntent.Create, invocation.Provider.ConnectionId, null), cancellationToken: token);
        if (invocation.EndpointOverride is { } endpoint)
        {
            SetupField field = settings.Steps.SelectMany(step => step.Sections).SelectMany(section => section.Fields)
                .Single(field => field is SetupExecutableField || field.Description.Id == "harness-arn");
            SetupEditResult edit = field switch
            {
                SetupExecutableField executable => executable.SetValue(new(endpoint)),
                SetupTextField arn => arn.SetValue(endpoint),
                _ => throw new ArgumentException("The provider has no executable or harness ARN field.")
            };
            if (edit.State != SetupEditState.Applied) throw new ArgumentException("Invalid endpoint override.");
        }
        if (invocation.Vertex is { } google)
        {
            var authentication = settings.Steps.SelectMany(step => step.Sections).SelectMany(section => section.Fields)
                .OfType<SetupChoiceField>().Single(field => field.Description.Id == "google-authentication");
            _ = authentication.SetValue(google.AuthenticationMode.ToString());
            if (google.AuthenticationMode == LlmGoogleAuthenticationMode.ApiKey) return (manager, settings);
            foreach (var (id, value) in new[] { ("google-project", google.ProjectId), ("google-location", google.Location) })
            {
                var field = settings.Steps.SelectMany(step => step.Sections).SelectMany(section => section.Fields)
                    .OfType<SetupTextField>().Single(field => field.Description.Id == id);
                _ = field.SetValue(value ?? string.Empty);
            }
        }
        return (manager, settings);
    }

    private sealed class UnsavedTarget : ISetupCommitTarget<LlmSetupChanges>
    {
        public Task<SetupCommitResult> CommitAsync(LlmSetupChanges changes, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Console inspection does not save settings.");
    }
}
