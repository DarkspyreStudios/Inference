using System.Security.Cryptography;
using System.Text;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>
/// Maps a session's logical tool names to the names a provider accepts, and back. A name the provider accepts passes
/// through unchanged; any other name is sent as its <see cref="Alias"/>.
/// </summary>
internal sealed class LlmToolNames
{
    private readonly Func<string, bool> _isValidNative;
    private readonly Dictionary<string, string> _logical = new(StringComparer.Ordinal);

    private LlmToolNames(Func<string, bool> isValidNative) => _isValidNative = isValidNative;

    /// <summary>Maps logical names to native names. A name isValidNative accepts passes through; any other gets Alias(name).</summary>
    internal static LlmToolNames Create(IEnumerable<string> logicalNames, Func<string, bool> isValidNative)
    {
        var names = new LlmToolNames(isValidNative);
        foreach (string logical in logicalNames)
        {
            names._logical.TryAdd(names.Native(logical), logical);
        }
        return names;
    }

    /// <summary>"t_" followed by the first 40 lowercase hex characters of SHA-256 over the UTF-8 name.</summary>
    internal static string Alias(string logicalName) =>
        "t_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(logicalName)))[..40];

    /// <summary>The native name for a logical name: the name itself when the provider accepts it, otherwise its alias.</summary>
    internal string Native(string logicalName) => _isValidNative(logicalName) ? logicalName : Alias(logicalName);

    /// <summary>The logical name for a native name, or null when the native name is not in the map.</summary>
    internal string? Logical(string nativeName) => _logical.GetValueOrDefault(nativeName);
}
