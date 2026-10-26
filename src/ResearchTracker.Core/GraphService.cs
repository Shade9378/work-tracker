namespace ResearchTracker.Core;

/// <summary>All graph/task invariants live here. A linked task is retained as history after resolution.</summary>
public class GraphService(Workspace workspace)
{
    public Workspace Workspace { get; } = workspace;
    public GraphEdge Connect(GraphNode source, GraphNode? target, string title)
    {
        if (target != null && target.ProjectId != source.ProjectId) throw new InvalidOperationException("Connections must stay within one project.");
        var edge = new GraphEdge { ProjectId = source.ProjectId, SourceId = source.Id, TargetId = target?.Id, Title = title };
        Workspace.Edges.Add(edge);
        GraphGeometry.InitializeFreeEnds(Workspace, edge);
        Synchronize();
        return edge;
    }
    public void MoveEndpoint(GraphEdge edge, EdgeEnd end, GraphNode? node, double x, double y)
    {
        if (!Workspace.Edges.Contains(edge)) throw new InvalidOperationException("Action no longer exists.");
        if (!double.IsFinite(x) || !double.IsFinite(y)) throw new ArgumentException("Endpoint coordinates must be finite.");
        if (node != null && (!Workspace.Nodes.Contains(node) || node.ProjectId != edge.ProjectId)) throw new InvalidOperationException("Attach to a state in the same project.");
        GraphGeometry.InitializeFreeEnds(Workspace, edge);
        if (end == EdgeEnd.Source) { edge.SourceX = x; edge.SourceY = y; edge.SourceId = node?.Id; }
        else
        {
            var wasAttached = edge.TargetId != null;
            edge.TargetX = x; edge.TargetY = y; edge.TargetId = node?.Id;
            if (node != null) edge.Status = WorkStatus.Done;
            else if (wasAttached) edge.Status = WorkStatus.Todo;
        }
        Synchronize();
    }
    public void Synchronize()
    {
        foreach (var task in Workspace.Tasks.Where(t => t.Status != WorkStatus.Done)) task.CompletedAt = null;
        foreach (var edge in Workspace.Edges)
        {
            GraphGeometry.InitializeFreeEnds(Workspace, edge);
            if (edge.TargetId != null) edge.Status = WorkStatus.Done;
            var task = Workspace.Tasks.SingleOrDefault(t => t.EdgeId == edge.Id);
            if (task == null && edge.TargetId == null)
            {
                task = new WorkTask { EdgeId = edge.Id };
                Workspace.Tasks.Add(task);
            }
            if (task == null) continue;
            task.ProjectId = edge.ProjectId;
            task.Title = edge.Title;
            task.Summary = edge.Summary;
            var nextStatus = edge.TargetId != null ? WorkStatus.Done : edge.Status;
            if (nextStatus == WorkStatus.Done && task.Status != WorkStatus.Done) task.CompletedAt = DateTimeOffset.Now;
            task.Status = nextStatus;
            if (task.Status != WorkStatus.Done) task.CompletedAt = null;
            if (task.Status is WorkStatus.Done or WorkStatus.Cancelled) TaskSchedule.Remove(task);
        }
    }
    public void UpdateTask(WorkTask task)
    {
        if (task.Status != WorkStatus.Done) task.CompletedAt = null;
        if (task.Status == WorkStatus.Cancelled) TaskSchedule.Remove(task);
        if (task.EdgeId is not { } id) return;
        var edge = Workspace.Edges.Single(e => e.Id == id);
        edge.Title = task.Title; edge.Summary = task.Summary; edge.Status = task.Status;
        // A resolved edge cannot have an open action. Reopening explicitly detaches its outcome.
        if (task.Status is WorkStatus.Todo or WorkStatus.InProgress) edge.TargetId = null;
        Synchronize();
    }
    public GraphNode Complete(WorkTask task, string outcomeTitle)
    {
        if (task.Dependencies.Any(id => Workspace.Tasks.All(t => t.Id != id || t.Status != WorkStatus.Done)))
            throw new InvalidOperationException("Complete this task's dependencies first.");
        GraphNode? node = null;
        if (task.EdgeId is { } edgeId)
        {
            var edge = Workspace.Edges.Single(e => e.Id == edgeId);
            node = edge.TargetId is { } target ? Workspace.Nodes.Single(n => n.Id == target) : null;
            if (node == null)
            {
                if (string.IsNullOrWhiteSpace(outcomeTitle)) throw new InvalidOperationException("Name the resulting state before completing this action.");
                var source = GraphGeometry.Endpoints(Workspace, edge).Source;
                node = new GraphNode { Title = outcomeTitle.Trim(), Kind = NodeKind.Outcome, ProjectId = edge.ProjectId, X = source.X + 70, Y = source.Y + 88 };
                Workspace.Nodes.Add(node); edge.TargetId = node.Id;
            }
            edge.Status = WorkStatus.Done;
        }
        task.CompletedAt ??= DateTimeOffset.Now; task.Status = WorkStatus.Done; TaskSchedule.Remove(task); Synchronize();
        return node!;
    }
    public void DeleteProject(Project project)
    {
        var taskIds = Workspace.Tasks.Where(t => t.ProjectId == project.Id).Select(t => t.Id).ToHashSet();
        Workspace.Tasks.RemoveAll(t => taskIds.Contains(t.Id));
        foreach (var task in Workspace.Tasks) task.Dependencies.RemoveAll(taskIds.Contains);
        Workspace.Edges.RemoveAll(e => e.ProjectId == project.Id);
        Workspace.Nodes.RemoveAll(n => n.ProjectId == project.Id);
        foreach (var day in Workspace.Days) day.AllowedProjects.Remove(project.Id);
        Workspace.Projects.Remove(project);
    }
    public void DeleteEdge(GraphEdge edge)
    {
        var ids = Workspace.Tasks.Where(t => t.EdgeId == edge.Id).Select(t => t.Id).ToHashSet();
        Workspace.Tasks.RemoveAll(t => ids.Contains(t.Id));
        foreach (var task in Workspace.Tasks) task.Dependencies.RemoveAll(ids.Contains);
        Workspace.Edges.Remove(edge);
    }
    public void DeleteNode(GraphNode node)
    {
        foreach (var edge in Workspace.Edges.Where(e => e.SourceId == node.Id).ToList()) DeleteEdge(edge);
        foreach (var edge in Workspace.Edges.Where(e => e.TargetId == node.Id)) { edge.TargetX = node.X; edge.TargetY = node.Y + GraphGeometry.NodeHeight / 2; edge.TargetId = null; edge.Status = WorkStatus.Todo; }
        Workspace.Nodes.Remove(node); Synchronize();
    }
    public void DeleteTask(WorkTask task)
    {
        if (task.EdgeId is { } id) { DeleteEdge(Workspace.Edges.Single(e => e.Id == id)); return; }
        Workspace.Tasks.Remove(task);
        foreach (var other in Workspace.Tasks) other.Dependencies.Remove(task.Id);
    }
    public void AddDependency(WorkTask task, WorkTask dependency)
    {
        bool Reaches(Guid from, HashSet<Guid> seen)
        {
            if (from == task.Id) return true;
            if (!seen.Add(from)) return false;
            return Workspace.Tasks.FirstOrDefault(t => t.Id == from)?.Dependencies.Any(id => Reaches(id, seen)) == true;
        }
        if (Reaches(dependency.Id, [])) throw new InvalidOperationException("This dependency would create a cycle.");
        if (!task.Dependencies.Contains(dependency.Id)) task.Dependencies.Add(dependency.Id);
    }
}
