using System.Reflection;
using System.Xml.Linq;

namespace Darkspyre.Inference.Common.Setup;

/// <summary>
/// A provider's logo: SVG elements without the enclosing svg element, and the view box. Only path, rect and circle
/// elements with fill colours; no groups, transforms or gradients.
/// </summary>
/// <param name="Markup">The SVG elements, without the enclosing svg element and without a namespace.</param>
/// <param name="ViewBox">The SVG view box: minimum x, minimum y, width and height.</param>
public sealed record SetupIcon(string Markup, string ViewBox);

/// <summary>The add-connection group a provider is listed under, in this order.</summary>
public enum SetupProviderGroup
{
    /// <summary>"Use a subscription you have".</summary>
    Subscription,
    /// <summary>"Use an API key".</summary>
    ApiKey,
    /// <summary>"Run on this computer".</summary>
    ThisComputer,
    /// <summary>"More": every provider that fits no other group.</summary>
    More
}

/// <summary>Reads provider logos that an assembly embeds as SVG files.</summary>
public static class SetupIcons
{
    /// <summary>The logical resource name of the OpenAI mark this assembly embeds, shared by every OpenAI provider and engine.</summary>
    public const string OpenAiResource = "Icons/openai.svg";

    /// <summary>Reads an embedded SVG file and splits it into markup and view box. Throws when the resource is missing.</summary>
    /// <param name="assembly">The assembly that embeds the file.</param>
    /// <param name="name">The file's logical resource name, such as <c>Icons/claude.svg</c>.</param>
    /// <exception cref="InvalidOperationException">The assembly embeds no resource with that name, or the file has no view box.</exception>
    public static SetupIcon FromResource(Assembly assembly, string name)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        using Stream stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"{assembly.GetName().Name} embeds no icon named '{name}'.");
        XElement root = XElement.Load(stream);
        string viewBox = (string?)root.Attribute("viewBox")
            ?? throw new InvalidOperationException($"The icon '{name}' has no view box.");
        string markup = string.Concat(root.Elements().Select(element => WithoutNamespace(element).ToString(SaveOptions.DisableFormatting)));
        return new(markup, viewBox);
    }

    private static XElement WithoutNamespace(XElement element) => new(element.Name.LocalName,
        element.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration)
            .Select(attribute => new XAttribute(attribute.Name.LocalName, attribute.Value)),
        element.Elements().Select(WithoutNamespace));
}
