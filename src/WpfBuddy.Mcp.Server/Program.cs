using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using WpfBuddy.Mcp.Server.Services;
using WpfBuddy.Mcp.Server.Tools;

// Per-monitor (V2) DPI awareness so screen-capture coordinates (GDI CopyFromScreen) line up
// with UIA's physical-pixel BoundingRectangles on scaled displays.
try { NativeMethods.SetProcessDpiAwarenessContext(NativeMethods.DpiAwarenessContextPerMonitorAwareV2); } catch { }

var builder = Host.CreateApplicationBuilder(args);

// A stdio MCP server must keep stdout JSON-only. Route all logging to stderr,
// and suppress the host lifetime status messages ("Application started.
// Press Ctrl+C to shut down.", "Hosting environment...", "Content root...")
// which otherwise leak to the console and garble the protocol.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.Configure<ConsoleLifetimeOptions>(options => options.SuppressStatusMessages = true);

// Tee all ILogger output to a file (default: %TEMP%\wpfbuddy-mcp-server.log,
// override with WPFBUDDY_SERVER_LOG) so the general server logs are persisted.
builder.Logging.AddProvider(new FileLoggerProvider(FileLoggerProvider.ResolveLogPath()));

builder.Services.AddSingleton<SessionManager>();
builder.Services.AddSingleton<AuditLog>();
builder.Services.AddSingleton<UiaAdapter>();
builder.Services.AddSingleton<SelectorBuilder>();
builder.Services.AddSingleton<ScreenshotService>();
builder.Services.AddSingleton<RecordingService>();
builder.Services.AddSingleton<ProbeClient>();
builder.Services.AddSingleton<ExplorerService>();
builder.Services.AddSingleton<DevWatcherService>();

builder.Services.AddMcpServer(options =>
{
    options.ServerInfo = new()
    {
        Name = "WpfBuddy MCP",
        Version = "0.1.0"
    };
})
.WithStdioServerTransport()
.WithToolsFromAssembly();

var app = builder.Build();

// Reset transient per-session state and drop the probe connection whenever the session
// changes. Resolve the session-coupled singletons up front so their constructors subscribe.
var session = app.Services.GetRequiredService<SessionManager>();
session.SessionChanged += app.Services.GetRequiredService<ProbeClient>().Disconnect;
app.Services.GetRequiredService<RecordingService>();
app.Services.GetRequiredService<DevWatcherService>();

await app.RunAsync();

static class NativeMethods
{
    // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 == -4
    public static readonly nint DpiAwarenessContextPerMonitorAwareV2 = -4;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool SetProcessDpiAwarenessContext(nint value);
}
