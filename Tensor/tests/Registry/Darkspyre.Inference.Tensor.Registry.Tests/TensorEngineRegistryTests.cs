using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Tensor.Common;
using Darkspyre.Inference.Tensor.Decision.Laya;
using Darkspyre.Inference.Tensor.Image.Supra;
using Darkspyre.Inference.Tensor.LLM;
using Darkspyre.Inference.Tensor.LLM.Qwen;
using Darkspyre.Inference.Tensor.STT.Whisper;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Tensor.Registry.Tests;

public sealed class TensorEngineRegistryTests
{
    private static TensorEngineRegistry Compose(Action<IServiceCollection>? more = null)
    {
        var services = new ServiceCollection();
        services.AddTensorEngines();
        more?.Invoke(services);
        return services.BuildServiceProvider().GetRequiredService<TensorEngineRegistry>();
    }

    [Fact]
    public void AddTensorEnginesListsTheFourBuiltInsInDisplayThenIdOrder()
    {
        TensorEngineRegistry registry = Compose();

        Assert.Equal(["darkspyre.tensor.laya", "darkspyre.tensor.qwen", "darkspyre.tensor.supra2-img", "darkspyre.tensor.whisper"],
            registry.Definitions.Select(definition => definition.Setup.Implementation.Id));
        Assert.Equal(["Laya", "Qwen", "Supra", "Whisper"], registry.Definitions.Select(definition => definition.ModelStoreKey));
        InferenceSetupDefinition[] setups = [LayaSetup.Definition, QwenSetup.Definition, SupraSetup.Definition, WhisperSetup.Definition];
        Assert.All(registry.Definitions.Zip(setups), pair =>
        {
            // The definition is a snapshot of the engine's setup metadata, not a second description of it.
            Assert.Same(pair.Second.Implementation, pair.First.Setup.Implementation);
            Assert.Equal(pair.Second with { Prerequisites = pair.First.Setup.Prerequisites }, pair.First.Setup);
            Assert.Equal(pair.Second.Prerequisites, pair.First.Setup.Prerequisites);
        });
    }

    [Fact]
    public void RepeatedAddTensorEnginesAddsNothing()
    {
        var services = new ServiceCollection();
        services.AddTensorEngines().AddTensorEngines();
        Assert.Equal(6, services.Count);
        Assert.Equal(4, services.BuildServiceProvider().GetRequiredService<TensorEngineRegistry>().Definitions.Count);
    }

