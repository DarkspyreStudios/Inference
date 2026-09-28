using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.ConsoleTester;
using SecureStore;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.LLM.Bedrock.Tests;

public sealed class ConsoleTesterTests
{
    private static ConsoleTesterProviders Providers { get; } = CreateProviders();
    private static ConsoleInvocation Parse(string[] args) => ConsoleInvocation.Parse(args, Providers);

    private static ConsoleTesterProviders CreateProviders()
    {
        var services = new ServiceCollection();
        services.AddLlmConnect();
        services.AddLlmConnectBedrock();
        using var container = services.BuildServiceProvider();
        return new(container.GetServices<ILlmClient>());
    }

    [Fact]
    public async Task AttachmentSmokeToolReturnsProviderNeutralBytes()
    {
        LlmAgentToolDefinition imageOnly = Program.CreateAttachmentTool(supportsDocuments: false);
        Assert.Equal("darkspyre_inference_connect_attachment", imageOnly.Name);
        Assert.Equal(
            ["image"],
            imageOnly.InputSchema.GetProperty("properties")
                .GetProperty("kind")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(value => value.GetString()));

        using JsonDocument imageArguments = JsonDocument.Parse("""{"kind":"image"}""");
        LlmToolOutput imageResult = await Program.ExecuteToolAsync(
            CreateToolCall("darkspyre_inference_connect_attachment", imageArguments.RootElement),
            CancellationToken.None);
        LlmToolOutputBinary image = Assert.Single(imageResult.Parts.OfType<LlmToolOutputBinary>());
        Assert.Equal((LlmBinaryKind.Image, "image/png"), (image.Kind, image.MediaType));
        Assert.Equal("smoke-test.png", image.FileName);
        Assert.Equal(0x89, image.Data.Span[0]);

        LlmAgentToolDefinition withDocuments = Program.CreateAttachmentTool(supportsDocuments: true);
        Assert.Equal(
            ["image", "document"],
            withDocuments.InputSchema.GetProperty("properties")
                .GetProperty("kind")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(value => value.GetString()));

        using JsonDocument documentArguments = JsonDocument.Parse("""{"kind":"document"}""");
        LlmToolOutput documentResult = await Program.ExecuteToolAsync(
            CreateToolCall("darkspyre_inference_connect_attachment", documentArguments.RootElement),
            CancellationToken.None);
        LlmToolOutputBinary document = Assert.Single(documentResult.Parts.OfType<LlmToolOutputBinary>());
        Assert.Equal((LlmBinaryKind.Document, "text/plain"), (document.Kind, document.MediaType));
        Assert.Equal("smoke-test.txt", document.FileName);
        Assert.Equal(
            "Darkspyre.Inference.Connect.LLM attachment smoke test document.",
            System.Text.Encoding.UTF8.GetString(document.Data.Span));
    }

    [Fact]
    public void InvocationRequiresAndResolvesProvider()
    {
        ConsoleInvocation invocation = Parse(
        [
            "prompt",
            "--model",
            "gpt-test",
            "--provider",
            "codex"
        ]);

        Assert.Equal("prompt", invocation.Command);
        Assert.Equal("codex", invocation.Provider.Id);
        Assert.Equal(["--model", "gpt-test"], invocation.CommandArguments);
        Assert.Throws<ArgumentException>(() => Parse(["status"]));
        Assert.Throws<ArgumentException>(() =>
            Parse(["status", "--provider", "unknown"]));

        ConsoleInvocation bedrock = Parse(
            ["login", "--provider", "bedrock", "--profile", "test", "--region", "us-east-1"]);
        Assert.Equal(LlmTransport.AwsBedrockConverse, bedrock.Provider.Transport);
        Assert.Equal(
            ["--profile", "test", "--region", "us-east-1"],
            bedrock.CommandArguments);

        ConsoleInvocation antigravity = Parse(
            ["usage", "--provider", "agy"]);
        Assert.Equal(LlmTransport.GoogleAntigravityCli, antigravity.Provider.Transport);

        ConsoleInvocation agentCore = Parse(
        [
            "agent",
            "--provider",
            "agentcore",
            "--harness-arn",
            "arn:aws:bedrock-agentcore:us-east-1:123456789012:harness/test",
            "--model",
            "default"
        ]);
        Assert.Equal(LlmTransport.AwsBedrockAgentCore, agentCore.Provider.Transport);
        Assert.Equal(
            "arn:aws:bedrock-agentcore:us-east-1:123456789012:harness/test",
            agentCore.EndpointOverride);
        Assert.Equal(["--model", "default"], agentCore.CommandArguments);
        Assert.Throws<ArgumentException>(() => Parse(
            ["agent", "--provider", "agentcore", "--model", "default"]));
    }

