using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Connect.HttpConfiguration.Consumer;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class HttpConfigurationConsumerTests
{
    [Fact]
    public void NonfriendGeneratedConsumerPreservesRefusalAndNullWithoutCallbacks()
    {
        string consumer = typeof(ConfigurationCaller).Assembly.GetName().Name!;
        Assert.DoesNotContain(typeof(InferenceHttpPipelineOptions).Assembly.GetCustomAttributes<InternalsVisibleToAttribute>(),
            attribute => attribute.AssemblyName == consumer);
        Assert.Equal("nonnull-refused|null-preserved|callbacks=0", ConfigurationCaller.Exercise());
        Assert.False(ConfigurationCaller.OptionsMetadata.Options.TypeInfoResolver!.GetType().IsPublic);
        Assert.Same(typeof(ConfigurationCaller).Assembly, ConfigurationCaller.OptionsMetadata.Options.TypeInfoResolver.GetType().Assembly);
    }

    [Fact]
    public void PublicFactorySupportsOnlyTwoExactRuntimeTypesWithNoPublicGenericConverter()
    {
        var factory = new InferenceHttpConfigurationJsonConverterFactory();
        var options = new JsonSerializerOptions();
        Type[] supported = [typeof(InferenceHttpPipelineOptions), typeof(InferenceHttpLoggingBuilder)];
        foreach (Type type in supported)
        {
            Assert.True(factory.CanConvert(type));
            JsonConverter converter = factory.CreateConverter(type, options);
            Assert.False(converter.GetType().IsPublic);
            Assert.True(converter.CanConvert(type));
            Assert.Same(typeof(InferenceHttpConfigurationJsonConverterFactory), type.GetCustomAttribute<JsonConverterAttribute>()!.ConverterType);
        }
        foreach (Type type in new[] { typeof(object), typeof(string), typeof(LlmConnection), typeof(InferenceHttpCreationMode), typeof(HttpConfigurationEnvelope) })
        {
            Assert.False(factory.CanConvert(type));
            Assert.Throws<NotSupportedException>(() => factory.CreateConverter(type, options));
        }
        Assert.DoesNotContain(typeof(InferenceHttpConfigurationJsonConverterFactory).Assembly.GetExportedTypes(),
            type => type.IsGenericTypeDefinition && typeof(JsonConverter).IsAssignableFrom(type));
    }

    [Fact]
    public void DefaultRuntimeSerializerStillRefusesBothTypesAndPreservesNull()
    {
        var options = new InferenceHttpPipelineOptions();
        InferenceHttpLoggingBuilder? logging = null;
        int callbacks = 0;
        options.ConfigureLogging("OpenAiResponsesLlmClient", builder =>
        {
            logging = builder;
            builder.AddLogger(_ => { callbacks++; throw new InvalidOperationException(); });
            builder.RedactLoggedHeaders(_ => { callbacks++; return true; });
        });
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(options));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(logging));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize<InferenceHttpPipelineOptions>("{}"));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize<InferenceHttpLoggingBuilder>("{}"));
        Assert.Equal("null", JsonSerializer.Serialize<InferenceHttpPipelineOptions>(null!));
        Assert.Equal("null", JsonSerializer.Serialize<InferenceHttpLoggingBuilder>(null!));
        Assert.Null(JsonSerializer.Deserialize<InferenceHttpPipelineOptions>("null"));
        Assert.Null(JsonSerializer.Deserialize<InferenceHttpLoggingBuilder>("null"));
        Assert.Equal(0, callbacks);
    }

    [Fact]
    public void ActualForeignConsumerPrivateMetadataAndFixedConvertersRetire()
    {
        WeakReference[] roots = ExerciseForeignConsumer();
        for (int attempt = 0; roots.Any(root => root.IsAlive) && attempt < 30; attempt++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10);
        }
        Assert.All(roots, root => Assert.False(root.IsAlive, "The foreign configuration consumer or its actual owned metadata remains rooted."));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] ExerciseForeignConsumer()
    {
        var generation = new ConsumerGeneration();
        try
        {
            Assembly consumer = generation.LoadFromAssemblyPath(typeof(ConfigurationCaller).Assembly.Location);
            Assembly core = generation.LoadFromAssemblyPath(typeof(InferenceHttpPipelineOptions).Assembly.Location);
            Type caller = consumer.GetType(typeof(ConfigurationCaller).FullName!, true)!;
            MethodInfo method = caller.GetMethod(nameof(ConfigurationCaller.Exercise))!;
            Assert.Equal("nonnull-refused|null-preserved|callbacks=0", (string)method.Invoke(null, null)!);
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(consumer));
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(core));
            Assert.Equal(typeof(ConfigurationCaller).Assembly.ManifestModule.ModuleVersionId, consumer.ManifestModule.ModuleVersionId);
            Assert.Equal(typeof(InferenceHttpPipelineOptions).Assembly.ManifestModule.ModuleVersionId, core.ManifestModule.ModuleVersionId);
            var roots = new List<WeakReference> { new(generation), new(consumer), new(core), new(caller), new(method) };
            foreach (string name in new[] { nameof(ConfigurationCaller.OptionsMetadata), nameof(ConfigurationCaller.LoggingMetadata), nameof(ConfigurationCaller.EnvelopeMetadata) })
            {
                PropertyInfo property = caller.GetProperty(name)!;
                var metadata = (JsonTypeInfo)property.GetValue(null)!;
                object resolver = metadata.Options.TypeInfoResolver!;
                Assert.Same(consumer, resolver.GetType().Assembly);
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(resolver.GetType().Assembly));
                Assert.Equal(consumer.ManifestModule.ModuleVersionId, resolver.GetType().Module.ModuleVersionId);
                JsonConverter converter = metadata.Converter;
                if (name != nameof(ConfigurationCaller.EnvelopeMetadata))
                {
                    Assert.Same(core, metadata.Type.Assembly);
                    Assert.Same(core, converter.GetType().Assembly);
                    Assert.Equal(core.ManifestModule.ModuleVersionId, converter.GetType().Module.ModuleVersionId);
                }
                roots.Add(new(property)); roots.Add(new(metadata)); roots.Add(new(metadata.Type));
                roots.Add(new(resolver)); roots.Add(new(converter)); roots.Add(new(converter.GetType()));
            }
            return roots.ToArray();
        }
        finally { generation.Unload(); }
    }

    private sealed class ConsumerGeneration() : AssemblyLoadContext(isCollectible: true)
    {
        private readonly string _directory = Path.GetDirectoryName(typeof(ConfigurationCaller).Assembly.Location)!;
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name!.StartsWith("Darkspyre.Inference", StringComparison.Ordinal))
            {
                string path = Path.Combine(_directory, name.Name + ".dll");
                if (File.Exists(path)) return LoadFromAssemblyPath(path);
            }
            return Default.LoadFromAssemblyName(name);
        }
    }
}
