using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using ResearchTracker.Application;
using ResearchTracker.Core;
using ResearchTracker.Desktop;
using ResearchTracker.Persistence;
using Xunit;

namespace ResearchTracker.Tests;

public sealed class EndpointTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "WorkTrackerEndpoints-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    [Fact] public void EachEndCanBeReattachedOrFreedWithoutMovingTheOther()
    {
        var data = SampleData.Create(); var graph = new GraphService(data); var edge = data.Edges[0]; var old = GraphGeometry.Endpoints(data, edge);
        graph.MoveEndpoint(edge, EdgeEnd.Source, null, 10, 20); Assert.Null(edge.SourceId); Assert.Equal(new GraphPoint(10, 20), GraphGeometry.Endpoints(data, edge).Source); Assert.Equal(old.Target, GraphGeometry.Endpoints(data, edge).Target);
        graph.MoveEndpoint(edge, EdgeEnd.Target, null, 500, 600); var task = data.Tasks.Single(t => t.EdgeId == edge.Id); task.Priority = Priority.Urgent; task.EffortHours = 3;
        Assert.Equal(WorkStatus.Todo, task.Status); var taskId = task.Id;
        graph.MoveEndpoint(edge, EdgeEnd.Source, data.Nodes[2], 0, 0); Assert.Equal(new GraphPoint(500, 600), GraphGeometry.Endpoints(data, edge).Target);
        graph.MoveEndpoint(edge, EdgeEnd.Target, data.Nodes[3], 0, 0); Assert.Equal(WorkStatus.Done, task.Status);
        graph.MoveEndpoint(edge, EdgeEnd.Target, null, 550, 650); Assert.Equal(taskId, data.Tasks.Single(t => t.EdgeId == edge.Id).Id); Assert.Equal(Priority.Urgent, task.Priority); Assert.Equal(3, task.EffortHours); Assert.Equal(WorkStatus.Todo, task.Status);
        var store = new WorkspaceStore(root); store.Save(data); var loaded = store.Load()!; Assert.Equal(new GraphPoint(550, 650), GraphGeometry.Endpoints(loaded, loaded.Edges[0]).Target);
    }
    [Fact] public void BothFreeActionCanCompleteAndForeignProjectSnapIsRejected()
    {
        var data = SampleData.Create(); var graph = new GraphService(data); var edge = data.Edges.Last();
        graph.MoveEndpoint(edge, EdgeEnd.Source, null, 50, 60); graph.MoveEndpoint(edge, EdgeEnd.Target, null, 300, 400);
        var result = graph.Complete(data.Tasks.Single(t => t.EdgeId == edge.Id), "Free-start outcome"); Assert.Equal(120, result.X); Assert.Equal(result.Id, edge.TargetId); Assert.Null(edge.SourceId);
        var foreign = new GraphNode { ProjectId = data.Projects[1].Id }; data.Nodes.Add(foreign);
        Assert.Throws<InvalidOperationException>(() => graph.MoveEndpoint(edge, EdgeEnd.Source, foreign, 0, 0)); Assert.Null(edge.SourceId);
        Assert.Throws<ArgumentException>(() => graph.MoveEndpoint(edge, EdgeEnd.Source, null, double.NaN, 0));
    }
    [Fact] public void VersionOneWorkspaceMigratesWithStableFreeCoordinates()
    {
        var store = new WorkspaceStore(root); var data = SampleData.Create(); data.Version = 1;
        var json = JsonSerializer.SerializeToNode(data)!;
        foreach (var edge in json["Edges"]!.AsArray()) foreach (var key in new[] { "SourceX", "SourceY", "TargetX", "TargetY" }) edge!.AsObject().Remove(key);
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + Path.Combine(root, "research.db")))
        {
            db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "INSERT INTO workspace VALUES(1,1,$json,'test')"; cmd.Parameters.AddWithValue("$json", json.ToJsonString()); cmd.ExecuteNonQuery();
        }
        var loaded = store.Load()!; Assert.Equal(4, loaded.Version); Assert.NotNull(loaded.Edges.Last().TargetX); var position = GraphGeometry.Endpoints(loaded, loaded.Edges.Last()).Target;
        loaded.Nodes.Single(n => n.Id == loaded.Edges.Last().SourceId).X += 100; Assert.Equal(position, GraphGeometry.Endpoints(loaded, loaded.Edges.Last()).Target); store.Save(loaded); Assert.Equal(4, store.Load()!.Version);
        Assert.Single(Directory.GetFiles(root, "before-schema-v4-*.db"));
    }
    [Fact] public void AutomationCanMoveBothEndpointsAndSaveTheirCoordinates()
    {
        var c = new WorkspaceCommands(root); c.workspace_init(true); var edge = new WorkspaceStore(root).Load()!.Edges[0];
        c.edges_endpoint(edge.Id, "Source", x: 10, y: 20); c.edges_endpoint(edge.Id, "Target", x: 30, y: 40);
        var data = new WorkspaceStore(root).Load()!; Assert.Null(data.Edges[0].SourceId); Assert.Null(data.Edges[0].TargetId); Assert.Equal((new GraphPoint(10, 20), new GraphPoint(30, 40)), GraphGeometry.Endpoints(data, data.Edges[0])); Assert.Single(data.Tasks, t => t.EdgeId == edge.Id);
    }
    private static void Press(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None) { window.KeyPress(key, modifiers, PhysicalKey.None, null); window.KeyRelease(key, modifiers, PhysicalKey.None, null); }
    [AvaloniaFact] public void PointerCanFreeBothEndsSnapToStateAndCancelWithoutAutosavingPreview()
    {
        var vm = new MainViewModel(new WorkspaceStore(root), seedSampleData: true); var window = new MainWindow(vm); window.Show();
        try
        {
            window.FindControl<TabControl>("ProjectTabs")!.SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
            var canvas = window.FindControl<GraphCanvas>("GraphView")!; canvas.Fit(); var edge = vm.Data.Edges[0]; vm.Selected = edge;
            Point At(Point world) => canvas.TranslatePoint(canvas.GraphToScreen(world), window)!.Value;
            void Move(EdgeEnd end, Point drop, RawInputModifiers modifiers = RawInputModifiers.None)
            {
                var ends = canvas.ArrowEnds(edge); var handle = At(end == EdgeEnd.Source ? ends.Source : ends.Target); var to = At(drop);
                window.MouseDown(handle, MouseButton.Left); window.MouseMove(to, RawInputModifiers.LeftMouseButton | modifiers); window.MouseUp(to, MouseButton.Left, modifiers); Dispatcher.UIThread.RunJobs();
            }
            Move(EdgeEnd.Source, new Point(120, 500)); Assert.Null(edge.SourceId); Assert.Equal(120d, edge.SourceX!.Value, 6);
            Move(EdgeEnd.Target, new Point(620, 500)); Assert.Null(edge.TargetId); var taskId = vm.Data.Tasks.Single(t => t.EdgeId == edge.Id).Id;
            var snap = vm.Data.Nodes[2]; Move(EdgeEnd.Target, new Point(snap.X + 100, snap.Y + GraphGeometry.NodeHeight / 2)); Assert.Equal(snap.Id, edge.TargetId);
            Move(EdgeEnd.Target, new Point(snap.X + 100, snap.Y + GraphGeometry.NodeHeight / 2), RawInputModifiers.Alt); Assert.Null(edge.TargetId); Assert.Equal(taskId, vm.Data.Tasks.Single(t => t.EdgeId == edge.Id).Id);
            var before = GraphGeometry.Endpoints(vm.Data, edge); var from = At(canvas.ArrowEnds(edge).Source); window.MouseDown(from, MouseButton.Left); window.MouseMove(At(new Point(50, 550)), RawInputModifiers.LeftMouseButton);
            vm.Save(); var saved = vm.Store.Load()!; Assert.Equal(before.Source, GraphGeometry.Endpoints(saved, saved.Edges[0]).Source);
            Press(window, Key.Escape); window.MouseUp(At(new Point(50, 550)), MouseButton.Left);
            Assert.False(canvas.EditingEndpoint); Assert.False(vm.GraphGestureActive); Assert.Equal(before, GraphGeometry.Endpoints(vm.Data, edge));
        }
        finally { window.Close(); }
    }
    [AvaloniaFact] public void KeyboardEditingAndSearchScopesStayIndependent()
    {
        var vm = new MainViewModel(new WorkspaceStore(root), seedSampleData: true); var window = new MainWindow(vm); window.Show();
        try
        {
            var originalProject = vm.SelectedProject; vm.ProjectQuery = "Literature"; Assert.Single(vm.FilteredProjects); Assert.Same(originalProject, vm.SelectedProject); Assert.Equal(3, vm.Tasks.Count());
            vm.Query = "does not match"; Assert.Equal(3, vm.Tasks.Count()); vm.TaskQuery = "literature"; Assert.Single(vm.Tasks); vm.ProjectQuery = ""; vm.TaskQuery = ""; vm.Query = "";
            var projectTabs = window.FindControl<TabControl>("ProjectTabs")!; projectTabs.SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
            var canvas = window.FindControl<GraphCanvas>("GraphView")!; var edge = vm.Data.Edges[0]; vm.Selected = edge; canvas.Focus(); var before = GraphGeometry.Endpoints(vm.Data, edge);
            Press(window, Key.Tab); Assert.Equal(EdgeEnd.Source, canvas.FocusedEnd);
            Press(window, Key.Right, RawInputModifiers.Shift);
            Press(window, Key.Enter, RawInputModifiers.Alt); Assert.Null(edge.SourceId); Assert.Equal(before.Source.X + 10, edge.SourceX);
            var main = window.FindControl<TabControl>("MainTabs")!;
            Press(window, Key.D2, RawInputModifiers.Control); Assert.Equal(1, main.SelectedIndex); Assert.Equal(3, vm.Tasks.Count());
            Press(window, Key.K, RawInputModifiers.Control); Assert.True(window.FindControl<TextBox>("TaskSearch")!.IsFocused);
            var count = vm.Data.Nodes.Count; window.KeyTextInput("cfd"); Assert.Equal(count, vm.Data.Nodes.Count); Assert.False(canvas.Connecting);
            Press(window, Key.G, RawInputModifiers.Control); Assert.Equal(0, main.SelectedIndex); Assert.Equal(1, projectTabs.SelectedIndex);
        }
        finally { window.Close(); }
    }
}
