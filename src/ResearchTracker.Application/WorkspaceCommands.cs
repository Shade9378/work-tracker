using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ResearchTracker.Core;
using ResearchTracker.Persistence;
using ResearchTracker.Persistence.Calendar;

namespace ResearchTracker.Application;

[AttributeUsage(AttributeTargets.Method)]
public sealed class CommandAttribute(bool readOnly = false, bool destructive = false) : Attribute
{
    public bool ReadOnly { get; } = readOnly;
    public bool Destructive { get; } = destructive;
}

/// <summary>Transport-independent use cases; CLI and MCP expose this exact same catalog.</summary>
public sealed class WorkspaceCommands(string root, bool readOnly = false)
{
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web) { WriteIndented = true, TypeInfoResolver = new DefaultJsonTypeInfoResolver(), Converters = { new JsonStringEnumConverter() } };
    public static MethodInfo[] Catalog => typeof(WorkspaceCommands).GetMethods().Where(m => m.GetCustomAttribute<CommandAttribute>() != null).OrderBy(m => m.Name).ToArray();
    private readonly object gate = new();
    private sealed class Context(WorkspaceStore store, Workspace data)
    {
        public WorkspaceStore Store { get; } = store;
        public Workspace Data { get; set; } = data;
        public GraphService Graph => new(Data);
        public Scheduler Scheduler => new() { CalendarEvents = new GoogleCalendarStore(Store.Root).LoadCache().Events };
    }
    private JsonElement Execute(bool write, Func<Context, object> action)
    {
        if (write && readOnly) throw new InvalidOperationException("This instance is read-only. Start without --read-only to allow mutations.");
        lock (gate)
        {
            using var lease = write ? WorkspaceLease.Acquire(root) : null;
            var store = new WorkspaceStore(root); var context = new Context(store, store.Load() ?? new Workspace());
            var result = action(context);
            if (write) { context.Graph.Synchronize(); store.Save(context.Data); }
            return JsonSerializer.SerializeToElement(result, Json);
        }
    }
    private static JsonElement View(Entity entity) => JsonSerializer.SerializeToElement(entity, entity.GetType(), Json);
    private static IEnumerable<Entity> Entities(Workspace w) => w.Projects.Cast<Entity>().Concat(w.Nodes).Concat(w.Edges).Concat(w.Tasks);
    private static T Find<T>(IEnumerable<T> items, Guid id) where T : Entity => items.SingleOrDefault(i => i.Id == id) ?? throw new ArgumentException($"{typeof(T).Name} {id} was not found.");
    private static Entity Entity(Workspace w, Guid id) => Find(Entities(w), id);
    private static string Title(string title) => string.IsNullOrWhiteSpace(title) ? throw new ArgumentException("Title must not be empty.") : title.Trim();
    private static T EnumValue<T>(string value) where T : struct, Enum => Enum.GetNames<T>().Any(n => n.Equals(value, StringComparison.OrdinalIgnoreCase)) && Enum.TryParse<T>(value, true, out var parsed) ? parsed : throw new ArgumentException($"Invalid {typeof(T).Name}: {value}. Expected {string.Join(", ", Enum.GetNames<T>())}.");
    private static void Effort(double value) { if (!double.IsFinite(value) || value <= 0 || value > 168) throw new ArgumentException("Effort must be greater than zero and at most 168 hours."); }
    private static DateTime Date(string value) => DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var result) ? result : throw new ArgumentException("Date format must be yyyy-MM-dd.");
    private static DateTime LocalTime(string value) => DateTime.TryParseExact(value, new[] { "yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var result) ? result : throw new ArgumentException("Local time format must be yyyy-MM-dd HH:mm.");
    private static bool Match(Entity e, string? query) => string.IsNullOrEmpty(query) || e.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || e.Summary.Contains(query, StringComparison.OrdinalIgnoreCase) || e.Content.Any(b => b.Text.Contains(query, StringComparison.OrdinalIgnoreCase));
    private static DayRule EnsureDay(Workspace w, DateTime date)
    {
        var day = w.Days.SingleOrDefault(d => d.Date == date);
        if (day != null) return day;
        day = new DayRule { Date = date }; w.Days.Add(day); return day;
    }
    private object Plan(Workspace w, DateTime start, List<ScheduleIssue>? issues = null) => new
    {
        weekStart = start, days = w.Days.Where(d => d.Date >= start && d.Date < start.AddDays(7)).OrderBy(d => d.Date).ToArray(),
        tasks = w.Tasks.Where(t => TaskSchedule.Occurrences(t).Any(s => s.Start >= start && s.Start < start.AddDays(7))).Select(View).ToArray(),
        unscheduled = w.Tasks.Where(t => t.ScheduledStart == null && t.Status is WorkStatus.Todo or WorkStatus.InProgress).Select(View).ToArray(),
        issues = issues ?? new Scheduler { CalendarEvents = new GoogleCalendarStore(root).LoadCache().Events }.Validate(w, start)
    };

    [Command(true), Description("Inspect the fixed local workspace location, version and record counts. Does not seed sample data.")]
    public JsonElement workspace_info() => Execute(false, c => new { root = Path.GetFullPath(root), initialized = c.Store.Load() != null, version = c.Data.Version, projects = c.Data.Projects.Count, nodes = c.Data.Nodes.Count, edges = c.Data.Edges.Count, tasks = c.Data.Tasks.Count, readOnly });

    [Command, Description("Initialize a new local workspace, optionally with sample data. Refuses to replace an initialized workspace.")]
    public JsonElement workspace_init(bool sample = false) => Execute(true, c =>
    {
        if (c.Store.Load() != null) throw new InvalidOperationException("Workspace is already initialized; no data was changed.");
        if (sample) c.Data = SampleData.Create();
        return new { initialized = true, sample };
    });

    [Command(true), Description("Search titles, summaries, rich note source text and attachment display names across all entity types.")]
    public JsonElement workspace_search(string query) => Execute(false, c => new { items = Entities(c.Data).Where(e => Match(e, query)).Select(e => new { type = e.GetType().Name, entity = View(e) }).ToArray() });

    [Command(true), Description("List projects, optionally filtering by text.")]
    public JsonElement projects_list(string? query = null) => Execute(false, c => new { projects = c.Data.Projects.Where(p => Match(p, query)).Select(View).ToArray() });

    [Command, Description("Create a research project.")]
    public JsonElement projects_add(string title, string summary = "") => Execute(true, c => { var p = new Project { Title = Title(title), Summary = summary, ColorHex = GraphColors.NextProject(c.Data.Projects) }; c.Data.Projects.Add(p); return new { project = View(p) }; });

    [Command(true), Description("List graph states, optionally scoped to a project and search text.")]
    public JsonElement nodes_list(Guid? project = null, string? query = null) => Execute(false, c => new { nodes = c.Data.Nodes.Where(n => project == null || n.ProjectId == project).Where(n => Match(n, query)).Select(View).ToArray() });

    [Command, Description("Create a graph state. kind is State, Problem, Finding or Outcome. Coordinates are native graph positions.")]
    public JsonElement nodes_add(Guid project, string title, string kind = "State", string summary = "", double x = 70, double y = 70) => Execute(true, c =>
    {
        Find(c.Data.Projects, project); if (!double.IsFinite(x) || !double.IsFinite(y)) throw new ArgumentException("Coordinates must be finite.");
        var n = new GraphNode { ProjectId = project, Title = Title(title), Kind = EnumValue<NodeKind>(kind), Summary = summary, X = x, Y = y }; c.Data.Nodes.Add(n); return new { node = View(n) };
    });

    [Command(true), Description("List directed graph actions, optionally filtering by project or dangling/planned actions.")]
    public JsonElement edges_list(Guid? project = null, bool dangling = false) => Execute(false, c => new { edges = c.Data.Edges.Where(e => project == null || e.ProjectId == project).Where(e => !dangling || e.TargetId == null).Select(View).ToArray() });

    [Command, Description("Connect two states, or omit target to create a dangling action and its linked task atomically.")]
    public JsonElement edges_add(Guid source, string title, Guid? target = null, string summary = "") => Execute(true, c =>
    {
        var edge = c.Graph.Connect(Find(c.Data.Nodes, source), target == null ? null : Find(c.Data.Nodes, target.Value), Title(title)); edge.Summary = summary; c.Graph.Synchronize();
        return new { edge = View(edge), task = c.Data.Tasks.FirstOrDefault(t => t.EdgeId == edge.Id) is { } task ? View(task) : (JsonElement?)null };
    });

    [Command, Description("Change an action's destination; omit target to reopen it as a planned task. Supports branches and merges.")]
    public JsonElement edges_retarget(Guid id, Guid? target = null) => Execute(true, c =>
    {
        var edge = Find(c.Data.Edges, id); var node = target == null ? null : Find(c.Data.Nodes, target.Value);
        if (node != null && node.ProjectId != edge.ProjectId) throw new ArgumentException("Connections must stay within one project.");
        var point = GraphGeometry.Endpoints(c.Data, edge).Target; c.Graph.MoveEndpoint(edge, EdgeEnd.Target, node, point.X, point.Y); return new { edge = View(edge) };
    });

    [Command, Description("Move either arrow endpoint. end is Source or Target. Omit node to leave it free at x,y; coordinates are optional when attaching to a state.")]
    public JsonElement edges_endpoint(Guid id, string end, Guid? node = null, double? x = null, double? y = null) => Execute(true, c =>
    {
        var edge = Find(c.Data.Edges, id); var which = EnumValue<EdgeEnd>(end); var points = GraphGeometry.Endpoints(c.Data, edge); var previous = which == EdgeEnd.Source ? points.Source : points.Target;
        if (x.HasValue != y.HasValue) throw new ArgumentException("Provide both x and y.");
        c.Graph.MoveEndpoint(edge, which, node == null ? null : Find(c.Data.Nodes, node.Value), x ?? previous.X, y ?? previous.Y);
        return new { edge = View(edge) };
    });

    [Command(true), Description("Get a complete project, node, edge or task by its GUID, including research content.")]
    public JsonElement entities_get(Guid id) => Execute(false, c => new { entity = View(Entity(c.Data, id)) });

    [Command(destructive: true), Description("Update title/summary; optionally change a node's kind/position or a project's color (#RRGGBB; empty string resets automatic). Omitted fields are retained.")]
    public JsonElement entities_update(Guid id, string? title = null, string? summary = null, string? kind = null, double? x = null, double? y = null, string? color = null) => Execute(true, c =>
    {
        var entity = Entity(c.Data, id); if (title != null) entity.Title = Title(title); if (summary != null) entity.Summary = summary;
        if (color != null)
        {
            var value = color.Length == 0 ? null : color.ToUpperInvariant();
            if (!GraphColors.IsValid(value)) throw new ArgumentException("Project color must use #RRGGBB; use an empty string for automatic.");
            if (entity is Project project) project.ColorHex = value;
            else throw new ArgumentException("Color applies only to projects. State and action headings inherit their project's color.");
        }
        if (kind != null || x != null || y != null)
        {
            if (entity is not GraphNode n) throw new ArgumentException("kind, x and y apply only to graph nodes.");
            if (kind != null) n.Kind = EnumValue<NodeKind>(kind); if (x != null) n.X = x.Value; if (y != null) n.Y = y.Value;
        }
        if (entity is WorkTask task) c.Graph.UpdateTask(task); else c.Graph.Synchronize();
        return new { entity = View(entity) };
    });

    [Command(destructive: true), Description("Delete a node, edge or task. Requires confirm=true. Cascades linked actions/tasks and cleans dependency references. Project deletion is not supported.")]
    public JsonElement entities_delete(Guid id, bool confirm = false) => Execute(true, c =>
    {
        if (!confirm) throw new ArgumentException("Deletion requires confirm=true (--confirm on the CLI).");
        switch (Entity(c.Data, id)) { case GraphNode n: c.Graph.DeleteNode(n); break; case GraphEdge e: c.Graph.DeleteEdge(e); break; case WorkTask t: c.Graph.DeleteTask(t); break; default: throw new ArgumentException("Project deletion is not supported."); }
        return new { deleted = id };
    });

    [Command(true), Description("List actionable or completed tasks. Filter by project, text, status (Todo/InProgress/Done/Cancelled), priority, or unscheduled work.")]
    public JsonElement tasks_list(Guid? project = null, string? query = null, string? status = null, string? priority = null, bool unscheduled = false) => Execute(false, c =>
    {
        var state = status == null ? (WorkStatus?)null : EnumValue<WorkStatus>(status); var rank = priority == null ? (Priority?)null : EnumValue<Priority>(priority);
        return new { tasks = c.Data.Tasks.Where(t => project == null || t.ProjectId == project).Where(t => Match(t, query)).Where(t => state == null || t.Status == state).Where(t => rank == null || t.Priority == rank).Where(t => !unscheduled || t.ScheduledStart == null && t.Status is WorkStatus.Todo or WorkStatus.InProgress).Select(View).ToArray() };
    });

    [Command, Description("Create a standalone task with priority, effort in hours and an optional local deadline (yyyy-MM-dd HH:mm).")]
    public JsonElement tasks_add(Guid? project = null, string title = "New task", string summary = "", string priority = "Normal", double effortHours = 1, string? deadline = null) => Execute(true, c =>
    {
        if (project is { } projectId) Find(c.Data.Projects, projectId); Effort(effortHours);
        var t = new WorkTask { ProjectId = project, Title = Title(title), Summary = summary, Priority = EnumValue<Priority>(priority), EffortHours = effortHours, Deadline = string.IsNullOrEmpty(deadline) ? null : new DateTimeOffset(LocalTime(deadline)) };
        c.Data.Tasks.Add(t); return new { task = View(t) };
    });

    [Command(destructive: true), Description("Update task planning metadata. Use an empty deadline to clear it. status accepts Todo, InProgress or Cancelled; use tasks_complete for Done.")]
    public JsonElement tasks_update(Guid id, string? priority = null, double? effortHours = null, string? deadline = null, string? status = null, Guid? project = null, bool clearProject = false) => Execute(true, c =>
    {
        var t = Find(c.Data.Tasks, id);
        if (clearProject) { if (t.EdgeId != null || project != null) throw new ArgumentException("Only standalone tasks can clear their project; do not supply project with clearProject."); t.ProjectId = null; }
        if (priority != null) t.Priority = EnumValue<Priority>(priority);
        if (effortHours != null) { Effort(effortHours.Value); t.EffortHours = effortHours.Value; }
        if (deadline != null) t.Deadline = deadline.Length == 0 ? null : new DateTimeOffset(LocalTime(deadline));
        if (project != null) { if (t.EdgeId != null) throw new ArgumentException("A graph task's project follows its action."); Find(c.Data.Projects, project.Value); t.ProjectId = project.Value; }
        if (status != null) { var parsed = EnumValue<WorkStatus>(status); if (parsed == WorkStatus.Done) throw new ArgumentException("Use tasks complete to record an outcome and validate dependencies."); t.Status = parsed; if (parsed == WorkStatus.Cancelled) TaskSchedule.Remove(t); }
        c.Graph.UpdateTask(t); return new { task = View(t) };
    });

    [Command, Description("Complete a task after its dependencies. Linked graph actions require outcomeTitle and create a resulting state atomically.")]
    public JsonElement tasks_complete(Guid id, string outcomeTitle = "") => Execute(true, c => { var task = Find(c.Data.Tasks, id); var outcome = c.Graph.Complete(task, outcomeTitle); return new { task = View(task), outcome = outcome == null ? (JsonElement?)null : View(outcome) }; });

    [Command(destructive: true), Description("Add or remove a task prerequisite. Dependency cycles and self-dependencies are rejected.")]
    public JsonElement tasks_depend(Guid id, Guid dependency, bool remove = false) => Execute(true, c =>
    {
        var task = Find(c.Data.Tasks, id); var prerequisite = Find(c.Data.Tasks, dependency);
        if (remove) task.Dependencies.Remove(dependency); else c.Graph.AddDependency(task, prerequisite); return new { task = View(task) };
    });

    [Command(destructive: true), Description("Add/update one day's placement (yyyy-MM-dd HH:mm). moveFrom moves an existing placement; removeStart removes one. Omit all times to unschedule all days. Conflicts require overrideConflicts=true.")]
    public JsonElement tasks_schedule(Guid id, string? start = null, bool overrideConflicts = false, string? moveFrom = null, string? removeStart = null) => Execute(true, c =>
    {
        var t = Find(c.Data.Tasks, id); List<string> conflicts = [];
        if (removeStart != null) { if (start != null || moveFrom != null) throw new ArgumentException("Use removeStart alone."); TaskSchedule.Remove(t, LocalTime(removeStart)); }
        else if (string.IsNullOrEmpty(start)) { if (moveFrom != null) throw new ArgumentException("start is required with moveFrom."); TaskSchedule.Remove(t); }
        else
        {
            if (t.Status is WorkStatus.Done or WorkStatus.Cancelled) throw new ArgumentException("Reopen the task before scheduling.");
            var date = LocalTime(start); EnsureDay(c.Data, date.Date);
            if (moveFrom != null) { var previous = LocalTime(moveFrom); if (!TaskSchedule.Occurrences(t).Any(s => s.Start == previous)) throw new ArgumentException("Original placement not found."); TaskSchedule.Remove(t, previous); }
            conflicts = c.Scheduler.Conflicts(c.Data, t, date);
            if (conflicts.Count > 0 && !overrideConflicts) throw new InvalidOperationException(string.Join(" ", conflicts) + " Use overrideConflicts=true to pin anyway.");
            TaskSchedule.Set(t, date, true);
        }
        return new { task = View(t), conflicts };
    });

    [Command(true), Description("Inspect a seven-day planner starting at week (yyyy-MM-dd), including constraints, reservations, unscheduled work and conflicts.")]
    public JsonElement planner_show(string week) => Execute(false, c =>
    {
        var start = Date(week); for (int i = 0; i < 7; i++) EnsureDay(c.Data, start.AddDays(i)); return Plan(c.Data, start);
    });

    [Command(destructive: true), Description("Set one day's constraints. Hours are decimal local hours. allowedProjects is a GUID array; [] allows all projects. Omitted fields are retained.")]
    public JsonElement planner_rule(string date, bool? unavailable = null, int? maxTasks = null, double? startHour = null, double? endHour = null, double? maxHours = null, Guid[]? allowedProjects = null) => Execute(true, c =>
    {
        var rule = EnsureDay(c.Data, Date(date)); if (unavailable != null) rule.Unavailable = unavailable.Value; if (maxTasks != null) rule.MaxTasks = maxTasks.Value;
        if (startHour != null) rule.StartHour = startHour.Value; if (endHour != null) rule.EndHour = endHour.Value; if (maxHours != null) rule.MaxHours = maxHours.Value;
        if (allowedProjects != null) { foreach (var id in allowedProjects) Find(c.Data.Projects, id); rule.AllowedProjects = allowedProjects.Distinct().ToList(); }
        if (!Scheduler.ValidRule(rule)) throw new ArgumentException("Invalid day rule: start < end, hours in 0–24, nonnegative workload and task count required.");
        return new { rule };
    });

    [Command(destructive: true), Description("Auto-assign a week (yyyy-MM-dd). Optional counts maps task GUIDs to 0–7 placements each (unspecified tasks get 0); replaces future placements. Exceeding weekly count or hours is rejected. Without counts schedules once per open task, retaining pins. Honors constraints and current local time.")]
    public JsonElement planner_schedule(string week, Dictionary<Guid, int>? counts = null) => Execute(true, c =>
    {
        var start = Date(week); for (int i = 0; i < 7; i++) EnsureDay(c.Data, start.AddDays(i)); var issues = c.Scheduler.Schedule(c.Data, start, DateTime.Now, counts); return Plan(c.Data, start, issues);
    });

    [Command, Description("Append a text block. Supports bold, italic and heading syntax. Use attachments_add to copy a local file.")]
    public JsonElement content_add(Guid id, string text) => Execute(true, c =>
    {
        var entity = Entity(c.Data, id);
        var block = new Core.ContentBlock { Kind = BlockKind.Text, Text = text }; entity.Content.Add(block); return new { block };
    });

    [Command(destructive: true), Description("Remove a content block from an entity. Local evidence files remain on disk for recovery.")]
    public JsonElement content_remove(Guid id, Guid block) => Execute(true, c => { var e = Entity(c.Data, id); if (e.Content.RemoveAll(b => b.Id == block) == 0) throw new ArgumentException("Content block not found."); return new { removed = block }; });

    [Command, Description("Copy a local file into workspace attachment storage and attach it to an entity.")]
    public JsonElement attachments_add(Guid id, string path) => Execute(true, c =>
    {
        var entity = Entity(c.Data, id); var block = new Core.ContentBlock { Kind = BlockKind.Attachment, Text = Path.GetFileName(path), FileName = c.Store.AddAttachment(path) }; entity.Content.Add(block); return new { block };
    });

    [Command, Description("Save a copy of an entity's attached file to an explicit local path. Refuses to overwrite an existing file.")]
    public JsonElement attachments_save(Guid id, Guid block, string path) => Execute(true, c =>
    {
        var b = Entity(c.Data, id).Content.SingleOrDefault(b => b.Id == block) ?? throw new ArgumentException("Content block not found.");
        if (b.Kind != BlockKind.Attachment) throw new ArgumentException("This block is not an attachment.");
        File.Copy(c.Store.AttachmentPath(b.FileName), path, false); return new { path = Path.GetFullPath(path) };
    });

    [Command, Description("Export the workspace and referenced attachments to a local ZIP. Refuses to overwrite an existing file.")]
    public JsonElement backup_export(string path) => Execute(true, c => { if (File.Exists(path)) throw new IOException("Destination already exists."); c.Store.Export(c.Data, path); return new { path = Path.GetFullPath(path) }; });

    [Command(destructive: true), Description("Replace the workspace from a local ZIP. Requires confirm=true and creates a recovery ZIP of the existing workspace before restoring.")]
    public JsonElement backup_restore(string path, bool confirm = false) => Execute(true, c =>
    {
        if (!confirm) throw new ArgumentException("Restore requires confirm=true (--confirm on the CLI).");
        var recovery = Path.Combine(c.Store.Root, "before-restore-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".zip");
        c.Store.Export(c.Data, recovery); c.Data = c.Store.Import(path); return new { restored = true, recovery };
    });
}
