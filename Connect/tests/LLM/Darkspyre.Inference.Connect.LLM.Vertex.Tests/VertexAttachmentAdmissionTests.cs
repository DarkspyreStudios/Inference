using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Vertex;

namespace Darkspyre.Inference.Connect.LLM.Vertex.Tests;

public sealed class VertexAttachmentAdmissionTests
{
    [Theory]
    [InlineData("stream")]
    [InlineData("complete")]
    [InlineData("count")]
    public async Task DirectRequestFreezesAttachmentsBeforeAuthentication(string operation)
    {
        var authentication = new Authentication();
        using var handler = new Handler(operation);
        using var http = new HttpClient(handler);
        var client = new VertexLlmClient(http, authentication);
        byte[] bytes = Encoding.UTF8.GetBytes("original");
        List<LlmMessageAttachment> attachments = [new LlmAttachment
        { Kind = LlmBinaryKind.Document, FileName = "original.txt", MediaType = "text/plain", Data = bytes }];
        var request = new LlmChatRequest
        {
            ModelId = "google/gemini-3-flash-preview",
            Messages = [new("user", "hello") { Attachments = attachments }]
        };
        var connection = new LlmConnection
        {
            Transport = LlmTransport.GoogleVertexAi,
            Version = "1",
            Endpoint = "https://aiplatform.googleapis.com",
            Vertex = new() { ProjectId = "test-project", Location = "global" },
            CapabilityOverrides = new() { SupportsStreaming = operation != "complete" }
        };
        Task run = ExecuteAsync();
        try
        {
            await authentication.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            bytes[0] = (byte)'X';
            attachments.Add(new LlmAttachment
            {
                Kind = LlmBinaryKind.Document,
                FileName = "late.txt",
                MediaType = "text/plain",
                Data = Encoding.UTF8.GetBytes("late")
            });
            authentication.Release.TrySetResult();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
            JsonNode payload = JsonNode.Parse(Assert.Single(handler.Bodies))!;
            JsonArray parts = payload["contents"]![0]!["parts"]!.AsArray();
            JsonNode data = Assert.Single(parts, part => part?["inlineData"] is not null)!["inlineData"]!;
            Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("original")), (string?)data["data"]);
            Assert.Equal("text/plain", (string?)data["mimeType"]);
            Assert.Equal(2, attachments.Count);
        }
        finally
        {
            authentication.Release.TrySetResult();
            try { await run.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception) when (run.IsCompleted) { }
        }

        async Task ExecuteAsync()
        {
            if (operation == "count") await client.CountInputTokensAsync(connection, request);
            else await foreach (var unused in client.StreamAsync(connection, request)) { }
        }
    }

    private sealed class Authentication : IVertexAuthentication
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<VertexAccess> ResolveAsync(LlmConnection connection, CancellationToken cancellationToken)
        { Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); return new("controlled", "identity"); }
    }

    private sealed class Handler(string operation) : HttpMessageHandler
    {
        internal List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            const string reply = "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"actual\"}]},\"finishReason\":\"STOP\"}]}";
            string body = operation == "count" ? "{\"totalTokens\":12}" : operation == "stream" ? $"data: {reply}\n\n" : reply;
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8,
                operation == "stream" ? "text/event-stream" : "application/json")
            };
        }
    }
}
