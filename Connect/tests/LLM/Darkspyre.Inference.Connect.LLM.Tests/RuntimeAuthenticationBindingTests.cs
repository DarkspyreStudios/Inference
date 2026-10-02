using Darkspyre.Inference.Common.Setup;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class RuntimeAuthenticationBindingTests
{
    [Fact]
    public void ExactSavedAndDraftBindingMethodsAreCallableSharedSurfaces()
    {
        var saved = typeof(LlmAgentProviderService).GetMethod("BindRuntimeAuthentication", [typeof(string)]);
        var draft = typeof(LlmAgentProviderService).GetMethod("BindRuntimeAuthentication", [typeof(LlmConnection), typeof(SetupConfigurationIdentity)]);
        Assert.NotNull(saved); Assert.NotNull(draft);
        Assert.Equal(typeof(ILlmAgentRuntimeAuthentication), saved.ReturnType);
        Assert.Equal(typeof(ILlmAgentRuntimeAuthentication), draft.ReturnType);
    }

    [Fact]
    public void SettingsCompositionAcceptsOptionalBorrowedAgentFacade()
    {
        var method = typeof(LlmProviderService).GetMethod("CreateSettingsManager", [typeof(ISetupCommitTarget<LlmSetupChanges>), typeof(LlmAgentProviderService)]);
        Assert.NotNull(method);
        Assert.True(method.GetParameters()[1].HasDefaultValue); Assert.Null(method.GetParameters()[1].DefaultValue);
    }

    [Fact]
    public void SettingsRetirementReleasesBorrowedComposition()
    {
        Assert.Contains(typeof(IDisposable), typeof(LlmSettingsManager).GetInterfaces());
        Assert.Contains(typeof(IAsyncDisposable), typeof(LlmSettingsManager).GetInterfaces());
    }
}
