using System.Windows;
using System.Windows.Media;
using IdeaLite.Core;

namespace IdeaLite;

public sealed record GraphEdge(int From, int To, int Color);
public sealed record LogRow(GitRevision Revision, int Lane, int Color, bool Incoming,
    IReadOnlyList<GraphEdge> Through, IReadOnlyList<GraphEdge> Outgoing, double GraphWidth)
{
    private sealed record LaneTip(string Hash, int Color);

    public static IReadOnlyList<LogRow> Build(IReadOnlyList<GitRevision> revisions, bool filtered)
    {
        var rows = new List<LogRow>();
        var lanes = new List<LaneTip>();
        int nextColor = 0, width = 1;
        foreach (var revision in revisions)
        {
            if (filtered) lanes.Clear(); // A text filter can hide parents. Don't draw false ancestry.
            var lane = lanes.FindIndex(t => t.Hash == revision.Hash);
            bool incoming = lane >= 0;
            if (!incoming)
            {
                lane = lanes.Count;
                lanes.Add(new(revision.Hash, nextColor++));
            }
            var color = lanes[lane].Color;
            var before = lanes.ToArray();
            lanes.RemoveAt(lane);
            if (!filtered)
            {
                int insertion = lane;
                foreach (var parent in revision.Parents)
                {
                    if (lanes.Any(t => t.Hash == parent)) continue;
                    int parentColor = parent == revision.Parents[0] ? color : nextColor++;
                    lanes.Insert(Math.Min(insertion++, lanes.Count), new(parent, parentColor));
                }
            }
            var through = new List<GraphEdge>();
            for (int i = 0; i < before.Length; i++)
            {
                if (i != lane)
                    through.Add(new(i, lanes.FindIndex(t => t.Hash == before[i].Hash), before[i].Color));
            }
            var outgoing = new List<GraphEdge>();
            if (!filtered)
                foreach (var parent in revision.Parents)
                {
                    var target = lanes.FindIndex(t => t.Hash == parent);
                    outgoing.Add(new(lane, target, lanes[target].Color));
                }
            width = Math.Max(width, Math.Max(before.Length, lanes.Count));
            rows.Add(new(revision, lane, color, incoming, through, outgoing, 0));
        }
        return rows.Select(row => row with { GraphWidth = Math.Clamp(width * 12 + 8, 24, 180) }).ToArray();
    }
}

public sealed class CommitGraph : FrameworkElement
{
    private static readonly Brush[] Colors =
    [
        Brushes.MediumSeaGreen, Brushes.CornflowerBlue, Brushes.MediumPurple,
        Brushes.SandyBrown, Brushes.LightSeaGreen, Brushes.PaleVioletRed
    ];
    public CommitGraph()
    {
        ClipToBounds = true;
        DataContextChanged += (_, _) => InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        if (DataContext is not LogRow row) return;
        double h = ActualHeight, mid = h / 2;
        static double X(int lane) => 10 + 12 * lane;
        static Pen Stroke(int color) => new(Colors[color % Colors.Length], 1.7);
        foreach (var edge in row.Through)
            dc.DrawLine(Stroke(edge.Color), new(X(edge.From), 0), new(X(edge.To), h));
        if (row.Incoming) dc.DrawLine(Stroke(row.Color), new(X(row.Lane), 0), new(X(row.Lane), mid));
        foreach (var edge in row.Outgoing)
            dc.DrawLine(Stroke(edge.Color), new(X(row.Lane), mid), new(X(edge.To), h));
        dc.DrawEllipse(Colors[row.Color % Colors.Length], null, new(X(row.Lane), mid), 3.7, 3.7);
    }
}
