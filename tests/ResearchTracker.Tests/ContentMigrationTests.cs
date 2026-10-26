using System.IO.Compression;
using System.Text.Json;
using ResearchTracker.Core;
using ResearchTracker.Persistence;
using Xunit;

namespace ResearchTracker.Tests;

public sealed class ContentMigrationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "WorkTrackerContent-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    private static Workspace Legacy()
    {
        var data = SampleData.Create(); data.Version = 2;
        data.Nodes[0].Content =
        [
            new ContentBlock { Kind = (BlockKind)1, Text = "evidence.png", FileName = "evidence.png" },
            new ContentBlock { Kind = (BlockKind)2, Text = "Batch\tResult\nA\t62%" },
            new ContentBlock { Kind = (BlockKind)3, Text = "Control,62\nCandidate,73" }
        ];
        return data;
    }
    [Fact] public void OlderContentConvertsWithoutLosingFilesOrText()
    {
        var store = new WorkspaceStore(root); var legacy = Legacy(); File.WriteAllText(store.AttachmentPath("evidence.png"), "original file bytes");
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + Path.Combine(root, "research.db")))
        {
            db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "INSERT INTO workspace VALUES(1,2,$json,'test')"; cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(legacy)); cmd.ExecuteNonQuery();
        }
        var data = store.Load()!; Assert.Equal(4, data.Version);
        Assert.Equal(BlockKind.Attachment, data.Nodes[0].Content[0].Kind); Assert.Equal("evidence.png", data.Nodes[0].Content[0].FileName);
        Assert.Equal(BlockKind.Text, data.Nodes[0].Content[1].Kind); Assert.Equal(legacy.Nodes[0].Content[1].Text, data.Nodes[0].Content[1].Text);
        Assert.Equal(BlockKind.Text, data.Nodes[0].Content[2].Kind); Assert.Equal(legacy.Nodes[0].Content[2].Text, data.Nodes[0].Content[2].Text);
        store.Save(data); Assert.Single(Directory.GetFiles(root, "before-schema-v4-*.db"));
        var path = Path.Combine(root, "new-backup.zip"); store.Export(data, path); var restored = store.Import(path);
        Assert.Equal("original file bytes", File.ReadAllText(store.AttachmentPath(restored.Nodes[0].Content[0].FileName)));
    }
    [Fact] public void OlderZipImportsAsTextAndAttachmentsOnly()
    {
        var store = new WorkspaceStore(root); var archive = Path.Combine(root, "old.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("workspace.json").Open())) writer.Write(JsonSerializer.Serialize(Legacy()));
            using (var writer = new StreamWriter(zip.CreateEntry("attachments/evidence.png").Open())) writer.Write("preserved attachment");
        }
        var data = store.Import(archive); Assert.Equal(4, data.Version);
        Assert.All(data.Projects.Cast<Entity>().Concat(data.Nodes).Concat(data.Edges).Concat(data.Tasks).SelectMany(e => e.Content), b => Assert.True(b.Kind is BlockKind.Text or BlockKind.Attachment));
        Assert.Equal("preserved attachment", File.ReadAllText(store.AttachmentPath(data.Nodes[0].Content[0].FileName)));
        Assert.Equal(new[] { BlockKind.Text, BlockKind.Attachment }, Enum.GetValues<BlockKind>());
    }
}
