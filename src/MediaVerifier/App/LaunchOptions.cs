using System.Globalization;
using MediaVerifier.Core.Ipc;

namespace MediaVerifier.App;

internal enum LaunchMode
{
    Gui,
    SelfCheck,
    RunPlan,
    RenderScreens,
    Cleanup,
}

/// <summary>What one launch of MediaVerifier.exe was asked to do (subplan 01, Command line).</summary>
internal sealed record LaunchOptions
{
    public LaunchMode Mode { get; init; } = LaunchMode.Gui;
    public bool Minimized { get; init; }
    public long? PlanId { get; init; }
    public string? RenderDirectory { get; init; }
    public string? ActivationUri { get; init; }
}

/// <summary>Hand-written, strict parser: anything it does not know is an error (exit 1, usage in the log).</summary>
internal static class CommandLineParser
{
    public const string Minimized = "--minimized";
    public const string SelfCheck = "--self-check";
    public const string RunPlan = "--run-plan";
    public const string RenderScreens = "--render-screens";
    public const string Cleanup = "--cleanup";

    public const string Usage =
        "Usage: MediaVerifier.exe [option]\n" +
        "  (none)                          Start the app, or bring the running one forward\n" +
        "  --minimized                     Start hidden in the notification area (when that setting is on)\n" +
        "  --self-check                    Run diagnostics without a window; exit 0 on success, 1 on failure\n" +
        "  --run-plan <id>                 Run a saved scan plan without a window\n" +
        "  --render-screens <dir>          Render every page to PNG files in <dir>, then exit\n" +
        "  --cleanup                       Remove scheduled tasks and per-user registrations, then exit\n" +
        "  mediaverifier://...             Open a link from a notification";

    public static bool TryParse(IReadOnlyList<string> args, out LaunchOptions options, out string error)
    {
        try
        {
            options = Parse(args);
            error = "";
            return true;
        }
        catch (FormatException ex)
        {
            options = new LaunchOptions();
            error = ex.Message;
            return false;
        }
    }

    public static LaunchOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count == 0)
        {
            return new LaunchOptions();
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        bool minimized = false;
        bool selfCheck = false;
        bool cleanup = false;
        long? planId = null;
        string? renderDirectory = null;
        string? uri = null;

        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i] ?? "";
            if (ActivationUriScheme.IsActivationUri(arg))
            {
                MarkSeen(seen, "uri", arg);
                uri = arg;
                continue;
            }

            string name = arg.ToLowerInvariant();
            switch (name)
            {
                case Minimized:
                    MarkSeen(seen, name, arg);
                    minimized = true;
                    break;
                case SelfCheck:
                    MarkSeen(seen, name, arg);
                    selfCheck = true;
                    break;
                case Cleanup:
                    MarkSeen(seen, name, arg);
                    cleanup = true;
                    break;
                case RunPlan:
                    MarkSeen(seen, name, arg);
                    planId = ParseId(TakeValue(args, ref i, arg), arg);
                    break;
                case RenderScreens:
                    MarkSeen(seen, name, arg);
                    renderDirectory = TakeValue(args, ref i, arg);
                    break;
                default:
                    throw new FormatException($"Unknown argument '{arg}'.");
            }
        }

        if (selfCheck)
        {
            RequireAlone(seen, SelfCheck);
            return new LaunchOptions { Mode = LaunchMode.SelfCheck };
        }

        if (cleanup)
        {
            RequireAlone(seen, Cleanup);
            return new LaunchOptions { Mode = LaunchMode.Cleanup };
        }

        if (planId is not null)
        {
            RequireAlone(seen, RunPlan);
            return new LaunchOptions { Mode = LaunchMode.RunPlan, PlanId = planId };
        }

        if (renderDirectory is not null)
        {
            RequireAlone(seen, RenderScreens);
            return new LaunchOptions { Mode = LaunchMode.RenderScreens, RenderDirectory = renderDirectory };
        }

        if (uri is not null)
        {
            RequireAlone(seen, "uri");
            return new LaunchOptions { ActivationUri = uri };
        }

        return new LaunchOptions { Minimized = minimized };
    }

    private static void MarkSeen(HashSet<string> seen, string key, string arg)
    {
        if (!seen.Add(key))
        {
            throw new FormatException($"Argument '{arg}' was given more than once.");
        }
    }

    private static void RequireAlone(HashSet<string> seen, string key)
    {
        if (seen.Count != 1)
        {
            throw new FormatException($"'{key}' cannot be combined with other arguments.");
        }
    }

    private static string TakeValue(IReadOnlyList<string> args, ref int index, string option)
    {
        if (index + 1 >= args.Count || string.IsNullOrWhiteSpace(args[index + 1]) || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new FormatException($"'{option}' needs a value.");
        }

        index++;
        return args[index];
    }

    private static long ParseId(string text, string option)
    {
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long value) || value <= 0)
        {
            throw new FormatException($"'{option}' needs a positive whole number, not '{text}'.");
        }

        return value;
    }
}
