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
        await using AudioSetupSession session = Service(http).CreateSetupSession(AudioTransport.TangoFluxHttp, new(SetupChangeIntent.Create, "new", null), target);
        Assert.False(session.Snapshot.Status.Configured.Value);
        Field<SetupUriField>(session, "endpoint").SetValue(new("https://audio.test"));
        Field<SetupDecimalField>(session, "duration").SetValue(2.5m);
        Field<SetupDecimalField>(session, "timeout").SetValue(12.5m);
        Field<SetupIntegerField>(session, "steps").SetValue(20);
        Field<SetupDecimalField>(session, "guidance").SetValue(3.5m);
        Field<SetupIntegerField>(session, "seed").SetValue(42);
        Field<SetupChoiceField>(session, "cache").SetValue("bypass");
        Assert.True((await session.ValidateAsync()).IsValid);
        Assert.Equal(SetupCommitState.Committed, (await session.CommitAsync()).State);
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
        Assert.Null(session.Snapshot.Status.Ready.Value);
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
        await using AudioSetupSession session = EditSession(http, target);
        SetupSecretField secret = Assert.Single(Assert.Single(Headers(session).Rows).Fields.OfType<SetupSecretField>());
        Assert.True(secret.HasValue);
        Assert.Equal(SetupSecretEditKind.Keep, secret.Change);
        secret.SetValue(kind switch { SetupSecretEditKind.Replace => SetupSecretEdit.Replace("replacement"), SetupSecretEditKind.Clear => SetupSecretEdit.Clear, _ => SetupSecretEdit.Keep });
        Assert.Equal(SetupCommitState.Committed, (await session.CommitAsync()).State);
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
        await using (AudioSetupSession session = EditSession(http, target))
        {
            SetupFieldRow row = Assert.Single(Headers(session).Rows);
            Assert.Single(row.Fields.OfType<SetupTextField>()).SetValue("X-Api-Key");
            await session.CommitAsync();
            Assert.Equal("Authorization", Assert.Single(target.Changes!.Headers.Remove));
            Assert.Equal("protected", target.Changes.Headers.Set["X-Api-Key"]);
        }
        await using (AudioSetupSession session = EditSession(http, target))
        {
            SetupRepeatableGroupField headers = Headers(session);
            headers.Remove(Assert.Single(headers.Rows).Id);
            await session.CommitAsync();
            Assert.Equal("Authorization", Assert.Single(target.Changes!.Headers.Remove));
            Assert.Empty(target.Changes.Headers.Set);
        }
    }

    [Fact]
    public async Task AddingHeadersAndRejectingDuplicatesUsesOnlyGenericFields()
    {
        using var http = new HttpClient();
        var target = new Target();
        await using AudioSetupSession session = EditSession(http, target);
        Headers(session).Add();
        SetupFieldRow row = Headers(session).Rows[1];
        string id = row.Id;
        Assert.Single(row.Fields.OfType<SetupTextField>()).SetValue("authorization");
        Assert.Single(Headers(session).Rows[1].Fields.OfType<SetupSecretField>()).SetValue(SetupSecretEdit.Replace("duplicate"));
        Assert.False((await session.ValidateAsync()).IsValid);
        Assert.Equal(SetupCommitState.Invalid, (await session.CommitAsync()).State);
        Assert.Null(target.Changes);
        Assert.Single(Headers(session).Rows.Single(item => item.Id == id).Fields.OfType<SetupTextField>()).SetValue("X-Api-Key");
        Assert.True((await session.ValidateAsync()).IsValid);
        await session.CommitAsync();
        Assert.Equal("duplicate", target.Changes!.Headers.Set["X-Api-Key"]);
        Assert.Single(target.Changes.Headers.Set);
        Assert.Empty(target.Changes.Headers.Remove);
    }

    [Fact]
    public async Task ChoicesArePagedSearchedAndRevisionCheckedWithoutNetwork()
    {
        using var http = new HttpClient();
        await using AudioSetupSession session = CreateSession(http, new Target());
        SetupChoiceField field = Field<SetupChoiceField>(session, "cache");
        long revision = session.Snapshot.Revision;
        SetupChoicePage page = await field.GetChoicesAsync(new(revision, PageSize: 1));
        Assert.Equal("default", Assert.Single(page.Choices).Value);
        Assert.NotNull(page.Continuation);
        SetupChoicePage next = await field.GetChoicesAsync(new(revision, Continuation: page.Continuation, PageSize: 1));
        Assert.Equal("use", Assert.Single(next.Choices).Value);
        Assert.Equal("bypass", Assert.Single((await field.GetChoicesAsync(new(revision, "Bypass"))).Choices).Value);
        Assert.Null((await field.GetChoicesAsync(new(revision, Continuation: int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture)))).Continuation);
        Assert.Equal(SetupChoiceState.Failed, (await field.GetChoicesAsync(new(revision, Continuation: "invalid"))).State);
        Assert.Equal(SetupEditState.Invalid, field.SetValue("unsupported").State);
        Assert.Equal(SetupEditState.Applied, field.SetValue("use").State);
        Assert.Equal(SetupEditState.Stale, field.SetValue("bypass").State);
        Assert.Equal(SetupChoiceState.Stale, (await field.GetChoicesAsync(new(session.Snapshot.Revision))).State);
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
        await using AudioSetupSession session = EditSession(http, new Target());
        Field<SetupDecimalField>(session, "duration").SetValue(7.5m);
        Field<SetupIntegerField>(session, "steps").SetValue(25);
        Field<SetupIntegerField>(session, "seed").SetValue(42);
        Field<SetupChoiceField>(session, "cache").SetValue("bypass");
        Assert.Single(Assert.Single(Headers(session).Rows).Fields.OfType<SetupSecretField>()).SetValue(SetupSecretEdit.Replace("replacement"));
        ISetupAction test = Assert.Single(session.Snapshot.Actions);
        Assert.Equal(SetupActionPurpose.Test, test.Descriptor.Purpose);
        Assert.Equal(SetupSideEffect.Network | SetupSideEffect.PotentialBilling, test.Descriptor.SideEffects);
        Assert.Equal(0, handler.Calls);
        Assert.Equal(SetupActionState.Completed, (await test.ExecuteAsync()).State);
        Assert.True(session.Snapshot.Status.Ready.Value);
        Assert.True(session.Snapshot.Status.Verified.Value);
        Field<SetupUriField>(session, "endpoint").SetValue(new("https://elsewhere.test"));
        Assert.True(session.Snapshot.Status.Ready.IsStale);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task InvalidFieldsBlockSaveAndTestWithoutNumericOverflow()
    {
        using var http = new HttpClient();
        var target = new Target();
        await using AudioSetupSession session = CreateSession(http, target);
        Field<SetupDecimalField>(session, "duration").SetValue(decimal.MaxValue);
        Field<SetupIntegerField>(session, "steps").SetValue(long.MaxValue);
        Field<SetupIntegerField>(session, "seed").SetValue(long.MinValue);
        Field<SetupDecimalField>(session, "guidance").SetValue(0);
        Field<SetupDecimalField>(session, "timeout").SetValue(decimal.MaxValue);
        Assert.Equal(5, (await session.ValidateAsync()).Issues.Count);
        Assert.Equal(SetupCommitState.Invalid, (await session.CommitAsync()).State);
        Assert.False(Assert.Single(session.Snapshot.Actions).Descriptor.IsEnabled);
        Assert.Equal(SetupActionState.Unavailable, (await Assert.Single(session.Snapshot.Actions).ExecuteAsync()).State);
        Assert.Null(target.Changes);
    }

    [Fact]
    public async Task CancelledEditDoesNotSaveAndCommitConflictRemainsEditable()
    {
        using var http = new HttpClient();
        var target = new Target { Result = new(SetupCommitState.Conflict) };
        await using AudioSetupSession session = EditSession(http, target);
        Assert.Equal(SetupCommitState.Conflict, (await session.CommitAsync()).State);
        Assert.False(session.Snapshot.IsClosed);
        Assert.Equal(SetupEditState.Applied, Field<SetupIntegerField>(session, "steps").SetValue(30).State);
        var cancelledTarget = new Target();
        AudioSetupSession cancelled = EditSession(http, cancelledTarget);
        Assert.Single(Assert.Single(Headers(cancelled).Rows).Fields.OfType<SetupSecretField>()).SetValue(SetupSecretEdit.Clear);
        await cancelled.DisposeAsync();
        Assert.Null(cancelledTarget.Changes);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(79228162514264337593543950335d)]
    public async Task InvalidSavedGuidanceCanBeDisplayedAndRepaired(double guidance)
    {
        using var http = new HttpClient();
        var target = new Target();
        await using AudioSetupSession session = Service(http).CreateSetupSession(AudioTransport.TangoFluxHttp,
            new(SetupChangeIntent.Edit, "connection", "v1"), target, new(Connection()) { GuidanceScale = guidance });
        SetupDecimalField field = Field<SetupDecimalField>(session, "guidance");
        Assert.False((await session.ValidateAsync()).IsValid);
        Assert.Equal(SetupEditState.Applied, field.SetValue(null).State);
        Assert.True((await session.ValidateAsync()).IsValid);
        await session.CommitAsync();
        Assert.Null(target.Changes!.Configuration.GuidanceScale);
    }

    [Fact]
    public async Task FailedRetestDoesNotRetainSuccessfulReadiness()
    {
        int calls = 0;
        using var handler = new Handler((_, _) => Task.FromResult(++calls == 1 ? Wave()
            : new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError) { Content = new StringContent("private response") }));
        using var http = new HttpClient(handler);
        await using AudioSetupSession session = CreateSession(http, new Target());
        Assert.Equal(SetupActionState.Completed, (await Assert.Single(session.Snapshot.Actions).ExecuteAsync()).State);
        Assert.True(session.Snapshot.Status.Ready.Value);
        SetupActionResult failure = await Assert.Single(session.Snapshot.Actions).ExecuteAsync();
        Assert.Equal(SetupActionState.Failed, failure.State);
        Assert.Null(session.Snapshot.Status.Ready.Value);
        Assert.Null(session.Snapshot.Status.Verified.Value);
        Assert.DoesNotContain("private response", failure.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task SessionDisposalCancelsAndWaitsForActiveTest()
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
        AudioSetupSession session = CreateSession(http, target);
        Task<SetupActionResult> running = Assert.Single(session.Snapshot.Actions).ExecuteAsync();
        await started.Task;
        Assert.Equal(SetupCommitState.Busy, (await session.CommitAsync()).State);
        Assert.Equal(SetupEditState.Busy, Field<SetupIntegerField>(session, "steps").SetValue(30).State);
        Task disposal = session.DisposeAsync().AsTask();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        bool wasWaiting = !disposal.IsCompleted;
        release.SetResult();
        await disposal;
        Assert.True(wasWaiting);
        Assert.Equal(SetupActionState.Cancelled, (await running).State);
        Assert.Null(target.Changes);
        Assert.Null(session.Snapshot.Status.Ready.Value);
    }

    private static AudioProviderService Service(HttpClient http) => new([new TangoFluxHttpProvider(http)]);
    private static AudioSetupSession CreateSession(HttpClient http, Target target) => Service(http).CreateSetupSession(AudioTransport.TangoFluxHttp,
        new(SetupChangeIntent.Create, "new", null), target);
    private static AudioSetupSession EditSession(HttpClient http, Target target) => Service(http).CreateSetupSession(AudioTransport.TangoFluxHttp,
        new(SetupChangeIntent.Edit, "connection", "v1"), target, new(Connection() with
        { Headers = new Dictionary<string, string> { ["Authorization"] = "protected" }, RequestTimeout = TimeSpan.FromSeconds(12.5) }));
    private static T Field<T>(IInferenceSetupSession session, string id) where T : SetupField =>
        Assert.IsType<T>(Assert.Single(session.Snapshot.Steps.SelectMany(step => step.Sections).SelectMany(section => section.Fields), field => field.Description.Id == id));
    private static SetupRepeatableGroupField Headers(IInferenceSetupSession session) => Field<SetupRepeatableGroupField>(session, "headers");
    private sealed class Target : ISetupCommitTarget<AudioSetupChanges>
    {
        public AudioSetupChanges? Changes { get; private set; }
        public SetupCommitResult Result { get; init; } = new(SetupCommitState.Committed, "v2");
        public Task<SetupCommitResult> CommitAsync(AudioSetupChanges changes, CancellationToken cancellationToken = default)
        { Changes = changes; return Task.FromResult(Result); }
    }
}