    [Fact]
    public void ADuplicateProviderRegistrationFailsInsteadOfReplacingTheFirst()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            Compose(services => services.AddSingleton<ITensorEngineProvider>(new QwenEngineProvider())));
        Assert.Contains("darkspyre.tensor.qwen", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("qwen")]
    [InlineData("QWEN")]
    [InlineData("Qwen")]
    public void StoreKeysThatDifferOnlyByCaseCollide(string storeKey)
    {
        Assert.Throws<ArgumentException>(() => Compose(services =>
            services.AddSingleton<ITensorEngineProvider>(StubProvider.Create("test.tensor.other", storeKey))));
    }

    [Fact]
    public void MissingProvidersAndDefinitionsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new TensorEngineRegistry([null!]));
        Assert.Throws<ArgumentException>(() => new TensorEngineRegistry([new StubProvider(null)]));
        Assert.Throws<ArgumentNullException>(() => new TensorEngineRegistry(null!));
    }

    [Fact]
    public void ImplementationIdsAreExactAndCaseSensitive()
    {
        TensorEngineRegistry registry = Compose(services =>
            services.AddSingleton<ITensorEngineProvider>(StubProvider.Create("DARKSPYRE.TENSOR.QWEN", "Other")));

        Assert.True(registry.TryGetProvider("darkspyre.tensor.qwen", out ITensorEngineProvider? qwen));
        Assert.IsType<QwenEngineProvider>(qwen);
        Assert.True(registry.TryGetProvider("DARKSPYRE.TENSOR.QWEN", out ITensorEngineProvider? other));
        Assert.IsType<StubProvider>(other);
        Assert.False(registry.TryGetProvider("darkspyre.tensor.missing", out ITensorEngineProvider? missing));
        Assert.Null(missing);
    }

    [Fact]
    public void TaskViewsFilterTheOneOrderedList()
    {
        TensorEngineRegistry registry = new([
            StubProvider.Create("b.llm", "B", displayOrder: 5), StubProvider.Create("a.llm", "A", displayOrder: 5),
            StubProvider.Create("z.llm", "Z", displayOrder: -1), StubProvider.Create("c.image", "C", InferenceTask.ImageGeneration)]);

        Assert.Equal(["z.llm", "c.image", "a.llm", "b.llm"], registry.Definitions.Select(definition => definition.Setup.Implementation.Id));
        Assert.Equal(["z.llm", "a.llm", "b.llm"],
            registry.GetDefinitions(InferenceTask.TextGeneration).Select(definition => definition.Setup.Implementation.Id));
        Assert.Equal(["c.image"], registry.GetDefinitions(InferenceTask.ImageGeneration).Select(definition => definition.Setup.Implementation.Id));
        Assert.Empty(registry.GetDefinitions(InferenceTask.SpeechSynthesis));
        Assert.Throws<ArgumentOutOfRangeException>(() => registry.GetDefinitions((InferenceTask)99));
    }

    [Fact]
    public void BuiltInTaskViewsNameEachCapability()
    {
        TensorEngineRegistry registry = Compose();
        string Only(InferenceTask task) => Assert.Single(registry.GetDefinitions(task)).Setup.Implementation.Id;

        Assert.Equal("darkspyre.tensor.qwen", Only(InferenceTask.TextGeneration));
        Assert.Equal("darkspyre.tensor.supra2-img", Only(InferenceTask.ImageGeneration));
        Assert.Equal("darkspyre.tensor.laya", Only(InferenceTask.Decision));
        Assert.Equal("darkspyre.tensor.whisper", Only(InferenceTask.SpeechTranscription));
        Assert.Empty(registry.GetDefinitions(InferenceTask.AudioGeneration));
    }

    [Fact]
    public void ViewsAndDefinitionsAreReadOnlySnapshots()
    {
        string[] prerequisites = ["runtime"];
        var definition = new TensorEngineDefinition(QwenSetup.Definition with { Prerequisites = prerequisites }, "Qwen");
        prerequisites[0] = "changed";
        Assert.Equal(["runtime"], definition.Setup.Prerequisites);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)definition.Setup.Prerequisites)[0] = "changed");

        TensorEngineRegistry registry = Compose();
        Assert.Throws<NotSupportedException>(() => ((IList<TensorEngineDefinition>)registry.Definitions).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<TensorEngineDefinition>)registry.GetDefinitions(InferenceTask.TextGeneration)).Clear());
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("/Qwen")]
    [InlineData("C:")]
    [InlineData("C:Qwen")]
    [InlineData(".hidden")]
    [InlineData("trailing.")]
    [InlineData(" Qwen")]
    [InlineData("Qwen ")]
    [InlineData("Qw en")]
    [InlineData("Qwén")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("com1")]
    [InlineData("12345678901234567890123456789012345678901234567890123456789012345")]
    public void InvalidStoreKeysAreRejected(string storeKey) =>
        Assert.Throws<ArgumentException>(() => new TensorEngineDefinition(QwenSetup.Definition, storeKey));

    [Theory]
    [InlineData("Qwen")]
    [InlineData("model_2.v1-a")]
    [InlineData("1234567890123456789012345678901234567890123456789012345678901234")]
    public void PortableStoreKeysAreAccepted(string storeKey) =>
        Assert.Equal(storeKey, new TensorEngineDefinition(QwenSetup.Definition, storeKey).ModelStoreKey);

    [Fact]
    public void NonTensorDescriptorsAreRejected()
    {
        var connect = new InferenceSetupDefinition(
            new("connect.test", "Connect", InferenceImplementationKind.Connect, InferenceTask.TextGeneration), "Connect");
        Assert.Throws<ArgumentException>(() => new TensorEngineDefinition(connect, "Connect"));
        Assert.Throws<ArgumentException>(() => new TensorEngineDefinition(QwenSetup.Definition with { Implementation = null! }, "Qwen"));
        Assert.Throws<ArgumentNullException>(() => new TensorEngineDefinition(null!, "Qwen"));
    }

    [Fact]
    public async Task ASyntheticFifthEngineIsListedConfiguredAndDrivenThroughTheSameConsumer()
    {
        TensorEngineRegistry registry = Compose(services => services.AddSingleton<ITensorEngineProvider, SyntheticLlmProvider>());
        Assert.Equal(5, registry.Definitions.Count);
        Assert.Equal(["darkspyre.tensor.qwen", "test.tensor.synthetic-llm"],
            registry.GetDefinitions(InferenceTask.TextGeneration).Select(definition => definition.Setup.Implementation.Id));

        var packs = new RecordingPacks(SyntheticLlmProvider.Pack);
        var target = new RecordingTarget();
        (ITensorModelInstance instance, TensorSettingsManager manager, TensorSettings settings) =
            await OpenAsync(registry, "test.tensor.synthetic-llm", packs, target);

        // Text routing selects the capability interface, not a concrete engine class.
        Assert.IsType<ITensorLlmInference>(instance, exactMatch: false);
        Assert.Equal(SetupEditState.Applied, Field(settings, "model").SetValue(SyntheticLlmProvider.Pack.Key.ToString()).State);
        Assert.Equal(SetupEditState.Applied, Field(settings, "backend").SetValue(nameof(TensorBackendKind.Cpu)).State);
        Assert.Equal(SetupCommitState.Committed, (await manager.SaveAsync(settings)).State);
        Assert.Equal(new TensorSetupConfiguration("test.tensor.synthetic-llm", SyntheticLlmProvider.Pack.Key, TensorBackendKind.Cpu),
            target.Changes!.Configuration);

        await instance.StartAndWaitAsync();
        await manager.RefreshStatusAsync(settings);
        SetupActionResult loaded = await instance.WaitForAsync(
            () => manager.RunAsync(settings, Action(settings, SetupActionPurpose.Load).Id), TensorEngineState.Loaded);
        Assert.Equal(SetupActionState.Completed, loaded.State);
        Assert.True(settings.Status.Ready.Value);

        await instance.DisposeAsync();
        Assert.True(((SyntheticLlm)instance).Disposed);
        Assert.DoesNotContain(packs.Calls, call => call is "install" or "import" or "register" or "remove");
    }

    [Theory]
    [InlineData("darkspyre.tensor.qwen", 1)]
    [InlineData("darkspyre.tensor.supra2-img", 1)]
    [InlineData("darkspyre.tensor.laya", 0)]
    [InlineData("darkspyre.tensor.whisper", 12)]
    public async Task BuiltInSetupOpensWithoutALiveInstanceAndWithoutPackEffects(string id, int builtInModels)
    {
        TensorEngineRegistry registry = Compose();
        Assert.True(registry.TryGetProvider(id, out ITensorEngineProvider? provider));
        var packs = new RecordingPacks();

        IReadOnlyList<TensorModelPackDefinition> offered = await provider.GetOfferedModelsAsync(packs);
        Assert.Equal(builtInModels, offered.Count);
        TensorSettingsManager manager = provider.CreateSettingsManager(new RecordingTarget(), packs, offered);
        TensorSettings settings = await manager.OpenAsync(new(SetupChangeIntent.Create, "entry", null));

        Assert.Equal(id, settings.Definition.Implementation.Id);
        Assert.False(Action(settings, SetupActionPurpose.Load).IsEnabled);
        Assert.Null(settings.Status.Loaded.Value);
        Assert.Contains(settings.Actions, action => action.Id == "import");
        Assert.Contains(settings.Actions, action => action.Purpose == SetupActionPurpose.CheckPrerequisites && action.IsEnabled);
        Assert.Equal(["definitions"], packs.Calls);
    }

    [Fact]
    public async Task LayaOffersOnlyRegisteredCompatiblePacksAndOpensWithNone()
    {
        Assert.True(Compose().TryGetProvider("darkspyre.tensor.laya", out ITensorEngineProvider? laya));
        Assert.Empty(await laya.GetOfferedModelsAsync(new RecordingPacks(TensorLlmModels.Qwen3FourBillionQ4Km)));

        TensorSettingsManager manager = laya.CreateSettingsManager(new RecordingTarget(), new RecordingPacks(), []);
        TensorSettings settings = await manager.OpenAsync(new(SetupChangeIntent.Create, "entry", null));
        Assert.Empty((await manager.QueryChoicesAsync(settings, "model", new())).Choices);
    }

    [Fact]
    public async Task OfferedModelsCombineBuiltInsWithRegisteredPacksOfTheSameFamily()
    {
        TensorModelPackDefinition variant = TensorLlmModels.Qwen3FourBillionQ4Km with { Key = new("custom-qwen", "r1") };
        var packs = new RecordingPacks(TensorLlmModels.Qwen3FourBillionQ4Km, variant, TensorImageModels.Supra2Image);

        IReadOnlyList<TensorModelPackDefinition> offered = await new QwenEngineProvider().GetOfferedModelsAsync(packs);

        Assert.Equal([TensorLlmModels.Qwen3FourBillionQ4Km.Key, variant.Key], offered.Select(model => model.Key));
    }

    [Fact]
    public async Task ARegisteredManifestNeverReplacesABuiltInAtTheSameKey()
    {
        TensorModelPackDefinition conflicting = TensorLlmModels.Qwen3FourBillionQ4Km with { License = "other" };
        await Assert.ThrowsAsync<ArgumentException>(() => new QwenEngineProvider().GetOfferedModelsAsync(new RecordingPacks(conflicting)));
    }

    [Fact]
    public async Task AnIncompatibleRegisteredPackOfTheFamilyFails()
    {
        TensorModelPackDefinition cuda = TensorSpeechModels.Whisper[0] with
        {
            Key = new("whisper-cuda", "r1"),
            SupportedBackends = new HashSet<TensorBackendKind> { TensorBackendKind.Cuda }
        };
        await Assert.ThrowsAsync<ArgumentException>(() => new WhisperEngineProvider().GetOfferedModelsAsync(new RecordingPacks(cuda)));
    }

    [Fact]
    public void AnInstanceOfAnotherImplementationOrCapabilityIsRejected()
    {
        var packs = new RecordingPacks();
        var otherLlm = new SyntheticLlm(new SyntheticLlmProvider().Definition.Setup.Implementation, TensorBackendKind.Cpu);
        IReadOnlyList<TensorModelPackDefinition> models = [TensorLlmModels.Qwen3FourBillionQ4Km];

        Assert.Throws<ArgumentException>(() => new QwenEngineProvider().CreateSettingsManager(new RecordingTarget(), packs, models, otherLlm));
        Assert.Throws<ArgumentException>(() => new QwenEngineProvider().CreateSettingsManager(new RecordingTarget(), packs, models, new BareInstance()));
        Assert.Throws<ArgumentException>(() => new SupraEngineProvider().CreateSettingsManager(new RecordingTarget(), packs, [], otherLlm));
        Assert.Throws<ArgumentException>(() => new LayaEngineProvider().CreateSettingsManager(new RecordingTarget(), packs, [], otherLlm));
        Assert.Throws<ArgumentException>(() => new WhisperEngineProvider().CreateSettingsManager(new RecordingTarget(), packs, [], otherLlm));
        Assert.Empty(packs.Calls);
    }

    [Theory]
    [InlineData("darkspyre.tensor.qwen", typeof(ITensorLlmInference))]
    [InlineData("darkspyre.tensor.supra2-img", typeof(Image.ITensorImageInference))]
    [InlineData("darkspyre.tensor.laya", typeof(Decision.ITensorDecisionInference))]
    [InlineData("darkspyre.tensor.whisper", typeof(STT.ITensorSpeechToTextInference))]
    public async Task CreatedInstancesImplementTheirCapabilityAndAreNotStarted(string id, Type capability)
    {
        Assert.True(Compose().TryGetProvider(id, out ITensorEngineProvider? provider));
        var packs = new RecordingPacks();

        await using ITensorModelInstance instance = provider.CreateInstance(packs, TensorBackendKind.Cpu);

        Assert.IsAssignableFrom(capability, instance);
        Assert.Equal(TensorBackendKind.Cpu, instance.RequestedBackend);
        Assert.Null((await instance.GetObservationAsync()).Model);
        TensorSettingsManager manager = provider.CreateSettingsManager(new RecordingTarget(), packs, [], instance);
        Assert.NotNull(manager);
        Assert.Empty(packs.Calls);
    }

    /// <summary>The generic host path: resolve by ID, offer models, create the instance and its settings.</summary>
    private static async Task<(ITensorModelInstance, TensorSettingsManager, TensorSettings)> OpenAsync(TensorEngineRegistry registry,
        string id, ITensorModelPackManager packs, ISetupCommitTarget<TensorSetupChanges> target)
    {
        Assert.True(registry.TryGetProvider(id, out ITensorEngineProvider? provider));
        IReadOnlyList<TensorModelPackDefinition> offered = await provider.GetOfferedModelsAsync(packs);
        ITensorModelInstance instance = provider.CreateInstance(packs, TensorBackendKind.Cpu);
        TensorSettingsManager manager = provider.CreateSettingsManager(target, packs, offered, instance);
        return (instance, manager, await manager.OpenAsync(new(SetupChangeIntent.Create, "entry", null)));
    }

    private static SetupChoiceField Field(TensorSettings settings, string id) =>
        Assert.Single(settings.Steps[0].Sections[0].Fields.OfType<SetupChoiceField>(), field => field.Description.Id == id);

    private static SetupActionDescriptor Action(TensorSettings settings, SetupActionPurpose purpose) =>
        Assert.Single(settings.Actions, action => action.Purpose == purpose);
}
