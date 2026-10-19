using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;

namespace Darkspyre.Inference.Connect.Image.Tests;

public sealed partial class ComfyUiImageTransportTests
{
    private static readonly TimeSpan ResourceBound = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task EnteredBinaryBytesSurviveHeldCapabilityAndReachActualComfyUpload()
    {
        byte[] bytes = "entered"u8.ToArray();
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        string? uploaded = null;
        var handler = new StubHandler(async request =>
        {
            uploaded = await request.Content!.ReadAsStringAsync();
            return JsonResponse("{\"name\":\"observed.png\",\"subfolder\":\"\",\"type\":\"input\"}");
        });
        var transport = new ImageTestTransport
        {
            CapabilityPause = () => { entered.SetResult(); return release.Task; },
            ReferenceMaterializer = CreateTransport(handler)
        };
        var inference = new ImageProviderService([transport]).Bind(Connection(), ImageTestTransport.Workflow(), pollInterval: TimeSpan.Zero);
        try
        {
            Task pending = inference.GenerateAsync(ImageTestTransport.Request() with { References = [new BinaryMediaSource(null, bytes)] });
            await entered.Task.WaitAsync(ResourceBound);
            "changed"u8.CopyTo(bytes);
            release.SetResult();
            await pending.WaitAsync(ResourceBound);
            Assert.Contains("entered", uploaded!, StringComparison.Ordinal);
            Assert.DoesNotContain("changed", uploaded!, StringComparison.Ordinal);
            Assert.Single(handler.Headers);
        }
        finally { await ((IAsyncDisposable)inference).DisposeAsync(); }
    }
}
