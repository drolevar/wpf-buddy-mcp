using WpfBuddy.Mcp.Server.Models;

namespace WpfBuddy.Mcp.Server.Services;

public sealed class AuditLog
{
    private const int MaxEntries = 5000;
    private readonly List<AuditEntry> _entries = [];
    private readonly object _lock = new();

    // The audit entry of the tool call in flight on the current async flow. Tools call Record at
    // their start (optimistic result="success"); a later failure flips it via MarkCurrentFailure.
    private static readonly AsyncLocal<AuditEntry?> _current = new();

    public void Record(string tool, ElementCriteria? selector = null, Dictionary<string, object?>? parameters = null, string result = "success", string? error = null)
    {
        var entry = new AuditEntry
        {
            TimestampUtc = DateTime.UtcNow,
            Tool = tool,
            Selector = selector,
            Parameters = parameters,
            Result = result,
            Error = error
        };
        lock (_lock)
        {
            _entries.Add(entry);
            // Bound growth for long-lived server processes.
            if (_entries.Count > MaxEntries)
                _entries.RemoveRange(0, _entries.Count - MaxEntries);
        }
        _current.Value = entry;
    }

    /// <summary>
    /// Mark the in-flight tool call's audit entry (this async flow) as a failure, so session and
    /// diagnostics reports reflect real outcomes instead of always "success". Called by ToolError.Fail.
    /// </summary>
    public static void MarkCurrentFailure(string error)
    {
        var entry = _current.Value;
        if (entry is not null)
        {
            entry.Result = "error";
            entry.Error = error;
        }
    }

    public List<AuditEntry> GetEntries()
    {
        lock (_lock)
        {
            return [.. _entries];
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
        }
    }
}
