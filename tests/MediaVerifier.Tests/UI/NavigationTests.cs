using System.Reflection;
using MediaVerifier.App;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Storage;
using MediaVerifier.Diagnostics;
using MediaVerifier.Tests.TestSupport;
using MediaVerifier.UI.Data;
using MediaVerifier.UI.Pages;

namespace MediaVerifier.Tests.UI;

/// <summary>Buttons that open another page open it on what they name.</summary>
[Collection(UiCollection.Name)]
public sealed class NavigationTests
{
    /// <summary>
    /// A Dashboard recommendation's Review opens Issues on its rule (it opened every problem), and the next request
    /// (a tier from the Dashboard) starts from a clean filter instead of keeping that rule.
    /// </summary>
    [Fact]
    public void Review_opens_Issues_on_the_recommendations_rule()
    {
        Sta.Run(() =>
        {
            using TempDirectory temp = new("navigation");
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            using UiServices services = DemoUiData.CreateServices(state, temp.Path);
            using MainForm form = new(services);
            ScreenRenderer.PrepareOffscreen(form, new Size(1280, 800));

            form.Navigate(PageId.Dashboard);
            ScreenRenderer.WaitFor(form.CurrentPageLoading);
            ((DashboardPage)form.CurrentPageControl!).RunRecommendation(new RecommendationItem(Severity.High, "41 albums look converted from MP3", "", "Review", "review:AUD-FAKE-LOSSLESS"));
            ScreenRenderer.WaitFor(form.CurrentPageLoading);
            FindingFilter review = Filter(form);
            Assert.Equal(new[] { "AUD-FAKE-LOSSLESS" }, review.RuleIds);
            Assert.Null(review.Severities);

            form.Navigate(PageId.Dashboard);
            ScreenRenderer.WaitFor(form.CurrentPageLoading);
            ((DashboardPage)form.CurrentPageControl!).OpenIssues(Severity.Critical, null);
            ScreenRenderer.WaitFor(form.CurrentPageLoading);
            FindingFilter tier = Filter(form);
            Assert.Null(tier.RuleIds);
            Assert.Equal(new[] { Severity.Critical }, tier.Severities);
            form.Close();
        });
    }

    private static FindingFilter Filter(MainForm form)
    {
        IssuesPage page = Assert.IsType<IssuesPage>(form.CurrentPageControl);
        return ((IssuesQuery)typeof(IssuesPage).GetField("_query", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!).Filter;
    }
}
