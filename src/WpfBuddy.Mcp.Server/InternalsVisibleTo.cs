using System.Runtime.CompilerServices;

// Expose internal contract-test seams (e.g. ScreenshotTools.ImageResult, SnapshotTools.KeyElements)
// to the integration-test assembly without widening the public API surface.
[assembly: InternalsVisibleTo("WpfBuddy.Mcp.IntegrationTests")]
