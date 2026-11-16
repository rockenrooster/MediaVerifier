using System.Drawing.Drawing2D;
using System.Globalization;

namespace MediaVerifier.UI.Kit;

/// <summary>The 46 icons of docs/design/icons.md plus the four file-state icons of subplan 08.</summary>
internal enum IconName
{
    Dashboard,
    Issues,
    Library,
    Scans,
    Schedule,
    Tools,
    Settings,
    Search,
    Bell,
    Filter,
    Refresh,
    Play,
    Pause,
    Stop,
    Close,
    Add,
    Check,
    ChevronRight,
    ChevronDown,
    ChevronLeft,
    ChevronUp,
    More,
    Folder,
    FolderAdd,
    Photo,
    Video,
    Music,
    Clock,
    Shield,
    ShieldCheck,
    Undo,
    Quarantine,
    Trash,
    Open,
    Copy,
    Download,
    Report,
    Info,
    Ignore,
    Drive,
    Update,
    Menu,
    Database,
    Engine,
    SortDown,
    SortUp,

    // File-state icons (subplan 08, File-state icons): 1.5 px stroke in Text3.
    StatePending,
    StateSkipped,
    StateMissing,
    StateError,
}

/// <summary>
/// Vector geometry of one icon on the 16 x 16 grid: a stroked path (round caps and joins) and a filled
/// path. Built once and reused for every size; the renderer scales by size / 16.
/// </summary>
internal sealed class IconShape
{
    public IconShape(GraphicsPath stroked, GraphicsPath filled, float[]? dashPattern = null)
    {
        Stroked = stroked;
        Filled = filled;
        DashPattern = dashPattern;
    }

    public GraphicsPath Stroked { get; }

    public GraphicsPath Filled { get; }

    /// <summary>Dash pattern in grid units, for the Pending state icon.</summary>
    public float[]? DashPattern { get; }
}

internal static class IconGeometry
{
    private static readonly IconShape[] Shapes = Build();

    public static int Count => Shapes.Length;

    public static IconShape Get(IconName name) => Shapes[(int)name];

