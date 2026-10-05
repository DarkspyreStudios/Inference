using System.Globalization;
using Darkspyre.Inference.Common.Setup;

namespace Darkspyre.Inference.Common.Tests;

public sealed class InferenceSettingsRegressionTests
{
    [Fact]
    public void AContinuationAtIntMaxValueEndsThePageWithoutOverflowing()
    {
        SetupChoice[] choices = [new("small", "Small"), new("medium", "Medium"), new("large", "Large")];

        SetupChoicePage page = InferenceSettings.Page(
            new(Continuation: int.MaxValue.ToString(CultureInfo.InvariantCulture), PageSize: 2), choices);

        Assert.Empty(page.Choices);
        Assert.Null(page.Continuation);
    }

    [Fact]
    public async Task AnUncertainSaveStaysVisibleEvenWhenNoFieldChanged()
    {
        var settings = new TestSettings();
        Assert.False(settings.HasUnsavedChanges);

        SetupCommitResult result = await new TestManager(new UncertainTarget()).SaveAsync(settings);

        Assert.Equal(SetupCommitState.Uncertain, result.State);
        Assert.True(settings.SaveOutcomeUncertain);
    }

    private sealed record Changes(string Name);

    private sealed class UncertainTarget : ISetupCommitTarget<Changes>
    {
        public Task<SetupCommitResult> CommitAsync(Changes changes, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SetupCommitResult(SetupCommitState.Uncertain));
    }

    private sealed class TestSettings() : InferenceSettings<Changes>(
        new(new("test", "Test", InferenceImplementationKind.Connect, InferenceTask.TextGeneration), "Test"),
        new(SetupChangeIntent.Create, "entry", null))
    {
        protected override IReadOnlyList<SetupStep> CreateSteps() =>
            [new("step", "Step", "Instructions", [new("section", "Section",
                [new SetupTextField(new("name", "Name"), "original", "original", _ => SetupEditResult.Applied)])])];

        protected override IReadOnlyList<SetupActionDescriptor> CreateActions() => [];
        protected override SetupValidation ValidateDraft() => SetupValidation.Valid;
        protected override Changes CreateChanges() => new("original");
        protected override bool IsDraftDirty => false;
    }

    private sealed class TestManager(UncertainTarget target) : InferenceSettingsManager<TestSettings, Changes>(target)
    {
        protected override Task<SetupActionResult> RunCoreAsync(TestSettings settings, SetupActionDescriptor action,
            IProgress<SetupProgress>? progress, CancellationToken cancellationToken) => Task.FromResult(SetupActionResult.Completed);
    }
}
