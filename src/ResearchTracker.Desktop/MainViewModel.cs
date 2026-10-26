using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using ResearchTracker.Core;
using ResearchTracker.Persistence;
using ResearchTracker.Persistence.Calendar;

namespace ResearchTracker.Desktop;
public partial class MainViewModel : ObservableObject
{
    public WorkspaceStore Store { get; }
    public Workspace Data { get; private set; }
    public ObservableCollection<Project> Projects { get; } = [];
    public ObservableCollection<Project> FilteredProjects { get; } = [];
    public GraphService Graph { get; private set; }
    public Scheduler Scheduler { get; } = new();
    public GoogleCalendarStore CalendarStore { get; }
    public GoogleCalendarClient GoogleCalendar { get; }
    public CalendarCache CalendarCache { get; private set; }
    public bool MonthView { get; set; }
    public DateTime CalendarDate { get; private set; } = DateTime.Today;
    public event Action? Refreshed;
    public event Action? SelectionChanged;
    private readonly HashSet<Guid> graphSelection = [];
    private bool settingSelection;
    public IReadOnlySet<Guid> GraphSelection => graphSelection;
    public bool IsGraphSelected(Entity entity) => graphSelection.Contains(entity.Id);
    [ObservableProperty] private string status = "Saved locally";
    [ObservableProperty] private string query = "";
    [ObservableProperty] private string projectQuery = "";
    [ObservableProperty] private string taskQuery = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasProject))] private Project? selectedProject;
    [ObservableProperty] private Entity? selected;
    [ObservableProperty] private DateTime weekStart;
    public bool HideCompleted { get; set; } = true;
    public List<ScheduleIssue> Issues { get; private set; } = [];
    private readonly HashSet<INotifyPropertyChanged> watched = [];
    private bool changing;
    public bool GraphGestureActive { get; private set; }
    private readonly Avalonia.Threading.DispatcherTimer autosave;
    private readonly WorkspaceHistory history;
    public bool IsRestoringHistory { get; private set; }
    public MainViewModel(WorkspaceStore store, bool seedSampleData = false)
    {
        Store = store; Data = store.Load() ?? (seedSampleData ? SampleData.Create() : new Workspace()); Graph = new GraphService(Data); Graph.Synchronize();
        CalendarStore = new GoogleCalendarStore(store.Root); GoogleCalendar = new GoogleCalendarClient(CalendarStore);
        CalendarCache = CalendarStore.LoadCache(); Scheduler.CalendarEvents = CalendarCache.Events;
        foreach (var p in Data.Projects) Projects.Add(p);
        UpdateProjectFilter();
        selectedProject = Data.Projects.FirstOrDefault();
        var today = DateTime.Today; weekStart = today.AddDays(-((int)today.DayOfWeek + 6) % 7);
        EnsureDays(); Watch();
        history = new WorkspaceHistory(Data);
        autosave = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        autosave.Tick += (_, _) => { autosave.Stop(); Save(); Refresh(false); };
        Save();
    }
    public IEnumerable<GraphNode> Nodes => Data.Nodes.Where(n => n.ProjectId == SelectedProject?.Id);
    public bool HasProject => SelectedProject != null;
    public IEnumerable<GraphEdge> Edges => Data.Edges.Where(e => e.ProjectId == SelectedProject?.Id);
    public bool Matches(Entity e) => string.IsNullOrWhiteSpace(Query) || e.Title.Contains(Query, StringComparison.OrdinalIgnoreCase) || e.Summary.Contains(Query, StringComparison.OrdinalIgnoreCase) || e.Content.Any(b => b.Text.Contains(Query, StringComparison.OrdinalIgnoreCase));
    public bool MatchesTask(Entity e) => string.IsNullOrWhiteSpace(TaskQuery) || e.Title.Contains(TaskQuery, StringComparison.OrdinalIgnoreCase) || e.Summary.Contains(TaskQuery, StringComparison.OrdinalIgnoreCase) || e.Content.Any(b => b.Text.Contains(TaskQuery, StringComparison.OrdinalIgnoreCase));
    public IEnumerable<WorkTask> Tasks => Data.Tasks.Where(MatchesTask).Where(t => !HideCompleted || t.Status is WorkStatus.Todo or WorkStatus.InProgress);
    public string ProjectSummary => SelectedProject == null ? "Select a project" : $"{Nodes.Count()} states · {Edges.Count()} actions · {Data.Tasks.Count(t => t.ProjectId == SelectedProject.Id && t.Status is WorkStatus.Todo or WorkStatus.InProgress)} open tasks";
    public IEnumerable<DayRule> Days => Data.Days.Where(d => d.Date >= WeekStart && d.Date < WeekStart.AddDays(7)).OrderBy(d => d.Date);
    partial void OnSelectedProjectChanged(Project? value) { if (IsRestoringHistory) return; Selected = null; Refresh(false); }
    partial void OnSelectedChanged(Entity? value)
    {
        if (!settingSelection) { graphSelection.Clear(); if (value is GraphNode or GraphEdge) graphSelection.Add(value.Id); }
        if (!IsRestoringHistory) SelectionChanged?.Invoke();
    }
    public void SelectGraph(IEnumerable<Entity> entities, Entity? primary = null)
    {
        var items = entities.Where(e => e is GraphNode or GraphEdge).ToList(); var ids = items.Select(e => e.Id).ToHashSet();
        var next = primary != null && ids.Contains(primary.Id) ? primary : items.FirstOrDefault();
        if (graphSelection.SetEquals(ids) && Selected == next) return;
        graphSelection.Clear(); graphSelection.UnionWith(ids); settingSelection = true;
        var same = Selected == next; Selected = next; settingSelection = false; if (same) SelectionChanged?.Invoke();
    }
    public void SelectGraphItem(Entity entity, bool toggle = false)
    {
        if (toggle)
        {
            var ids = graphSelection.ToHashSet(); if (!ids.Add(entity.Id)) ids.Remove(entity.Id);
            SelectGraph(Data.Nodes.Cast<Entity>().Concat(Data.Edges).Where(e => ids.Contains(e.Id)), entity); return;
        }
        if (graphSelection.Contains(entity.Id)) SelectGraph(Data.Nodes.Cast<Entity>().Concat(Data.Edges).Where(e => graphSelection.Contains(e.Id)).ToList(), entity);
        else SelectGraph([entity], entity);
    }
    partial void OnQueryChanged(string value) => Refresh(false);
    partial void OnTaskQueryChanged(string value) => Refresh(false);
    partial void OnProjectQueryChanged(string value) => UpdateProjectFilter();
    private void UpdateProjectFilter()
    {
        var matches = Projects.Where(p => string.IsNullOrWhiteSpace(ProjectQuery) || p.Title.Contains(ProjectQuery, StringComparison.OrdinalIgnoreCase) || p.Summary.Contains(ProjectQuery, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var p in FilteredProjects.Where(p => !matches.Contains(p)).ToList()) FilteredProjects.Remove(p);
        foreach (var p in matches.Where(p => !FilteredProjects.Contains(p))) FilteredProjects.Add(p);
    }
    public void EnsureDays()
    {
        for (int i = 0; i < 7; i++) if (Data.Days.All(d => d.Date != WeekStart.AddDays(i))) Data.Days.Add(new DayRule { Date = WeekStart.AddDays(i) });
    }
    private void Watch()
    {
        foreach (var item in watched) item.PropertyChanged -= Changed;
        watched.Clear();
        foreach (var item in Data.Projects.Cast<INotifyPropertyChanged>().Concat(Data.Nodes).Concat(Data.Edges).Concat(Data.Tasks).Concat(Data.Days))
            if (watched.Add(item)) item.PropertyChanged += Changed;
    }
    private void Changed(object? sender, PropertyChangedEventArgs args)
    {
        if (changing) return;
        changing = true;
        try
        {
            if (sender is WorkTask t && args.PropertyName is nameof(Entity.Title) or nameof(Entity.Summary) or nameof(WorkTask.Status)) Graph.UpdateTask(t);
            else if (sender is GraphEdge) Graph.Synchronize();
            Status = "Saving…"; autosave.Stop(); if (!GraphGestureActive) autosave.Start();
        }
        finally { changing = false; }
    }
    public void Save()
    {
        if (GraphGestureActive) return;
        try { Store.Save(Data); history.Record(Data); Status = "Saved locally · " + DateTime.Now.ToString("HH:mm:ss"); }
        catch (Exception ex) { Status = "SAVE FAILED · " + ex.Message; }
    }
    public void QueueSave() { autosave.Stop(); if (!GraphGestureActive) autosave.Start(); }
    public void BeginGraphGesture() { autosave.Stop(); Save(); GraphGestureActive = true; }
    public void EndGraphGesture(bool save) { GraphGestureActive = false; Refresh(save); SelectionChanged?.Invoke(); }
    public void Refresh(bool save = true)
    {
        foreach (var p in Data.Projects.Where(p => !Projects.Contains(p))) Projects.Add(p);
        foreach (var p in Projects.Where(p => !Data.Projects.Contains(p)).ToList()) Projects.Remove(p);
        UpdateProjectFilter(); OnPropertyChanged(nameof(ProjectSummary));
        changing = true;
        try
        {
            Graph.Synchronize(); EnsureDays(); Watch();
            Issues = Scheduler.Validate(Data, WeekStart);
        }
        finally { changing = false; }
        if (save) Save(); Refreshed?.Invoke();
    }
    public void AutoSchedule(IReadOnlyDictionary<Guid, int>? counts = null)
    {
        Save();
        changing = true;
        try { Issues = Scheduler.Schedule(Data, WeekStart, DateTime.Now, counts); }
        finally { changing = false; }
        Watch(); Save(); Refreshed?.Invoke();
        if (counts != null) Status = $"Auto assigned {Data.Tasks.Sum(t => TaskSchedule.Occurrences(t).Count(s => s.Start >= WeekStart && s.Start < WeekStart.AddDays(7)))} placements. " + (Issues.Count == 0 ? "All requested placements fit." : "Some placements could not fit the day constraints.");
    }
    public void ChangeWeek(int delta) { WeekStart = WeekStart.AddDays(7 * delta); CalendarDate = WeekStart; EnsureDays(); Refresh(); }
    public void NavigateCalendar(int delta)
    {
        if (!MonthView) { ChangeWeek(delta); return; }
        CalendarDate = CalendarDates.MonthStart(CalendarDate).AddMonths(delta); WeekStart = CalendarDates.Monday(CalendarDate); Refresh();
    }
    public void ShowCalendarDate(DateTime date, bool week = false)
    {
        CalendarDate = date.Date; WeekStart = CalendarDates.Monday(date); if (week) MonthView = false; Refresh();
    }
    public void ReloadCalendar()
    {
        CalendarCache = CalendarStore.LoadCache(); Scheduler.CalendarEvents = CalendarCache.Events; Refresh(false);
    }
    public async Task RefreshCalendarAsync(CancellationToken ct = default)
    {
        var days = MonthView ? CalendarDates.MonthCells(CalendarDate) : Enumerable.Range(0, 7).Select(i => WeekStart.AddDays(i)).ToArray();
        await GoogleCalendar.RefreshAsync(days[0], days[^1].AddDays(1), ct); ReloadCalendar();
    }
    public GraphNode AddNode(string title = "New state")
    {
        var project = SelectedProject ?? throw new InvalidOperationException("Create a project first.");
        var count = Nodes.Count(); var n = new GraphNode { ProjectId = project.Id, Title = title, X = 70 + count % 3 * 310, Y = 70 + count / 3 * 200 };
        Data.Nodes.Add(n); Selected = n; Refresh(); return n;
    }
    public void AddTask()
    {
        var t = new WorkTask { Title = "New task" }; Data.Tasks.Add(t); Selected = t; Refresh();
    }
    public void DeleteProject(Project project)
    {
        Save(); Graph.DeleteProject(project);
        if (SelectedProject?.Id == project.Id) SelectedProject = Data.Projects.FirstOrDefault();
        Selected = null; Refresh();
    }
    public void DeleteSelected()
    {
        if (graphSelection.Count > 1)
        {
            foreach (var node in Data.Nodes.Where(n => graphSelection.Contains(n.Id)).ToList()) Graph.DeleteNode(node);
            foreach (var edge in Data.Edges.Where(e => graphSelection.Contains(e.Id)).ToList()) Graph.DeleteEdge(edge);
            Selected = null; Refresh(); return;
        }
        switch (Selected)
        {
            case GraphNode n: Graph.DeleteNode(n); break;
            case GraphEdge e: Graph.DeleteEdge(e); break;
            case WorkTask t: Graph.DeleteTask(t); break;
        }
        Selected = null; Refresh();
    }
    public void Replace(Workspace data)
    {
        autosave.Stop(); Data = data; Graph = new GraphService(data); Selected = null; SelectedProject = data.Projects.FirstOrDefault(); Refresh();
    }
    public void ManualSchedule(WorkTask task, DateTime start, DateTime? previousStart = null)
    {
        if (task.Status is WorkStatus.Done or WorkStatus.Cancelled) throw new InvalidOperationException("Reopen the task before scheduling.");
        if (Data.Days.All(d => d.Date != start.Date)) Data.Days.Add(new DayRule { Date = start.Date });
        if (previousStart != null) TaskSchedule.Remove(task, previousStart);
        TaskSchedule.Set(task, start, true); Refresh();
    }
    public void Undo() { if (!GraphGestureActive) RestoreHistory(history.Undo(Data), "Undid last edit"); }
    public void Redo() { if (!GraphGestureActive) RestoreHistory(history.Redo(Data), "Redid last edit"); }
    private void RestoreHistory(Workspace? restored, string message)
    {
        if (GraphGestureActive) return;
        autosave.Stop(); if (restored == null) { Status = "Nothing to " + (message.StartsWith("Undid") ? "undo" : "redo"); return; }
        IsRestoringHistory = true;
        var projectId = SelectedProject?.Id; var selectedId = Selected?.Id;
        foreach (var item in watched) item.PropertyChanged -= Changed; watched.Clear();
        Data = restored; Graph = new GraphService(Data);
        Projects.Clear(); foreach (var project in Data.Projects) Projects.Add(project);
        SelectedProject = Data.Projects.FirstOrDefault(p => p.Id == projectId) ?? Data.Projects.FirstOrDefault();
        Selected = Data.Projects.Cast<Entity>().Concat(Data.Nodes).Concat(Data.Edges).Concat(Data.Tasks).FirstOrDefault(e => e.Id == selectedId);
        graphSelection.Clear(); if (Selected is GraphNode or GraphEdge) graphSelection.Add(Selected.Id);
        Refresh(false); OnPropertyChanged(nameof(SelectedProject)); OnPropertyChanged(nameof(Selected)); SelectionChanged?.Invoke();
        history.AdoptRestored(Data); IsRestoringHistory = false;
        try { Store.Save(Data); Status = message; } catch (Exception ex) { Status = "SAVE FAILED · " + ex.Message; }
    }
    public const string ClipboardPrefix = "work-tracker/entity-v1:";
    private sealed record EntityCopy(string Root, string Kind, JsonElement Data, GraphPoint Source, GraphPoint Target, WorkTask? LinkedTask);
    public string CopySelected()
    {
        if (Selected is not GraphNode and not GraphEdge and not WorkTask) throw new InvalidOperationException("Select a state, action or task first.");
        var items = graphSelection.Count > 1 ? Data.Nodes.Cast<Entity>().Concat(Data.Edges).Where(e => graphSelection.Contains(e.Id)).ToList() : new List<Entity> { Selected };
        var nodeIds = items.OfType<GraphNode>().Select(n => n.Id).ToHashSet();
        items.AddRange(Data.Edges.Where(e => e.SourceId != null && e.TargetId != null && nodeIds.Contains(e.SourceId.Value) && nodeIds.Contains(e.TargetId.Value) && !items.Contains(e)));
        var copies = items.Select(item =>
        {
            var ends = item is GraphEdge edge ? GraphGeometry.Endpoints(Data, edge) : default;
            return new EntityCopy(Store.Root, item.GetType().Name, JsonSerializer.SerializeToElement(item, item.GetType()), ends.Source, ends.Target, item is GraphEdge action ? Data.Tasks.FirstOrDefault(t => t.EdgeId == action.Id) : null);
        });
        return ClipboardPrefix + JsonSerializer.Serialize(copies);
    }
    public Entity Paste(string clipboard)
    {
        if (!clipboard.StartsWith(ClipboardPrefix, StringComparison.Ordinal)) throw new InvalidOperationException("Copy a Work Tracker state, action or task first.");
        var copies = JsonSerializer.Deserialize<List<EntityCopy>>(clipboard[ClipboardPrefix.Length..]) ?? throw new InvalidDataException("Invalid clipboard content.");
        if (copies.Count == 0 || copies.Any(copy => !string.Equals(copy.Root, Store.Root, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Entity paste is supported within this local workspace. Use a backup to transfer workspaces.");
        var project = SelectedProject; if (project == null && copies.Any(c => c.Kind != nameof(WorkTask))) throw new InvalidOperationException("Select a project first.");
        var idMap = copies.ToDictionary(copy => copy.Data.GetProperty("Id").GetGuid(), _ => Guid.NewGuid());
        var pasted = new List<Entity>(); var addedTaskIds = new List<Guid>();
        try
        {
        foreach (var copy in copies.OrderBy(c => c.Kind == nameof(GraphNode) ? 0 : 1))
        {
            Entity entity = copy.Kind switch { nameof(GraphNode) => copy.Data.Deserialize<GraphNode>()!, nameof(GraphEdge) => copy.Data.Deserialize<GraphEdge>()!, nameof(WorkTask) => copy.Data.Deserialize<WorkTask>()!, _ => throw new InvalidDataException("Unsupported clipboard item.") };
            entity.Id = idMap[entity.Id]; entity.Title += " (copy)"; foreach (var block in entity.Content) block.Id = Guid.NewGuid(); pasted.Add(entity);
            switch (entity)
            {
            case GraphNode node: node.ProjectId = project!.Id; node.X += 40; node.Y += 40; Data.Nodes.Add(node); break;
            case GraphEdge action:
                action.SourceId = action.SourceId is { } source && idMap.TryGetValue(source, out var newSource) ? newSource : action.ProjectId == project!.Id ? action.SourceId : null;
                action.TargetId = action.TargetId is { } target && idMap.TryGetValue(target, out var newTarget) ? newTarget : action.ProjectId == project!.Id ? action.TargetId : null;
                action.ProjectId = project!.Id; action.SourceX = copy.Source.X + 40; action.SourceY = copy.Source.Y + 40; action.TargetX = copy.Target.X + 40; action.TargetY = copy.Target.Y + 40;
                if (action.SourceId != null && Data.Nodes.All(n => n.Id != action.SourceId)) action.SourceId = null;
                if (action.TargetId != null && Data.Nodes.All(n => n.Id != action.TargetId)) action.TargetId = null;
                action.Status = action.TargetId == null ? WorkStatus.Todo : WorkStatus.Done; Data.Edges.Add(action);
                if (copy.LinkedTask is { } linked && action.TargetId == null)
                {
                    linked.Id = Guid.NewGuid(); linked.EdgeId = action.Id; linked.ProjectId = project!.Id; TaskSchedule.Remove(linked); linked.Status = WorkStatus.Todo;
                    linked.Dependencies.RemoveAll(id => Data.Tasks.All(t => t.Id != id)); foreach (var block in linked.Content) block.Id = Guid.NewGuid(); Data.Tasks.Add(linked); addedTaskIds.Add(linked.Id);
                }
                break;
            case WorkTask task:
                task.ProjectId = task.ProjectId == null ? null : project?.Id ?? task.ProjectId; task.EdgeId = null; TaskSchedule.Remove(task); task.Status = WorkStatus.Todo; task.Dependencies.RemoveAll(id => Data.Tasks.All(t => t.Id != id)); Data.Tasks.Add(task); break;
            }
        }
        Graph.Synchronize(); WorkspaceStore.Validate(Data);
        }
        catch { var ids = pasted.Select(e => e.Id).ToHashSet(); Data.Nodes.RemoveAll(n => ids.Contains(n.Id)); Data.Edges.RemoveAll(e => ids.Contains(e.Id)); Data.Tasks.RemoveAll(t => ids.Contains(t.Id) || t.EdgeId is { } id && ids.Contains(id) || addedTaskIds.Contains(t.Id)); throw; }
        if (pasted.Any(e => e is GraphNode or GraphEdge)) SelectGraph(pasted); else Selected = pasted[0]; Refresh(); return pasted[0];
    }
    public Entity DuplicateSelected() => Paste(CopySelected());
}
