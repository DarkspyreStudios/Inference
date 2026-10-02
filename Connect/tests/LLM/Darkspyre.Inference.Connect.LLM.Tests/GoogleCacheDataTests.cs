using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class GoogleCacheDataTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2030-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    private static readonly string[] PageNames = ["second", "first"];

    [Theory]
    [InlineData(1, "0.0000001s")]
    [InlineData(10000000, "1s")]
    [InlineData(12345678, "1.2345678s")]
    [InlineData(3155760000009999999L, "315576000000.9999999s")]
    public void TtlPreservesEveryClrTickWithoutFloatingPointRounding(long ticks, string expected) =>
        Assert.Equal(expected, GoogleCacheData.Duration(TimeSpan.FromTicks(ticks)));

    [Fact]
    public void TtlOutsidePublishedProtobufDurationRangeRefusesBeforePrefixSnapshot()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GoogleCacheData.Snapshot(Request() with { TimeToLive = TimeSpan.MaxValue }, Now));
    }

    [Fact]
    public void DurationOutsidePublishedProtobufRangeCannotBeEmitted()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GoogleCacheData.Duration(TimeSpan.FromTicks(3155760000010000000L)));
    }

    [Theory]
    [InlineData("2030-01-01T00:00:00Z", "2030-01-01T00:00:00Z")]
    [InlineData("2030-01-01T00:00:00.123456700Z", "2030-01-01T00:00:00.1234567Z")]
    [InlineData("2030-01-01T05:30:00.000000100+05:30", "2030-01-01T00:00:00.0000001Z")]
    [InlineData("2030-01-01T00:00:00.000000000Z", "2030-01-01T00:00:00Z")]
    public void ObservedTimestampIsExactAndUtc(string wire, string normalized) =>
        Assert.Equal(normalized, GoogleCacheData.Timestamp(GoogleCacheData.ReadTimestamp(wire)));

    [Theory]
    [InlineData("2030-01-01T00:00:00.123456789Z")]
    [InlineData("2030-01-01T00:00:00.00000001Z")]
    [InlineData("2030-01-01T00:00:00.0000000001Z")]
    [InlineData("2030-01-01T00:00:00")]
    [InlineData("2030-02-30T00:00:00Z")]
    [InlineData("2030-01-01 00:00:00Z")]
    [InlineData("2030-01-01T00:00:00Z\n")]
    public void InvalidOrSubTickTimestampCannotBecomeRoundedReceipt(string wire) =>
        Assert.Throws<JsonException>(() => GoogleCacheData.ReadTimestamp(wire));

    [Fact]
    public void SnapshotRequiresExplicitLifetimeAndExactReconciliationName()
    {
        var request = Request();
        Assert.Throws<ArgumentException>(() => GoogleCacheData.Snapshot(request, Now));
        Assert.Throws<ArgumentException>(() => GoogleCacheData.Snapshot(request with { TimeToLive = TimeSpan.FromSeconds(1), ExpiresAt = Now.AddDays(1) }, Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => GoogleCacheData.Snapshot(request with { TimeToLive = TimeSpan.Zero }, Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => GoogleCacheData.Snapshot(request with { ExpiresAt = Now }, Now));
        Assert.Throws<ArgumentException>(() => GoogleCacheData.Snapshot(request with { TimeToLive = TimeSpan.FromSeconds(1), DisplayName = "other" }, Now));
        var frozen = GoogleCacheData.Snapshot(request with { TimeToLive = TimeSpan.FromTicks(1), DisplayName = " name " }, Now);
        var body = new JsonObject();
        GoogleCacheData.AddExpiration(body, frozen);
        Assert.Equal("0.0000001s", body["ttl"]!.GetValue<string>());
        Assert.False(body.ContainsKey("expireTime"));
        body = new();
        GoogleCacheData.AddExpiration(body, GoogleCacheData.Snapshot(request with { ExpiresAt = Now.AddTicks(1) }, Now));
        Assert.Equal("2030-01-01T00:00:00.0000001Z", body["expireTime"]!.GetValue<string>());
        Assert.False(body.ContainsKey("ttl"));
    }

    [Fact]
    public void SnapshotOwnsPrefixListsBinaryBytesAndToolSchema()
    {
        byte[] bytes = [1, 2, 3];
        using var schema = JsonDocument.Parse("{\"type\":\"object\"}");
        List<LlmMessageAttachment> attachments = [new LlmAttachment { Kind = LlmBinaryKind.Document, FileName = "a.txt", Data = bytes, MediaType = "text/plain" }];
        List<LlmChatMessage> messages = [new("user", "prefix") { Attachments = attachments }];
        List<LlmClientToolDefinition> tools = [new LlmToolDefinition { Name = "f", InputSchema = schema.RootElement }];
        var frozen = GoogleCacheData.Snapshot(Request() with { TimeToLive = TimeSpan.FromSeconds(1), Messages = messages, Tools = tools }, Now);
        bytes[0] = 9;
        messages.Clear();
        attachments.Clear();
        tools.Clear();
        schema.Dispose();
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.IsType<LlmAttachment>(Assert.Single(Assert.Single(frozen.Messages).Attachments)).Data.ToArray());
        Assert.Equal("object", Assert.IsType<LlmToolDefinition>(Assert.Single(frozen.Tools)).InputSchema.GetProperty("type").GetString());
    }

    [Fact]
    public void ShellPrefixRefusesBeforeCacheCreate()
    {
        Assert.Throws<NotSupportedException>(() => GoogleCacheData.Snapshot(Request() with
        { TimeToLive = TimeSpan.FromSeconds(1), Tools = [new LlmShellTool([])] }, Now));
    }

    [Fact]
    public void ReceiptUsesObservedExpiryNotRequestedLifetimeAndKeepsMissingFactsUnknown()
    {
        var receipt = Read("""{"name":"cachedContents/id","model":"models/m","expireTime":"2030-01-01T00:00:01Z"}""");
        Assert.Equal(Now.AddSeconds(1), receipt.ExpiresAt);
        Assert.Equal(receipt.ExpiresAt, receipt.Reference.ExpiresAt);
        Assert.Equal("m", receipt.Reference.ModelId);
        Assert.Equal("bound-scope", receipt.Reference.ScopeId);
        Assert.Null(receipt.CreatedAt);
        Assert.Null(receipt.UpdatedAt);
        Assert.Null(receipt.DisplayName);
        Assert.Null(receipt.Usage);
        receipt = Read("""{"name":"cachedContents/id","model":"models/m","expireTime":"2030-01-01T00:00:01Z","usageMetadata":{}}""");
        Assert.NotNull(receipt.Usage);
        Assert.Null(receipt.Usage.TotalTokens);
    }

    [Fact]
    public void GeminiAndVertexKeepDifferentActualUsageFacts()
    {
        const string wire = """{"name":"cachedContents/id","model":"models/m","expireTime":"2030-01-01T00:00:01Z","usageMetadata":{"totalTokenCount":0,"textCount":0,"imageCount":0,"audioDurationSeconds":0,"videoDurationSeconds":2}}""";
        var gemini = Read(wire).Usage!;
        Assert.Equal(0, gemini.TotalTokens);
        Assert.Null(gemini.TextCharacters);
        Assert.Null(gemini.ImageCount);
        Assert.Null(gemini.AudioDuration);
        Assert.Null(gemini.VideoDuration);
        var vertex = Read(wire, vertex: true).Usage!;
        Assert.Equal(0, vertex.TextCharacters);
        Assert.Equal(0, vertex.ImageCount);
        Assert.Equal(TimeSpan.Zero, vertex.AudioDuration);
        Assert.Equal(TimeSpan.FromSeconds(2), vertex.VideoDuration);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"name\":\"cachedContents/id\",\"model\":\"models/m\"}")]
    [InlineData("{\"name\":\"cachedContents/id\",\"model\":\"models/m\",\"expireTime\":null}")]
    [InlineData("{\"name\":\"cachedContents/id\",\"model\":\"models/m\",\"expireTime\":\"2030-01-01T00:00:01Z\",\"usageMetadata\":{\"totalTokenCount\":-1}}")]
    [InlineData("{\"name\":\"cachedContents/id\",\"model\":\"models/m\",\"expireTime\":\"2030-01-01T00:00:01Z\",\"usageMetadata\":{\"totalTokenCount\":1.5}}")]
    [InlineData("{\"name\":\"cachedContents/id\",\"model\":\"models/m\",\"expireTime\":\"2030-01-01T00:00:01Z\",\"usageMetadata\":{\"audioDurationSeconds\":9223372036854775807}}")]
    public void MalformedReceiptFactsDoNotBecomeDefaults(string wire) => Assert.Throws<JsonException>(() => Read(wire, vertex: true));

    [Fact]
    public void ReceiptRequiresActualResourceAndModelValidationByCaller()
    {
        using var document = JsonDocument.Parse("""{"name":"cachedContents/id","model":"models/m","expireTime":"2030-01-01T00:00:01Z"}""");
        int resources = 0, models = 0;
        _ = GoogleCacheData.Receipt(document.RootElement, "scope", name => { Assert.Equal("cachedContents/id", name); resources++; },
            model => { Assert.Equal("models/m", model); models++; return "m"; }, vertex: false);
        Assert.Equal(1, resources);
        Assert.Equal(1, models);
        var error = new InvalidOperationException("scope mismatch");
        Assert.Same(error, Assert.Throws<InvalidOperationException>(() => GoogleCacheData.Receipt(document.RootElement, "scope",
            _ => throw error, _ => throw new InvalidOperationException("must not run"), vertex: false)));
    }

    [Fact]
    public void OptionalProtoJsonNullFactsStayUnknownAndIntegralStringsStayExact()
    {
        var receipt = Read("""{"name":"cachedContents/id","model":"models/m","expireTime":"2030-01-01T00:00:01Z","createTime":null,"updateTime":null,"displayName":null,"usageMetadata":null}""");
        Assert.Null(receipt.Usage);
        Assert.Null(receipt.CreatedAt);
        Assert.Null(receipt.UpdatedAt);
        Assert.Null(receipt.DisplayName);
        receipt = Read("""{"name":"cachedContents/id","model":"models/m","expireTime":"2030-01-01T00:00:01Z","usageMetadata":{"totalTokenCount":"1e2","textCount":null,"imageCount":"0"}}""", vertex: true);
        Assert.Equal(100, receipt.Usage!.TotalTokens);
        Assert.Null(receipt.Usage.TextCharacters);
        Assert.Equal(0, receipt.Usage.ImageCount);
    }

    [Theory]
    [InlineData("1.000000000000000000000000000001")]
    [InlineData("1e-100000")]
    [InlineData("1e100000")]
    [InlineData("\"1e100000000000000000000000000000\"")]
    [InlineData("9223372036854775808")]
    public void FractionalOrOverflowingCountsCannotRoundIntoIntegerFacts(string count)
    {
        string wire = "{\"name\":\"cachedContents/id\",\"model\":\"models/m\",\"expireTime\":\"2030-01-01T00:00:01Z\",\"usageMetadata\":{\"totalTokenCount\":" + count + "}}";
        Assert.Throws<JsonException>(() => Read(wire));
    }

    [Fact]
    public void OnePagePreservesOrderTokenAndDocumentedPageMaximumWithoutChasing()
    {
        using var document = JsonDocument.Parse("""{"cachedContents":[{"name":"second"},{"name":"first"}],"nextPageToken":"opaque+/="}""");
        var page = GoogleCacheData.Page(document.RootElement, entry =>
            new(new(entry.GetProperty("name").GetString()!, "m", "s", Now), null, null, null, Now, null));
        Assert.Equal(PageNames, page.Items.Select(item => item.Reference.ResourceName));
        Assert.Equal("opaque+/=", page.NextPageToken);
        GoogleCacheData.ValidatePage(new(1000));
        Assert.Throws<ArgumentOutOfRangeException>(() => GoogleCacheData.ValidatePage(new(1001)));
        using var empty = JsonDocument.Parse("{\"nextPageToken\":\"not-terminal\"}");
        page = GoogleCacheData.Page(empty.RootElement, _ => throw new InvalidOperationException("No item exists."));
        Assert.Empty(page.Items);
        Assert.Equal("not-terminal", page.NextPageToken);
        using var malformed = JsonDocument.Parse("{\"cachedContents\":[null]}");
        Assert.Throws<JsonException>(() => GoogleCacheData.Page(malformed.RootElement, entry =>
            GoogleCacheData.Receipt(entry, "s", _ => { }, _ => "m", vertex: false)));
    }

    [Fact]
    public void ActualForeignReceiptParserAndOwnedReceiptMetadataCollectTogether()
    {
        WeakReference[] roots = ExerciseForeign();
        for (int attempt = 0; roots.Any(root => root.IsAlive) && attempt < 20; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Thread.Sleep(10);
        }
        Assert.All(roots, root => Assert.False(root.IsAlive));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] ExerciseForeign()
    {
        var generation = new Generation(Path.GetDirectoryName(typeof(GoogleCacheDataTests).Assembly.Location)!);
        var roots = new List<WeakReference> { new(generation) };
        try
        {
            Assembly fixture = generation.LoadFromAssemblyPath(typeof(GoogleCacheDataTests).Assembly.Location);
            Assembly connect = generation.LoadFromAssemblyPath(typeof(ConnectLlmJson).Assembly.Location);
            Assembly common = generation.LoadFromAssemblyPath(typeof(InferenceJson).Assembly.Location);
            foreach ((Assembly source, Assembly actual) in new[]
                { (typeof(GoogleCacheDataTests).Assembly, fixture), (typeof(ConnectLlmJson).Assembly, connect), (typeof(InferenceJson).Assembly, common) })
            {
                Assert.NotSame(source, actual);
                Assert.Equal(source.ManifestModule.ModuleVersionId, actual.ManifestModule.ModuleVersionId);
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(actual));
                roots.Add(new(actual));
            }
            Type caller = fixture.GetType(typeof(GoogleCacheDataTests).FullName!, throwOnError: true)!;
            MethodInfo method = caller.GetMethod(nameof(ExerciseReceiptInOwningGeneration))!;
            roots.Add(new(caller));
            roots.Add(new(method));
            roots.AddRange((WeakReference[])method.Invoke(null, null)!);
        }
        finally { generation.Unload(); }
        return roots.ToArray();
    }

    public static WeakReference[] ExerciseReceiptInOwningGeneration()
    {
        AssemblyLoadContext generation = AssemblyLoadContext.GetLoadContext(typeof(GoogleCacheDataTests).Assembly)!;
        if (!generation.IsCollectible || !ReferenceEquals(generation, AssemblyLoadContext.GetLoadContext(typeof(GoogleCacheData).Assembly))
            || !ReferenceEquals(generation, AssemblyLoadContext.GetLoadContext(typeof(LlmExplicitCacheReference).Assembly)))
            throw new InvalidOperationException("The actual parser, Common and fixture must share the private generation.");
        var receipt = Read("""{"name":"cachedContents/id","model":"models/m","expireTime":"2030-01-01T00:00:01.000000100Z","usageMetadata":{"totalTokenCount":0}}""");
        JsonTypeInfo metadata = ConnectLlmJson.LlmContextCache;
        if (!ReferenceEquals(metadata.Type.Assembly, typeof(GoogleCacheData).Assembly))
            throw new InvalidOperationException("The cache receipt metadata must belong to the actual Connect assembly.");
        string json = JsonSerializer.Serialize(receipt, metadata);
        object roundTrip = JsonSerializer.Deserialize(json, metadata)!;
        if (!ReferenceEquals(roundTrip.GetType().Assembly, typeof(GoogleCacheData).Assembly)
            || receipt.Reference.ExpiresAt != Now.AddSeconds(1).AddTicks(1) || receipt.Usage!.TotalTokens != 0)
            throw new InvalidOperationException("The actual parser and owning codec did not preserve observed receipt facts.");
        JsonTypeInfo commonMetadata = InferenceJson.LlmExplicitCacheReference;
        if (!ReferenceEquals(commonMetadata.Type.Assembly, typeof(LlmExplicitCacheReference).Assembly)
            || !ReferenceEquals(metadata.Properties.Single(property => property.Name == nameof(LlmContextCache.Reference)).PropertyType.Assembly,
                typeof(LlmExplicitCacheReference).Assembly))
            throw new InvalidOperationException("The nested reference metadata must use the actual foreign Common contract.");
        return [new(typeof(GoogleCacheData)), new(metadata), new(commonMetadata), new(receipt), new(roundTrip), new(receipt.Reference)];
    }

    private sealed class Generation(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name is not ("Darkspyre.Inference.Common" or "Darkspyre.Inference.Connect.LLM" or "Darkspyre.Inference.Connect.LLM.Tests")) return null;
            return LoadFromAssemblyPath(Path.Combine(directory, name.Name + ".dll"));
        }
    }

    private static LlmContextCacheCreate Request() => new() { ModelId = "m", Correlation = new("operation", " name ") };

    private static LlmContextCache Read(string wire, bool vertex = false)
    {
        using var document = JsonDocument.Parse(wire);
        return GoogleCacheData.Receipt(document.RootElement, "bound-scope", _ => { }, model => model["models/".Length..], vertex);
    }
}
