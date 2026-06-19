using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using WpfBuddy.Mcp.Server.Services;

namespace WpfBuddy.Mcp.Server.Tools;

[McpServerToolType]
public sealed class MvvmTools
{
    private readonly ProbeClient _probe;
    private readonly AuditLog _audit;

    public MvvmTools(ProbeClient probe, AuditLog audit)
    {
        _probe = probe;
        _audit = audit;
    }

    [McpServerTool(Name = "wpf_get_viewmodel", ReadOnly = true), Description("Get ViewModel type and property summary via probe.")]
    public async Task<string> GetViewModel([Description("Title of the target window whose ViewModel to inspect; optional, defaults to the main/active window when omitted.")] string? windowTitle = null)
    {
        _audit.Record("wpf_get_viewmodel");
        if (!_probe.IsConnected)
            return Error("Probe not connected.");

        var response = await _probe.SendAsync("get_datacontext", Params(windowTitle));
        return FormatResponse(response);
    }

    [McpServerTool(Name = "wpf_get_viewmodel_properties", ReadOnly = true), Description("Get all ViewModel properties with current values.")]
    public async Task<string> GetViewModelProperties([Description("Title of the target window whose ViewModel properties to read; optional, defaults to the main/active window when omitted.")] string? windowTitle = null)
    {
        _audit.Record("wpf_get_viewmodel_properties");
        if (!_probe.IsConnected)
            return Error("Probe not connected.");

        var response = await _probe.SendAsync("get_viewmodel_properties", Params(windowTitle));
        return FormatResponse(response);
    }

    [McpServerTool(Name = "wpf_get_commands", ReadOnly = true), Description("List all ICommand properties on ViewModel.")]
    public async Task<string> GetCommands([Description("Title of the target window whose ViewModel commands to list; optional, defaults to the main/active window when omitted.")] string? windowTitle = null)
    {
        _audit.Record("wpf_get_commands");
        if (!_probe.IsConnected)
            return Error("Probe not connected.");

        // Uses get_command_state probe method which returns all commands with CanExecute state
        var response = await _probe.SendAsync("get_command_state", Params(windowTitle));
        return FormatResponse(response);
    }

    [McpServerTool(Name = "wpf_get_command_state", ReadOnly = true), Description("Get CanExecute state of a specific command.")]
    public async Task<string> GetCommandState([Description("Name of the ICommand property on the ViewModel to query (required); matched case-insensitively.")] string commandName, [Description("Title of the target window hosting the command; optional, defaults to the main/active window when omitted.")] string? windowTitle = null)
    {
        _audit.Record("wpf_get_command_state");
        if (!_probe.IsConnected)
            return Error("Probe not connected.");

        var response = await _probe.SendAsync("get_command_state", Params(windowTitle));
        if (response?.Data is not null)
        {
            // Filter to specific command
            try
            {
                var commands = JsonSerializer.Deserialize<List<CommandInfo>>(response.Data);
                var cmd = commands?.FirstOrDefault(c => c.Name.Equals(commandName, StringComparison.OrdinalIgnoreCase));
                if (cmd is not null)
                    return JsonSerializer.Serialize(new { commandName = cmd.Name, canExecute = cmd.CanExecute }, JsonOptions.Default);
                return Error($"Command '{commandName}' not found.");
            }
            catch { }
        }
        return FormatResponse(response);
    }

    [McpServerTool(Name = "wpf_execute_command", Destructive = true), Description("Execute an ICommand on ViewModel via probe.")]
    public async Task<string> ExecuteCommand([Description("Name of the ICommand property on the ViewModel to execute (required).")] string commandName, [Description("Optional command parameter passed to Execute; serialized as a string. Omit if the command takes no parameter.")] string? parameter = null, [Description("Title of the target window hosting the command; optional, defaults to the main/active window when omitted.")] string? windowTitle = null)
    {
        _audit.Record("wpf_execute_command");
        if (!_probe.IsConnected)
            return Error("Probe not connected.");

        var @params = new Dictionary<string, string>();
        if (windowTitle is not null) @params["windowTitle"] = windowTitle;
        @params["commandName"] = commandName;
        if (parameter is not null) @params["parameter"] = parameter;

        var response = await _probe.SendAsync("execute_command", @params);
        return FormatResponse(response);
    }

    [McpServerTool(Name = "wpf_get_binding_errors", ReadOnly = true), Description("Get all WPF binding errors from the target app.")]
    public async Task<string> GetBindingErrors()
    {
        _audit.Record("wpf_get_binding_errors");
        if (!_probe.IsConnected)
            return Error("Probe not connected.");

        var response = await _probe.SendAsync("get_binding_errors");
        return FormatResponse(response);
    }

    [McpServerTool(Name = "wpf_get_bindings", ReadOnly = true), Description("Get all active bindings in the window.")]
    public async Task<string> GetBindings([Description("Title of the target window whose active bindings to list; optional, defaults to the main/active window when omitted.")] string? windowTitle = null)
    {
        _audit.Record("wpf_get_bindings");
        if (!_probe.IsConnected)
            return Error("Probe not connected.");

        var response = await _probe.SendAsync("get_bindings", Params(windowTitle));
        return FormatResponse(response);
    }

    [McpServerTool(Name = "wpf_get_validation_state", ReadOnly = true), Description("Get validation errors from the ViewModel/View.")]
    public async Task<string> GetValidationState([Description("Title of the target window whose validation state to read; optional, defaults to the main/active window when omitted.")] string? windowTitle = null)
    {
        _audit.Record("wpf_get_validation_state");
        if (!_probe.IsConnected)
            return Error("Probe not connected.");

        var response = await _probe.SendAsync("get_validation_state", Params(windowTitle));
        return FormatResponse(response);
    }

    [McpServerTool(Name = "wpf_get_dispatcher_status", ReadOnly = true), Description("Get WPF Dispatcher thread status.")]
    public async Task<string> GetDispatcherStatus()
    {
        _audit.Record("wpf_get_dispatcher_status");
        if (!_probe.IsConnected)
            return Error("Probe not connected.");

        var response = await _probe.SendAsync("get_dispatcher_status");
        return FormatResponse(response);
    }

    [McpServerTool(Name = "wpf_get_datacontext", ReadOnly = true), Description("Get DataContext type and value for a window.")]
    public async Task<string> GetDataContext([Description("Title of the target window whose DataContext to inspect; optional, defaults to the main/active window when omitted.")] string? windowTitle = null)
    {
        _audit.Record("wpf_get_datacontext");
        if (!_probe.IsConnected)
            return Error("Probe not connected.");

        var response = await _probe.SendAsync("get_datacontext", Params(windowTitle));
        return FormatResponse(response);
    }

    private static Dictionary<string, string>? Params(string? windowTitle)
    {
        if (windowTitle is null) return null;
        return new Dictionary<string, string> { ["windowTitle"] = windowTitle };
    }

    private static string FormatResponse(ProbeResponse? response)
    {
        if (response is null)
            return Error("No response from probe.");
        if (response.Error is not null)
            return Error(response.Error);
        return response.Data ?? JsonSerializer.Serialize(new { result = response.Result }, JsonOptions.Default);
    }

    private static string Error(string message) =>
        throw ToolError.Fail(message);

    private class CommandInfo
    {
        public string Name { get; set; } = "";
        public bool CanExecute { get; set; }
    }
}
