using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class CacheErrorContractTests
{
    [Theory]
    [InlineData("LlmContextCacheUnavailableException", false)]
    [InlineData("LlmContextCacheMutationUncertainException", false)]
    [InlineData("LlmContextCacheMutationCancelledException", true)]
    public void RequiredPublicErrorsExistWithoutJsonRoots(string name, bool cancelled)
    {
        Type? type = typeof(ConnectLlmJson).Assembly.GetType("Darkspyre.Inference.Connect.LLM." + name);
        Assert.NotNull(type);
        Assert.True(type.IsPublic);
        Assert.True(type.IsSealed);
        Assert.True((cancelled ? typeof(OperationCanceledException) : typeof(Exception)).IsAssignableFrom(type));
        Assert.Null(typeof(ConnectLlmJson).GetProperty(name));
    }

    public static TheoryData<string, int> UndefinedValues => new()
    {
        { "LlmContextCacheUnavailableReason", -1 }, { "LlmContextCacheUnavailableReason", 99 },
        { "LlmContextCacheUnavailableReason", int.MinValue }, { "LlmContextCacheUnavailableReason", int.MaxValue },
        { "LlmContextCacheMutation", -1 }, { "LlmContextCacheMutation", 99 },
        { "LlmContextCacheMutation", int.MinValue }, { "LlmContextCacheMutation", int.MaxValue }
    };

    [Theory, MemberData(nameof(UndefinedValues))]
    public void UndefinedOwnedReadRefuses(string name, int number)
    {
        JsonTypeInfo metadata = Metadata(name);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(number.ToString(System.Globalization.CultureInfo.InvariantCulture), metadata));
    }

    [Theory, MemberData(nameof(UndefinedValues))]
    public void UndefinedOwnedWriteRefuses(string name, int number)
    {
        JsonTypeInfo metadata = Metadata(name);
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(Enum.ToObject(metadata.Type, number), metadata));
    }

    [Theory]
    [InlineData("LlmContextCacheUnavailableReason")]
    [InlineData("LlmContextCacheMutation")]
    public void DefinedNumericValuesRoundTrip(string name)
    {
        JsonTypeInfo metadata = Metadata(name);
        foreach (int value in new[] { 0, 1 })
        {
            string json = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            object restored = JsonSerializer.Deserialize(json, metadata)!;
            Assert.Equal(value, Convert.ToInt32(restored, System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(json, JsonSerializer.Serialize(restored, metadata));
        }
    }

    private static JsonTypeInfo Metadata(string name)
    {
        PropertyInfo? getter = typeof(ConnectLlmJson).GetProperty(name);
        Assert.NotNull(getter);
        return Assert.IsAssignableFrom<JsonTypeInfo>(getter.GetValue(null));
    }
}
