using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using WpfBuddy.Mcp.Server.Services;

namespace WpfBuddy.Mcp.Server.Tools;

[McpServerToolType]
public sealed class ProbeTools
{
    private readonly ProbeClient _probe;
    private readonly SessionManager _session;
    private readonly AuditLog _audit;
    private readonly ILogger<ProbeTools> _logger;

    public ProbeTools(ProbeClient probe, SessionManager session, AuditLog audit, ILogger<ProbeTools> logger)
    {
        _probe = probe;
        _session = session;
        _audit = audit;
        _logger = logger;
    }

    [McpServerTool(Name = "wpf_probe_status", ReadOnly = true), Description("Check if probe is connected and responding.")]
    public async Task<string> ProbeStatus()
    {
        _audit.Record("wpf_probe_status");
        if (!_probe.IsConnected)
            return JsonSerializer.Serialize(new { connected = false, message = "Probe not connected. Use wpf_probe_connect first." }, JsonOptions.Default);

        var response = await _probe.SendAsync("ping");
        return JsonSerializer.Serialize(new
        {
            connected = true,
            pipeName = _probe.PipeName,
            responsive = response?.Result == "pong"
        }, JsonOptions.Default);
    }

    [McpServerTool(Name = "wpf_probe_connect", Destructive = false), Description("Connect to the in-process probe via named pipe.")]
    public async Task<string> ProbeConnect(IMcpServer server, [Description("Explicit named-pipe name to connect to (e.g. 'wpfbuddy-mcp-probe-{ProcessId}'). Optional; if omitted, the pipe is resolved from the currently attached app's process id.")] string? pipeName = null)
    {
        _audit.Record("wpf_probe_connect");
        bool connected;
        if (!string.IsNullOrEmpty(pipeName))
        {
            _logger.LogInformation("wpf_probe_connect: connecting by explicit pipe name '{PipeName}'", pipeName);
            connected = await _probe.ConnectAsync(pipeName);
        }
        else
        {
            // Read the PID directly (a cached int) instead of GetStatus(), which would
            // trigger blocking UI-Automation calls (ActiveWindow.Title etc.) and can hang.
            _logger.LogInformation("wpf_probe_connect: no pipe name given, resolving attached process id");
            var pid = _session.Application?.ProcessId;
            _logger.LogInformation("wpf_probe_connect: resolved process id = {Pid}", pid);
            if (pid is null or 0)
            {
                // No attached app — try to auto-discover a published probe pipe.
                var pipes = ProbeClient.EnumerateProbePipes();
                if (pipes.Count == 1)
                {
                    _logger.LogInformation("wpf_probe_connect: no session; auto-discovered probe pipe '{Pipe}'", pipes[0]);
                    connected = await _probe.ConnectAsync(pipes[0]);
                    return JsonSerializer.Serialize(new { connected, pipeName = _probe.PipeName, autoDiscovered = true }, JsonOptions.Default);
                }
                throw ToolError.Fail(pipes.Count == 0
                    ? "No session attached and no probe pipes found. Attach to an app (wpf_attach) or pass an explicit pipeName."
                    : "No session attached and multiple probe pipes found. Pass an explicit pipeName.");
            }
            _logger.LogInformation("wpf_probe_connect: connecting to probe for pid {Pid}", pid);
            connected = await _probe.ConnectAsync(pid.Value);
        }

        _logger.LogInformation("wpf_probe_connect: connected={Connected} pipe='{PipeName}'", connected, _probe.PipeName);
        // Client-facing (MCP notifications/message) log of a user-relevant event, via the SDK's
        // client logger provider. Curated + sanitized; verbose tracing stays on stderr/file. (LOG-4)
        try { server.AsClientLoggerProvider().CreateLogger("wpfbuddy.probe").LogInformation("Probe {Status} (pipe {Pipe}).", connected ? "connected" : "not connected", _probe.PipeName); } catch { }
        return JsonSerializer.Serialize(new { connected, pipeName = _probe.PipeName }, JsonOptions.Default);
    }

    [McpServerTool(Name = "wpf_probe_disconnect", Destructive = false, Idempotent = true), Description("Disconnect from the in-process probe.")]
    public string ProbeDisconnect()
    {
        _audit.Record("wpf_probe_disconnect");
        _probe.Disconnect();
        return JsonSerializer.Serialize(new { result = "disconnected" }, JsonOptions.Default);
    }

    [McpServerTool(Name = "wpf_probe_capabilities", ReadOnly = true), Description("List methods supported by connected probe.")]
    public string ProbeCapabilities()
    {
        _audit.Record("wpf_probe_capabilities");
        var methods = new[]
        {
            "ping", "info", "get_datacontext", "get_viewmodel_properties", "get_binding_errors",
            "get_bindings", "get_command_state", "get_validation_state", "execute_command",
            "get_dispatcher_status"
        };
        return JsonSerializer.Serialize(new { connected = _probe.IsConnected, methods }, JsonOptions.Default);
    }

    [McpServerTool(Name = "wpf_probe_health", ReadOnly = true), Description("Run probe health check.")]
    public async Task<string> ProbeHealth()
    {
        _audit.Record("wpf_probe_health");
        if (!_probe.IsConnected)
            return JsonSerializer.Serialize(new { healthy = false, error = "Not connected." }, JsonOptions.Default);

        var ping = await _probe.SendAsync("ping");
        var dispatcher = await _probe.SendAsync("get_dispatcher_status");
        var info = await _probe.SendAsync("info");

        return JsonSerializer.Serialize(new
        {
            healthy = ping?.Result == "pong",
            pingOk = ping?.Result == "pong",
            dispatcherOk = dispatcher?.Error is null,
            dispatcherStatus = dispatcher?.Data,
            probeInfo = info?.Data,
            sessionPid = _session.Application?.ProcessId,
            pipeName = _probe.PipeName
        }, JsonOptions.Default);
    }

    [McpServerTool(Name = "wpf_probe_install_instructions", ReadOnly = true), Description("Return instructions for installing the probe NuGet in a WPF app.")]
    public string ProbeInstallInstructions()
    {
        _audit.Record("wpf_probe_install_instructions");
        var instructions = @"
## Install the WpfBuddy MCP Probe

1. Add the NuGet package to your WPF project:
   ```
   dotnet add package WpfBuddy.Mcp.Probe
   ```

2. In your App.xaml.cs, add:
   ```csharp
   using WpfBuddy.Mcp.Probe;

   protected override void OnStartup(StartupEventArgs e)
   {
       base.OnStartup(e);
       ProbeHost.Start(); // pipe name auto-generated from PID
   }
   ```

3. The probe will listen on named pipe: `wpfbuddy-mcp-probe-{ProcessId}`

4. Connect from MCP tools using `wpf_probe_connect`.
";
        return JsonSerializer.Serialize(new { instructions }, JsonOptions.Default);
    }
}
