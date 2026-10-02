using Amazon;
using Amazon.Bedrock;
using Amazon.Bedrock.Model;
using Amazon.Runtime;
using Amazon.BedrockRuntime.Model;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Bedrock;

namespace Darkspyre.Inference.Connect.LLM.Bedrock.Tests;

public sealed class BedrockAttachmentAdmissionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectConverseFreezesAttachmentsBeforeLoggingAwait(bool streaming)
    {
        var control = new Control();
        await using var factory = new BedrockClientFactory(_ => throw new InvalidOperationException(), _ => control,
            _ => throw new InvalidOperationException());
        await using var guard = new BedrockLoggingGuard();
        var invoker = new StubConverseInvoker
        {
            Response = StubConverseInvoker.Converse([new ContentBlock { Text = "actual" }], "end_turn"),
            Events = [new MessageStopEvent { StopReason = new("end_turn") }]
        };
        var client = new BedrockLlmClient(factory, guard, invoker);
        byte[] bytes = "original"u8.ToArray();
        List<LlmMessageAttachment> attachments = [new LlmAttachment { Kind = LlmBinaryKind.Document,
            FileName = "original.txt", MediaType = "text/plain", Data = bytes }];
        var connection = new LlmConnection
        {
            Version = "1",
            Transport = LlmTransport.AwsBedrockConverse,
            Endpoint = "https://bedrock.invalid",
            Credential = new LlmCredential
            {
                Aws = new()
                { Region = "us-east-1", AccessKeyId = "controlled", SecretAccessKey = "controlled" }
            }.Serialize(),
            CapabilityOverrides = new() { SupportsStreaming = streaming }
        };
        Task run = ReadAsync();
        try
        {
            await control.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            bytes[0] = (byte)'X';
            attachments.Add(new LlmAttachment
            {
                Kind = LlmBinaryKind.Document,
                FileName = "late.txt",
                MediaType = "text/plain",
                Data = "late"u8.ToArray()
            });
            control.Release.TrySetResult();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
            IReadOnlyList<ContentBlock> content = streaming ? Assert.Single(invoker.StreamRequests).Messages[0].Content
                : Assert.Single(invoker.ConverseRequests).Messages[0].Content;
            DocumentBlock document = Assert.Single(content, part => part.Document is not null).Document;
            Assert.Equal("original"u8.ToArray(), document.Source.Bytes.ToArray());
            Assert.Equal(2, attachments.Count);
        }
        finally
        {
            control.Release.TrySetResult();
            try { await run.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception) when (run.IsCompleted) { }
        }

        async Task ReadAsync()
        {
            await foreach (var unused in client.StreamAsync(connection, new()
            {
                ModelId = "us.anthropic.claude-sonnet-4-6",
                Messages = [new("user", "hello") { Attachments = attachments }]
            })) { }
        }
    }

    private sealed class Control() : AmazonBedrockClient(new AnonymousAWSCredentials(), RegionEndpoint.USEast1)
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task<GetModelInvocationLoggingConfigurationResponse> GetModelInvocationLoggingConfigurationAsync(
            GetModelInvocationLoggingConfigurationRequest request, CancellationToken cancellationToken = default)
        { Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); return new(); }
    }
}
