namespace ResearchTracker.Core;

public static class SampleData
{
    public static Workspace Create()
    {
        var w = new Workspace();
        var p = new Project { Title = "Adaptive materials", Summary = "Investigating a more resilient composite.", ColorHex = GraphColors.ProjectPalette[0] };
        var p2 = new Project { Title = "Literature review", Summary = "Map the evidence and open questions.", ColorHex = GraphColors.ProjectPalette[1] }; w.Projects.AddRange([p, p2]);
        var n1 = new GraphNode { ProjectId = p.Id, Title = "Baseline composite", Summary = "Reference formulation • batch A", X = 70, Y = 190 };
        var n2 = new GraphNode { ProjectId = p.Id, Title = "Unexpected brittleness", Summary = "Failure below target strain", Kind = NodeKind.Problem, X = 400, Y = 90 };
        var n3 = new GraphNode { ProjectId = p.Id, Title = "Thermal stability improves", Summary = "+18% at elevated temperatures", Kind = NodeKind.Finding, X = 400, Y = 350 };
        var n4 = new GraphNode { ProjectId = p.Id, Title = "Candidate formulation", Summary = "Combine thermal and mechanical evidence", Kind = NodeKind.Outcome, X = 750, Y = 240 };
        n3.Content.Add(new ContentBlock { Kind = BlockKind.Text, Text = "# Thermal screening\n**Finding:** the additive improves stability.\n*Next:* validate with three independent batches." });
        n3.Content.Add(new ContentBlock { Kind = BlockKind.Text, Text = "# Results\nControl: 62\nCandidate: 73\nReplicate: 71" });
        n3.Content.Add(new ContentBlock { Kind = BlockKind.Text, Text = "# Batch results\nBatch A at 120 C: 62% retention\nBatch B at 120 C: 73% retention" });
        w.Nodes.AddRange([n1, n2, n3, n4]); var graph = new GraphService(w);
        graph.Connect(n1, n2, "Tensile test"); graph.Connect(n1, n3, "Thermal screening"); graph.Connect(n3, n4, "Compare evidence"); graph.Connect(n2, n4, "Revise formulation");
        var edge = graph.Connect(n2, null, "Run microscopy"); edge.Summary = "Look for interfacial defects in batch A."; graph.Synchronize();
        var action = w.Tasks.Single(); action.Priority = Priority.High; action.EffortHours = 2; action.Deadline = new DateTimeOffset(DateTime.Today.AddDays(4).AddHours(17));
        w.Tasks.Add(new WorkTask { ProjectId = p2.Id, Title = "Review interface literature", Summary = "Summarize three papers and note competing explanations.", EffortHours = 1.5, Priority = Priority.Normal });
        w.Tasks.Add(new WorkTask { ProjectId = p.Id, Title = "Draft experiment protocol", EffortHours = 1, Dependencies = [action.Id] });
        return w;
    }
}
