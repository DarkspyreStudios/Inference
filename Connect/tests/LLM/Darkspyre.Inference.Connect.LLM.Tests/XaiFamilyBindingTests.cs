using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class XaiFamilyBindingTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    private static LlmConnection Connection => new()
    {
        Version = "v1",
        Transport = LlmTransport.XaiResponses,
        Endpoint = "https://api.x.ai/v1",
        Xai = new(),
        Credential = new LlmCredential { OAuth = new() { AccessToken = "old", AccountId = "account" } }.Serialize()
    };

    [Theory]
    [InlineData("transport")]
    [InlineData("region")]
    [InlineData("api-key")]
    public async Task KnownIncompatibilityRefusesBeforeRefreshOrSave(string kind)
    {
        var source = new Source
        {
            Current = kind switch
            {
                "transport" => Connection with { Transport = LlmTransport.OpenAiCodex },
                "region" => Connection with { Endpoint = "https://us.api.x.ai/v1" },
                _ => Connection with { Credential = new LlmCredential { ApiKey = "not-oauth" }.Serialize() }
            }
        };
        var authenticator = new Authenticator();
        await using var access = new LlmConnectionAccess(source, [authenticator]);
        var binding = new XaiFamilyBinding(new("connection"), access);
        if (kind == "api-key")
        {
            InferenceException error = await Assert.ThrowsAsync<InferenceException>(() => binding.AcquireAsync());
            Assert.Equal(InferenceFailureCode.AuthenticationRequired, error.Code);
        }
        else
            await Assert.ThrowsAsync<ArgumentException>(() => binding.AcquireAsync());
        Assert.Equal(1, source.Reads);
        Assert.Equal(0, authenticator.Calls);
        Assert.Equal(0, source.Writes);
    }

    [Fact]
    public async Task BoundCompositeUsesOneSnapshotAndNeverReacquiresAuthorization()
    {
        var source = new Source();
        var authenticator = new Authenticator();
        await using var access = new LlmConnectionAccess(source, [authenticator]);
        var binding = new XaiFamilyBinding(new("connection"), access);
        int validations = 0;
        XaiFamilyBinding entered = await binding.AcquireBoundAsync(_ => validations++);
        LlmConnection snapshot = await entered.AcquireAsync();
        source.Current = Connection with { Version = "changed", Endpoint = "https://us.api.x.ai/v1", Xai = new() { Region = XaiRegion.UnitedStates } };
        Assert.Same(snapshot, await entered.AcquireAsync());
        Assert.Equal("fresh", (await entered.Authorization.GetAsync()).Value);
        Assert.Equal("https://api.x.ai/v1", snapshot.Endpoint);
        Assert.Equal("v1", snapshot.Version);
        Assert.Equal(1, source.Reads);
        Assert.Equal(1, authenticator.Calls);
        Assert.Equal(1, source.Writes);
        Assert.Equal("v1", source.ExpectedVersion);
        Assert.Equal(2, validations);
        await binding.AcquireAsync();
        Assert.Equal(2, source.Reads);
    }

    [Fact]
    public async Task ValidationTakingOwnerFreezesCallerHeadersOptionsAndCapabilitiesBeforeAwait()
    {
        var headers = new Dictionary<string, string> { ["header"] = "initial" };
        var levels = new HashSet<LlmReasoningLevel> { LlmReasoningLevel.Low };
        using JsonDocument document = JsonDocument.Parse("{\"value\":1}");
        var options = new Dictionary<string, JsonElement> { ["option"] = document.RootElement };
        var overrides = new Dictionary<string, LlmCapabilityOverrides> { ["model"] = new() { ReasoningLevels = levels } };
        var source = new Source { Current = Connection with { AdditionalHeaders = headers, ProviderOptions = options, ModelCapabilityOverrides = overrides } };
        var authenticator = new Authenticator { Release = new() };
        await using var access = new LlmConnectionAccess(source, [authenticator]);
        Task<LlmConnection> pending = access.AcquireAsync("connection", _ => { });
        await authenticator.Entered.Task.WaitAsync(Bound);
        headers["header"] = "changed";
        levels.Clear();
        overrides.Clear();
        options.Clear();
        document.Dispose();
        authenticator.Release.SetResult();
        LlmConnection snapshot = await pending.WaitAsync(Bound);
        Assert.Equal("initial", snapshot.AdditionalHeaders["header"]);
        Assert.Equal(1, snapshot.ProviderOptions["option"].GetProperty("value").GetInt32());
        Assert.Contains(LlmReasoningLevel.Low, snapshot.ModelCapabilityOverrides["model"].ReasoningLevels!);
    }

    [Fact]
    public async Task VersionConflictPropagatesWithoutRetryOrSecondResolve()
    {
        var source = new Source { RefuseSave = true };
        var authenticator = new Authenticator();
        await using var access = new LlmConnectionAccess(source, [authenticator]);
        await Assert.ThrowsAsync<ConflictException>(() => new XaiFamilyBinding(new("connection"), access).AcquireAsync());
        Assert.Equal(1, source.Reads);
        Assert.Equal(1, authenticator.Calls);
        Assert.Equal(0, source.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IgnoringResolveIsDrainedAndCannotValidateOrRefreshAfterRetirement(bool cancelCaller)
    {
        var source = new Source { Release = new() };
        var authenticator = new Authenticator();
        using var cancellation = new CancellationTokenSource();
        var access = new LlmConnectionAccess(source, [authenticator]);
        int validations = 0;
        Task<LlmConnection> pending = access.AcquireAsync("connection", _ => validations++, cancellation.Token);
        await source.Entered.Task.WaitAsync(Bound);
        if (cancelCaller)
            cancellation.Cancel();
        Task retirement = access.DisposeAsync().AsTask();
        Assert.False(retirement.IsCompleted);
        source.Release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Bound));
        await retirement.WaitAsync(Bound);
        Assert.Equal(0, validations);
        Assert.Equal(0, authenticator.Calls);
        Assert.Equal(0, source.Writes);
        Assert.False(source.Disposed);
        Assert.False(authenticator.Disposed);
    }

    [Fact]
    public async Task RuntimeBindingRefusesInActualCallerOwnedGeneratedEnvelopeBeforeCallbacks()
    {
        var source = new Source();
        await using var access = new LlmConnectionAccess(source, []);
        var binding = new XaiFamilyBinding(new("connection"), access);
        var options = new JsonSerializerOptions();
        options.Converters.Add(Assert.Single(ConnectLlmJson.Connection.Options.Converters,
            converter => converter.CanConvert(typeof(XaiFamilyBinding))));
        var caller = new XaiBindingCallerJson(options);
        Assert.Equal("{\"Binding\":null}", JsonSerializer.Serialize(new XaiBindingEnvelope(null), caller.XaiBindingEnvelope));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(new XaiBindingEnvelope(binding), caller.XaiBindingEnvelope));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize("{\"Binding\":{}}", caller.XaiBindingEnvelope));
        Assert.Equal(0, source.Reads);
        Assert.Null(typeof(ConnectLlmJson).GetProperty("XaiFamilyBinding"));
    }

    [Fact]
    public void ActualForeignSelectionMetadataRetires()
    {
        WeakReference weak = ExerciseForeign();
        for (int attempt = 0; weak.IsAlive && attempt < 20; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Thread.Sleep(10);
        }
        Assert.False(weak.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ExerciseForeign()
    {
        var generation = new AssemblyLoadContext("xAI selection metadata", true);
        generation.Resolving += (_, name) => name.Name == typeof(InferenceJson).Assembly.GetName().Name
            ? generation.LoadFromAssemblyPath(typeof(InferenceJson).Assembly.Location) : null;
        var weak = new WeakReference(generation);
        try
        {
            Assembly assembly = generation.LoadFromAssemblyPath(typeof(ConnectLlmJson).Assembly.Location);
            Assert.NotSame(typeof(ConnectLlmJson).Assembly, assembly);
            Assert.Equal(typeof(ConnectLlmJson).Assembly.ManifestModule.ModuleVersionId, assembly.ManifestModule.ModuleVersionId);
            var metadata = (JsonTypeInfo)assembly.GetType(typeof(ConnectLlmJson).FullName!, true)!
                .GetProperty("XaiConnectionSelection", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
            Assert.Same(assembly, metadata.Type.Assembly);
            Assert.Same(assembly, metadata.Options.TypeInfoResolver!.GetType().Assembly);
            object value = JsonSerializer.Deserialize("{\"ConnectionId\":\"connection\"}", metadata)!;
            Assert.Same(assembly, value.GetType().Assembly);
            Assert.Equal("{\"ConnectionId\":\"connection\"}", JsonSerializer.Serialize(value, metadata));
        }
        finally
        {
            generation.Unload();
        }
        return weak;
    }

    private sealed class Source : ILlmConnectionSource, IDisposable
    {
        public LlmConnection Current { get; set; } = Connection;
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public string? ExpectedVersion { get; private set; }
        public bool RefuseSave { get; init; }
        public bool Disposed { get; private set; }
        public TaskCompletionSource? Release { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
        {
            Reads++;
            Entered.TrySetResult();
            if (Release is not null)
                await Release.Task;
            return Current;
        }
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default)
        {
            ExpectedVersion = expectedVersion;
            if (RefuseSave)
                throw new ConflictException();
            Writes++;
            Current = Current with { Credential = credentialJson, Version = "v2" };
            return Task.CompletedTask;
        }
        public void Dispose() => Disposed = true;
    }

    private sealed class Authenticator : ILlmAuthenticator, IDisposable
    {
        public LlmTransport Transport => LlmTransport.XaiResponses;
        public int Calls { get; private set; }
        public bool Disposed { get; private set; }
        public TaskCompletionSource? Release { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<LlmCredential> EnsureFreshAsync(LlmCredential credential, CancellationToken cancellationToken = default)
        {
            Calls++;
            Entered.TrySetResult();
            if (Release is not null)
                await Release.Task;
            return credential with { OAuth = credential.OAuth! with { AccessToken = "fresh" } };
        }
        public void Dispose() => Disposed = true;
    }

    private sealed class ConflictException : InvalidOperationException;
}

internal sealed record XaiBindingEnvelope(XaiFamilyBinding? Binding);

[JsonSerializable(typeof(XaiBindingEnvelope))]
internal sealed partial class XaiBindingCallerJson : JsonSerializerContext;