    private static IconShape[] Build()
    {
        IconShape[] shapes = new IconShape[Enum.GetValues<IconName>().Length];
        void Define(IconName name, Action<Builder> build, float[]? dash = null)
        {
            Builder builder = new();
            build(builder);
            shapes[(int)name] = new IconShape(builder.Stroked, builder.Filled, dash);
        }

        Define(IconName.Dashboard, b => b.Rect(2.5f, 2.5f, 4.5f, 5, 1).Rect(9, 2.5f, 4.5f, 3, 1).Rect(9, 7.5f, 4.5f, 6, 1).Rect(2.5f, 9.5f, 4.5f, 4, 1));
        Define(IconName.Issues, b => b.Polygon(8, 2.5f, 14, 13, 2, 13).Line(8, 6.5f, 8, 9.5f).Dot(8, 11.25f, 0.5f));
        Define(IconName.Library, b => b.Line(3.5f, 2.5f, 3.5f, 13.5f).Line(7, 2.5f, 7, 13.5f).Line(10, 3.5f, 13, 13.5f));
        Define(IconName.Scans, b => b.Polyline(1.5f, 8, 4.5f, 8, 6.5f, 3.5f, 9.5f, 12.5f, 11.5f, 8, 14.5f, 8));
        Define(IconName.Schedule, b => b.Rect(2.5f, 3.5f, 11, 10, 1.5f).Line(2.5f, 6.5f, 13.5f, 6.5f).Line(5.5f, 2, 5.5f, 4.5f).Line(10.5f, 2, 10.5f, 4.5f).Dot(5.5f, 9.75f, 0.5f).Dot(8, 9.75f, 0.5f).Dot(10.5f, 9.75f, 0.5f));
        Define(IconName.Tools, b => b.Rect(2.5f, 5, 11, 8.5f, 1.5f).Polyline(6, 5, 6, 3, 10, 3, 10, 5).Line(8, 7.5f, 8, 11).Line(6.25f, 9.25f, 9.75f, 9.25f));
        Define(IconName.Settings, b => b.Line(2.5f, 4.5f, 8, 4.5f).Line(12, 4.5f, 13.5f, 4.5f).Circle(10, 4.5f, 1.75f).Line(2.5f, 11.5f, 4, 11.5f).Line(8, 11.5f, 13.5f, 11.5f).Circle(6, 11.5f, 1.75f));
        Define(IconName.Search, b => b.Circle(7, 7, 4.5f).Line(10.4f, 10.4f, 14, 14));
        Define(IconName.Bell, b => b.Path("M4 11.5 V7.5 C4 5 5.8 3 8 3 C10.2 3 12 5 12 7.5 V11.5").Line(2.75f, 11.5f, 13.25f, 11.5f).Line(6.75f, 13.75f, 9.25f, 13.75f));
        Define(IconName.Filter, b => b.Polygon(2.5f, 3.5f, 13.5f, 3.5f, 9.5f, 8.5f, 9.5f, 12.5f, 6.5f, 13.5f, 6.5f, 8.5f));
        Define(IconName.Refresh, b => b.Path("M13 8 C13 10.76 10.76 13 8 13 C5.24 13 3 10.76 3 8 C3 5.24 5.24 3 8 3 C9.6 3 11 3.75 11.9 4.9").Polyline(12.25f, 2.25f, 12.25f, 5.25f, 9.25f, 5.25f));
        Define(IconName.Play, b => b.Polygon(5, 3.5f, 12.5f, 8, 5, 12.5f));
        Define(IconName.Pause, b => b.Line(5.5f, 3.5f, 5.5f, 12.5f).Line(10.5f, 3.5f, 10.5f, 12.5f));
        Define(IconName.Stop, b => b.Rect(4, 4, 8, 8, 1));
        Define(IconName.Close, b => b.Line(4, 4, 12, 12).Line(12, 4, 4, 12));
        Define(IconName.Add, b => b.Line(8, 3, 8, 13).Line(3, 8, 13, 8));
        Define(IconName.Check, b => b.Polyline(3, 8.5f, 6.5f, 12, 13, 4.5f));
        Define(IconName.ChevronRight, b => b.Polyline(6, 3.5f, 10.5f, 8, 6, 12.5f));
        Define(IconName.ChevronDown, b => b.Polyline(3.5f, 6, 8, 10.5f, 12.5f, 6));
        Define(IconName.ChevronLeft, b => b.Polyline(10, 3.5f, 5.5f, 8, 10, 12.5f));
        Define(IconName.ChevronUp, b => b.Polyline(3.5f, 10, 8, 5.5f, 12.5f, 10));
        Define(IconName.More, b => b.Dot(3.5f, 8, 0.6f).Dot(8, 8, 0.6f).Dot(12.5f, 8, 0.6f));
        Define(IconName.Folder, b => b.Path("M2 4.5 H6 L7.5 6 H14 V12.5 H2 Z"));
        Define(IconName.FolderAdd, b => b.Path("M2 4.5 H6 L7.5 6 H14 V12.5 H2 Z").Line(8, 7.75f, 8, 10.75f).Line(6.5f, 9.25f, 9.5f, 9.25f));
        Define(IconName.Photo, b => b.Rect(2.5f, 3.5f, 11, 9, 1.5f).Circle(5.75f, 6.75f, 1).Polyline(2.5f, 11, 6, 8, 8.5f, 10.25f, 10.5f, 8.5f, 13.5f, 11));
        Define(IconName.Video, b => b.Rect(2, 4, 8.5f, 8, 1.5f).Polyline(10.5f, 6.9f, 14, 4.9f, 14, 11.1f, 10.5f, 9.1f));
        Define(IconName.Music, b => b.Polyline(6, 11.5f, 6, 4, 12.5f, 3, 12.5f, 10.5f).Circle(4.5f, 11.5f, 1.5f).Circle(11, 10.5f, 1.5f));
        Define(IconName.Clock, b => b.Circle(8, 8, 5.5f).Polyline(8, 4.75f, 8, 8, 10.25f, 9.5f));
        Define(IconName.Shield, b => b.Path("M8 2 L13 4 V8 C13 10.9 10.9 13.2 8 14 C5.1 13.2 3 10.9 3 8 V4 Z"));
        Define(IconName.ShieldCheck, b => b.Path("M8 2 L13 4 V8 C13 10.9 10.9 13.2 8 14 C5.1 13.2 3 10.9 3 8 V4 Z").Polyline(5.75f, 8, 7.4f, 9.6f, 10.25f, 6.5f));
        Define(IconName.Undo, b => b.Path("M3.5 6.5 H10 C12 6.5 13.5 8 13.5 10 C13.5 12 12 13.5 10 13.5 H6.5").Polyline(6, 4, 3.5f, 6.5f, 6, 9));
        Define(IconName.Quarantine, b => b.Rect(2.5f, 3, 11, 3, 0.75f).Polyline(3.5f, 6, 3.5f, 12.5f, 12.5f, 12.5f, 12.5f, 6).Line(6.5f, 8.75f, 9.5f, 8.75f));
        Define(IconName.Trash, b => b.Line(3, 4.5f, 13, 4.5f).Polyline(6, 4.5f, 6, 3, 10, 3, 10, 4.5f).Polyline(4.25f, 4.5f, 5, 13.5f, 11, 13.5f, 11.75f, 4.5f).Line(6.75f, 7, 6.75f, 11).Line(9.25f, 7, 9.25f, 11));
        Define(IconName.Open, b => b.Polyline(7, 3.5f, 3.5f, 3.5f, 3.5f, 12.5f, 12.5f, 12.5f, 12.5f, 9).Polyline(9.5f, 3.5f, 12.5f, 3.5f, 12.5f, 6.5f).Line(12.5f, 3.5f, 7.5f, 8.5f));
        Define(IconName.Copy, b => b.Rect(5.5f, 5.5f, 8, 8, 1.5f).Polyline(10.5f, 3.5f, 10.5f, 2.5f, 2.5f, 2.5f, 2.5f, 10.5f, 3.5f, 10.5f));
        Define(IconName.Download, b => b.Line(8, 2.5f, 8, 10.5f).Polyline(4.5f, 7.5f, 8, 11, 11.5f, 7.5f).Line(3, 13.5f, 13, 13.5f));
        Define(IconName.Report, b => b.Path("M4 2.5 H9.5 L12 5 V13.5 H4 Z").Polyline(9.5f, 2.5f, 9.5f, 5, 12, 5).Line(6, 8, 10, 8).Line(6, 10.5f, 10, 10.5f));
        Define(IconName.Info, b => b.Circle(8, 8, 5.5f).Line(8, 7.25f, 8, 11).Dot(8, 5.1f, 0.5f));
        Define(IconName.Ignore, b => b.Circle(8, 8, 5.5f).Line(4.1f, 4.1f, 11.9f, 11.9f));
        Define(IconName.Drive, b => b.Rect(2.5f, 4, 11, 8, 1.5f).Line(2.5f, 9, 13.5f, 9).Dot(11, 10.5f, 0.4f));
        Define(IconName.Update, b => b.Circle(8, 8, 5.5f).Polyline(5.5f, 8, 8, 5.5f, 10.5f, 8).Line(8, 5.5f, 8, 11));
        Define(IconName.Menu, b => b.Line(2.5f, 4.5f, 13.5f, 4.5f).Line(2.5f, 8, 13.5f, 8).Line(2.5f, 11.5f, 13.5f, 11.5f));
        Define(IconName.Database, b => b
            .Path("M3 4.5 C3 3.4 5.2 2.5 8 2.5 C10.8 2.5 13 3.4 13 4.5 V11.5 C13 12.6 10.8 13.5 8 13.5 C5.2 13.5 3 12.6 3 11.5 Z")
            .Path("M3 4.5 C3 5.6 5.2 6.5 8 6.5 C10.8 6.5 13 5.6 13 4.5")
            .Path("M3 8 C3 9.1 5.2 10 8 10 C10.8 10 13 9.1 13 8"));
        Define(IconName.Engine, b => b.Rect(4.5f, 4.5f, 7, 7, 1).Line(6.5f, 2, 6.5f, 4.5f).Line(9.5f, 2, 9.5f, 4.5f).Line(6.5f, 11.5f, 6.5f, 14).Line(9.5f, 11.5f, 9.5f, 14).Line(2, 6.5f, 4.5f, 6.5f).Line(2, 9.5f, 4.5f, 9.5f).Line(11.5f, 6.5f, 14, 6.5f).Line(11.5f, 9.5f, 14, 9.5f));
        Define(IconName.SortDown, b => b.Polyline(4.5f, 6.5f, 8, 10, 11.5f, 6.5f));
        Define(IconName.SortUp, b => b.Polyline(4.5f, 9.5f, 8, 6, 11.5f, 9.5f));

        Define(IconName.StatePending, b => b.Circle(8, 8, 5.75f), dash: [2.2f, 2.3f]);
        Define(IconName.StateSkipped, b => b.Circle(8, 8, 5.75f).Line(5.25f, 8, 10.75f, 8));
        Define(IconName.StateMissing, b => b.Circle(8, 8, 5.75f).Path("M6.4 6.5 C6.4 5.5 7.1 4.9 8 4.9 C8.9 4.9 9.6 5.5 9.6 6.4 C9.6 7.5 8 7.6 8 8.9").Dot(8, 11, 0.5f));
        Define(IconName.StateError, b => b.Rect(2.5f, 2.5f, 11, 11, 2).Line(8, 5.25f, 8, 8.75f).Dot(8, 10.9f, 0.5f));

        foreach (IconName name in Enum.GetValues<IconName>())
        {
            if (shapes[(int)name] is null)
            {
                throw new InvalidOperationException($"Icon {name} has no geometry.");
            }
        }

        return shapes;
    }

