using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ResearchTracker.Application;
using ResearchTracker.Cli;
using ResearchTracker.Core;
using ResearchTracker.Persistence;
using Xunit;

namespace ResearchTracker.Tests;

public sealed class CommandTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "WorkTrackerCommands-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    private WorkspaceCommands Commands => new(root);
    private Guid Project() => Commands.projects_add("Research").GetProperty("project").GetProperty("id").GetGuid();
    [Fact] public void StandaloneCommandsAllowOptionalProjectAndCanClearAssignment()
    {
        var c = Commands; var id = c.tasks_add(title: "No project needed").GetProperty("task").GetProperty("id").GetGuid();
        var store = new WorkspaceStore(root); Assert.Empty(store.Load()!.Projects); Assert.Null(store.Load()!.Tasks.Single().ProjectId);
        var project = Project(); c.tasks_update(id, project: project); Assert.Equal(project, store.Load()!.Tasks.Single().ProjectId);
        c.tasks_update(id, clearProject: true); Assert.Null(store.Load()!.Tasks.Single().ProjectId);
        c.tasks_complete(id); Assert.Equal(WorkStatus.Done, store.Load()!.Tasks.Single().Status); WorkspaceStore.Validate(store.Load()!);
    }
    [Fact] public void SharedCommandsRunGraphTaskAndOutcomeWorkflow()
    {
        var c = Commands; var project = Project(); var node = c.nodes_add(project, "Baseline").GetProperty("node").GetProperty("id").GetGuid();
        var edge = c.edges_add(node, "Experiment"); var taskId = edge.GetProperty("task").GetProperty("id").GetGuid();
        c.entities_update(taskId, "Renamed action", "Evidence"); c.tasks_update(taskId, "High", 2);
        c.content_add(taskId, "**Result:** good"); var result = c.tasks_complete(taskId, "Resulting state");
        Assert.Equal("Done", result.GetProperty("task").GetProperty("status").GetString());
        var data = new WorkspaceStore(root).Load()!; Assert.Equal(2, data.Nodes.Count); Assert.Equal("Renamed action", data.Edges[0].Title); Assert.NotNull(data.Edges[0].TargetId); Assert.Equal(2, data.Tasks[0].EffortHours);
    }
    [Fact] public void CommandFailuresAreAtomicAndReadOnlyAndLeaseAreEnforced()
    {
        var c = Commands; var project = Project(); var t = c.tasks_add(project, "Task").GetProperty("task").GetProperty("id").GetGuid();
        Assert.Throws<ArgumentException>(() => c.tasks_update(t, priority: "High", effortHours: -1));
        Assert.Equal(Priority.Normal, new WorkspaceStore(root).Load()!.Tasks[0].Priority);
        Assert.Throws<InvalidOperationException>(() => new WorkspaceCommands(root, true).projects_add("Denied"));
        using (WorkspaceLease.Acquire(root))
        {
            Assert.Single(c.projects_list().GetProperty("projects").EnumerateArray());
            Assert.Throws<IOException>(() => c.projects_add("Busy"));
        }
        Assert.Single(c.projects_list().GetProperty("projects").EnumerateArray());
    }
    [Fact] public void SchedulingRequiresExplicitConflictOverrideAndValidDependencies()
    {
        var c = Commands; var project = Project(); var a = c.tasks_add(project, "A").GetProperty("task").GetProperty("id").GetGuid(); var b = c.tasks_add(project, "B").GetProperty("task").GetProperty("id").GetGuid();
        c.tasks_depend(b, a); Assert.Throws<InvalidOperationException>(() => c.tasks_depend(a, b));
        Assert.Throws<InvalidOperationException>(() => c.tasks_complete(b));
        c.planner_rule("2030-01-07", unavailable: true);
        Assert.Throws<InvalidOperationException>(() => c.tasks_schedule(a, "2030-01-07 09:00"));
        Assert.NotEmpty(c.tasks_schedule(a, "2030-01-07 09:00", true).GetProperty("conflicts").EnumerateArray());
        c.planner_rule("2030-01-07", unavailable: false);
        var result = c.planner_schedule("2030-01-07"); Assert.Empty(result.GetProperty("issues").EnumerateArray());
        var data = new WorkspaceStore(root).Load()!; Assert.True(data.Tasks[1].ScheduledStart >= data.Tasks[0].ScheduledEnd); Assert.True(data.Tasks[0].Manual);
    }
    [Fact] public void BackupAndAttachmentCommandsRoundTripAndPreserveRecovery()
    {
        var c = Commands; var project = Project(); var file = Path.Combine(root, "source.txt"); File.WriteAllText(file, "offline evidence");
        var block = c.attachments_add(project, file).GetProperty("block").GetProperty("id").GetGuid();
        var copy = Path.Combine(root, "copy.txt"); c.attachments_save(project, block, copy); Assert.Equal("offline evidence", File.ReadAllText(copy));
        var backup = Path.Combine(root, "backup.zip"); c.backup_export(backup); c.projects_add("Later project");
        Assert.Throws<ArgumentException>(() => c.backup_restore(backup));
        var restored = c.backup_restore(backup, true); Assert.True(File.Exists(restored.GetProperty("recovery").GetString())); Assert.Single(new WorkspaceStore(root).Load()!.Projects);
    }
    [Fact] public async Task CliParsesNamedArgumentsJsonFilesAndReportsErrorsWithoutPollutingStdout()
    {
        var output = new StringWriter(); var error = new StringWriter();
        var code = await EntryPoint.RunAsync(["--data", root, "projects", "add", "--title", "CLI project"], output, error);
        Assert.Equal(0, code); Assert.Empty(error.ToString()); var project = JsonDocument.Parse(output.ToString()).RootElement.GetProperty("project").GetProperty("id").GetGuid();
        output.GetStringBuilder().Clear();
        var file = Path.Combine(root, "args.json"); File.WriteAllText(file, JsonSerializer.Serialize(new { project, title = "Task via file", effortHours = 1.5 }));
        Assert.Equal(0, await EntryPoint.RunAsync(["tasks", "add", "--data", root, "--args-file", file], output, error));
        Assert.Equal(1.5, JsonDocument.Parse(output.ToString()).RootElement.GetProperty("task").GetProperty("effortHours").GetDouble());
        output.GetStringBuilder().Clear();
        Assert.Equal(2, await EntryPoint.RunAsync(["--data", root, "tasks", "add", "--unknown", "bad"], output, error)); Assert.Empty(output.ToString()); Assert.Contains("Unknown option", error.ToString());
        error.GetStringBuilder().Clear();
        using (WorkspaceLease.Acquire(root)) Assert.Equal(3, await EntryPoint.RunAsync(["--data", root, "projects", "add", "--title", "Busy"], output, error));
    }
    [Fact] public async Task HelpDoesNotCreateAWorkspaceAndUnknownArgumentsFail()
    {
        var output = new StringWriter(); var error = new StringWriter(); Assert.Equal(0, await EntryPoint.RunAsync(["--data", root, "--help"], output, error)); Assert.False(Directory.Exists(root));
        Assert.Contains("mcp", output.ToString());
        Assert.Equal(2, await EntryPoint.RunAsync(["--data", root, "projects", "add", "--title", "One", "--title", "Two"], output, error)); Assert.False(Directory.Exists(root));
    }
    [Fact] public void InitDoesNotReplaceDataAndDeletionCleansGraphLinks()
    {
        var c = Commands; c.workspace_init(true); Assert.Throws<InvalidOperationException>(() => c.workspace_init());
        var data = new WorkspaceStore(root).Load()!; var node = data.Nodes[0]; Assert.Throws<ArgumentException>(() => c.entities_delete(node.Id));
        c.entities_delete(node.Id, true); var after = new WorkspaceStore(root).Load()!; Assert.DoesNotContain(after.Nodes, n => n.Id == node.Id); Assert.DoesNotContain(after.Edges, e => e.SourceId == node.Id);
    }
}

