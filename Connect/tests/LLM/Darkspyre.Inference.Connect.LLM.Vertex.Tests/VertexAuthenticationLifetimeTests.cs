using Darkspyre.Inference.Connect.LLM.Vertex;
using System.Runtime.CompilerServices;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.LLM.Vertex.Tests;

public sealed class VertexAuthenticationLifetimeTests
{
    private static LlmConnection Connection => new()
    {
        Transport = LlmTransport.GoogleVertexAi,
        Version = "1",
        Endpoint = "https://aiplatform.googleapis.com",
        Vertex = new()
        {
            ProjectId = "project-a",
            Location = "global",
            AuthenticationMode = LlmGoogleAuthenticationMode.ServiceAccount
        },
        Credential = new LlmCredential
        {
            Google = new()
            {
                ServiceAccountJson = """{"type":"service_account","client_email":"fixture@example.invalid","private_key":"fixture-key","token_uri":"https://oauth2.googleapis.com/token"}"""
            }
        }.Serialize()
    };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RetirementCancelsTokenAcquisitionAndRejectsLateTokens(bool cooperative)
    {
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var authentication = Create(accessToken: (_, token) =>
        {
            entered.SetResult(token);
            return cooperative ? release.Task.WaitAsync(token) : release.Task;
        });
        Task<VertexAccess> request = authentication.ResolveAsync(Connection, default);
        try
        {
            CancellationToken token = await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Task retirement = authentication.DisposeAsync().AsTask();
            Assert.True(token.IsCancellationRequested);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => authentication.ResolveAsync(Connection, default));
            if (!cooperative) Assert.False(retirement.IsCompleted);
            release.TrySetResult("late-fixture-token");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            await retirement.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { release.TrySetResult("fixture-token"); }
    }

