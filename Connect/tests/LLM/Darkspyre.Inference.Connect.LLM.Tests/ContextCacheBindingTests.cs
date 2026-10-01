using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class ContextCacheBindingTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    private static readonly LlmConnection Connection = new()
    {
        Version = "version", Transport = LlmTransport.OpenAiResponses, Endpoint = "https://example.invalid/v1",
        Credential = new LlmCredential { OAuth = new() { AccessToken = "private", RefreshToken = "private", ExpiresAtUnixSeconds = 0 } }.Serialize()
    };

    [Fact]
    public async Task BindingHasExactPublicSignatureAndRefusesWithoutAdapterEffects()
    {
        MethodInfo method = BindingMethod();
        Assert.Equal(typeof(Task<ILlmContextCacheClient>), method.ReturnType);
        Assert.Equal(new[] { typeof(string), typeof(CancellationToken) }, method.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.True(method.GetParameters()[1].HasDefaultValue);
        using var source = new Source(Connection);
        using var client = new Client();
        using var authenticator = new Authenticator();
        await using var facade = new LlmProviderService(source, [client], [authenticator]);
        NotSupportedException error = await Assert.ThrowsAsync<NotSupportedException>(() => Bind(facade));
        Assert.Equal("No explicit context cache adapter is implemented.", error.Message);
        Assert.Equal(1, source.Reads);
        Assert.Equal(0, source.Saves);
        Assert.Equal(0, client.Effects);
        Assert.Equal(0, authenticator.Calls);
        Assert.Equal("version", source.Value!.Version);
    }

    [Theory]
    [InlineData("blank")] [InlineData("missing")] [InlineData("version")] [InlineData("transport")] [InlineData("failure")]
    public async Task ExistingResolutionAndRegistrationFailuresRemainAuthoritative(string scenario)
    {
        using var source = new Source(scenario == "missing" ? null : scenario == "version" ? Connection with { Version = "" } : Connection);
        var original = new InvalidOperationException("Controlled source failure.");
        if (scenario == "failure") source.Failure = original;
        using var client = new Client();
        await using var facade = new LlmProviderService(source, scenario == "transport" ? [] : [client]);
        if (scenario == "blank")
        {
            await Assert.ThrowsAsync<ArgumentException>(() => Bind(facade, " "));
            Assert.Equal(0, source.Reads);
        }
        else if (scenario == "transport")
        {
            NotSupportedException error = await Assert.ThrowsAsync<NotSupportedException>(() => Bind(facade));
            Assert.Equal("No LLM client is registered for transport OpenAiResponses.", error.Message);
        }
        else
        {
            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => Bind(facade));
            if (scenario == "failure") Assert.Same(original, error);
            else Assert.Contains(scenario == "missing" ? "No LLM connection found" : "concurrency version", error.Message, StringComparison.Ordinal);
        }
        Assert.Equal(0, client.Effects);
        Assert.Equal(0, source.Saves);
    }

    [Fact]
    public async Task CancelledAndRetiredAdmissionNeverReadsSource()
    {
        using var source = new Source(Connection);
        var facade = new LlmProviderService(source, []);
        try
        {
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Bind(facade, cancellationToken: cancellation.Token));
            await facade.DisposeAsync();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => Bind(facade));
            Assert.Equal(0, source.Reads);
            Assert.False(source.Disposed);
        }
        finally { await facade.DisposeAsync(); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CancelledBindingDrainsIgnoringResolutionAndRejectsLateResult(bool retireOwner)
    {
        using var source = new Source(Connection) { Hold = true };
        using var client = new Client();
        var facade = new LlmProviderService(source, [client]);
        using var cancellation = new CancellationTokenSource();
        Task<ILlmContextCacheClient>? binding = null;
        Task? retirement = null;
        try
        {
            binding = Bind(facade, cancellationToken: cancellation.Token);
            await source.Entered.Task.WaitAsync(Bound);
            if (retireOwner) retirement = facade.DisposeAsync().AsTask();
            else await cancellation.CancelAsync();
            await source.Stopped.Task.WaitAsync(Bound);
            Assert.False(binding.IsCompleted);
            if (retirement is not null) Assert.False(retirement.IsCompleted);
            source.Release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => binding.WaitAsync(Bound));
            await (retirement ??= facade.DisposeAsync().AsTask()).WaitAsync(Bound);
            Assert.False(source.Disposed); Assert.False(client.Disposed);
            Assert.Equal(0, client.Effects); Assert.Equal(0, source.Saves);
        }
        finally
        {
            retirement ??= facade.DisposeAsync().AsTask();
            source.Release.TrySetResult();
            try { if (binding is not null) await IgnoreCancellationAsync(binding); }
            finally { await retirement.WaitAsync(Bound); }
        }
    }

    [Fact]
    public async Task ThrowingCancellationCallbackStillDrainsAndPreservesSharedRetirementFailure()
    {
        var original = new InvalidOperationException("Controlled callback failure.");
        using var source = new Source(Connection) { Hold = true, CancellationFailure = original };
        using var client = new Client();
        var facade = new LlmProviderService(source, [client]);
        Task<ILlmContextCacheClient>? binding = null;
        Task? retirement = null;
        try
        {
            binding = Bind(facade);
            await source.Entered.Task.WaitAsync(Bound);
            retirement = facade.DisposeAsync().AsTask();
            Assert.Same(retirement, facade.DisposeAsync().AsTask());
            await source.Stopped.Task.WaitAsync(Bound);
            Assert.False(retirement.IsCompleted);
            source.Release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => binding.WaitAsync(Bound));
            AggregateException error = await Assert.ThrowsAsync<AggregateException>(() => retirement.WaitAsync(Bound));
            Assert.Contains(original, error.Flatten().InnerExceptions);
            Assert.Same(error, await Assert.ThrowsAsync<AggregateException>(() => facade.DisposeAsync().AsTask()));
            Assert.False(source.Disposed); Assert.False(client.Disposed); Assert.Equal(0, client.Effects);
        }
        finally
        {
            retirement ??= facade.DisposeAsync().AsTask();
            source.Release.TrySetResult();
            try { if (binding is not null) await IgnoreCancellationAsync(binding); }
            finally { try { await retirement.WaitAsync(Bound); } catch (AggregateException) when (retirement.IsFaulted) { } }
        }
    }

    [Fact]
    public void ActualForeignBindingCallerRetiresAfterRefusalAndHeldResolution()
    {
        WeakReference generation = ExerciseForeign();
        for (int attempt = 0; generation.IsAlive && attempt < 20; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); }
        Assert.False(generation.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ExerciseForeign()
    {
        var generation = new Generation(Path.GetDirectoryName(typeof(ContextCacheBindingTests).Assembly.Location)!);
        var weak = new WeakReference(generation);
        try
        {
            Assembly? fixture = null;
            foreach (Assembly original in new[] { typeof(ConnectLlmJson).Assembly, typeof(InferenceJson).Assembly, typeof(ContextCacheBindingTests).Assembly })
            {
                Assembly loaded = generation.LoadFromAssemblyPath(original.Location);
                Assert.NotSame(original, loaded); Assert.Equal(original.ManifestModule.ModuleVersionId, loaded.ManifestModule.ModuleVersionId);
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(loaded));
                if (original == typeof(ContextCacheBindingTests).Assembly) fixture = loaded;
            }
            Type caller = fixture!.GetType(typeof(ContextCacheBindingTests).FullName!, true)!;
            var operation = (Task)caller.GetMethod(nameof(ForeignCallerAsync), BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
            operation.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        }
        finally { generation.Unload(); }
        return weak;
    }

    private static async Task ForeignCallerAsync()
    {
        AssemblyLoadContext? generation = AssemblyLoadContext.GetLoadContext(typeof(ContextCacheBindingTests).Assembly);
        Assert.True(generation!.IsCollectible);
        Assert.Same(generation, AssemblyLoadContext.GetLoadContext(BindingMethod().DeclaringType!.Assembly));
        var fixture = new ContextCacheBindingTests();
        await fixture.BindingHasExactPublicSignatureAndRefusesWithoutAdapterEffects();
        await fixture.CancelledBindingDrainsIgnoringResolutionAndRejectsLateResult(false);
        await fixture.CancelledBindingDrainsIgnoringResolutionAndRejectsLateResult(true);
        await fixture.ThrowingCancellationCallbackStillDrainsAndPreservesSharedRetirementFailure();
    }

    private static MethodInfo BindingMethod() => Assert.IsType<MethodInfo>(typeof(LlmProviderService)
        .GetMethod("BindContextCacheAsync", [typeof(string), typeof(CancellationToken)]), exactMatch: false);

    private static Task<ILlmContextCacheClient> Bind(LlmProviderService facade, string connectionId = "connection",
        CancellationToken cancellationToken = default)
    {
        try { return (Task<ILlmContextCacheClient>)BindingMethod().Invoke(facade, [connectionId, cancellationToken])!; }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }

    private static async Task IgnoreCancellationAsync(Task binding)
    {
        try { await binding.WaitAsync(Bound); }
        catch (OperationCanceledException) when (binding.IsCanceled) { }
    }

    private sealed class Source(LlmConnection? value) : ILlmConnectionSource, IDisposable
    {
        internal LlmConnection? Value { get; } = value;
        internal int Reads { get; private set; }
        internal int Saves { get; private set; }
        internal bool Disposed { get; private set; }
        internal bool Hold { get; init; }
        internal Exception? Failure { get; set; }
        internal Exception? CancellationFailure { get; init; }
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
        {
            Reads++;
            using CancellationTokenRegistration registration = cancellationToken.Register(() =>
            { Stopped.TrySetResult(); if (CancellationFailure is not null) throw CancellationFailure; });
            Entered.TrySetResult();
            if (Hold) await Release.Task;
            if (Failure is not null) throw Failure;
            return Value;
        }
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default)
        { Saves++; throw new InvalidOperationException("Binding must not save credentials."); }
        public void Dispose() => Disposed = true;
    }

    private sealed class Client : ILlmClient, ILlmConnectionPreparation, IDisposable
    {
        internal int Effects { get; private set; }
        internal bool Disposed { get; private set; }
        public LlmTransport Transport => LlmTransport.OpenAiResponses;
        public LlmProviderDefinition ProviderDefinition => ProviderDefinitions.OpenAiApiKey;
        public LlmProviderCapabilities GetCapabilities(LlmConnection connection, string? modelId = null) { Effects++; throw new InvalidOperationException("No cache implementation."); }
        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmConnection connection, LlmChatRequest request, CancellationToken cancellationToken = default)
        { Effects++; throw new InvalidOperationException("Binding must not generate."); }
        public Task<IReadOnlyList<LlmModel>> ListModelsAsync(LlmConnection connection, CancellationToken cancellationToken = default)
        { Effects++; throw new InvalidOperationException("Binding must not discover models."); }
        public Task<LlmConnection> PrepareAsync(LlmConnection connection, CancellationToken cancellationToken)
        { Effects++; throw new InvalidOperationException("Binding must not prepare credentials."); }
        public void Dispose() => Disposed = true;
    }

    private sealed class Authenticator : ILlmAuthenticator, IDisposable
    {
        internal int Calls { get; private set; }
        public LlmTransport Transport => LlmTransport.OpenAiResponses;
        public Task<LlmCredential> EnsureFreshAsync(LlmCredential credential, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("Binding must not refresh credentials."); }
        public void Dispose() { }
    }

    private sealed class Generation(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (!name.Name!.StartsWith("Darkspyre.Inference.", StringComparison.Ordinal)) return null;
            string path = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }
}
