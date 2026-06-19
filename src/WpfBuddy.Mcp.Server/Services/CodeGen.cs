namespace WpfBuddy.Mcp.Server.Services;

/// <summary>
/// Helpers for emitting generated C# source safely.
/// </summary>
internal static class CodeGen
{
    /// <summary>
    /// Escapes a string for safe embedding inside a C# double-quoted "..." literal, so that
    /// element names/values containing quotes, backslashes, or newlines cannot break — or inject
    /// into — the generated source. (Recording/test-gen input is attacker-influenceable.)
    /// </summary>
    public static string Escape(string? s) =>
        s is null
            ? string.Empty
            : s.Replace("\\", "\\\\")
               .Replace("\"", "\\\"")
               .Replace("\r", "\\r")
               .Replace("\n", "\\n")
               .Replace("\t", "\\t");
}
