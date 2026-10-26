using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ResearchTracker.Core;

namespace ResearchTracker.Persistence;

public sealed class WorkspaceStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public string Root { get; }
    public string Attachments => Path.Combine(Root, "attachments");
    private string Db => Path.Combine(Root, "research.db");
    public WorkspaceStore(string root)
    {
        Root = Path.GetFullPath(root); Directory.CreateDirectory(Attachments);
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS workspace (id INTEGER PRIMARY KEY CHECK(id=1), version INTEGER NOT NULL, document TEXT NOT NULL, saved_utc TEXT NOT NULL);";
        cmd.ExecuteNonQuery();
    }
    private SqliteConnection Open() { var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Db }.ToString()); db.Open(); return db; }
    public Workspace? Load()
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT document FROM workspace WHERE id=1";
        return cmd.ExecuteScalar() is string json ? Parse(json) : null;
    }
    public void Save(Workspace workspace)
    {
        Validate(workspace);
        var json = JsonSerializer.Serialize(workspace, Json);
        using var db = Open();
        using (var previous = db.CreateCommand())
        {
            previous.CommandText = "SELECT document FROM workspace WHERE id=1";
            if (previous.ExecuteScalar() is string original)
            {
                using var document = JsonDocument.Parse(original);
                if (document.RootElement.TryGetProperty("Version", out var version) && version.GetInt32() < workspace.Version)
                {
                    var backupPath = Path.Combine(Root, "before-schema-v" + workspace.Version + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".db");
                    using var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backupPath }.ToString()); backup.Open(); db.BackupDatabase(backup);
                }
            }
        }
        using var tx = db.BeginTransaction(); using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO workspace VALUES(1,$version,$json,$time) ON CONFLICT(id) DO UPDATE SET version=$version,document=$json,saved_utc=$time";
        cmd.Parameters.AddWithValue("$version", workspace.Version);
        cmd.Parameters.AddWithValue("$json", json); cmd.Parameters.AddWithValue("$time", DateTime.UtcNow.ToString("O")); cmd.ExecuteNonQuery(); tx.Commit();
    }
    private static Workspace Parse(string json)
    {
        var data = JsonSerializer.Deserialize<Workspace>(json) ?? throw new InvalidDataException("Empty backup.");
        if (data.Version == 1) { data.Version = 2; foreach (var edge in data.Edges) GraphGeometry.InitializeFreeEnds(data, edge); }
        if (data.Version == 2)
        {
            foreach (var block in data.Projects.Cast<Entity>().Concat(data.Nodes).Concat(data.Edges).Concat(data.Tasks).SelectMany(e => e.Content))
                block.Kind = (int)block.Kind switch { 1 => BlockKind.Attachment, 2 or 3 => BlockKind.Text, _ => block.Kind };
            data.Version = 3;
        }
        if (data.Version == 3) { foreach (var day in data.Days.Where(d => d.MaxTasks == 5)) day.MaxTasks = 3; data.Version = 4; }
        Validate(data); return data;
    }
    public static void Validate(Workspace w)
    {
        if (w.Version != 4) throw new InvalidDataException("Unsupported workspace version.");
        var all = w.Projects.Cast<Entity>().Concat(w.Nodes).Concat(w.Edges).Concat(w.Tasks).ToList();
        if (all.Any(e => e.Title == null || e.Summary == null || e.Content == null || e.Content.Any(b => b.Text == null || b.FileName == null || !Enum.IsDefined(b.Kind)))) throw new InvalidDataException("Invalid entity text or content.");
        if (w.Projects.Any(p => !GraphColors.IsValid(p.ColorHex)) || w.Nodes.Any(n => !GraphColors.IsValid(n.ColorHex)) || w.Edges.Any(e => !GraphColors.IsValid(e.ColorHex))) throw new InvalidDataException("Project or graph color must use #RRGGBB or automatic (null).");
        if (w.Nodes.Any(n => !Enum.IsDefined(n.Kind)) || w.Tasks.Any(t => !Enum.IsDefined(t.Priority) || !Enum.IsDefined(t.Status)) || w.Edges.Any(e => !Enum.IsDefined(e.Status))) throw new InvalidDataException("Invalid entity type, priority or status.");
        if (all.Select(e => e.Id).Distinct().Count() != all.Count) throw new InvalidDataException("Duplicate entity IDs.");
        var projects = w.Projects.Select(p => p.Id).ToHashSet();
        if (w.Nodes.Any(n => !projects.Contains(n.ProjectId) || !double.IsFinite(n.X) || !double.IsFinite(n.Y)) || w.Tasks.Any(t => t.ProjectId is { } projectId && !projects.Contains(projectId) || !double.IsFinite(t.EffortHours) || t.EffortHours <= 0 || t.EffortHours > 168)) throw new InvalidDataException("Invalid project, position or effort.");
        foreach (var e in w.Edges)
        {
            if (!double.IsFinite(e.LabelOffsetX) || !double.IsFinite(e.LabelOffsetY)) throw new InvalidDataException("Invalid action label position.");
            if (!projects.Contains(e.ProjectId) || e.SourceId != null && !w.Nodes.Any(n => n.Id == e.SourceId && n.ProjectId == e.ProjectId) || e.TargetId != null && !w.Nodes.Any(n => n.Id == e.TargetId && n.ProjectId == e.ProjectId)) throw new InvalidDataException("Invalid graph connection.");
            if (new[] { e.SourceX, e.SourceY, e.TargetX, e.TargetY }.Any(v => v != null && !double.IsFinite(v.Value)) || e.SourceX.HasValue != e.SourceY.HasValue || e.TargetX.HasValue != e.TargetY.HasValue) throw new InvalidDataException("Invalid free endpoint coordinates.");
        }
        if (w.Tasks.Where(t => t.EdgeId != null).GroupBy(t => t.EdgeId).Any(g => g.Count() > 1) || w.Tasks.Any(t => t.EdgeId != null && w.Edges.All(e => e.Id != t.EdgeId))) throw new InvalidDataException("Invalid action/task link.");
        foreach (var t in w.Tasks)
        {
            if (t.AdditionalSchedules == null || t.AdditionalSchedules.Count > 0 && t.ScheduledStart == null || TaskSchedule.Occurrences(t).GroupBy(s => s.Start.Date).Any(g => g.Count() > 1)) throw new InvalidDataException("Invalid or duplicate task schedule.");
            var seen = new HashSet<Guid>();
            void Visit(Guid id, HashSet<Guid> stack)
            {
                if (!stack.Add(id)) throw new InvalidDataException("Cyclic dependencies.");
                var dep = w.Tasks.FirstOrDefault(x => x.Id == id) ?? throw new InvalidDataException("Missing dependency.");
                if (seen.Add(id)) foreach (var child in dep.Dependencies) Visit(child, stack);
                stack.Remove(id);
            }
            Visit(t.Id, []);
        }
        if (w.Days.Select(d => d.Date.Date).Distinct().Count() != w.Days.Count || w.Days.Any(d => !Scheduler.ValidRule(d) || d.AllowedProjects.Any(id => !projects.Contains(id)))) throw new InvalidDataException("Invalid calendar constraints.");
        foreach (var b in all.SelectMany(e => e.Content).Where(b => b.Kind == BlockKind.Attachment)) SafeFileName(b.FileName);
    }
    private static void SafeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) || name.Contains('\\') || name.Contains('/') || name.Contains(':') || name is "." or "..") throw new InvalidDataException("Unsafe attachment name.");
    }
    public string AddAttachment(string path)
    {
        var name = Guid.NewGuid().ToString("N") + Path.GetExtension(path);
        File.Copy(path, Path.Combine(Attachments, name)); return name;
    }
    public string AttachmentPath(string name) { SafeFileName(name); return Path.Combine(Attachments, name); }
    public void Export(Workspace data, string destination)
    {
        Save(data);
        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("workspace.json"); using (var writer = new StreamWriter(entry.Open())) writer.Write(JsonSerializer.Serialize(data, Json));
                foreach (var name in data.Projects.Cast<Entity>().Concat(data.Nodes).Concat(data.Edges).Concat(data.Tasks).SelectMany(e => e.Content).Where(b => b.Kind == BlockKind.Attachment).Select(b => b.FileName).Distinct())
                    zip.CreateEntryFromFile(AttachmentPath(name), "attachments/" + name);
            }
            File.Move(temp, destination, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public Workspace Import(string source)
    {
        using var zip = ZipFile.OpenRead(source);
        if (zip.Entries.Sum(e => e.Length) > 1024L * 1024 * 1024) throw new InvalidDataException("Backup exceeds 1 GiB.");
        var entry = zip.GetEntry("workspace.json") ?? throw new InvalidDataException("Missing workspace.json.");
        if (entry.Length > 32 * 1024 * 1024) throw new InvalidDataException("Workspace is too large.");
        Workspace data; using (var reader = new StreamReader(entry.Open())) data = Parse(reader.ReadToEnd());
        // Give imported files fresh names: a failed import cannot overwrite any live attachment.
        var blocks = data.Projects.Cast<Entity>().Concat(data.Nodes).Concat(data.Edges).Concat(data.Tasks).SelectMany(e => e.Content).Where(b => b.Kind == BlockKind.Attachment).ToList();
        var copied = new List<string>();
        try
        {
            foreach (var group in blocks.GroupBy(b => b.FileName))
            {
                var attachment = zip.GetEntry("attachments/" + group.Key) ?? throw new InvalidDataException("Missing attachment: " + group.Key);
                var name = Guid.NewGuid().ToString("N") + Path.GetExtension(group.Key); var path = AttachmentPath(name);
                copied.Add(path); attachment.ExtractToFile(path);
                foreach (var block in group) block.FileName = name;
            }
            new GraphService(data).Synchronize(); Save(data); return data;
        }
        catch { foreach (var path in copied) if (File.Exists(path)) File.Delete(path); throw; }
    }
}
