using System.Text.Json;

namespace ResearchTracker.Core;

/// <summary>Session-local atomic snapshots preserve graph/task relationships as a single edit.</summary>
public sealed class WorkspaceHistory(Workspace workspace, int capacity = 100)
{
    private string current = JsonSerializer.Serialize(workspace);
    private readonly List<string> undo = [];
    private readonly Stack<string> redo = new();
    public void AdoptRestored(Workspace data) => current = JsonSerializer.Serialize(data);
    public void Record(Workspace data)
    {
        var next = JsonSerializer.Serialize(data); if (next == current) return;
        undo.Add(current); if (undo.Count > capacity) undo.RemoveAt(0); current = next; redo.Clear();
    }
    public Workspace? Undo(Workspace data)
    {
        Record(data); if (undo.Count == 0) return null;
        redo.Push(current); current = undo[^1]; undo.RemoveAt(undo.Count - 1); return JsonSerializer.Deserialize<Workspace>(current)!;
    }
    public Workspace? Redo(Workspace data)
    {
        Record(data); if (redo.Count == 0) return null;
        undo.Add(current); current = redo.Pop(); return JsonSerializer.Deserialize<Workspace>(current)!;
    }
}
