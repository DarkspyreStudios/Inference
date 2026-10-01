using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class ConnectionAccessTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    private static LlmCredential Credential(string access, string refresh) => new()
    { OAuth = new() { AccessToken = access, RefreshToken = refresh, ExpiresAtUnixSeconds = 0, AccountId = "account" } };
    private static LlmConnection Connection => new()
    {
        Version = "v1",
        Transport = LlmTransport.OpenAiCodex,
        Endpoint = "https://provider.invalid/v1",
        Credential = Credential("protected-old", "protected-refresh").Serialize()
    };
    private static LlmEmbeddingRequest Embedding => new() { ModelId = "embedding", Inputs = ["input"] };

    [Fact]
    public async Task RotatedCredentialUsesExpectedVersionAndAuthorizationReadsCurrentConnectionEachTime()
    {
        using var source = new Source(new Gate());
        using var auth = new Authenticator(new Gate());
        await using var access = new LlmConnectionAccess(source, [auth]);
        var authorization = new LlmConnectionAuthorization("connection", access);
        Assert.IsAssignableFrom<IProviderAuthorization>(authorization);
        ProviderAuthorizationHeader first = await authorization.GetAsync();
        ProviderAuthorizationHeader second = await authorization.GetAsync();
        Assert.Equal("Bearer", first.Scheme);
        Assert.Equal("protected-fresh", first.Value);
        Assert.Equal(first, second);
        Assert.DoesNotContain("protected", first.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, source.Reads);
        Assert.Equal(2, auth.Calls);
        Assert.Equal(1, source.Writes);
        Assert.Equal("v1", Assert.Single(source.ExpectedVersions));
        Assert.Equal("protected-rotated", LlmCredential.Parse(source.Current!.Credential)!.OAuth!.RefreshToken);
        Assert.Equal("account", LlmCredential.Parse(source.Current.Credential)!.OAuth!.AccountId);
        Assert.False(source.Disposed);
        Assert.False(auth.Disposed);
    }

    [Fact]
    public async Task AlreadyFreshOAuthCredentialIsNotWritten()
    {
        using var source = new Source(new Gate());
        using var auth = new Authenticator(new Gate()) { Rotate = false };
        await using var access = new LlmConnectionAccess(source, [auth]);
        Assert.Same(source.Current, await access.AcquireAsync("connection"));
        Assert.Equal(1, auth.Calls);
        Assert.Equal(0, source.Writes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("malformed-protected-credential")]
    [InlineData("{\"apiKey\":\"protected-api-key\"}")]
    [InlineData("{\"secretHeaders\":{\"x-api-key\":\"protected-header\"}}")]
    public async Task NonOAuthCredentialsRemainUnchangedWithoutRefresh(string? credential)
    {
        using var source = new Source(new Gate()) { Current = Connection with { Credential = credential } };
        using var auth = new Authenticator(new Gate());
        await using var access = new LlmConnectionAccess(source, [auth]);
        Assert.Same(source.Current, await access.AcquireAsync("connection"));
        Assert.Equal(0, auth.Calls);
        Assert.Equal(0, source.Writes);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new LlmConnectionAuthorization("connection", access).GetAsync().AsTask());
        Assert.Equal("The connection requires an OAuth sign-in.", error.Message);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task AnUnregisteredAuthenticatorDoesNotFabricateARefresh()
    {
        using var source = new Source(new Gate());
        await using var access = new LlmConnectionAccess(source, []);
        Assert.Same(source.Current, await access.AcquireAsync("connection"));
        Assert.Equal(0, source.Writes);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("version")]
    [InlineData("identity")]
    public async Task InvalidConnectionResolutionDoesNotStartRefresh(string variant)
    {
        using var source = new Source(new Gate());
        using var auth = new Authenticator(new Gate());
        if (variant == "missing") source.Current = null;
        if (variant == "version") source.Current = Connection with { Version = "" };
        await using var access = new LlmConnectionAccess(source, [auth]);
        Exception? error = await Record.ExceptionAsync(() => access.AcquireAsync(variant == "identity" ? "" : "connection"));
        Assert.True(error is ArgumentException or InvalidOperationException, error?.ToString());
        Assert.Equal(0, auth.Calls);
        Assert.Equal(0, source.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARefreshCannotOverwriteReplacementOrResurrectDeletion(bool deleted)
    {
        var gate = new Gate { Boundary = "refresh" };
        using var source = new Source(gate);
        using var auth = new Authenticator(gate);
        await using var access = new LlmConnectionAccess(source, [auth]);
        Task<LlmConnection> request = access.AcquireAsync("connection");
        try
        {
            await gate.Entered.Task.WaitAsync(Bound);
            LlmConnection? replacement = deleted ? null : Connection with
            { Version = "v2", Credential = Credential("protected-replacement", "protected-new-refresh").Serialize() };
            source.Current = replacement;
            gate.Release.TrySetResult();
            await Assert.ThrowsAsync<ConnectionChangedException>(() => request);
            Assert.Same(replacement, source.Current);
            Assert.Equal("v1", Assert.Single(source.ExpectedVersions));
            Assert.Equal(0, source.Writes);
            Assert.Equal(1, auth.Calls);
        }
        finally { gate.Release.TrySetResult(); }
    }

    [Theory]
    [InlineData("resolve", false, false)]
    [InlineData("resolve", false, true)]
    [InlineData("resolve", true, false)]
    [InlineData("resolve", true, true)]
    [InlineData("refresh", false, false)]
    [InlineData("refresh", false, true)]
    [InlineData("refresh", true, false)]
    [InlineData("refresh", true, true)]
    [InlineData("save", false, false)]
    [InlineData("save", false, true)]
    [InlineData("save", true, false)]
    [InlineData("save", true, true)]
    public async Task CancellationAndRetirementDrainEveryCredentialBoundary(string boundary, bool retire, bool ignoreCancellation)
    {
        var gate = new Gate { Boundary = boundary, IgnoreCancellation = ignoreCancellation };
        using var source = new Source(gate);
        using var auth = new Authenticator(gate);
        await using var access = new LlmConnectionAccess(source, [auth]);
        using var caller = new CancellationTokenSource();
        Task<ProviderAuthorizationHeader> request = new LlmConnectionAuthorization("connection", access).GetAsync(caller.Token).AsTask();
        try
        {
            await gate.Entered.Task.WaitAsync(Bound);
            Task stopping = retire ? access.DisposeAsync().AsTask() : caller.CancelAsync();
            await gate.Stopped.Task.WaitAsync(Bound);
            if (ignoreCancellation)
            {
                Assert.False(request.IsCompleted);
                if (retire) Assert.False(stopping.IsCompleted);
            }
            gate.Release.TrySetResult();
            await stopping.WaitAsync(Bound);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            Assert.Equal(0, source.Writes);
            Assert.False(source.Disposed);
            Assert.False(auth.Disposed);
            if (retire)
            {
                Assert.Same(stopping, access.DisposeAsync().AsTask());
                await Assert.ThrowsAsync<ObjectDisposedException>(() => access.AcquireAsync("connection"));
            }
            else
            {
                await access.AcquireAsync("connection");
                Assert.Equal(1, source.Writes);
            }
        }
        finally { gate.Release.TrySetResult(); }
    }

    [Fact]
    public async Task LateSaveCompletionIsDrainedAndNeverReturnsAuthorization()
    {
        var gate = new Gate { Boundary = "save", IgnoreCancellation = true };
        using var source = new Source(gate) { IgnoreCommitCancellation = true };
        using var auth = new Authenticator(gate);
        await using var access = new LlmConnectionAccess(source, [auth]);
        Task<ProviderAuthorizationHeader> request = new LlmConnectionAuthorization("connection", access).GetAsync().AsTask();
        try
        {
            await gate.Entered.Task.WaitAsync(Bound);
            Task stopping = access.DisposeAsync().AsTask();
            await gate.Stopped.Task.WaitAsync(Bound);
            Assert.False(stopping.IsCompleted);
            gate.Release.TrySetResult();
            await stopping.WaitAsync(Bound);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            Assert.Equal(1, source.Writes);
            Assert.Equal(1, auth.Calls);
        }
        finally { gate.Release.TrySetResult(); }
    }

    [Theory]
    [InlineData("acquire", "resolve")]
    [InlineData("acquire", "refresh")]
    [InlineData("acquire", "save")]
    [InlineData("refresh", "refresh")]
    [InlineData("refresh", "save")]
    [InlineData("authorization", "save")]
    public async Task AccessOwnerRejectsLateResultsWhileLinkedCancellationPropagationIsBlocked(string entry, string boundary)
    {
        var anchorGate = new Gate { Boundary = "resolve", IgnoreCancellation = true };
        var targetGate = new Gate { Boundary = boundary, IgnoreCancellation = true };
        using var source = new Source(targetGate) { AnchorGate = anchorGate, IgnoreCommitCancellation = true };
        using var auth = new Authenticator(targetGate);
        using var caller = new CancellationTokenSource();
        using var unblockCancellation = new ManualResetEventSlim();
        var cancellationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var access = new LlmConnectionAccess(source, [auth]);
        Task anchor = access.AcquireAsync("connection");
        await anchorGate.Entered.Task.WaitAsync(Bound);
        CancellationToken ownerStop = source.LastReadToken;
        Task operation = entry switch
        {
            "refresh" => access.RefreshAsync("connection", Connection, caller.Token),
            "authorization" => new LlmConnectionAuthorization("connection", access).GetAsync(caller.Token).AsTask(),
            _ => access.AcquireAsync("connection", caller.Token)
        };
        Task? retirement = null;
        CancellationTokenRegistration blocker = default;
        try
        {
            await targetGate.Entered.Task.WaitAsync(Bound);
            CancellationToken linked = boundary switch
            {
                "resolve" => source.LastReadToken,
                "refresh" => auth.LastRefreshToken,
                _ => source.LastSaveToken
            };
            blocker = ownerStop.Register(() =>
            {
                cancellationEntered.TrySetResult();
                if (!unblockCancellation.Wait(Bound))
                    throw new TimeoutException("The controlled access cancellation callback was not released.");
            });
            retirement = access.DisposeAsync().AsTask();
            await cancellationEntered.Task.WaitAsync(Bound);
            Assert.True(ownerStop.IsCancellationRequested);
            Assert.False(linked.IsCancellationRequested);
            Assert.False(retirement.IsCompleted);
            Assert.Same(retirement, access.DisposeAsync().AsTask());
            await Assert.ThrowsAsync<ObjectDisposedException>(() => access.AcquireAsync("connection"));
            targetGate.Release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(Bound));
            Assert.False(linked.IsCancellationRequested);
            Assert.False(retirement.IsCompleted);
            Assert.Equal(boundary == "resolve" ? 0 : 1, auth.Calls);
            Assert.Equal(boundary == "save" ? 1 : 0, source.Writes);
            Assert.Equal(boundary == "save" ? "protected-fresh" : "protected-old", LlmCredential.Parse(source.Current!.Credential)!.OAuth!.AccessToken);
            Assert.False(source.Disposed);
            Assert.False(auth.Disposed);
        }
        finally
        {
            unblockCancellation.Set();
            targetGate.Release.TrySetResult();
            anchorGate.Release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => anchor.WaitAsync(Bound));
            await (retirement ?? access.DisposeAsync().AsTask()).WaitAsync(Bound);
            blocker.Dispose();
        }
    }

    [Fact]
    public void ActualForeignCredentialAccessRetiresAndReleasesItsGenerationWithDelayedPropagation()
    {
        WeakReference generation = ExerciseGeneration();
        for (int attempt = 0; generation.IsAlive && attempt < 20; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Thread.Sleep(10);
        }
        Assert.False(generation.IsAlive, "The actual retired credential-access generation remains rooted.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ExerciseGeneration()
    {
        string directory = Path.GetDirectoryName(typeof(ConnectionAccessTests).Assembly.Location)!;
        var generation = new ConnectGeneration(directory);
        var weak = new WeakReference(generation);
        try
        {
            Assembly connect = generation.LoadFromAssemblyPath(typeof(LlmConnectionAccess).Assembly.Location);
            Assembly common = generation.LoadFromAssemblyPath(typeof(ProviderAuthorizationHeader).Assembly.Location);
            Assembly fixture = generation.LoadFromAssemblyPath(typeof(ConnectionAccessTests).Assembly.Location);
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(connect));
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(common));
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(fixture));
            Assert.Equal(typeof(LlmConnectionAccess).Assembly.ManifestModule.ModuleVersionId, connect.ManifestModule.ModuleVersionId);
            Assert.Equal(typeof(ProviderAuthorizationHeader).Assembly.ManifestModule.ModuleVersionId, common.ManifestModule.ModuleVersionId);
            Assert.Equal(typeof(ConnectionAccessTests).Assembly.ManifestModule.ModuleVersionId, fixture.ManifestModule.ModuleVersionId);
            Type caller = fixture.GetType(typeof(ConnectionAccessTests).FullName!, true)!;
            var operation = (Task)caller.GetMethod(nameof(ForeignCallerAsync), BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
            operation.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        }
        finally { generation.Unload(); }
        return weak;
    }

    private static async Task ForeignCallerAsync()
    {
        AssemblyLoadContext generation = AssemblyLoadContext.GetLoadContext(typeof(ConnectionAccessTests).Assembly)!;
        Assert.True(generation.IsCollectible);
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(typeof(LlmConnectionAccess).Assembly));
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(typeof(ProviderAuthorizationHeader).Assembly));
        var fixture = new ConnectionAccessTests();
        foreach (string boundary in new[] { "resolve", "refresh", "save" })
            await fixture.AccessOwnerRejectsLateResultsWhileLinkedCancellationPropagationIsBlocked("acquire", boundary);
        await fixture.AccessOwnerRejectsLateResultsWhileLinkedCancellationPropagationIsBlocked("refresh", "refresh");
        await fixture.AccessOwnerRejectsLateResultsWhileLinkedCancellationPropagationIsBlocked("refresh", "save");
        await fixture.AccessOwnerRejectsLateResultsWhileLinkedCancellationPropagationIsBlocked("authorization", "save");
    }

    private sealed class ConnectGeneration(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name?.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal) != true) return null;
            string path = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }

    [Fact]
    public async Task EmbeddingsUseTheSameRefreshAndVersionSaveAsChat()
    {
        using var source = new Source(new Gate());
        using var auth = new Authenticator(new Gate());
        using var client = new ChatClient();
        using var embedding = new EmbeddingClient();
        await using var access = new LlmConnectionAccess(source, [auth], [client]);
        await using var chat = new LlmProviderService(source, [client], [auth], connectionAccess: access);
        await using var embeddings = new LlmEmbeddingProviderService(source, [embedding], connectionAccess: access);
        await embeddings.EmbedAsync("connection", Embedding);
        await chat.ListModelsAsync("connection");
        Assert.Equal("protected-fresh", LlmCredential.Parse(embedding.LastConnection!.Credential)!.OAuth!.AccessToken);
        Assert.Equal("protected-fresh", LlmCredential.Parse(client.LastConnection!.Credential)!.OAuth!.AccessToken);
        Assert.Equal(1, source.Writes);
        Assert.Equal(2, auth.Calls);
        await embeddings.DisposeAsync();
        await chat.DisposeAsync();
        Assert.Equal("protected-fresh", (await new LlmConnectionAuthorization("connection", access).GetAsync()).Value);
        Assert.False(auth.Disposed);
        Assert.False(client.Disposed);
        Assert.False(embedding.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FacadeRetirementCancelsItsRefreshWithoutRetiringSharedAccess(bool embeddings)
    {
        var gate = new Gate { Boundary = "refresh", IgnoreCancellation = true };
        using var source = new Source(gate);
        using var auth = new Authenticator(gate);
        using var client = new ChatClient();
        using var embedding = new EmbeddingClient();
        await using var access = new LlmConnectionAccess(source, [auth], [client]);
        await using var chat = new LlmProviderService(source, [client], [auth], connectionAccess: access);
        await using var facade = new LlmEmbeddingProviderService(source, [embedding], connectionAccess: access);
        Task request = embeddings ? facade.EmbedAsync("connection", Embedding) : chat.ListModelsAsync("connection");
        try
        {
            await gate.Entered.Task.WaitAsync(Bound);
            Task stopping = embeddings ? facade.DisposeAsync().AsTask() : chat.DisposeAsync().AsTask();
            await gate.Stopped.Task.WaitAsync(Bound);
            Assert.False(stopping.IsCompleted);
            gate.Release.TrySetResult();
            await stopping.WaitAsync(Bound);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            Assert.Null(client.LastConnection);
            Assert.Null(embedding.LastConnection);
            Assert.Equal(0, source.Writes);
            await access.AcquireAsync("connection");
            Assert.Equal(1, source.Writes);
            Assert.False(auth.Disposed);
        }
        finally { gate.Release.TrySetResult(); }
    }

    [Fact]
    public async Task AHandBuiltEmbeddingFacadeRefreshesWithItsOwnedAccess()
    {
        using var source = new Source(new Gate());
        using var auth = new Authenticator(new Gate());
        using var client = new EmbeddingClient();
        await using var facade = new LlmEmbeddingProviderService(source, [client], [auth]);
        await facade.EmbedAsync("connection", Embedding);
        Assert.Equal("protected-fresh", LlmCredential.Parse(client.LastConnection!.Credential)!.OAuth!.AccessToken);
        Assert.Equal("v1", Assert.Single(source.ExpectedVersions));
        await facade.DisposeAsync();
        Assert.False(source.Disposed);
        Assert.False(auth.Disposed);
        Assert.False(client.Disposed);
    }

    [Fact]
    public async Task UnsupportedEmbeddingRoutesFailBeforeRefresh()
    {
        using var source = new Source(new Gate());
        using var auth = new Authenticator(new Gate());
        await using var embeddings = new LlmEmbeddingProviderService(source, [], [auth]);
        await Assert.ThrowsAsync<NotSupportedException>(() => embeddings.EmbedAsync("connection", Embedding));
        Assert.Equal(0, auth.Calls);
        Assert.Equal(0, source.Writes);
    }

    [Fact]
    public async Task StoredCredentialIgnoringAdaptersKeepTheirBypass()
    {
        using var source = new Source(new Gate());
        using var auth = new Authenticator(new Gate());
        using var client = new IgnoringClient();
        await using var access = new LlmConnectionAccess(source, [auth], [client]);
        Assert.Same(source.Current, await access.AcquireAsync("connection"));
        await using var chat = new LlmProviderService(source, [client], [auth], connectionAccess: access);
        await chat.ListModelsAsync("connection");
        Assert.Equal(0, auth.Calls);
        Assert.Equal(0, source.Writes);
    }

    [Fact]
    public async Task FacadesRejectAnAccessOwnerForAnotherSource()
    {
        using var first = new Source(new Gate());
        using var second = new Source(new Gate());
        await using var access = new LlmConnectionAccess(first, []);
        Assert.Throws<ArgumentException>(() => new LlmProviderService(second, [], [], connectionAccess: access));
        Assert.Throws<ArgumentException>(() => new LlmEmbeddingProviderService(second, [], connectionAccess: access));
        Assert.Equal(0, first.Reads);
        Assert.Equal(0, second.Reads);
    }

    [Fact]
    public async Task RegistrationSharesAccessWithinScopeAndKeepsScopesIndependent()
    {
        using var source = new Source(new Gate());
        using var auth = new Authenticator(new Gate()) { Rotate = false };
        using var client = new ChatClient();
        using var embedding = new EmbeddingClient();
        var services = new ServiceCollection();
        services.AddLlmConnect();
        services.RemoveAll<ILlmClient>();
        services.RemoveAll<ILlmAuthenticator>();
        services.RemoveAll<ILlmEmbeddingClient>();
        services.AddSingleton<ILlmConnectionSource>(source);
        services.AddSingleton<ILlmClient>(client);
        services.AddSingleton<ILlmAuthenticator>(auth);
        services.AddSingleton<ILlmEmbeddingClient>(embedding);
        await using ServiceProvider provider = services.BuildServiceProvider();
        await using AsyncServiceScope first = provider.CreateAsyncScope();
        await using AsyncServiceScope second = provider.CreateAsyncScope();
        LlmConnectionAccess access = first.ServiceProvider.GetRequiredService<LlmConnectionAccess>();
        Assert.Same(access, first.ServiceProvider.GetRequiredService<LlmConnectionAccess>());
        LlmConnectionAccess other = second.ServiceProvider.GetRequiredService<LlmConnectionAccess>();
        Assert.NotSame(access, other);
        var chat = first.ServiceProvider.GetRequiredService<LlmProviderService>();
        var embeddings = first.ServiceProvider.GetRequiredService<LlmEmbeddingProviderService>();
        await access.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => chat.ListModelsAsync("connection"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => embeddings.EmbedAsync("connection", Embedding));
        await other.AcquireAsync("connection");
        Assert.Equal(1, auth.Calls);
        Assert.False(source.Disposed);
        Assert.False(auth.Disposed);
    }

    private sealed class Gate
    {
        internal string? Boundary { get; init; }
        internal bool IgnoreCancellation { get; init; }
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal async Task WaitAsync(string boundary, CancellationToken token)
        {
            if (Boundary != boundary) return;
            using CancellationTokenRegistration registration = token.Register(() => Stopped.TrySetResult());
            Entered.TrySetResult();
            try
            {
                if (IgnoreCancellation) await Release.Task;
                else await Release.Task.WaitAsync(token);
            }
            finally { if (token.IsCancellationRequested) Stopped.TrySetResult(); }
        }
    }

    private sealed class Source(Gate gate) : ILlmConnectionSource, IDisposable
    {
        internal LlmConnection? Current { get; set; } = Connection;
        internal Gate? AnchorGate { get; init; }
        internal bool IgnoreCommitCancellation { get; init; }
        internal int Reads { get; private set; }
        internal int Writes { get; private set; }
        internal List<string> ExpectedVersions { get; } = [];
        internal bool Disposed { get; private set; }
        internal CancellationToken LastReadToken { get; private set; }
        internal CancellationToken LastSaveToken { get; private set; }

        public async Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
        {
            Assert.Equal("connection", connectionId);
            Reads++;
            LastReadToken = cancellationToken;
            if (Reads == 1 && AnchorGate is { } anchor) await anchor.WaitAsync("resolve", cancellationToken);
            await gate.WaitAsync("resolve", cancellationToken);
            return Current;
        }

        public async Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default)
        {
            Assert.Equal("connection", connectionId);
            LastSaveToken = cancellationToken;
            ExpectedVersions.Add(expectedVersion);
            await gate.WaitAsync("save", cancellationToken);
            if (!IgnoreCommitCancellation) cancellationToken.ThrowIfCancellationRequested();
            if (Current?.Version != expectedVersion) throw new ConnectionChangedException();
            Current = Current with { Version = "v2", Credential = credentialJson };
            Writes++;
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class Authenticator(Gate gate) : ILlmAuthenticator, IDisposable
    {
        internal bool Rotate { get; init; } = true;
        internal int Calls { get; private set; }
        internal bool Disposed { get; private set; }
        internal CancellationToken LastRefreshToken { get; private set; }
        public LlmTransport Transport => LlmTransport.OpenAiCodex;

        public async Task<LlmCredential> EnsureFreshAsync(LlmCredential credential, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastRefreshToken = cancellationToken;
            await gate.WaitAsync("refresh", cancellationToken);
            return !Rotate || credential.OAuth!.AccessToken == "protected-fresh"
                ? credential : credential with { OAuth = credential.OAuth with { AccessToken = "protected-fresh", RefreshToken = "protected-rotated" } };
        }

        public void Dispose() => Disposed = true;
    }

    private class ChatClient : ILlmClient, IDisposable
    {
        internal LlmConnection? LastConnection { get; private set; }
        internal bool Disposed { get; private set; }
        public LlmTransport Transport => LlmTransport.OpenAiCodex;
        public LlmProviderDefinition ProviderDefinition => new()
        { Id = "fixture", DisplayName = "Fixture", Transport = Transport, Description = "Fixture", DefaultEndpoint = "https://provider.invalid/v1", SetupSteps = [] };
        public LlmProviderCapabilities GetCapabilities(LlmConnection connection, string? modelId = null) => new();
        public Task<IReadOnlyList<LlmModel>> ListModelsAsync(LlmConnection connection, CancellationToken cancellationToken = default)
        { LastConnection = connection; return Task.FromResult<IReadOnlyList<LlmModel>>([]); }
        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmConnection connection, LlmChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { await Task.CompletedTask; yield break; }
        public void Dispose() => Disposed = true;
    }

    private sealed class IgnoringClient : ChatClient, IIgnoresStoredCredential;
    private sealed class ConnectionChangedException : InvalidOperationException;

    private sealed class EmbeddingClient : ILlmEmbeddingClient, IDisposable
    {
        internal LlmConnection? LastConnection { get; private set; }
        internal bool Disposed { get; private set; }
        public bool Supports(LlmTransport transport) => transport == LlmTransport.OpenAiCodex;
        public void ValidateRequest(LlmConnection connection, LlmEmbeddingRequest request) => Assert.NotEmpty(request.Inputs);
        public Task<LlmEmbeddingResult> EmbedAsync(LlmConnection connection, LlmEmbeddingRequest request, CancellationToken cancellationToken = default)
        { LastConnection = connection; return Task.FromResult(new LlmEmbeddingResult { Vectors = [new float[] { 1 }], Diagnostics = new(200, null, null) }); }
        public void Dispose() => Disposed = true;
    }
}
