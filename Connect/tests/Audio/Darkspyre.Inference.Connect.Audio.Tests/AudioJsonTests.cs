using System.Reflection;
using System.Text.Json.Serialization.Metadata;

namespace Darkspyre.Inference.Connect.Audio.Tests;

public sealed class AudioJsonTests
{
    [Theory]
    [InlineData(nameof(AudioTransport))]
    [InlineData(nameof(TangoFluxEndpointOptions))]
    [InlineData(nameof(AudioConnection))]
    [InlineData(nameof(AudioGenerationRequest))]
    [InlineData(nameof(AudioGenerationCapabilities))]
    [InlineData(nameof(AudioGenerationResult))]
    [InlineData(nameof(AudioSetupConfiguration))]
    [InlineData(nameof(AudioSetupHeaderChanges))]
    [InlineData(nameof(AudioSetupChanges))]
    public void ExactPublicGetterOwnsExistingDataRoot(string name)
    {
        Assembly assembly = typeof(AudioConnection).Assembly;
        Type? owner = assembly.GetType("Darkspyre.Inference.Connect.Audio.ConnectAudioJson");
        Assert.NotNull(owner);
        PropertyInfo? property = owner.GetProperty(name, BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(property);
        JsonTypeInfo metadata = Assert.IsAssignableFrom<JsonTypeInfo>(property.GetValue(null));
        Assert.Equal("Darkspyre.Inference.Connect.Audio." + name, metadata.Type.FullName);
        Assert.Same(assembly, metadata.Options.TypeInfoResolver!.GetType().Assembly);
    }
}
