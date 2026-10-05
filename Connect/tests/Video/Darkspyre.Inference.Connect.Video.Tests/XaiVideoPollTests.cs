using System.Net;
using System.Text.Json;
using Darkspyre.Inference.Common.Video;
using Darkspyre.Inference.Connect.Video.Internal;

namespace Darkspyre.Inference.Connect.Video.Tests;

public sealed class XaiVideoPollTests
{
    [Fact]
    public void AModeratedVideoWithAnEmptyUrlCompletesAndKeepsTheEmptyUrl()
    {
        XaiVideoPoll poll = XaiVideoProtocol.ParsePoll(HttpStatusCode.OK,
            """{"status":"done","video":{"duration":6,"respect_moderation":false,"url":""}}""",
            static _ => false, static () => { });

        Assert.Equal(VideoJobState.Completed, poll.State);
        JsonElement output = poll.Output!.Value;
        Assert.False(output.GetProperty("respect_moderation").GetBoolean());
        Assert.Equal(string.Empty, output.GetProperty("url").GetString());
    }
}
