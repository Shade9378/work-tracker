using System.Text.RegularExpressions;

namespace ResearchTracker.Core;

public static class GraphColors
{
    public static readonly string[] ProjectPalette = ["#6179AD", "#4F8F81", "#B57A54", "#8972AC", "#A75F76", "#A58A55", "#637F94", "#787B85"];
    public static string Project(Project project) => project.ColorHex ?? ProjectPalette[project.Id.ToByteArray()[0] % ProjectPalette.Length];
    public static string NextProject(IEnumerable<Project> projects) => ProjectPalette.OrderBy(color => projects.Count(p => Project(p) == color)).First();
    public static bool IsValid(string? color) => color == null || Regex.IsMatch(color, "^#[0-9a-fA-F]{6}$");
    public static string Node(GraphNode node) => node.ColorHex ?? node.Kind switch
    {
        NodeKind.Problem => "#B57A54", NodeKind.Finding => "#4F8F81", NodeKind.Outcome => "#8972AC", _ => "#6179AD"
    };
    public static string Action(GraphEdge edge) => edge.ColorHex ?? edge.Status switch
    {
        WorkStatus.Done => "#65855E", WorkStatus.Cancelled => "#787B85", _ => edge.TargetId == null ? "#A58A55" : "#637F94"
    };
}
