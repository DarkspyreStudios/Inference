using System.Text.Json;
using Darkspyre.Inference.Common.Setup;
using static Darkspyre.Inference.Connect.Audio.Tests.TangoFluxTests;

namespace Darkspyre.Inference.Connect.Audio.Tests;

public sealed class AudioSetupTests
{
    [Fact]
    public async Task GenericTypedFieldsSaveCreateWithoutConnecting()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("No network expected."));
        using var http = new HttpClient(handler);
        var target = new Target();
        var (manager, settings) = await CreateAsync(http, target);
        Assert.False(settings.Status.Configured.Value);
        Field<SetupUriField>(settings, "endpoint").SetValue(new("https://audio.test"));
        Field<SetupDecimalField>(settings, "duration").SetValue(2.5m);
        Field<SetupDecimalField>(settings, "timeout").SetValue(12.5m);
        Field<SetupIntegerField>(settings, "steps").SetValue(20);
        Field<SetupDecimalField>(settings, "guidance").SetValue(3.5m);
        Field<SetupIntegerField>(settings, "seed").SetValue(42);
        Field<SetupChoiceField>(settings, "cache").SetValue("bypass");
        Assert.True(settings.Validate().IsValid);
        Assert.Equal(SetupCommitState.Committed, (await manager.SaveAsync(settings)).State);
        AudioSetupChanges changes = Assert.IsType<AudioSetupChanges>(target.Changes);
        Assert.Equal(SetupChangeIntent.Create, changes.Identity.Intent);
        Assert.Null(changes.Identity.ExpectedVersion);
        Assert.Equal(new Uri("https://audio.test"), changes.Configuration.Connection.Endpoint);
        Assert.Equal(TimeSpan.FromSeconds(12.5), changes.Configuration.Connection.RequestTimeout);
        Assert.Equal(TimeSpan.FromSeconds(2.5), changes.Configuration.Duration);
        Assert.Equal(20, changes.Configuration.Steps);
        Assert.Equal(3.5, changes.Configuration.GuidanceScale);
        Assert.Equal(42, changes.Configuration.Seed);
        Assert.False(changes.Configuration.UseCache);
        Assert.Null(settings.Status.Ready.Value);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(SetupSecretEditKind.Keep)]
    [InlineData(SetupSecretEditKind.Replace)]
    [InlineData(SetupSecretEditKind.Clear)]
    public async Task EditProtectsSecretsAndPreservesExpectedVersion(SetupSecretEditKind kind)
    {
        using var http = new HttpClient();
        var target = new Target();
        var (manager, settings) = await EditAsync(http, target);
        SetupSecretField secret = Assert.Single(Assert.Single(Headers(settings).Rows).Fields.OfType<SetupSecretField>());
        Assert.True(secret.HasValue);
        Assert.Equal(SetupSecretEditKind.Keep, secret.Change);
        secret.SetValue(kind switch { SetupSecretEditKind.Replace => SetupSecretEdit.Replace("replacement"), SetupSecretEditKind.Clear => SetupSecretEdit.Clear, _ => SetupSecretEdit.Keep });
        Assert.Equal(SetupCommitState.Committed, (await manager.SaveAsync(settings)).State);
        AudioSetupChanges changes = Assert.IsType<AudioSetupChanges>(target.Changes);
        Assert.Equal("v1", changes.Identity.ExpectedVersion);
        Assert.Empty(changes.Configuration.Connection.Headers);
        Assert.Equal(TimeSpan.FromSeconds(12.5), changes.Configuration.Connection.RequestTimeout);
        if (kind == SetupSecretEditKind.Replace) { Assert.Equal("replacement", changes.Headers.Set["Authorization"]); }
        else { Assert.Empty(changes.Headers.Set); }
        if (kind == SetupSecretEditKind.Clear) { Assert.Equal("Authorization", Assert.Single(changes.Headers.Remove)); }
        else { Assert.Empty(changes.Headers.Remove); }
    }

    [Fact]
    public async Task RemovingOrRenamingHeadersProducesExplicitMutations()
    {
        using var http = new HttpClient();
        var target = new Target();
        {
            var (manager, settings) = await EditAsync(http, target);
            SetupFieldRow row = Assert.Single(Headers(settings).Rows);
            Assert.Single(row.Fields.OfType<SetupTextField>()).SetValue("X-Api-Key");
            await manager.SaveAsync(settings);
            Assert.Equal("Authorization", Assert.Single(target.Changes!.Headers.Remove));
            Assert.Equal("protected", target.Changes.Headers.Set["X-Api-Key"]);
        }
        {
            var (manager, settings) = await EditAsync(http, target);
            SetupRepeatableGroupField headers = Headers(settings);
            headers.Remove(Assert.Single(headers.Rows).Id);
            await manager.SaveAsync(settings);
            Assert.Equal("Authorization", Assert.Single(target.Changes!.Headers.Remove));
            Assert.Empty(target.Changes.Headers.Set);
        }
    }

    [Fact]
    public async Task AddingHeadersAndRejectingDuplicatesUsesOnlyGenericFields()
    {
        using var http = new HttpClient();
        var target = new Target();
        var (manager, settings) = await EditAsync(http, target);
        Headers(settings).Add();
        SetupFieldRow row = Headers(settings).Rows[1];
        string id = row.Id;
        Assert.Single(row.Fields.OfType<SetupTextField>()).SetValue("authorization");
        Assert.Single(Headers(settings).Rows[1].Fields.OfType<SetupSecretField>()).SetValue(SetupSecretEdit.Replace("duplicate"));
        Assert.False(settings.Validate().IsValid);
        Assert.Equal(SetupCommitState.Invalid, (await manager.SaveAsync(settings)).State);
        Assert.Null(target.Changes);
        Assert.Single(Headers(settings).Rows.Single(item => item.Id == id).Fields.OfType<SetupTextField>()).SetValue("X-Api-Key");
        Assert.True(settings.Validate().IsValid);
        await manager.SaveAsync(settings);
        Assert.Equal("duplicate", target.Changes!.Headers.Set["X-Api-Key"]);
        Assert.Single(target.Changes.Headers.Set);
        Assert.Empty(target.Changes.Headers.Remove);
    }

    [Fact]
    public async Task ChoicesArePagedAndSearchedWithoutNetwork()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("No network expected."));
        using var http = new HttpClient(handler);
        var (manager, settings) = await CreateAsync(http, new Target());
        SetupChoiceField field = Field<SetupChoiceField>(settings, "cache");
        Task<SetupChoicePage> Query(SetupChoiceQuery query) => manager.QueryChoicesAsync(settings, "cache", query);
        SetupChoicePage page = await Query(new(PageSize: 1));
        Assert.Equal("default", Assert.Single(page.Choices).Value);
        Assert.NotNull(page.Continuation);
        SetupChoicePage next = await Query(new(Continuation: page.Continuation, PageSize: 1));
        Assert.Equal("use", Assert.Single(next.Choices).Value);
        Assert.Equal("bypass", Assert.Single((await Query(new("Bypass"))).Choices).Value);
        Assert.Null((await Query(new(Continuation: int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture)))).Continuation);
        Assert.Equal(SetupChoiceState.Failed, (await Query(new(Continuation: "invalid"))).State);
        Assert.Equal(SetupEditState.Invalid, field.SetValue("unsupported").State);
        Assert.Equal(SetupEditState.Applied, field.SetValue("use").State);
        Assert.Equal(SetupEditState.Applied, field.SetValue("bypass").State);
        Assert.Equal("bypass", Field<SetupChoiceField>(settings, "cache").Value);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task TestIsExplicitAndUsesDraftValuesThenEditsInvalidateReadiness()
    {
        using var handler = new Handler(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/generate", request.RequestUri!.AbsolutePath);
            Assert.Equal("replacement", Assert.Single(request.Headers.GetValues("Authorization")));
            using JsonDocument body = await JsonDocument.ParseAsync(await request.Content!.ReadAsStreamAsync(token), cancellationToken: token);
            Assert.Equal(7.5, body.RootElement.GetProperty("duration").GetDouble());
            Assert.Equal(25, body.RootElement.GetProperty("steps").GetInt32());
            Assert.Equal(42, body.RootElement.GetProperty("seed").GetInt32());
            Assert.False(body.RootElement.GetProperty("useCache").GetBoolean());
            return Wave();
        });
        using var http = new HttpClient(handler);
        var (manager, settings) = await EditAsync(http, new Target());
        Field<SetupDecimalField>(settings, "duration").SetValue(7.5m);
        Field<SetupIntegerField>(settings, "steps").SetValue(25);
        Field<SetupIntegerField>(settings, "seed").SetValue(42);
        Field<SetupChoiceField>(settings, "cache").SetValue("bypass");
        Assert.Single(Assert.Single(Headers(settings).Rows).Fields.OfType<SetupSecretField>()).SetValue(SetupSecretEdit.Replace("replacement"));
        SetupActionDescriptor test = Assert.Single(settings.Actions);
        Assert.Equal(SetupActionPurpose.Test, test.Purpose);
        Assert.Equal(SetupSideEffect.Network | SetupSideEffect.PotentialBilling, test.SideEffects);
        Assert.Equal(0, handler.Calls);
        Assert.Equal(SetupActionState.Completed, (await manager.RunAsync(settings, test.Id)).State);
        Assert.True(settings.Status.Ready.Value);
        Assert.True(settings.Status.Verified.Value);
        Field<SetupUriField>(settings, "endpoint").SetValue(new("https://elsewhere.test"));
        Assert.True(settings.Status.Ready.IsStale);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task InvalidFieldsBlockSaveAndTestWithoutNumericOverflow()
    {
        using var http = new HttpClient();
        var target = new Target();
        var (manager, settings) = await CreateAsync(http, target);
        Field<SetupDecimalField>(settings, "duration").SetValue(decimal.MaxValue);
        Field<SetupIntegerField>(settings, "steps").SetValue(long.MaxValue);
        Field<SetupIntegerField>(settings, "seed").SetValue(long.MinValue);
        Field<SetupDecimalField>(settings, "guidance").SetValue(0);
        Field<SetupDecimalField>(settings, "timeout").SetValue(decimal.MaxValue);
        Assert.Equal(5, settings.Validate().Issues.Count);
        Assert.Equal(SetupCommitState.Invalid, (await manager.SaveAsync(settings)).State);
        Assert.False(Assert.Single(settings.Actions).IsEnabled);
        Assert.Equal(SetupActionState.Unavailable, (await manager.RunAsync(settings, Assert.Single(settings.Actions).Id)).State);
        Assert.Null(target.Changes);
    }

    [Fact]
    public async Task UnsavedEditNeverReachesTheHostAndConflictRemainsEditable()
    {
        using var http = new HttpClient();
        var target = new Target { Result = new(SetupCommitState.Conflict) };
        var (manager, settings) = await EditAsync(http, target);
        Assert.Equal(SetupCommitState.Conflict, (await manager.SaveAsync(settings)).State);
        Assert.False(settings.IsSaved);
        Assert.Equal(SetupEditState.Applied, Field<SetupIntegerField>(settings, "steps").SetValue(30).State);
        var unsavedTarget = new Target();
        var (_, unsaved) = await EditAsync(http, unsavedTarget);
        Assert.Single(Assert.Single(Headers(unsaved).Rows).Fields.OfType<SetupSecretField>()).SetValue(SetupSecretEdit.Clear);
        Assert.True(unsaved.HasUnsavedChanges);
        Assert.Null(unsavedTarget.Changes);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(79228162514264337593543950335d)]
    public async Task InvalidSavedGuidanceCanBeDisplayedAndRepaired(double guidance)
    {
        using var http = new HttpClient();
        var target = new Target();
        AudioSettingsManager manager = Service(http).CreateSettingsManager(target);
        AudioSettings settings = await manager.OpenAsync(AudioTransport.TangoFluxHttp,
            new(SetupChangeIntent.Edit, "connection", "v1"), new(Connection()) { GuidanceScale = guidance });
        SetupDecimalField field = Field<SetupDecimalField>(settings, "guidance");
        Assert.False(settings.Validate().IsValid);
        Assert.Equal(SetupEditState.Applied, field.SetValue(null).State);
        Assert.True(settings.Validate().IsValid);
        await manager.SaveAsync(settings);
        Assert.Null(target.Changes!.Configuration.GuidanceScale);
    }

    [Fact]
    public async Task FailedRetestDoesNotRetainSuccessfulReadiness()
    {
        int calls = 0;
        using var handler = new Handler((_, _) => Task.FromResult(++calls == 1 ? Wave()
            : new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError) { Content = new StringContent("private response") }));
        using var http = new HttpClient(handler);
        var (manager, settings) = await CreateAsync(http, new Target());
        Assert.Equal(SetupActionState.Completed, (await manager.RunAsync(settings, Assert.Single(settings.Actions).Id)).State);
        Assert.True(settings.Status.Ready.Value);
        SetupActionResult failure = await manager.RunAsync(settings, Assert.Single(settings.Actions).Id);
        Assert.Equal(SetupActionState.Failed, failure.State);
        Assert.Null(settings.Status.Ready.Value);
        Assert.Null(settings.Status.Verified.Value);
        Assert.DoesNotContain("private response", failure.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task CancellingAnActiveTestCancelsTheRequestAndClearsReadiness()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(async (_, token) =>
        {
            using CancellationTokenRegistration registration = token.Register(() => cancelled.TrySetResult());
            started.SetResult();
            await release.Task;
            token.ThrowIfCancellationRequested();
            return Wave();
        });
        using var http = new HttpClient(handler);
        var target = new Target();
        var (manager, settings) = await CreateAsync(http, target);
        using var cancellation = new CancellationTokenSource();
        Task<SetupActionResult> running = manager.RunAsync(settings, Assert.Single(settings.Actions).Id, cancellationToken: cancellation.Token);
        await started.Task;
        await cancellation.CancelAsync();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.SetResult();
        Assert.Equal(SetupActionState.Cancelled, (await running).State);
        Assert.Null(target.Changes);
        Assert.Null(settings.Status.Ready.Value);
    }

    [Fact]
    public async Task DirtyTracksRevertsForNumericDefaultsAndProtectedHeaderEdits()
    {
        using var http = new HttpClient();
        var (manager, settings) = await EditAsync(http, new Target());
        Assert.False(settings.HasUnsavedChanges);
        Field<SetupDecimalField>(settings, "timeout").SetValue(13.5m);
        Assert.True(settings.HasUnsavedChanges);
        Field<SetupDecimalField>(settings, "timeout").SetValue(12.5m);
        Assert.False(settings.HasUnsavedChanges);
        SetupSecretField Secret() => Assert.Single(Assert.Single(Headers(settings).Rows).Fields.OfType<SetupSecretField>());
        Secret().SetValue(SetupSecretEdit.Clear);
        Assert.True(settings.HasUnsavedChanges);
        Secret().SetValue(SetupSecretEdit.Keep);
        Assert.False(settings.HasUnsavedChanges);
        Assert.Equal(SetupChoiceCapabilities.Bounded, Field<SetupChoiceField>(settings, "cache").Capabilities);
    }

    private static AudioProviderService Service(HttpClient http) => new([new TangoFluxHttpProvider(http)]);
    private static async Task<(AudioSettingsManager Manager, AudioSettings Settings)> CreateAsync(HttpClient http, Target target)
    {
        AudioSettingsManager manager = Service(http).CreateSettingsManager(target);
        return (manager, await manager.OpenAsync(AudioTransport.TangoFluxHttp, new(SetupChangeIntent.Create, "new", null)));
    }
    private static async Task<(AudioSettingsManager Manager, AudioSettings Settings)> EditAsync(HttpClient http, Target target)
    {
        AudioSettingsManager manager = Service(http).CreateSettingsManager(target);
        return (manager, await manager.OpenAsync(AudioTransport.TangoFluxHttp, new(SetupChangeIntent.Edit, "connection", "v1"), new(Connection() with
        { Headers = new Dictionary<string, string> { ["Authorization"] = "protected" }, RequestTimeout = TimeSpan.FromSeconds(12.5) })));
    }
    private static T Field<T>(InferenceSettings settings, string id) where T : SetupField =>
        Assert.IsType<T>(Assert.Single(settings.Steps.SelectMany(step => step.Sections).SelectMany(section => section.Fields), field => field.Description.Id == id));
    private static SetupRepeatableGroupField Headers(InferenceSettings settings) => Field<SetupRepeatableGroupField>(settings, "headers");
    private sealed class Target : ISetupCommitTarget<AudioSetupChanges>
    {
        public AudioSetupChanges? Changes { get; private set; }
        public SetupCommitResult Result { get; init; } = new(SetupCommitState.Committed, "v2");
        public Task<SetupCommitResult> CommitAsync(AudioSetupChanges changes, CancellationToken cancellationToken = default)
        { Changes = changes; return Task.FromResult(Result); }
    }
}
