using System.Text.Json;
using Microsoft.Data.Sqlite;
using Morsa.Domain.Common;
using Morsa.Mcp.Tools;

namespace Morsa.IntegrationTests;

/// <summary>
/// Verifies that public MCP file operations cannot cross the selected workspace boundary.
/// </summary>
[Collection("ConsoleIsolation")]
public sealed class McpWorkspaceConfinementIntegrationTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "morsa-mcp-boundary", Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        await ProjectScopeTools.ProjectInit(_root, "mcp-boundary");
    }

    public Task DisposeAsync()
    {
        // Release pooled SQLite handles before removing the ephemeral workspace on Windows.
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task IngestFile_OutsideWorkspace_IsRejectedBeforeReadingFile()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"morsa-outside-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(outside, "must not be read through MCP");

        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                ArtifactDiscoveryTools.IngestFile(_root, outside));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task ExportGraph_OutsideReportsDirectory_IsRejectedBeforeWritingFile()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"morsa-outside-{Guid.NewGuid():N}.graphml");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            ReportingPipelineTools.ExportGraph(_root, "graphml", outside));

        Assert.False(File.Exists(outside));
    }

    [Fact]
    public async Task ProjectStatus_AfterDurableRun_OrdersUtcTickDatesWithoutSqliteTranslationFailure()
    {
        var input = Path.Combine(_root, "status.txt");
        await File.WriteAllTextAsync(input, "status fixture");
        await ArtifactDiscoveryTools.IngestFile(_root, input);

        // Serialize the anonymous MCP contract exactly as the stdio transport does.
        var status = await ProjectScopeTools.ProjectStatus(_root);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(status, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var recentRuns = document.RootElement.GetProperty("recent_runs");
        Assert.Single(recentRuns.EnumerateArray());
        Assert.Equal((int)ExecutionStatus.Completed, recentRuns[0].GetProperty("status").GetInt32());
    }
}
