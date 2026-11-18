using MediaVerifier.App;

namespace MediaVerifier.Tests.Foundation;

public sealed class CommandLineParserTests
{
    [Fact]
    public void NoArgumentsStartsTheGui()
    {
        LaunchOptions options = CommandLineParser.Parse([]);
        Assert.Equal(LaunchMode.Gui, options.Mode);
        Assert.False(options.Minimized);
    }

    [Fact]
    public void MinimizedStartsTheGuiHidden()
    {
        LaunchOptions options = CommandLineParser.Parse(["--minimized"]);
        Assert.Equal(LaunchMode.Gui, options.Mode);
        Assert.True(options.Minimized);
    }

    [Fact]
    public void SelfCheckIsItsOwnMode()
    {
        Assert.Equal(LaunchMode.SelfCheck, CommandLineParser.Parse(["--self-check"]).Mode);
        Assert.Equal(LaunchMode.SelfCheck, CommandLineParser.Parse(["--SELF-CHECK"]).Mode);
    }

    [Fact]
    public void RunPlanTakesAPositiveId()
    {
        LaunchOptions options = CommandLineParser.Parse(["--run-plan", "3"]);
        Assert.Equal(LaunchMode.RunPlan, options.Mode);
        Assert.Equal(3, options.PlanId);
    }

    [Fact]
    public void RenderScreensTakesADirectory()
    {
        LaunchOptions options = CommandLineParser.Parse(["--render-screens", @"C:\out dir"]);
        Assert.Equal(LaunchMode.RenderScreens, options.Mode);
        Assert.Equal(@"C:\out dir", options.RenderDirectory);
    }

    [Fact]
    public void CleanupIsItsOwnMode()
    {
        Assert.Equal(LaunchMode.Cleanup, CommandLineParser.Parse(["--cleanup"]).Mode);
    }

    [Fact]
    public void ActivationUriIsForwarded()
    {
        LaunchOptions options = CommandLineParser.Parse(["mediaverifier://finding/123"]);
        Assert.Equal(LaunchMode.Gui, options.Mode);
        Assert.Equal("mediaverifier://finding/123", options.ActivationUri);
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("scan")]
    [InlineData("--run-plan")]
    [InlineData("--run-plan", "0")]
    [InlineData("--run-plan", "-3")]
    [InlineData("--run-plan", "3x")]
    [InlineData("--run-plan", "1", "--minimized")]
    [InlineData("--render-screens")]
    [InlineData("--render-screens", "--minimized")]
    [InlineData("--notify", "5")]
    [InlineData("--minimized", "--notify", "42")]
    [InlineData("--minimized", "--minimized")]
    [InlineData("--self-check", "--cleanup")]
    [InlineData("--self-check", "--minimized")]
    [InlineData("--cleanup", "extra")]
    [InlineData("mediaverifier://a", "--minimized")]
    [InlineData("https://example.com")]
    public void RejectsUnknownOrBadlyCombinedArguments(params string[] args)
    {
        Assert.False(CommandLineParser.TryParse(args, out _, out string error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}
