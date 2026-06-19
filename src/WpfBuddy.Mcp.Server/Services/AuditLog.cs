using WpfBuddy.Mcp.Server.Models;

namespace WpfBuddy.Mcp.Server.Services;

public sealed class AuditLog
{
    private const int MaxEntries = 5000;
    private readonly List<AuditEntry> _entries = [];
    private readonly object _lock = new();

    public void Record(string tool, ElementCriteria? selector = null, Dictionary<string, object?>? parameters = null, string result = "success", string? error = null)
    {
        lock (_lock)
        {
            _entries.Add(new AuditEntry
            {
                TimestampUtc = DateTime.UtcNow,
                Tool = tool,
                Selector = selector,
                Parameters = parameters,
                Result = result,
                Error = error
            });
            // Bound growth for long-lived server processes.
            if (_entries.Count > MaxEntries)
                _entries.RemoveRange(0, _entries.Count - MaxEntries);
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