    [Fact]
    public async Task ConcurrentRetirementSharesCompletionUntilTokenAcquisitionDrains()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var authentication = Create(accessToken: (_, _) => { entered.SetResult(); return release.Task; });
        Task<VertexAccess> request = authentication.ResolveAsync(Connection, default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Task first = authentication.DisposeAsync().AsTask();
            Task second = authentication.DisposeAsync().AsTask();
            Assert.Same(first, second);
            Assert.False(first.IsCompleted);
            release.TrySetResult("late-fixture-token");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            await first.WaitAsync(TimeSpan.FromSeconds(10));
            authentication.Dispose();
        }
        finally { release.TrySetResult("fixture-token"); }
    }

    [Fact]
    public async Task CallerCancellationLeavesOtherAcquisitionsAndOwnerUsable()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        await using var authentication = Create(accessToken: (_, token) =>
        {
            if (Interlocked.Increment(ref calls) != 1) return Task.FromResult("other-fixture-token");
            entered.SetResult();
            return release.Task.WaitAsync(token);
        });
        using var caller = new CancellationTokenSource();
        Task<VertexAccess> request = authentication.ResolveAsync(Connection, caller.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("other-fixture-token", (await authentication.ResolveAsync(Connection, default)).Token);
            await caller.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            Assert.Equal("other-fixture-token", (await authentication.ResolveAsync(Connection, default)).Token);
        }
        finally { release.TrySetResult("fixture-token"); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SdkFailureAfterCancellationPreservesCancellationWithoutSecretDiagnostics(bool retireOwner)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var authentication = Create(accessToken: (_, _) => { entered.SetResult(); return release.Task; });
        using var caller = new CancellationTokenSource();
        Task<VertexAccess> request = authentication.ResolveAsync(Connection, caller.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Task? retirement = retireOwner ? authentication.DisposeAsync().AsTask() : null;
            if (!retireOwner) await caller.CancelAsync();
            release.TrySetException(new InvalidOperationException("secret-sdk-diagnostic"));
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            Assert.DoesNotContain("secret", error.ToString(), StringComparison.Ordinal);
            Assert.Null(error.InnerException);
            if (retirement is not null) await retirement.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { release.TrySetResult("fixture-token"); }
    }

    [Fact]
    public async Task FailedCredentialConstructionCanBeRetried()
    {
        int created = 0;
        await using var authentication = Create(createCredential: (_, _) =>
        {
            if (Interlocked.Increment(ref created) == 1) throw new InvalidOperationException("secret-sdk-diagnostic");
            return GoogleCredential.FromAccessToken("fixture-token");
        });
        var error = await Assert.ThrowsAsync<LlmProviderException>(() => authentication.ResolveAsync(Connection, default));
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("secret", error.ToString(), StringComparison.Ordinal);
        Assert.Equal("fixture-token", (await authentication.ResolveAsync(Connection, default)).Token);
        Assert.Equal("fixture-token", (await authentication.ResolveAsync(Connection, default)).Token);
        Assert.Equal(2, created);
    }

    [Fact]
    public async Task ConcurrentServiceAccountAcquisitionConstructsOneCredential()
    {
        int created = 0;
        await using var authentication = Create(createCredential: (_, _) =>
        {
            Interlocked.Increment(ref created);
            return GoogleCredential.FromAccessToken("fixture-token");
        });
        var results = await Task.WhenAll(Enumerable.Range(0, 24)
            .Select(_ => Task.Run(() => authentication.ResolveAsync(Connection, default))));
        Assert.Equal(1, created);
        Assert.Single(results.Select(result => result.Scope).Distinct(StringComparer.Ordinal));
    }

    [Fact]
    public async Task RetirementDrainsSynchronousConstructionBeforeStartingTokenAcquisition()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        int tokens = 0;
        await using var authentication = Create(createCredential: (_, _) =>
        {
            entered.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            return GoogleCredential.FromAccessToken("fixture-token");
        }, accessToken: (_, _) => { Interlocked.Increment(ref tokens); return Task.FromResult("fixture-token"); });
        Task<VertexAccess> request = Task.Run(() => authentication.ResolveAsync(Connection, default));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Task retirement = authentication.DisposeAsync().AsTask();
            Assert.False(retirement.IsCompleted);
            release.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            await retirement.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, tokens);
        }
        finally { release.Set(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDrainsAmbientDiscoveryBeforeRejectingItsLateCredential(bool retireOwner)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<GoogleCredential>(TaskCreationOptions.RunContinuationsAsynchronously);
        int tokens = 0;
        await using var authentication = Create(ambientCredential: () => { entered.SetResult(); return release.Task; },
            accessToken: (_, _) => { Interlocked.Increment(ref tokens); return Task.FromResult("fixture-token"); });
        using var caller = new CancellationTokenSource();
        Task<VertexAccess> request = authentication.ResolveAsync(Connection with
        {
            Vertex = Connection.Vertex! with { AuthenticationMode = LlmGoogleAuthenticationMode.ApplicationDefault },
            Credential = null
        }, caller.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Task? retirement = retireOwner ? authentication.DisposeAsync().AsTask() : null;
            if (!retireOwner) await caller.CancelAsync();
            Assert.False(request.IsCompleted);
            if (retirement is not null) Assert.False(retirement.IsCompleted);
            release.TrySetResult(GoogleCredential.FromAccessToken("late-fixture-token"));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            if (retirement is not null) await retirement.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, tokens);
        }
        finally { release.TrySetResult(GoogleCredential.FromAccessToken("fixture-token")); }
    }

    [Fact]
    public async Task AsynchronousContainerDisposalRetiresRegisteredAuthentication()
    {
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ServiceCollection();
        services.AddLlmConnectVertex();
        services.AddSingleton<IVertexAuthentication>(_ => Create(accessToken: (_, token) =>
        {
            entered.SetResult(token);
            return release.Task;
        }));
        await using var provider = services.BuildServiceProvider();
        var authentication = provider.GetRequiredService<IVertexAuthentication>();
        Task<VertexAccess> request = authentication.ResolveAsync(Connection, default);
        try
        {
            CancellationToken token = await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Task retirement = provider.DisposeAsync().AsTask();
            Assert.True(token.IsCancellationRequested);
            Assert.False(retirement.IsCompleted);
            release.TrySetResult("late-fixture-token");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            await retirement.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => authentication.ResolveAsync(Connection, default));
        }
        finally { release.TrySetResult("fixture-token"); }
    }

    [Fact]
    public async Task SynchronousRetirementFencesWarmAndColdScopes()
    {
        using var authentication = Create();
        await authentication.ResolveAsync(Connection, default);
        authentication.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => authentication.ResolveAsync(Connection, default));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => authentication.ResolveAsync(Connection with
        { Vertex = Connection.Vertex! with { ProjectId = "project-b" } }, default));
        await authentication.DisposeAsync();
    }

    [Fact]
    public async Task RetirementReleasesCachedCredentialAndFactoryCapture()
    {
        var (authentication, credential) = CachedCredential();
        await using (authentication)
        {
            Assert.True(credential.IsAlive);
            await authentication.DisposeAsync();
            for (int attempt = 0; attempt < 5 && credential.IsAlive; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            Assert.False(credential.IsAlive);
            GC.KeepAlive(authentication);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (VertexAuthentication Authentication, WeakReference Credential) CachedCredential()
    {
        var credential = GoogleCredential.FromAccessToken("fixture-token");
        var authentication = Create(createCredential: (_, _) => credential);
        authentication.ResolveAsync(Connection, default).GetAwaiter().GetResult();
        return (authentication, new WeakReference(credential));
    }

    private static VertexAuthentication Create(Func<string, string, GoogleCredential>? createCredential = null,
        Func<Task<GoogleCredential>>? ambientCredential = null,
        Func<GoogleCredential, CancellationToken, Task<string>>? accessToken = null) =>
        new(createCredential ?? ((_, _) => GoogleCredential.FromAccessToken("fixture-token")),
            () => (Path.Combine(Path.GetTempPath(), "missing-adc-" + Guid.NewGuid().ToString("N")), false),
            ambientCredential, accessToken);
}