    /// <summary>Collects primitives into a stroked and a filled path.</summary>
    internal sealed class Builder
    {
        public GraphicsPath Stroked { get; } = new(FillMode.Winding);

        public GraphicsPath Filled { get; } = new(FillMode.Winding);

        public Builder Rect(float x, float y, float width, float height, float radius)
        {
            Stroked.StartFigure();
            AddRoundedRect(Stroked, new RectangleF(x, y, width, height), radius);
            return this;
        }

        public Builder Line(float x1, float y1, float x2, float y2)
        {
            Stroked.StartFigure();
            Stroked.AddLine(x1, y1, x2, y2);
            return this;
        }

        public Builder Polyline(params float[] coordinates)
        {
            Stroked.StartFigure();
            Stroked.AddLines(ToPoints(coordinates));
            return this;
        }

        public Builder Polygon(params float[] coordinates)
        {
            Stroked.StartFigure();
            Stroked.AddPolygon(ToPoints(coordinates));
            return this;
        }

        public Builder Circle(float cx, float cy, float r)
        {
            Stroked.StartFigure();
            Stroked.AddEllipse(cx - r, cy - r, r * 2, r * 2);
            return this;
        }

        public Builder Dot(float cx, float cy, float r)
        {
            Filled.StartFigure();
            Filled.AddEllipse(cx - r, cy - r, r * 2, r * 2);
            return this;
        }

