using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using WpfBuddy.Mcp.Server.Models;
using WpfBuddy.Mcp.Server.Services;

namespace WpfBuddy.Mcp.Server.Tools;

[McpServerToolType]
public sealed class PolicyTools
{
    private readonly SessionManager _session;
    private readonly UiaAdapter _uia;
    private readonly AuditLog _audit;
    private readonly ProbeClient _probe;

    private static RecordingPolicy _currentPolicy = new();
    private static readonly List<RedactionRule> _redactionRules = new();
    private static readonly object _policyLock = new();
    private static readonly object _redactionLock = new();

    public PolicyTools(SessionManager session, UiaAdapter uia, AuditLog audit, ProbeClient probe)
    {
        _session = session;
        _uia = uia;
        _audit = audit;
        _probe = probe;
    }

    [McpServerTool(Name = "wpf_get_capabilities", ReadOnly = true), Description("List all available tool categories and their status.")]
    public string GetCapabilities()
    {
        _audit.Record("wpf_get_capabilities");
        var capabilities = new
        {
            categories = new[]
            {
                new { name = "session", tools = 8, status = "available" },
                new { name = "snapshot", tools = 7, status = "available" },
                new { name = "action", tools = 12, status = "available" },
                new { name = "wait", tools = 8, status = "available" },
                new { name = "assertion", tools = 8, status = "available" },
                new { name = "selector", tools = 7, status = "available" },
                new { name = "recording", tools = 14, status = "available" },
                new { name = "datagrid", tools = 15, status = "available" },
                new { name = "accessibility", tools = 8, status = "available" },
                new { name = "testgen", tools = 7, status = "available" },
                new { name = "screenshot", tools = 7, status = "available" },
                new { name = "diagnostics", tools = 4, status = "available" },
                new { name = "policy", tools = 8, status = "available" },
                new { name = "reporting", tools = 5, status = "available" },
                new { name = "clipboard", tools = 6, status = "available" },
                new { name = "probe/mvvm", tools = 16, status = "requires_probe" }
            },
            policyActive = true,
            attached = _session.IsAttached,
            probeConnected = _probe.IsConnected
        };
        return JsonSerializer.Serialize(capabilities, JsonOptions.Default);
    }

    [McpServerTool(Name = "wpf_set_policy", Destructive = false, Idempotent = true), Description("Set execution policy: allowDestructive, allowCoordinateFallback, timeoutMs, maxRetries.")]
    public string SetPolicy([Description("If true, allow destructive actions (e.g. delete/clear/overwrite) to be executed. Omit to leave unchanged.")] bool? allowDestructive = null, [Description("If true, allow falling back to coordinate-based interaction when UIA element targeting fails. Omit to leave unchanged.")] bool? allowCoordinateFallback = null, [Description("Default operation timeout in milliseconds. Omit to leave unchanged.")] int? timeoutMs = null, [Description("Maximum number of retry attempts for an operation. Omit to leave unchanged.")] int? maxRetries = null)
    {
        _audit.Record("wpf_set_policy");
        lock (_policyLock)
        {
            if (allowDestructive.HasValue) _currentPolicy.AllowDestructive = allowDestructive.Value;
            if (allowCoordinateFallback.HasValue) _currentPolicy.AllowCoordinateFallback = allowCoordinateFallback.Value;
            if (timeoutMs.HasValue) _currentPolicy.TimeoutMs = timeoutMs.Value;
            if (maxRetries.HasValue) _currentPolicy.MaxRetries = maxRetries.Value;
        }

        return JsonSerializer.Serialize(new { result = "policy_updated", policy = _currentPolicy }, JsonOptions.Default);
    }

    [McpServerTool(Name = "wpf_get_policy", ReadOnly = true), Description("Get current execution policy.")]
    public string GetPolicy()
    {
        _audit.Record("wpf_get_policy");
        return JsonSerializer.Serialize(new { policy = _currentPolicy }, JsonOptions.Default);
    }

    [McpServerTool(Name = "wpf_preview_action", ReadOnly = true), Description("Show what an action would do without executing it (dry-run).")]
    public string PreviewAction([Description("Action to preview. One of: click, invoke, set_value, toggle.")] string action, [Description("AutomationId of the target element. Preferred selector.")] string? automationId = null, [Description("Element Name/content; used when automationId is omitted.")] string? name = null, [Description("Value to apply for set_value previews; ignored for other actions.")] string? value = null)
    {
        _audit.Record("wpf_preview_action");
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name };
        var element = _uia.FindElement(criteria);

        var preview = new
        {
            action,
            targetFound = element is not null,
            targetInfo = element is null ? null : new
            {
                automationId = element.Properties.AutomationId.ValueOrDefault,
                name = element.Properties.Name.ValueOrDefault,
                controlType = element.Properties.ControlType.ValueOrDefault.ToString(),
                isEnabled = element.Properties.IsEnabled.ValueOrDefault,
                bounds = element.BoundingRectangle.ToString()
            },
            value,
            willExecute = false,
            policyCheck = new
            {
                allowedByPolicy = true,
                coordinateFallbackNeeded = false
            }
        };

