using System.Diagnostics;

namespace MediaVerifier.Tests.TestSupport;

/// <summary>
/// What the window tests share (import with <c>using static</c>): pumping the message loop, walking the control tree,
/// answering a dialog and an engine mutex of the test's own.
/// </summary>
internal static class Ui
{
    /// <summary>Pumps the message loop until <paramref name="done"/>, or for <paramref name="limit"/> (10 s) at most.</summary>
    public static void Settle(Func<bool> done, TimeSpan? limit = null)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!done() && watch.Elapsed < (limit ?? TimeSpan.FromSeconds(10)))
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }
    }

    /// <summary>Pumps the message loop until <paramref name="task"/> ends, for 10 s at most.</summary>
    public static void Settle(Task task) => Settle(() => task.IsCompleted);

    /// <summary>Pumps the message loop for <paramref name="time"/>.</summary>
    public static void Pump(TimeSpan time) => Settle(() => false, time);

    public static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (Control nested in Descendants(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>The first <typeparamref name="T"/> under <paramref name="root"/> that matches; throws when there is none.</summary>
    public static T Find<T>(Control root, Func<T, bool>? match = null)
        where T : Control =>
        Descendants(root).OfType<T>().FirstOrDefault(match ?? (_ => true)) ?? throw new InvalidOperationException($"No matching {typeof(T).Name} in {root.GetType().Name}.");

    /// <summary>Runs <paramref name="open"/>, answers the first <typeparamref name="T"/> dialog it shows, returns its title.</summary>
    public static string? Answer<T>(Action open, Action<T> answer)
        where T : Form
    {
        string? title = null;
        using System.Windows.Forms.Timer timer = new() { Interval = 100 };
        timer.Tick += (_, _) =>
        {
            if (title is null && Application.OpenForms.OfType<T>().FirstOrDefault() is T dialog && dialog.Visible)
            {
                title = dialog.Text;
                answer(dialog);
            }
        };
        timer.Start();
        open();
        Settle(() => title is not null && !Application.OpenForms.OfType<T>().Any());
        timer.Stop();
        return title;
    }

    /// <summary>An engine mutex of the test's own: a MediaVerifier scanning on this machine does not block the test.</summary>
    public static string OwnEngine() => @"Local\MediaVerifier.Engine.Test." + Guid.NewGuid().ToString("N");
}