    [Fact]
    public void BedrockLoginRequiresProfileAndRegion()
    {
        Program.BedrockLoginOptions options = Program.ParseBedrockLoginOptions(
            ["--profile", "test", "--region", "us-east-1"]);

        Assert.Equal("test", options.Profile);
        Assert.Equal("us-east-1", options.Region);
        Assert.Throws<ArgumentException>(() =>
            Program.ParseBedrockLoginOptions(["--profile", "test"]));
    }

    [Fact]
    public async Task SecureConnectionSourcePersistsCredentialsAndRejectsStaleVersions()
    {
        var vault = new InMemoryCredentialStore();
        var source = new SecureConnectionSource(vault, Providers);
        ConsoleTesterProvider bedrock = Providers.Resolve("bedrock");
        LlmConnection original = Assert.IsType<LlmConnection>(
            await source.GetConnectionAsync(bedrock.ConnectionId));
        string credential = new LlmCredential
        {
            Aws = new LlmAwsCredentials { Profile = "test", Region = "us-east-1" }
        }.Serialize();

        await source.SaveCredentialAsync(
            bedrock.ConnectionId,
            credential,
            original.Version);

        var restartedSource = new SecureConnectionSource(vault, Providers);
        LlmConnection persisted = Assert.IsType<LlmConnection>(
            await restartedSource.GetConnectionAsync(bedrock.ConnectionId));
        Assert.NotEqual(original.Version, persisted.Version);
        Assert.NotNull(LlmCredential.Parse(persisted.Credential)?.Aws);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            source.SaveCredentialAsync(
                bedrock.ConnectionId,
                credential,
                original.Version));
    }

    [Fact]
    public async Task SecureConnectionSourceKeepsProviderCredentialsSeparate()
    {
        var vault = new InMemoryCredentialStore();
        await vault.SetAsync(
            "provider-credential-claude",
            new LlmCredential
            {
                OAuth = new LlmOAuthTokens { AccessToken = "legacy-catalog-token" }
            }.Serialize());
        var source = new SecureConnectionSource(vault, Providers);
        ConsoleTesterProvider codex = Providers.Resolve("codex");
        ConsoleTesterProvider claude = Providers.Resolve("claude");
        ConsoleTesterProvider bedrock = Providers.Resolve("bedrock");
        LlmConnection bedrockConnection = Assert.IsType<LlmConnection>(
            await source.GetConnectionAsync(bedrock.ConnectionId));
        await source.SaveCredentialAsync(
            bedrock.ConnectionId,
            new LlmCredential
            {
                Aws = new LlmAwsCredentials { Profile = "test", Region = "us-east-1" }
            }.Serialize(),
            bedrockConnection.Version);

        LlmConnection codexConnection = Assert.IsType<LlmConnection>(
            await source.GetConnectionAsync(codex.ConnectionId));
        await source.SaveCredentialAsync(
            codex.ConnectionId,
            new LlmCredential
            {
                OAuth = new LlmOAuthTokens { AccessToken = "host-owned-codex", AccountId = "account" }
            }.Serialize(),
            codexConnection.Version);

        Assert.Equal("host-owned-codex", LlmCredential.Parse((await source.GetConnectionAsync(codex.ConnectionId))?.Credential)?.OAuth?.AccessToken);
        Assert.Null((await source.GetConnectionAsync(claude.ConnectionId))?.Credential);
        Assert.NotNull((await source.GetConnectionAsync(bedrock.ConnectionId))?.Credential);
    }

    private static LlmAgentToolCall CreateToolCall(string name, JsonElement arguments) => new(
        "thread",
        "turn",
        "call",
        Namespace: null,
        name,
        arguments.Clone());
}
