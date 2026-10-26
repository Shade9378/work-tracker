using CommunityToolkit.Mvvm.ComponentModel;

namespace ResearchTracker.Core;

public enum NodeKind { State, Problem, Finding, Outcome }
public enum WorkStatus { Todo, InProgress, Done, Cancelled }
public enum Priority { Low, Normal, High, Urgent }
public enum BlockKind { Text = 0, Attachment = 4 }
public enum EdgeEnd { Source, Target }

public abstract partial class Entity : ObservableObject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [ObservableProperty] private string title = "Untitled";
    [ObservableProperty] private string summary = "";
    public List<ContentBlock> Content { get; set; } = [];
    public override string ToString() => Title;
}
public partial class Project : Entity
{
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DisplayColor))] private string? colorHex;
    [System.Text.Json.Serialization.JsonIgnore] public string DisplayColor => GraphColors.Project(this);
}
public partial class GraphNode : Entity
{
    [ObservableProperty] private string? colorHex;
    [ObservableProperty] private Guid projectId;
    [ObservableProperty] private NodeKind kind;
    [ObservableProperty] private double x;
    [ObservableProperty] private double y;
}
public partial class GraphEdge : Entity
{
    [ObservableProperty] private string? colorHex;
    [ObservableProperty] private double labelOffsetX;
    [ObservableProperty] private double labelOffsetY;
    [ObservableProperty] private Guid projectId;
    [ObservableProperty] private Guid? sourceId;
    [ObservableProperty] private Guid? targetId;
    [ObservableProperty] private double? sourceX;
    [ObservableProperty] private double? sourceY;
    [ObservableProperty] private double? targetX;
    [ObservableProperty] private double? targetY;
    [ObservableProperty] private WorkStatus status;
}
public partial class WorkTask : Entity
{
    [ObservableProperty] private Guid? projectId;
    public Guid? EdgeId { get; set; }
    [ObservableProperty] private Priority priority = Priority.Normal;
    [ObservableProperty] private WorkStatus status;
    [ObservableProperty] private DateTimeOffset? deadline;
    [ObservableProperty] private double effortHours = 1;
    public List<Guid> Dependencies { get; set; } = [];
    [ObservableProperty] private DateTime? scheduledStart;
    [ObservableProperty] private DateTimeOffset? completedAt;
    [ObservableProperty] private bool manual;
    public List<TaskOccurrence> AdditionalSchedules { get; set; } = [];
    public DateTime? ScheduledEnd => ScheduledStart?.AddHours(EffortHours);
}
public sealed record TaskOccurrence(DateTime Start, bool Manual = false);
public partial class DayRule : ObservableObject
{
    public DateTime Date { get; set; }
    [ObservableProperty] private bool unavailable;
    [ObservableProperty] private int maxTasks = 3;
    [ObservableProperty] private double startHour = 9;
    [ObservableProperty] private double endHour = 17;
    [ObservableProperty] private double maxHours = 6;
    public List<Guid> AllowedProjects { get; set; } = [];
    public string Label => Date.ToString("ddd, MMM d");
}
public class ContentBlock
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public BlockKind Kind { get; set; }
    public string Text { get; set; } = "";
    public string FileName { get; set; } = "";
}
public class Workspace
{
    public int Version { get; set; } = 4;
    public List<Project> Projects { get; set; } = [];
    public List<GraphNode> Nodes { get; set; } = [];
    public List<GraphEdge> Edges { get; set; } = [];
    public List<WorkTask> Tasks { get; set; } = [];
    public List<DayRule> Days { get; set; } = [];
}
