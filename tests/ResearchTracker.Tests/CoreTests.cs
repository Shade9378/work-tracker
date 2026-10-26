using ResearchTracker.Core;
using ResearchTracker.Persistence;
using Xunit;

namespace ResearchTracker.Tests;
public class CoreTests
{
    private static Workspace Empty()
    {
        var w = new Workspace(); w.Projects.Add(new Project { Title = "Test" });
        w.Nodes.Add(new GraphNode { ProjectId = w.Projects[0].Id, Title = "Start" }); return w;
    }
    private static WorkTask Task(Workspace w, string title = "Task", double effort = 1)
    {
        var t = new WorkTask { Title = title, ProjectId = w.Projects[0].Id, EffortHours = effort }; w.Tasks.Add(t); return t;
    }
    private static readonly DateTime Monday = new(2026, 10, 26);
    private static void Days(Workspace w)
    {
        for (int i = 0; i < 7; i++) w.Days.Add(new DayRule { Date = Monday.AddDays(i), Unavailable = i > 4 });
    }
    [Fact] public void DanglingActionSynchronizesIdempotentlyInBothDirections()
    {
        var w = Empty(); var g = new GraphService(w); var e = g.Connect(w.Nodes[0], null, "Experiment");
        g.Synchronize(); g.Synchronize(); var t = Assert.Single(w.Tasks); Assert.Equal(e.Id, t.EdgeId);
        t.Title = "Renamed"; t.Status = WorkStatus.InProgress; g.UpdateTask(t);
        Assert.Equal(t.Title, e.Title); Assert.Equal(t.Status, e.Status);
        e.Summary = "Evidence"; g.Synchronize(); Assert.Equal("Evidence", t.Summary);
    }
    [Fact] public void CompleteCreatesOutcomeAndDeletingOutcomeReopensTask()
    {
        var w = Empty(); var g = new GraphService(w); var e = g.Connect(w.Nodes[0], null, "Action"); var t = Assert.Single(w.Tasks);
        var result = g.Complete(t, "Finding"); Assert.Equal(result.Id, e.TargetId); Assert.Equal(WorkStatus.Done, t.Status);
        Assert.Equal(result.Id, g.Complete(t, "Again").Id); Assert.Equal(2, w.Nodes.Count);
        g.DeleteNode(result); Assert.Null(e.TargetId); Assert.Equal(WorkStatus.Todo, t.Status); Assert.Single(w.Tasks);
    }
    [Fact] public void NodeDeletionCleansOutgoingTasksAndDependencyReferences()
    {
        var w = Empty(); var g = new GraphService(w); g.Connect(w.Nodes[0], null, "Action"); var linked = Assert.Single(w.Tasks);
        var other = Task(w); other.Dependencies.Add(linked.Id); g.DeleteNode(w.Nodes[0]);
        Assert.Empty(w.Edges); Assert.Single(w.Tasks); Assert.Empty(other.Dependencies);
    }
    [Fact] public void RejectsCyclesAndCrossProjectConnections()
    {
        var w = Empty(); var a = Task(w, "A"); var b = Task(w, "B"); var c = Task(w, "C"); var g = new GraphService(w);
        g.AddDependency(b, a); g.AddDependency(c, b);
        Assert.Throws<InvalidOperationException>(() => g.AddDependency(a, c)); Assert.Throws<InvalidOperationException>(() => g.AddDependency(a, a));
        Assert.Throws<InvalidOperationException>(() => g.Connect(w.Nodes[0], new GraphNode { ProjectId = Guid.NewGuid() }, "No"));
    }
    [Fact] public void CannotCompleteBeforeDependencies()
    {
        var w = Empty(); var a = Task(w); var b = Task(w); b.Dependencies.Add(a.Id);
        Assert.Throws<InvalidOperationException>(() => new GraphService(w).Complete(b, ""));
        a.Status = WorkStatus.Done; new GraphService(w).Complete(b, ""); Assert.Equal(WorkStatus.Done, b.Status);
    }
    [Fact] public void ResolvingAndReopeningPreservesTaskMetadata()
    {
        var w = Empty(); var g = new GraphService(w); var e = g.Connect(w.Nodes[0], null, "Action"); var t = w.Tasks[0]; t.EffortHours = 3; t.Priority = Priority.Urgent;
        e.TargetId = w.Nodes[0].Id; g.Synchronize(); Assert.Equal(WorkStatus.Done, t.Status);
        t.Status = WorkStatus.Todo; g.UpdateTask(t); Assert.Null(e.TargetId); Assert.Equal(3, t.EffortHours); Assert.Equal(Priority.Urgent, t.Priority);
    }
    [Fact] public void SchedulerOrdersDependenciesAndAvoidsOverlap()
    {
        var w = Empty(); Days(w); var a = Task(w, "Prerequisite", 2); var b = Task(w, "Urgent dependent", 1); b.Priority = Priority.Urgent; b.Dependencies.Add(a.Id);
        var scheduler = new Scheduler(); var issues = scheduler.Schedule(w, Monday);
        Assert.Empty(issues); Assert.True(b.ScheduledStart >= a.ScheduledEnd); Assert.Equal(Monday.AddHours(9), a.ScheduledStart);
    }
    [Fact] public void SchedulerEnforcesCapacityAndUnavailableDays()
    {
        var w = Empty(); Days(w); foreach (var d in w.Days) { d.MaxHours = 1; d.MaxTasks = 1; }
        for (int i = 0; i < 7; i++) Task(w, "Work " + i);
        var issues = new Scheduler().Schedule(w, Monday);
        Assert.Equal(5, w.Tasks.Count(t => t.ScheduledStart != null)); Assert.Equal(2, issues.Count);
        Assert.All(w.Tasks.Where(t => t.ScheduledStart != null), t => Assert.True(t.ScheduledStart!.Value.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday));
    }
    [Fact] public void SchedulerHonorsProjectsDeadlinesAndWorkingHours()
    {
        var w = Empty(); Days(w); var p2 = new Project(); w.Projects.Add(p2);
        foreach (var d in w.Days) d.AllowedProjects = [p2.Id];
        var denied = Task(w, "Wrong project"); Assert.Single(new Scheduler().Schedule(w, Monday)); Assert.Null(denied.ScheduledStart);
        foreach (var d in w.Days) { d.AllowedProjects.Clear(); d.StartHour = 13; d.EndHour = 15; }
        denied.Deadline = new DateTimeOffset(Monday.AddHours(12)); Assert.Single(new Scheduler().Schedule(w, Monday));
        denied.Deadline = new DateTimeOffset(Monday.AddHours(15)); Assert.Empty(new Scheduler().Schedule(w, Monday)); Assert.Equal(Monday.AddHours(13), denied.ScheduledStart);
    }
    [Fact] public void ManualOverridesArePreservedAndConflictsReported()
    {
        var w = Empty(); Days(w); var a = Task(w); a.ScheduledStart = Monday.AddDays(6).AddHours(2); a.Manual = true;
        var b = Task(w); b.ScheduledStart = a.ScheduledStart; b.Manual = true;
        var issues = new Scheduler().Schedule(w, Monday);
        Assert.Equal(Monday.AddDays(6).AddHours(2), a.ScheduledStart); Assert.Contains(issues, i => i.Message.Contains("unavailable")); Assert.Contains(issues, i => i.Message.Contains("Overlaps"));
    }
    [Fact] public void PriorityBreaksDeadlineTiesAndCompletedWorkDoesNotUseCapacity()
    {
        var w = Empty(); Days(w); var low = Task(w, "low"); var urgent = Task(w, "urgent"); urgent.Priority = Priority.Urgent;
        var done = Task(w); done.Status = WorkStatus.Done; done.ScheduledStart = Monday.AddHours(9);
        Assert.Empty(new Scheduler().Schedule(w, Monday)); Assert.Equal(Monday.AddHours(9), urgent.ScheduledStart); Assert.Equal(Monday.AddHours(10), low.ScheduledStart);
    }
    [Fact] public void CancelledOrMissingDependencyCannotBeScheduled()
    {
        var w = Empty(); Days(w); var a = Task(w); a.Status = WorkStatus.Cancelled; var b = Task(w); b.Dependencies.Add(a.Id);
        Assert.Single(new Scheduler().Schedule(w, Monday)); Assert.Null(b.ScheduledStart);
        b.Dependencies = [Guid.NewGuid()]; Assert.Single(new Scheduler().Schedule(w, Monday));
    }
    [Fact] public void NoSlotForOversizedEffortAndInvalidDayRules()
    {
        var w = Empty(); Days(w); Task(w, "Oversized", 20); foreach (var d in w.Days) d.EndHour = 8;
        Assert.Single(new Scheduler().Schedule(w, Monday)); Assert.Null(w.Tasks[0].ScheduledStart);
    }
    [Fact] public void EarlierWeekScheduledDependenciesAreRespected()
    {
        var w = Empty(); Days(w); var a = Task(w); a.ScheduledStart = Monday.AddDays(-1).AddHours(12); var b = Task(w); b.Dependencies.Add(a.Id);
        Assert.Empty(new Scheduler().Schedule(w, Monday)); Assert.Equal(Monday.AddDays(-1).AddHours(12), a.ScheduledStart); Assert.NotNull(b.ScheduledStart);
    }
    [Fact] public void SchedulerNeverPlacesWorkBeforeExplicitCurrentTime()
    {
        var w = Empty(); Days(w); var task = Task(w);
        Assert.Empty(new Scheduler().Schedule(w, Monday, Monday.AddHours(10).AddMinutes(7)));
        Assert.Equal(Monday.AddHours(10).AddMinutes(15), task.ScheduledStart);
    }
}
public sealed class PersistenceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "WorkTrackerTests-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    [Fact] public void SqliteRoundTripRetainsGraphTaskAndNotes()
    {
        var store = new WorkspaceStore(root); var w = SampleData.Create(); store.Save(w); var loaded = store.Load()!;
        Assert.Equal(w.Edges.Count, loaded.Edges.Count); Assert.Equal(w.Tasks[0].Id, loaded.Tasks[0].Id); Assert.Equal(w.Nodes[2].Content[1].Text, loaded.Nodes[2].Content[1].Text);
        loaded.Tasks[0].Priority = Priority.Urgent; store.Save(loaded); Assert.Equal(Priority.Urgent, store.Load()!.Tasks[0].Priority);
    }
    [Fact] public void BackupRoundTripIncludesAttachmentsAndKeepsExistingFiles()
    {
        var store = new WorkspaceStore(root); var w = SampleData.Create(); var source = Path.Combine(root, "evidence.txt"); File.WriteAllText(source, "local evidence");
        var name = store.AddAttachment(source); w.Nodes[0].Content.Add(new ContentBlock { Kind = BlockKind.Attachment, FileName = name, Text = "evidence.txt" });
        var zip = Path.Combine(root, "backup.zip"); store.Export(w, zip); var imported = store.Import(zip);
        var newName = imported.Nodes[0].Content[0].FileName; Assert.NotEqual(name, newName); Assert.Equal("local evidence", File.ReadAllText(store.AttachmentPath(newName))); Assert.True(File.Exists(store.AttachmentPath(name)));
    }
    [Fact] public void RejectsMalformedImportsWithoutReplacingLiveWorkspace()
    {
        var store = new WorkspaceStore(root); var w = SampleData.Create(); store.Save(w);
        var zip = Path.Combine(root, "bad.zip"); using (var z = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create)) using (var writer = new StreamWriter(z.CreateEntry("workspace.json").Open())) writer.Write("{\"Version\":99}");
        Assert.Throws<InvalidDataException>(() => store.Import(zip)); Assert.Equal(w.Nodes.Count, store.Load()!.Nodes.Count);
    }
    [Theory] [InlineData("../secret.txt")] [InlineData("C:\\secret.txt")] [InlineData("..\\secret.txt")] [InlineData("data:stream")]
    public void RejectsAttachmentTraversal(string name)
    {
        var store = new WorkspaceStore(root); Assert.Throws<InvalidDataException>(() => store.AttachmentPath(name));
    }
    [Fact] public void RejectsBrokenReferencesAndDependencyCyclesBeforeSaving()
    {
        var store = new WorkspaceStore(root); var w = SampleData.Create(); store.Save(w); w.Tasks[0].Dependencies.Add(w.Tasks[0].Id);
        Assert.Throws<InvalidDataException>(() => store.Save(w)); Assert.Empty(store.Load()!.Tasks[0].Dependencies);
    }
    [Fact] public void MissingBackupAttachmentLeavesLiveWorkspaceIntact()
    {
        var store = new WorkspaceStore(root); var w = SampleData.Create(); store.Save(w);
        w.Nodes[0].Content.Add(new ContentBlock { Kind = BlockKind.Attachment, FileName = "missing.txt" });
        var zip = Path.Combine(root, "missing.zip"); using (var z = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create)) using (var writer = new StreamWriter(z.CreateEntry("workspace.json").Open())) writer.Write(System.Text.Json.JsonSerializer.Serialize(w));
        Assert.Throws<InvalidDataException>(() => store.Import(zip)); Assert.Empty(store.Load()!.Nodes[0].Content);
    }
}
