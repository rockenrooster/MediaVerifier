using MediaVerifier.App;

namespace MediaVerifier;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 1 && string.Equals(args[0], "--self-check", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }
}