        return JsonSerializer.Serialize(preview, JsonOptions.Default);
    }

    [McpServerTool(Name = "wpf_confirm_action", Destructive = true), Description("Confirm and execute a previously previewed action.")]
    public string ConfirmAction([Description("Action to execute. One of: click, invoke, set_value, toggle.")] string action, [Description("AutomationId of the target element. Preferred selector.")] string? automationId = null, [Description("Element Name/content; used when automationId is omitted.")] string? name = null, [Description("Value to set when action is set_value; ignored for other actions.")] string? value = null)
    {
        _audit.Record("wpf_confirm_action");
        lock (_policyLock)
        {
            if (!_currentPolicy.AllowDestructive)
                throw ToolError.Fail("Destructive actions are disabled by policy. Enable via wpf_set_policy(allowDestructive: true).");
        }
        var criteria = new ElementCriteria { AutomationId = automationId, Name = name };
        var element = _uia.FindElement(criteria);
        if (element is null)
            throw ToolError.Fail("Element not found.");

        try
        {
            switch (action.ToLowerInvariant())
            {
                case "click":
                    element.Click();
                    break;
                case "invoke":
                    if (element.Patterns.Invoke.IsSupported) element.Patterns.Invoke.Pattern.Invoke();
                    else element.Click();
                    break;
                case "set_value":
                    if (element.Patterns.Value.IsSupported && value is not null)
                        element.Patterns.Value.Pattern.SetValue(value);
                    break;
                case "toggle":
                    if (element.Patterns.Toggle.IsSupported)
                        element.Patterns.Toggle.Pattern.Toggle();
                    break;
            }
            return JsonSerializer.Serialize(new { result = "action_executed", action }, JsonOptions.Default);
        }
        catch (Exception ex)
        {
            throw ToolError.Fail(ex.Message);
        }
    }

    [McpServerTool(Name = "wpf_clear_audit_log", Destructive = true, Idempotent = true), Description("Clear the audit log.")]
    public string ClearAuditLog()
    {
        _audit.Record("wpf_clear_audit_log");
        _audit.Clear();
        return JsonSerializer.Serialize(new { result = "audit_log_cleared" }, JsonOptions.Default);
    }

    [McpServerTool(Name = "wpf_redact_snapshot", ReadOnly = true), Description("Return a snapshot with sensitive fields redacted.")]
    public string RedactSnapshot([Description("Maximum depth of the UI element tree to capture. Defaults to 5.")] int maxDepth = 5)
    {
        _audit.Record("wpf_redact_snapshot");
        var snapshot = _uia.CaptureSnapshot(maxDepth: maxDepth);
        RedactTree(snapshot.Tree);
        return JsonSerializer.Serialize(snapshot, JsonOptions.Default);
    }

    [McpServerTool(Name = "wpf_set_redaction_rules", Destructive = false), Description("Add redaction rules for sensitive AutomationId patterns.")]
    public string SetRedactionRules([Description("Substring patterns matched (case-insensitive) against element AutomationId or Name; matching elements have their Name and Value redacted. Patterns are added to the existing rule set; duplicates are ignored.")] string[] patterns)
    {
        _audit.Record("wpf_set_redaction_rules");
        lock (_redactionLock)
        {
            foreach (var p in patterns)
            {
                if (!_redactionRules.Any(r => r.Pattern == p))
                    _redactionRules.Add(new RedactionRule { Pattern = p });
            }
            return JsonSerializer.Serialize(new { result = "rules_updated", ruleCount = _redactionRules.Count }, JsonOptions.Default);
        }
    }

    [McpServerTool(Name = "wpf_get_redaction_rules", ReadOnly = true), Description("List current redaction rules.")]
    public string GetRedactionRules()
    {
        _audit.Record("wpf_get_redaction_rules");
        return JsonSerializer.Serialize(new { rules = _redactionRules }, JsonOptions.Default);
    }

    private static void RedactTree(List<UiElement> elements)
    {
        foreach (var el in elements)
        {
            if (ShouldRedact(el))
            {
                el.Value = "***REDACTED***";
                el.Name = "***REDACTED***";
            }
            RedactTree(el.Children);
        }
    }

    private static bool ShouldRedact(UiElement el)
    {
        lock (_redactionLock)
        {
            if (_redactionRules.Count == 0) return false;
            foreach (var rule in _redactionRules)
            {
                if (el.AutomationId?.Contains(rule.Pattern, StringComparison.OrdinalIgnoreCase) == true)
                    return true;
                if (el.Name?.Contains(rule.Pattern, StringComparison.OrdinalIgnoreCase) == true)
                    return true;
            }
            return false;
        }
    }
}

public class RedactionRule
{
    public string Pattern { get; set; } = "";
}