        public Builder Path(string data)
        {
            AddSvgPath(Stroked, data);
            return this;
        }
    }

    public static void AddRoundedRect(GraphicsPath path, RectangleF rect, float radius)
    {
        if (radius <= 0)
        {
            path.AddRectangle(rect);
            return;
        }

        float d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
        path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
    }

    /// <summary>Parses the absolute SVG commands M, L, H, V, C and Z used in icons.md.</summary>
    public static void AddSvgPath(GraphicsPath path, string data)
    {
        List<string> tokens = Tokenize(data);
        PointF current = default;
        PointF start = default;
        char command = 'M';
        int i = 0;
        bool figureOpen = false;
        path.StartFigure();

        float Next()
        {
            if (i >= tokens.Count || char.IsLetter(tokens[i][0]))
            {
                throw new FormatException($"Missing number in path '{data}'.");
            }

            return float.Parse(tokens[i++], NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        while (i < tokens.Count)
        {
            string token = tokens[i];
            if (char.IsLetter(token[0]))
            {
                command = token[0];
                i++;
                if (command == 'Z')
                {
                    path.CloseFigure();
                    current = start;
                    figureOpen = false;
                    continue;
                }
            }

            switch (command)
            {
                case 'M':
                    if (figureOpen)
                    {
                        path.StartFigure();
                    }

                    current = new PointF(Next(), Next());
                    start = current;
                    figureOpen = true;
                    command = 'L';
                    break;
                case 'L':
                    {
                        PointF next = new(Next(), Next());
                        path.AddLine(current, next);
                        current = next;
                        break;
                    }

                case 'H':
                    {
                        PointF next = new(Next(), current.Y);
                        path.AddLine(current, next);
                        current = next;
                        break;
                    }

                case 'V':
                    {
                        PointF next = new(current.X, Next());
                        path.AddLine(current, next);
                        current = next;
                        break;
                    }

                case 'C':
                    {
                        PointF c1 = new(Next(), Next());
                        PointF c2 = new(Next(), Next());
                        PointF end = new(Next(), Next());
                        path.AddBezier(current, c1, c2, end);
                        current = end;
                        break;
                    }

                default:
                    throw new FormatException($"Unsupported path command '{command}' in '{data}'.");
            }
        }
    }

    /// <summary>Splits path data into single-letter commands and numbers ("M4 11.5" gives M, 4, 11.5).</summary>
    internal static List<string> Tokenize(string data)
    {
        List<string> tokens = [];
        int index = 0;
        while (index < data.Length)
        {
            char c = data[index];
            if (char.IsWhiteSpace(c) || c == ',')
            {
                index++;
            }
            else if (char.IsLetter(c) && c is not ('e' or 'E'))
            {
                tokens.Add(c.ToString());
                index++;
            }
            else
            {
                int begin = index;
                index++;
                while (index < data.Length && (char.IsDigit(data[index]) || data[index] == '.' || data[index] is 'e' or 'E' || (data[index] == '-' && data[index - 1] is 'e' or 'E')))
                {
                    index++;
                }

                tokens.Add(data[begin..index]);
            }
        }

        return tokens;
    }

    private static PointF[] ToPoints(float[] coordinates)
    {
        if (coordinates.Length % 2 != 0)
        {
            throw new ArgumentException("Coordinates come in pairs.", nameof(coordinates));
        }

        PointF[] points = new PointF[coordinates.Length / 2];
        for (int i = 0; i < points.Length; i++)
        {
            points[i] = new PointF(coordinates[i * 2], coordinates[(i * 2) + 1]);
        }

        return points;
    }
}
