using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class CodexVersionParityTests
{
    private const string TargetVersion = "0.159.3";
    private const string Completion = """
        data: {"type":"response.completed","response":{"id":"response","status":"completed","output":[]}}

        """;

    [Theory]
    [InlineData("catalog")]
    [InlineData("generation")]
    [InlineData("usage")]
    public async Task EveryProductionPathEmitsTheSelectedReleaseVersion(string operation)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = new OpenAiCodexLlmClient(http);
        LlmConnection connection = DirectCodexTests.Connection() with
        {
            AdditionalHeaders = new Dictionary<string, string> { ["X-Caller-Trace"] = "trace" }
        };
        await InvokeAsync(client, connection, operation);

        Assert.Equal(TargetVersion, Assert.Single(handler.Headers["version"]));
        Assert.Equal("Darkspyre.Inference.Connect.LLM/" + TargetVersion,
            Assert.Single(handler.Headers["User-Agent"]));
        Assert.Equal("trace", Assert.Single(handler.Headers["X-Caller-Trace"]));
        Assert.Equal("Bearer token", Assert.Single(handler.Headers["Authorization"]));
        Assert.Equal("account", Assert.Single(handler.Headers["ChatGPT-Account-ID"]));
        Assert.Equal(operation switch
        {
            "catalog" => "/backend-api/codex/models?client_version=" + TargetVersion,
            "generation" => "/backend-api/codex/responses",
            _ => "/backend-api/wham/usage"
        }, handler.Uri?.PathAndQuery);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task VersionSensitiveCatalogPublishesOnlyTheModelsReturnedForTheSelectedVersion()
    {
        using var handler = new Handler(versionSensitiveCatalog: true);
        using var http = new HttpClient(handler);
        var client = new OpenAiCodexLlmClient(http);
        IReadOnlyList<LlmModel> models = await client.ListModelsAsync(DirectCodexTests.Connection());

        Assert.Equal("release-selected-model", Assert.Single(models).Id);
        Assert.Equal(123456, models[0].MaxContextTokens);
        Assert.Null(models[0].MaxOutputTokens);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("version", "catalog")]
    [InlineData("VeRsIoN", "generation")]
    [InlineData("version", "usage")]
    [InlineData("User-Agent", "catalog")]
    [InlineData("uSeR-aGeNt", "generation")]
    [InlineData("User-Agent", "usage")]
    public async Task ConflictingVersionHeadersRefuseBeforeHttp(string header, string operation)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = new OpenAiCodexLlmClient(http);
        LlmConnection connection = DirectCodexTests.Connection() with
        {
            AdditionalHeaders = new Dictionary<string, string> { [header] = "caller/0.155.1" }
        };

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => InvokeAsync(client, connection, operation));
        Assert.Contains("reserved", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("caller/0.155.1", error.Message);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("{\"context_window\":200000,\"max_context_window\":400000}", 200000L)]
    [InlineData("{\"context_window\":null,\"max_context_window\":400000}", 400000L)]
    [InlineData("{\"max_context_window\":400000}", 400000L)]
    [InlineData("{\"context_window\":null,\"max_context_window\":null}", null)]
    [InlineData("{}", null)]
    [InlineData("{\"context_window\":0,\"max_context_window\":400000}", null)]
    public async Task CatalogLimitsUseTheUpstreamNullableFallbackWithoutInventingOccupancy(
        string properties, long? expected)
    {
        JsonObject entry = JsonNode.Parse(properties)!.AsObject();
        entry["slug"] = "model";
        var catalog = new JsonObject { ["models"] = new JsonArray(entry) };
        using var handler = new Handler(catalog: catalog.ToJsonString());
        using var http = new HttpClient(handler);
        LlmModel model = Assert.Single(await new OpenAiCodexLlmClient(http)
            .ListModelsAsync(DirectCodexTests.Connection()));

        Assert.Equal(expected, model.MaxContextTokens);
        Assert.Null(model.MaxOutputTokens);
    }

    private static async Task InvokeAsync(OpenAiCodexLlmClient client, LlmConnection connection, string operation)
    {
        switch (operation)
        {
            case "catalog":
                await client.ListModelsAsync(connection);
                break;
            case "generation":
                await foreach (LlmStreamEvent _ in client.StreamAsync(connection,
                    new() { ModelId = "model", Messages = [new("user", "hello")] })) { }
                break;
            case "usage":
                await client.GetAccountUsageAsync(connection);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    private sealed class Handler(bool versionSensitiveCatalog = false, string? catalog = null) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public Uri? Uri { get; private set; }
        public Dictionary<string, string[]> Headers { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Uri = request.RequestUri;
            Headers = request.Headers.ToDictionary(header => header.Key, header => header.Value.ToArray(),
                StringComparer.OrdinalIgnoreCase);
            bool isCatalog = Uri!.AbsolutePath.EndsWith("/models", StringComparison.Ordinal);
            string body = isCatalog
                ? catalog ?? (versionSensitiveCatalog && Uri.Query != "?client_version=" + TargetVersion
                    ? "{\"models\":[{\"slug\":\"old-version-model\"}]}"
                    : "{\"models\":[{\"slug\":\"release-selected-model\",\"context_window\":123456}]}")
                : request.Method == HttpMethod.Post ? Completion : "{}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8,
                    request.Method == HttpMethod.Post ? "text/event-stream" : "application/json")
            });
        }
    }
}
