namespace ResearchTracker.Core;

public readonly record struct GraphPoint(double X, double Y);
public static class GraphGeometry
{
    public const double NodeWidth = 240, NodeHeight = 88;
    public static (GraphPoint Source, GraphPoint Target) Endpoints(Workspace data, GraphEdge edge)
    {
        var source = data.Nodes.FirstOrDefault(n => n.Id == edge.SourceId);
        var target = data.Nodes.FirstOrDefault(n => n.Id == edge.TargetId);
        var a = source != null ? new GraphPoint(source.X + NodeWidth, source.Y + NodeHeight / 2)
            : new GraphPoint(edge.SourceX ?? (target?.X - 220 ?? 70), edge.SourceY ?? (target?.Y - 58 ?? 70));
        var b = target != null ? new GraphPoint(target.X, target.Y + NodeHeight / 2)
            : new GraphPoint(edge.TargetX ?? a.X + 220, edge.TargetY ?? a.Y + 110);
        return (a, b);
    }
    public static void InitializeFreeEnds(Workspace data, GraphEdge edge)
    {
        var (a, b) = Endpoints(data, edge);
        if (edge.SourceId == null) { edge.SourceX ??= a.X; edge.SourceY ??= a.Y; }
        if (edge.TargetId == null) { edge.TargetX ??= b.X; edge.TargetY ??= b.Y; }
    }
}
