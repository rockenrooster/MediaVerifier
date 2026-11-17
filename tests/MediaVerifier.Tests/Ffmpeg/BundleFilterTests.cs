using System.Text;
using System.Text.RegularExpressions;
using MediaVerifier.Core.Analysis;
using MediaVerifier.Core.Analysis.Providers;
using MediaVerifier.Tests.TestSupport;

namespace MediaVerifier.Tests.Ffmpeg;

/// <summary>
/// Subplan 17: the bundle is built with a fixed list of filters, and a graph that names any other filter fails at run
/// time ("No such filter"), even though every test passes against a full FFmpeg build. Every filter graph the app
/// passes to FFmpeg is checked here against the build script's <c>--enable-filter</c> list: literal graphs are read
/// from the source, and graphs built by code are built by that code. A new place that passes a graph some other way
/// fails until its builder is added to <see cref="BuiltGraphs"/>.
/// </summary>
public sealed partial class BundleFilterTests
{
    /// <summary>Graphs built by code, keyed by the expression that passes them to FFmpeg.</summary>
    private static readonly Dictionary<string, string[]> BuiltGraphs = new(StringComparer.Ordinal)
    {
        ["ThumbnailFilter"] = [PictureRenderer.ThumbnailFilter],
        ["StripGraph"] = [PictureRenderer.StripGraph(3)],
        ["CompareGraph"] = [PictureRenderer.CompareGraph(1920, 1080, 720)],
        ["Graph"] = [InterlaceProvider.Graph(3)],
        ["FrameSampling.Graph"] =
        [
            FrameSampling.Graph(3, 640, 360, [720, 540], [(1280, 720), (960, 540)], deinterlace: true, new MetricsWindow(0, 0, 320, 180, SpatialOnly: false)),
            FrameSampling.Graph(3, 640, 360, [720], [(1280, 720)], deinterlace: false, new MetricsWindow(0, 0, 320, 180, SpatialOnly: true)),
            FrameSampling.Graph(3, 640, 360, [720], [(1280, 720)], deinterlace: false),
        ],
    };

    [Fact]
    public void Every_filter_the_app_uses_is_built_into_the_bundle()
    {
        HashSet<string> bundled = BundledFilters();
        IEnumerable<(string Where, string Graph)> graphs = Scan().Literals.Concat(
            BuiltGraphs.SelectMany(pair => pair.Value.Select(graph => (Where: pair.Key, Graph: graph))));
        List<string> missing = [];
        foreach ((string where, string graph) in graphs)
        {
            missing.AddRange(FilterNames(graph).Where(name => !bundled.Contains(name)).Select(name => $"{name} ({where}: {graph})"));
        }

        Assert.True(missing.Count == 0, "Filters missing from --enable-filter in deps/ffmpeg/build-ffmpeg.sh:\n" + string.Join('\n', missing.Distinct()));
    }

    [Fact]
    public void Every_graph_passed_to_FFmpeg_is_checked()
    {
        (List<(string Where, string Graph)> literals, List<string> unseen) = Scan();
        Assert.True(unseen.Count == 0, "Graphs this test cannot see; add their builder to BuiltGraphs:\n" + string.Join('\n', unseen));
        Assert.True(literals.Count >= 4, $"The scan found only {literals.Count} literal graphs; it no longer matches the source.");
    }

    [Theory]
    [InlineData("volume=-6dB,adeclip", "volume adeclip")]
    [InlineData("[0:v]trim=end_frame=1,setpts=PTS-STARTPTS,split[a][b];[a]crop='min(iw,384)':'min(ih,216)'[c1];[c1][c2]hstack[out]", "trim setpts split crop hstack")]
    [InlineData("[f0][f1]concat=n=2:v=1:a=0,tile=2x1[out]", "concat tile")]
    public void The_graph_reader_finds_every_filter(string graph, string expected) =>
        Assert.Equal(expected.Split(' '), FilterNames(graph));

    /// <summary>The filter names in a graph: the first word of each filter, after its input labels.</summary>
    internal static List<string> FilterNames(string graph)
    {
        List<string> names = [];
        StringBuilder filter = new();
        bool quoted = false;
        foreach (char c in graph + ";")
        {
            if (c == '\'')
            {
                quoted = !quoted;
            }

            if (!quoted && c is ',' or ';')
            {
                Match name = FilterName().Match(filter.ToString());
                if (name.Success)
                {
                    names.Add(name.Groups["name"].Value);
                }

                filter.Clear();
            }
            else
            {
                filter.Append(c);
            }
        }

        return names;
    }

    private static HashSet<string> BundledFilters()
    {
        Match list = EnableFilter().Match(File.ReadAllText(RepositoryPaths.Combine("deps", "ffmpeg", "build-ffmpeg.sh")));
        Assert.True(list.Success, "deps/ffmpeg/build-ffmpeg.sh has no --enable-filter list.");
        return new HashSet<string>(list.Groups["list"].Value.Split(','), StringComparer.Ordinal);
    }

    /// <summary>
    /// Every filter option in the app's source with the argument after it: literal graphs are returned, and anything
    /// that is not a literal or a key of <see cref="BuiltGraphs"/> is reported.
    /// </summary>
    private static (List<(string Where, string Graph)> Literals, List<string> Unseen) Scan()
    {
        string source = RepositoryPaths.Combine("src");
        List<(string Where, string Graph)> literals = [];
        List<string> unseen = [];
        foreach (string file in Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            if (relative.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase) || relative.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string text = File.ReadAllText(file);
            foreach (Match option in FilterOption().Matches(text))
            {
                string where = $"{relative}:{text.AsSpan(0, option.Index).Count('\n') + 1}";
                Match argument = Argument().Match(text, option.Index + option.Length);
                if (argument.Success && argument.Groups["literal"].Success)
                {
                    literals.Add((where, argument.Groups["literal"].Value));
                }
                else if (!argument.Success || !BuiltGraphs.ContainsKey(argument.Groups["call"].Value))
                {
                    unseen.Add($"{where}: {option.Value} {(argument.Success ? argument.Value.Trim(' ', ',') : "followed by an argument this scan cannot read")}");
                }
            }
        }

        return (literals, unseen);
    }

    [GeneratedRegex(@"--enable-filter=(?<list>[a-z0-9_,]+)", RegexOptions.CultureInvariant)]
    private static partial Regex EnableFilter();

    [GeneratedRegex(@"""(?:-vf|-af|-lavfi|-filter_complex|-filter(?::[a-z0-9]+)*)""", RegexOptions.CultureInvariant)]
    private static partial Regex FilterOption();

    [GeneratedRegex(@"\G\s*,\s*(?:""(?<literal>[^""\\]*)""(?=\s*[,\]\)])|(?<call>[A-Za-z_][A-Za-z0-9_.]*)(?=\s*[,\]\)\(]))", RegexOptions.CultureInvariant)]
    private static partial Regex Argument();

    [GeneratedRegex(@"^\s*(?:\[[^\]]*\]\s*)*(?<name>[a-z0-9_]+)", RegexOptions.CultureInvariant)]
    private static partial Regex FilterName();
}
