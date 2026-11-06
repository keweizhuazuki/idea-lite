using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;

namespace IdeaLite;

// Original line icons share one inexpensive drawing surface and follow the control's foreground.
public sealed class IdeIcon : Control
{
    private static readonly Dictionary<string, Geometry> Shapes = CreateShapes();
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(string),
        typeof(IdeIcon), new FrameworkPropertyMetadata("File", FrameworkPropertyMetadataOptions.AffectsRender));
    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }

    public IdeIcon() { Width = 17; Height = 17; Focusable = false; IsHitTestVisible = false; }
    protected override void OnRender(DrawingContext context)
    {
        if (!Shapes.TryGetValue(Kind, out var shape)) return;
        var pen = new Pen(Foreground, 1.55)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        context.PushTransform(new ScaleTransform(RenderSize.Width / 20, RenderSize.Height / 20));
        context.DrawGeometry(null, pen, shape);
        context.Pop();
    }

    private static Dictionary<string, Geometry> CreateShapes()
    {
        var paths = new Dictionary<string, string>
        {
            ["Menu"] = "M3,5H17 M3,10H17 M3,15H17",
            ["Folder"] = "M2,5Q2,4 3,4H8L10,6H17Q18,6 18,7V16H2Z",
            ["SourceRoot"] = "M2,5Q2,4 3,4H8L10,6H17Q18,6 18,7V16H2Z M6,10L4,12L6,14 M13,10L15,12L13,14",
            ["Module"] = "M2,5Q2,4 3,4H8L10,6H17Q18,6 18,7V11 M2,5V16H10 M12,12H19V19H12Z",
            ["Project"] = "M2,5Q2,4 3,4H8L10,6H17Q18,6 18,7V16H2Z",
            ["Commit"] = "M1,10H6 M14,10H19 M14,10A4,4 0 1 1 6,10A4,4 0 1 1 14,10",
            ["Git"] = "M5,17V6 M5,14C12,14 15,12 15,8 M7.5,4A2.5,2.5 0 1 1 2.5,4A2.5,2.5 0 1 1 7.5,4 M17.5,6A2.5,2.5 0 1 1 12.5,6A2.5,2.5 0 1 1 17.5,6",
            ["Structure"] = "M2,2H8V8H2Z M12,5H18V11H12Z M7,13H13V19H7Z",
            ["Services"] = "M2,3H8V9H2Z M2,12H8V18H2Z M12,9L18,13L12,17Z",
            ["Terminal"] = "M2,3H18V17H2Z M5,7L8,10L5,13 M10,13H14",
            ["Maven"] = "M1,15L5,6L4,13L10,6L9,13L15,6L14,14Q16,16 19,11",
            ["Run"] = "M5,3L17,10L5,17Z",
            ["Stop"] = "M5,5H15V15H5Z",
            ["Sparkles"] = "M8,3L10,8L15,10L10,12L8,17L6,12L1,10L6,8Z M16,1V5 M14,3H18 M17,14V18 M15,16H19",
            ["Build"] = "M3,5L7,1L13,7L9,11Z M10,10L17,17L15,19L8,12 M3,5L1,7L4,10L6,8",
            ["Delete"] = "M4,5H16 M8,3H12 M6,5L7,17H13L14,5 M9,8V14 M11,8V14",
            ["Search"] = "M14,8A6,6 0 1 1 2,8A6,6 0 1 1 14,8 M12.5,12.5L18,18",
            ["Settings"] = "M8,2H12L13,5L15,5L17,6L18,9L16,11L16,14L14,16L11,16L10,18L7,17L6,15L3,14L2,11L4,9L4,6L6,4L8,4Z M12.5,10A2.5,2.5 0 1 1 7.5,10A2.5,2.5 0 1 1 12.5,10",
            ["Down"] = "M6,8L10,12L14,8",
            ["Next"] = "M5,8L10,13L15,8",
            ["Previous"] = "M5,12L10,7L15,12",
            ["Plus"] = "M3,10H17 M10,3V17",
            ["Minus"] = "M4,10H16",
            ["Close"] = "M5,5L15,15 M15,5L5,15",
            ["Maximize"] = "M4,4H16V16H4Z",
            ["Restore"] = "M4,7H13V16H4Z M7,7V4H16V13H13",
            ["Refresh"] = "M16,7A6.5,6.5 0 0 0 4,6 M16,3V7H12 M4,13A6.5,6.5 0 0 0 16,14 M4,17V13H8",
            ["Target"] = "M16,10A6,6 0 1 1 4,10A6,6 0 1 1 16,10 M10,1V6 M10,14V19 M1,10H6 M14,10H19",
            ["Collapse"] = "M5,2L10,7L15,2 M5,18L10,13L15,18",
            ["Diff"] = "M2,3H8V17H2Z M12,3H18V17H12Z M5,7H6 M14,11H16",
            ["More"] = "M4,10H4.5 M9.7,10H10.2 M15.5,10H16",
            ["File"] = "M4,2H12L16,6V18H4Z M12,2V7H16 M7,10H13 M7,13H12",
            ["Class"] = "M18,10A8,8 0 1 1 2,10A8,8 0 1 1 18,10 M12,7C6,4 6,16 12,13",
            ["Package"] = "M3,4H17V17H3Z M7,8H13V13H7Z",
            ["Xml"] = "M6,5L2,10L6,15 M14,5L18,10L14,15 M11,4L9,16",
            ["Push"] = "M3,16L15,4 M7,4H15V12",
            ["Pull"] = "M17,4L5,16 M5,8V16H13",
            ["Back"] = "M8,5L3,10L8,15 M3,10H17",
            ["Merge"] = "M4,3V6L10,12V18 M16,3V6L10,12",
            ["Check"] = "M3,10L8,15L17,5",
            ["Log"] = "M17,10A7,7 0 1 1 3,10A7,7 0 1 1 17,10 M10,5V10L13,12",
            ["Tag"] = "M2,9L9,2H16L18,4V11L11,18Z M13,6H13.5"
        };
        return paths.ToDictionary(p => p.Key, p =>
        {
            var geometry = Geometry.Parse(p.Value);
            geometry.Freeze();
            return geometry;
        });
    }
}
