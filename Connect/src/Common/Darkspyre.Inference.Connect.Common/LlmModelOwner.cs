using System.Buffers.Text;
using System.Security.Cryptography;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>Transient private model ownership for one admitted session and endpoint.</summary>
public sealed class LlmModelOwner
{
    /// <summary>Constructs an owner from its actual coordination name and protected base64url proof.</summary>
    /// <param name="name">The actual session's coordination name.</param>
    /// <param name="proof">The private base64url encoding of 32 random bytes.</param>
    /// <gotcha>The proof is private runtime authority, not connection configuration or a model argument.</gotcha>
    public LlmModelOwner(string name, string proof)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(proof);
        Span<byte> decoded = stackalloc byte[32];
        if (!Base64Url.TryDecodeFromChars(proof, decoded, out int written) || written != decoded.Length)
            throw new ArgumentException("The model owner proof must encode 32 bytes.", nameof(proof));
        Id = Convert.ToHexStringLower(SHA256.HashData(decoded));
        Name = name;
        Proof = proof;
    }

    /// <summary>The public SHA-256 identity derived from the private proof.</summary>
    public string Id { get; }

    /// <summary>The actual session's coordination name.</summary>
    public string Name { get; }

    internal string Proof { get; }

    /// <summary>Returns a constant redacted label, never the proof.</summary>
    public override string ToString() => "Model owner (private)";
}
