namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Compares two declared content types for image resources.</summary>
internal static class ImageContentTypes
{
    internal static bool ContentTypesConflict(string? first, string? second)
    {
        if (first is null || second is null) return false;
        if (!System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(first, out var left)
            || !System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(second, out var right)) return true;
        if (!string.Equals(left.MediaType, right.MediaType, StringComparison.OrdinalIgnoreCase)) return true;
        return left.Parameters.Any(parameter => right.Parameters.Any(other =>
            string.Equals(parameter.Name, other.Name, StringComparison.OrdinalIgnoreCase) && !parameter.Equals(other)));
    }
}
