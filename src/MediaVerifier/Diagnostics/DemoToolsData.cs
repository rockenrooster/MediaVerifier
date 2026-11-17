using MediaVerifier.UI.Data;

namespace MediaVerifier.Diagnostics;

/// <summary>The Tools cards of the mockup (docs/design/tools-dark.svg).</summary>
internal sealed partial class DemoUiData
{
    public ToolsData LoadTools() => new(
        41,
        (long)(12.8 * 1024 * 1024 * 1024),
        9,
        "Rebuilt index of 38 videos",
        At(15, 21, 14),
        true,
        325,
        At(10, 9, 0),
        (long)(48.2 * 1024 * 1024),
        @"%LOCALAPPDATA%\MediaVerifier",
        At(9, 3, 0),
        12_507,
        []);
}
