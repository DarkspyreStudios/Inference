using System.Text.Json;
using Xunit;

namespace GpuArbiter.Splash.Tests;

public sealed class StatusTests
{
    private const string Fresh = """
        {"schema_version":5,"ready":true,"metal":{"healthy":true},"memory_pressure":"normal",
         "memory_actual":{"current_bytes":100,"peak_bytes":200},"instance":{"pid":777},
         "transport":{"ready":true,"recovering":false,"status_stale":false,"pending":0},
         "http":{"requests":{"active":0}},"admission":{"waiting":0,"waiting_memory":0,"waiting_concurrency":0,"suspended":0},
         "scheduler":{"queued":0,"waiting_resources":0,"waiting_prefix":0,"prefilling":0,"decoding":0,"waiting_mask":0,"terminal":0}}
        """;

    [Fact]
    public void FreshIdleStatusProjectsOwnedMemoryAndCompletion()
    {
        using var json = JsonDocument.Parse(Fresh);
        var observation = SplashControl.Parse(json.RootElement);
        Assert.True(observation.Idle);
        Assert.Equal(100, observation.CurrentBytes);
        Assert.Equal(777, observation.ServerPid);
    }

    [Theory]
    [InlineData("\"status_stale\":false", "\"status_stale\":true")]
    [InlineData("\"decoding\":0", "\"decoding\":1")]
    [InlineData("\"pending\":0", "\"pending\":1")]
    [InlineData("\"healthy\":true", "\"healthy\":false")]
    [InlineData("\"active\":0", "\"active\":1")]
    [InlineData("\"queued\":0", "\"missing_counter\":0")]
    public void StaleActiveOrIncompleteStatusNeverProvesCancellation(string from, string to)
    {
        using var json = JsonDocument.Parse(Fresh.Replace(from, to));
        Assert.False(SplashControl.Parse(json.RootElement).Idle);
    }

    [Fact]
    public void UnknownSchemaIsExplicitlyUnavailable()
    {
        using var json = JsonDocument.Parse(Fresh.Replace("\"schema_version\":5", "\"schema_version\":6"));
        Assert.False(SplashControl.Parse(json.RootElement).Available);
    }
}
