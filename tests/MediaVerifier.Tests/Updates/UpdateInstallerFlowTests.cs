using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using MediaVerifier.App;
using MediaVerifier.Diagnostics;
using MediaVerifier.Core.Ffmpeg;
using MediaVerifier.Core.Updates;
using MediaVerifier.Tests.TestSupport;
using MediaVerifier.UI;
using MediaVerifier.UI.Kit;
using MediaVerifier.UI.Kit.Popups;
using static MediaVerifier.Tests.TestSupport.Ui;

namespace MediaVerifier.Tests.Updates;

/// <summary>
/// Download and install through the real window, off-screen (tasks U-08, U-09): the installer is hashed and started
/// off the UI thread, and a declined Windows permission prompt leaves the app open and says what happened.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class UpdateInstallerFlowTests
{
    private const string SetupUrl = "https://github.com/rockenrooster/MediaVerifier/releases/download/v0.1.0.2/MediaVerifier-Setup.exe";

    [Fact]
    public void The_installer_is_checked_and_started_off_the_UI_thread()
    {
        int uiThread = 0;
        int installerThread = 0;
        bool closed = false;
        Run(
            _ => installerThread = Environment.CurrentManagedThreadId,
            (form, _, _) =>
            {
                uiThread = Environment.CurrentManagedThreadId;
                form.FormClosed += (_, _) => closed = true;
            },
            (_, _, _) => { });

        Assert.NotEqual(0, installerThread);
        Assert.NotEqual(uiThread, installerThread);
        Assert.True(closed, "MediaVerifier did not close for the installer.");
    }

    [Fact]
    public void Saying_no_to_the_permission_prompt_keeps_the_app_open_and_says_so()
    {
        List<string> toasts = [];
        bool closed = false;
        string? card = null;
        Run(
            _ => throw new Win32Exception(1223),
            (form, services, _) =>
            {
                form.FormClosed += (_, _) => closed = true;
                Action<ToastContent>? shown = services.ShowToast;
                services.ShowToast = toast =>
                {
                    toasts.Add(toast.Title + " | " + toast.Text);
                    shown?.Invoke(toast);
                };
            },
            (_, _, updates) => card = UpdateStatusText.For(updates.IsChecking, updates.IsDownloading, updates.DownloadProgress, updates.LastResult));

        Assert.False(closed, "MediaVerifier closed although nothing was installed.");
        Assert.Contains(toasts, t => t.EndsWith(Strings.Settings.InstallCancelled, StringComparison.Ordinal));
        Assert.Equal(Strings.Settings.InstallCancelled, card);
    }

    /// <summary>A checked update, the window off-screen, Download and install answered with Install, then the checks.</summary>
    private static void Run(Action<string> startInstaller, Action<MainForm, UiServices, UpdateService> prepare, Action<MainForm, UiServices, UpdateService> check)
    {
        using RSA key = RSA.Create(3072);
        byte[] setup = new byte[4_500_000];
        new Random(9).NextBytes(setup);
        Dictionary<string, (HttpStatusCode, byte[])> responses = UpdateCheckTests.Release(key, manifest => manifest["sha256"] = Convert.ToHexStringLower(SHA256.HashData(setup)));
        responses[SetupUrl] = (HttpStatusCode.OK, setup);
        Sta.Run(() =>
        {
            using TempDirectory temp = new("update-installer");
            using AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
            state.Settings.CheckForUpdatesOnStartup = false;
            using UpdateService updates = new(new UpdateCheckTests.FakeGitHub(responses), temp.Combine("Updates"), new Version(0, 1, 0, 1), key.ExportSubjectPublicKeyInfoPem())
            {
                StartInstaller = startInstaller,
            };
            using UiServices services = UiServices.CreateLive(state, new FfmpegLocation(temp.Combine("ffmpeg"), IsOverride: false), updates, startupMaintenance: false, engineMutexName: @"Local\MediaVerifier.Engine.Test." + Guid.NewGuid().ToString("N"));
            using MainForm form = new(services);
            ScreenRenderer.PrepareOffscreen(form, MainForm.DefaultClientSize);
            Settle(() => form.CurrentPageLoading.IsCompleted);
            UpdateCheckResult result = Task.Run(() => updates.CheckAsync()).GetAwaiter().GetResult();
            Assert.True(result.CanDownload, result.Message);
            prepare(form, services, updates);

            Task? flow = null;
            Answer<MessageDialog>(() => flow = UpdateInstaller.RunAsync(form, services, updates, portable: false), dialog => dialog.DialogResult = DialogResult.OK);
            Settle(() => flow?.IsCompleted == true, TimeSpan.FromSeconds(30));
            Assert.True(flow?.IsCompleted, "Download and install did not finish.");
            Pump(TimeSpan.FromMilliseconds(200));
            check(form, services, updates);
        });
    }

    private static void Answer<T>(Action open, Action<T> answer)
        where T : Form
    {
        bool answered = false;
        using System.Windows.Forms.Timer timer = new() { Interval = 100 };
        timer.Tick += (_, _) =>
        {
            if (!answered && Application.OpenForms.OfType<T>().FirstOrDefault() is T dialog && dialog.Visible)
            {
                answered = true;
                answer(dialog);
            }
        };
        timer.Start();
        open();
        Settle(() => answered && !Application.OpenForms.OfType<T>().Any());
        timer.Stop();
    }
}
