namespace Darkspyre.Inference.UI.Settings;

/// <summary>A numeric control edit, including text that cannot yet become a typed provider value.</summary>
/// <param name="Text">The text as typed.</param>
/// <param name="Value">The parsed number, or null when the text does not parse.</param>
/// <param name="Error">The message that explains why the text is not accepted, or null when it is.</param>
public sealed record SetupNumberEdit<T>(string Text, T? Value, string? Error) where T : struct;
