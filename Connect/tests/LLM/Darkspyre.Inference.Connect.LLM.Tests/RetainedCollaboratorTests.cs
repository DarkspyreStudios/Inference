using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Setup;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class RetainedCollaboratorTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ChatRetirementDrainsBothOwnersAndPreservesOperationAndPrivateCatalogFailures()
    {
        var state = new ResolutionState(true) { Block = false };
        var source = new ForeignCollaborator(state.ResolveAsync, state.Record);
        var client = new FailingCatalogClient();
        var service = new LlmProviderService(source, [client]);
        Task listing = service.ListModelsAsync("connection");
        Task? resolution = null;
        Task? retirement = null;
        try
        {
            await client.Entered.Task.WaitAsync(Bound);
            state.Block = true;
            resolution = service.GetObservedAccountUsageAsync("connection");
            await state.Entered.Task.WaitAsync(Bound);
            retirement = service.DisposeAsync().AsTask();
            await state.Canceled.Task.WaitAsync(Bound);
            await client.Canceled.Task.WaitAsync(Bound);
            Assert.False(retirement.IsCompleted);
            state.Release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolution.WaitAsync(Bound));
            Assert.False(retirement.IsCompleted);
            client.Release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listing.WaitAsync(Bound));
            AggregateException failure = await Assert.ThrowsAsync<AggregateException>(() => retirement.WaitAsync(Bound));
            Assert.Contains(state.Failure, failure.Flatten().InnerExceptions);
            Assert.Contains(client.Failure, failure.Flatten().InnerExceptions);
            Assert.Same(failure, await Assert.ThrowsAsync<AggregateException>(() => service.DisposeAsync().AsTask()));
            Assert.False(client.Disposed);
            Assert.DoesNotContain("dispose", state.Executed);
        }
        finally
        {
            state.Release.TrySetResult();
            client.Release.TrySetResult();
            try { await DrainExpectedFailureAsync(listing); }
            finally
            {
                try { if (resolution is not null) await DrainExpectedFailureAsync(resolution); }
                finally { await DrainExpectedFailureAsync(retirement ?? service.DisposeAsync().AsTask()); }
            }
        }
    }

    [Fact]
    public async Task RetirementDrainsAnAdmittedSynchronousClaudeLoginWithoutDisposingTheAuthenticator()
    {
        using var auth = new BlockingLogin();
        var state = new ResolutionState(false) { Block = false };
        var source = new ForeignCollaborator(state.ResolveAsync, state.Record);
        var service = new LlmProviderService(source, [], [auth]);
        Task<LlmAuthorizationSession> login = Task.Run(() => service.StartClaudeLoginAsync("https://callback.invalid"));
        Task? retirement = null;
        try
        {
            await auth.Entered.Task.WaitAsync(Bound);
            retirement = service.DisposeAsync().AsTask();
            Assert.False(retirement.IsCompleted);
            Assert.Same(retirement, service.DisposeAsync().AsTask());
            await Assert.ThrowsAsync<ObjectDisposedException>(() => service.StartClaudeLoginAsync("https://callback.invalid"));
            Assert.False(auth.Disposed);
            auth.Release.Set();
            Assert.Same(auth.Session, await login.WaitAsync(Bound));
            await retirement.WaitAsync(Bound);
            Assert.False(auth.Disposed);
            Assert.DoesNotContain("dispose", state.Executed);
        }
        finally
        {
            auth.Release.Set();
            try { await login.WaitAsync(Bound); }
            finally { await (retirement ?? service.DisposeAsync().AsTask()).WaitAsync(Bound); }
        }
    }

    [Theory]
    [InlineData("access", false)]
    [InlineData("access", true)]
    [InlineData("chat", false)]
    [InlineData("chat", true)]
    [InlineData("embedding", false)]
    [InlineData("embedding", true)]
    [InlineData("agent", false)]
    [InlineData("agent", true)]
    [InlineData("chat-borrowed-access", false)]
    [InlineData("chat-borrowed-access", true)]
    [InlineData("embedding-borrowed-access", false)]
    [InlineData("embedding-borrowed-access", true)]
    public void DisposedDefaultOwnersReleaseForeignCollaboratorsWhileRemainingRetained(string lane, bool failCleanup)
    {
        RetirementEvidence evidence = ExerciseGeneration(lane, failCleanup);
        try
        {
            for (int attempt = 0; (evidence.Generation.IsAlive || evidence.BorrowedAccess?.IsAlive == true) && attempt < 30; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                Thread.Sleep(10);
            }
            Assert.False(evidence.Generation.IsAlive,
                $"The retired default-context {lane} still roots its separately collectible collaborator.");
            if (evidence.BorrowedAccess is not null)
                Assert.False(evidence.BorrowedAccess.IsAlive, "The retired facade still retains its borrowed access owner.");
        }
        finally { GC.KeepAlive(evidence.RetainedOwners); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static RetirementEvidence ExerciseGeneration(string lane, bool failCleanup)
    {
        var generation = new CollaboratorGeneration();
        var weak = new WeakReference(generation);
        try
        {
            CallerEvidence caller = ExerciseCallerAsync(generation, lane, failCleanup).GetAwaiter().GetResult();
            return new(weak, caller.RetainedOwners, caller.BorrowedAccess);
        }
        finally { generation.Unload(); }
    }

    private static async Task<CallerEvidence> ExerciseCallerAsync(AssemblyLoadContext generation, string lane, bool failCleanup)
    {
        var state = new ResolutionState(failCleanup);
        Assembly fixture = generation.LoadFromAssemblyPath(typeof(RetainedCollaboratorTests).Assembly.Location);
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(fixture));
        Assert.Equal(typeof(RetainedCollaboratorTests).Assembly.ManifestModule.ModuleVersionId,
            fixture.ManifestModule.ModuleVersionId);
        Type collaboratorType = fixture.GetType(typeof(ForeignCollaborator).FullName!, true)!;
        Assert.Contains(typeof(ILlmConnectionSource), collaboratorType.GetInterfaces());
        Assert.Contains(typeof(ILlmSetupEnvironment), collaboratorType.GetInterfaces());
        Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(typeof(ILlmConnectionSource).Assembly));
        Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(typeof(SetupValidation).Assembly));
        var collaborator = (ILlmConnectionSource)Activator.CreateInstance(collaboratorType,
            (Func<string, CancellationToken, Task<LlmConnection?>>)state.ResolveAsync,
            (Action<string>)state.Record,
            Providers.ProviderDefinitions.OpenAiCompatible)!;
        var environment = (ILlmSetupEnvironment)collaborator;
        Assert.True(environment.Validate(state.Connection).IsValid);

        LlmConnectionAccess? borrowedAccess = lane.EndsWith("borrowed-access", StringComparison.Ordinal)
            ? new(collaborator, []) : null;
        IAsyncDisposable owner;
        object? authorization = null;
        Task request;
        switch (lane)
        {
            case "access":
                var access = new LlmConnectionAccess(collaborator, []);
                var bound = new LlmConnectionAuthorization("connection", access);
                owner = access;
                authorization = bound;
                request = bound.GetAsync().AsTask();
                break;
            case "chat":
            case "chat-borrowed-access":
                var chat = new LlmProviderService(collaborator, [(ILlmClient)collaborator],
                    [(ILlmAuthenticator)collaborator], [environment], connectionAccess: borrowedAccess);
                owner = chat;
                request = chat.GetObservedAccountUsageAsync("connection");
                break;
            case "embedding":
            case "embedding-borrowed-access":
                var embedding = new LlmEmbeddingProviderService(collaborator, [], connectionAccess: borrowedAccess);
                owner = embedding;
                request = embedding.EmbedAsync("connection", new() { ModelId = "model", Inputs = ["input"] });
                break;
            case "agent":
                var agent = new LlmAgentProviderService(collaborator, []);
                owner = agent;
                request = agent.StartSessionAsync("connection", new() { ModelId = "model" });
                break;
            default: throw new InvalidOperationException("Unknown controlled owner lane.");
        }

        Task? retirement = null;
        try
        {
            await state.Entered.Task.WaitAsync(Bound);
            Assert.Equal(1, state.Reads);
            Assert.Contains("resolve", state.Executed);
            Assert.Contains("validate", state.Executed);
            if (lane.StartsWith("chat", StringComparison.Ordinal))
            {
                Assert.Contains("transport", state.Executed);
                Assert.Contains("definition", state.Executed);
                Assert.Same(Providers.ProviderDefinitions.OpenAiCompatible,
                    Assert.Single(((LlmProviderService)owner).SupportedProviders));
            }
            retirement = owner.DisposeAsync().AsTask();
            await state.Canceled.Task.WaitAsync(Bound);
            Assert.False(retirement.IsCompleted);
            Assert.False(request.IsCompleted);
            Assert.Same(retirement, owner.DisposeAsync().AsTask());
            await AssertRetiredAsync(owner, authorization);
            Assert.Equal(1, state.Reads);
            Assert.DoesNotContain("dispose", state.Executed);
            state.Release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(Bound));
            if (failCleanup)
            {
                AggregateException first = await Assert.ThrowsAsync<AggregateException>(() => retirement.WaitAsync(Bound));
                Assert.Contains(state.Failure, first.Flatten().InnerExceptions);
                AggregateException repeated = await Assert.ThrowsAsync<AggregateException>(() => owner.DisposeAsync().AsTask());
                Assert.Same(first, repeated);
            }
            else { await retirement.WaitAsync(Bound); }
            await AssertRetiredAsync(owner, authorization);
            Assert.Equal(1, state.Reads);
            Assert.DoesNotContain("dispose", state.Executed);

            if (borrowedAccess is not null)
            {
                state.Block = false;
                // A facade must release, not retire, an injected credential owner.
                Assert.Same(state.Connection, await borrowedAccess.AcquireAsync("connection"));
                Assert.Equal(2, state.Reads);
                await borrowedAccess.DisposeAsync();
                authorization = new LlmConnectionAuthorization("connection", borrowedAccess);
                await Assert.ThrowsAsync<ObjectDisposedException>(() => ((LlmConnectionAuthorization)authorization).GetAsync().AsTask());
            }
            Assert.DoesNotContain("dispose", state.Executed);
            Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(owner.GetType().Assembly));
            return borrowedAccess is null
                ? new([owner, .. authorization is null ? [] : new object[] { authorization }], null)
                : new([owner], new WeakReference(borrowedAccess));
        }
        finally
        {
            state.Release.TrySetResult();
            try { await DrainExpectedFailureAsync(request); }
            finally
            {
                try { await DrainExpectedFailureAsync(retirement ?? owner.DisposeAsync().AsTask()); }
                finally
                {
                    if (borrowedAccess is not null) await borrowedAccess.DisposeAsync().AsTask().WaitAsync(Bound);
                }
            }
        }
    }

    private static async Task DrainExpectedFailureAsync(Task operation)
    {
        try { await operation.WaitAsync(Bound); }
        catch (OperationCanceledException) { }
        catch (AggregateException) { }
    }

    private static async Task AssertRetiredAsync(IAsyncDisposable owner, object? authorization)
    {
        Task refused = owner switch
        {
            LlmConnectionAccess access => access.AcquireAsync("connection"),
            LlmProviderService chat => chat.GetObservedAccountUsageAsync("connection"),
            LlmEmbeddingProviderService embedding => embedding.EmbedAsync("connection", new() { ModelId = "model", Inputs = ["input"] }),
            LlmAgentProviderService agent => agent.StartSessionAsync("connection", new() { ModelId = "model" }),
            _ => throw new InvalidOperationException("Unknown controlled owner.")
        };
        await Assert.ThrowsAsync<ObjectDisposedException>(() => refused);
        if (authorization is LlmConnectionAuthorization bound)
            await Assert.ThrowsAsync<ObjectDisposedException>(() => bound.GetAsync().AsTask());
        if (owner is LlmProviderService facade)
        {
            Assert.Throws<ObjectDisposedException>(() => facade.Bind("connection"));
            Assert.Throws<ObjectDisposedException>(() => facade.CreateSettingsManager(null!));
        }
    }

    private sealed record RetirementEvidence(WeakReference Generation, object[] RetainedOwners, WeakReference? BorrowedAccess);
    private sealed record CallerEvidence(object[] RetainedOwners, WeakReference? BorrowedAccess);

    private sealed class FailingCatalogClient : ILlmClient, IDisposable
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal InvalidOperationException Failure { get; } = new("Controlled default-context catalog cleanup failure.");
        internal bool Disposed { get; private set; }
        public LlmTransport Transport => LlmTransport.OpenAiCompatibleChat;
        public LlmProviderDefinition ProviderDefinition => Providers.ProviderDefinitions.OpenAiCompatible;
        public LlmProviderCapabilities GetCapabilities(LlmConnection connection, string? modelId = null) => new() { SupportsModelListing = true };
        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmConnection connection, LlmChatRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async Task<IReadOnlyList<LlmModel>> ListModelsAsync(LlmConnection connection,
            CancellationToken cancellationToken = default)
        {
            using CancellationTokenRegistration registration = cancellationToken.Register(() =>
            {
                Canceled.TrySetResult();
                throw Failure;
            });
            Entered.TrySetResult();
            await Release.Task;
            return [];
        }
        public void Dispose() => Disposed = true;
    }

    private sealed class BlockingLogin : IClaudePkceLogin, IDisposable
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Release { get; } = new();
        internal bool Disposed { get; private set; }
        internal LlmAuthorizationSession Session { get; } = new()
        { AuthorizeUrl = "https://provider.invalid/authorize", State = "state", CodeVerifier = "controlled-verifier", RedirectUri = "https://callback.invalid" };
        public LlmTransport Transport => LlmTransport.AnthropicClaudeCli;
        public LlmAuthorizationSession Start(string redirectUri)
        {
            Assert.Equal(Session.RedirectUri, redirectUri);
            Entered.TrySetResult();
            if (!Release.Wait(Bound)) throw new TimeoutException("The controlled synchronous login was not released.");
            return Session;
        }
        public Task<LlmCredential> CompleteAsync(LlmAuthorizationSession session, string code, string state,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LlmCredential> EnsureFreshAsync(LlmCredential credential, CancellationToken cancellationToken = default) =>
            Task.FromResult(credential);
        public void Dispose() { Disposed = true; Release.Dispose(); }
    }

    private sealed class CollaboratorGeneration() : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name) =>
            Default.Assemblies.FirstOrDefault(assembly => AssemblyName.ReferenceMatchesDefinition(name, assembly.GetName()));
    }

    private sealed class ResolutionState(bool failCleanup)
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal InvalidOperationException Failure { get; } = new("Controlled default-context cancellation cleanup failure.");
        internal List<string> Executed { get; } = [];
        internal bool Block { get; set; } = true;
        internal int Reads { get; private set; }
        internal LlmConnection Connection { get; } = new()
        {
            Version = "v1",
            Transport = LlmTransport.OpenAiCompatibleChat,
            Endpoint = "https://provider.invalid/v1",
            Credential = new LlmCredential { OAuth = new() { AccessToken = "controlled-token" } }.Serialize()
        };
        internal void Record(string method) => Executed.Add(method);

        internal async Task<LlmConnection?> ResolveAsync(string connectionId, CancellationToken token)
        {
            Assert.Equal("connection", connectionId);
            Reads++;
            if (!Block) return Connection;
            using CancellationTokenRegistration registration = token.Register(() =>
            {
                Canceled.TrySetResult();
                if (failCleanup) throw Failure;
            });
            Entered.TrySetResult();
            await Release.Task;
            return Connection;
        }
    }

    // Only this fixture assembly is foreign; every implemented contract and returned task is default-owned.
    public sealed class ForeignCollaborator(
        Func<string, CancellationToken, Task<LlmConnection?>> resolve,
        Action<string> executed,
        LlmProviderDefinition? definition = null) : ILlmConnectionSource, ILlmSetupEnvironment,
        ILlmClient, ILlmAuthenticator, ILlmAccountUsageClient, IDisposable
    {
        public LlmTransport Transport { get { executed("transport"); return LlmTransport.OpenAiCompatibleChat; } }
        public LlmProviderDefinition ProviderDefinition
        { get { executed("definition"); return definition ?? Providers.ProviderDefinitions.OpenAiCompatible; } }
        public LlmProviderCapabilities GetCapabilities(LlmConnection connection, string? modelId = null) => new();
        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmConnection connection, LlmChatRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<LlmModel>> ListModelsAsync(LlmConnection connection,
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<LlmModel>>([]);
        public Task<LlmCredential> EnsureFreshAsync(LlmCredential credential, CancellationToken cancellationToken = default) =>
            Task.FromResult(credential);
        public Task<LlmAccountUsageSnapshot> GetAccountUsageAsync(LlmConnection connection,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
        {
            executed("resolve");
            return resolve(connectionId, cancellationToken);
        }
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SetupChoice>> GetProfilesAsync(CancellationToken cancellationToken = default)
        { executed("profiles"); return Task.FromResult<IReadOnlyList<SetupChoice>>([]); }
        public Task<IReadOnlyList<SetupChoice>> GetRegionsAsync(string? profile, CancellationToken cancellationToken = default)
        { executed("regions"); return Task.FromResult<IReadOnlyList<SetupChoice>>([]); }
        public SetupValidation Validate(LlmConnection connection)
        { executed("validate"); return SetupValidation.Valid; }
        public void Dispose() => executed("dispose");
    }
}