public sealed class McpTests : IAsyncDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "WorkTrackerMcp-" + Guid.NewGuid().ToString("N"));
    private McpClient? client;
    private async Task<McpClient> Connect(string? protocolVersion = null, bool readOnly = false)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var args = new List<string> { typeof(EntryPoint).Assembly.Location, "--data", root, "mcp" }; if (readOnly) args.Add("--read-only");
        client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions { Name = "Work Tracker test", Command = "dotnet", Arguments = args }), new McpClientOptions { ProtocolVersion = protocolVersion }, cancellationToken: timeout.Token); return client;
    }
    public async ValueTask DisposeAsync() { if (client != null) await client.DisposeAsync(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    private static JsonElement Payload(CallToolResult result)
    {
        Assert.False(result.IsError == true, string.Join(" ", result.Content.OfType<TextContentBlock>().Select(t => t.Text)));
        return JsonSerializer.SerializeToElement(result.StructuredContent);
    }
    [Theory] [InlineData(null)] [InlineData("2025-11-25")] [InlineData("2025-06-18")]
    public async Task StdioHandshakeDiscoveryMutationAndErrorsUseRealProtocol(string? protocolVersion)
    {
        var mcp = await Connect(protocolVersion); var tools = await mcp.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken); Assert.Equal(WorkspaceCommands.Catalog.Length, tools.Count);
        var create = tools.Single(t => t.Name == "work_tracker_projects_add"); Assert.Contains("title", create.JsonSchema.GetRawText());
        var result = Payload(await mcp.CallToolAsync("work_tracker_projects_add", new Dictionary<string, object?> { ["title"] = "MCP project" }, cancellationToken: TestContext.Current.CancellationToken));
        var project = result.GetProperty("project").GetProperty("id").GetGuid();
        var task = Payload(await mcp.CallToolAsync("work_tracker_tasks_add", new Dictionary<string, object?> { ["project"] = project, ["title"] = "MCP task", ["effortHours"] = 2 }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(2, task.GetProperty("task").GetProperty("effortHours").GetDouble());
        var error = await mcp.CallToolAsync("work_tracker_tasks_add", new Dictionary<string, object?> { ["project"] = project, ["title"] = "Bad task", ["effortHours"] = -1 }, cancellationToken: TestContext.Current.CancellationToken); Assert.True(error.IsError); Assert.Contains("greater than zero", string.Join(" ", error.Content.OfType<TextContentBlock>().Select(t => t.Text)));
        Assert.Single(new WorkspaceStore(root).Load()!.Tasks);
        using (WorkspaceLease.Acquire(root))
        {
            var busy = await mcp.CallToolAsync("work_tracker_projects_add", new Dictionary<string, object?> { ["title"] = "Blocked" }, cancellationToken: TestContext.Current.CancellationToken); Assert.True(busy.IsError); Assert.Contains("Workspace is busy", string.Join(" ", busy.Content.OfType<TextContentBlock>().Select(t => t.Text)));
            var read = Payload(await mcp.CallToolAsync("work_tracker_projects_list", new Dictionary<string, object?>(), cancellationToken: TestContext.Current.CancellationToken)); Assert.Single(read.GetProperty("projects").EnumerateArray());
        }
    }
    [Fact] public async Task ReadOnlyMcpAdvertisesOnlyQueries()
    {
        var mcp = await Connect(readOnly: true); var tools = await mcp.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken); Assert.Equal(WorkspaceCommands.Catalog.Count(m => m.GetCustomAttribute<CommandAttribute>()!.ReadOnly), tools.Count);
        Assert.DoesNotContain(tools, t => t.Name == "work_tracker_projects_add");
        var info = Payload(await mcp.CallToolAsync("work_tracker_workspace_info", new Dictionary<string, object?>(), cancellationToken: TestContext.Current.CancellationToken)); Assert.True(info.GetProperty("readOnly").GetBoolean());
    }
}
