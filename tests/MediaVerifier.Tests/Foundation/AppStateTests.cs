using System.Security.AccessControl;
using System.Security.Principal;
using MediaVerifier.App;
using MediaVerifier.Core;
using MediaVerifier.Diagnostics;
using MediaVerifier.Tests.TestSupport;
using Microsoft.Win32;

namespace MediaVerifier.Tests.Foundation;

public sealed class AppStateTests
{
    [Fact]
    public void ServicesAreCreatedLazilyAndDisposedInReverseOrder()
    {
        using TempDirectory temp = new("state");
        List<string> disposed = [];
        AppState state = AppState.Load(temp.CreateAppPaths(), installSharedLog: false);
        Recorder? first = null;
        Recorder? second = null;

        Assert.Equal(1, state.OwnedCount); // the log
        Recorder a = state.GetOrCreate(ref first, () => new Recorder("first", disposed));
        Recorder b = state.GetOrCreate(ref second, () => new Recorder("second", disposed));
        Assert.Same(a, state.GetOrCreate(ref first, () => new Recorder("again", disposed)));
        Assert.Equal(3, state.OwnedCount);

        state.Dispose();

        Assert.Equal(["second", "first"], disposed);
        Assert.Throws<ObjectDisposedException>(() => state.GetOrCreate(ref second, () => b));
    }

    [Fact]
    public void SettingsSaveRaisesChanged()
    {
        using TempDirectory temp = new("state");
        AppPaths paths = temp.CreateAppPaths();
        using AppState state = AppState.Load(paths, installSharedLog: false);
        int raised = 0;
        state.SettingsChanged += (_, _) => raised++;
        state.Settings.MinimizeToTray = true;

        Assert.True(state.SaveSettings());
        Assert.Equal(1, raised);
        Assert.True(File.Exists(paths.SettingsFile));
    }

    [Fact]
    public void SelfCheckPassesOnACleanProfile()
    {
        using TempDirectory temp = new("selfcheck");
        Assert.Equal(0, SelfCheck.Run(temp.CreateAppPaths(), checkMediaEngine: false));
    }

    [Fact]
    public void SelfCheckFailsWhenTheDataDirectoryIsReadOnly()
    {
        using TempDirectory temp = new("selfcheck-ro");
        AppPaths paths = temp.CreateAppPaths();
        paths.EnsureCreated();
        DirectoryInfo local = new(paths.LocalDirectory);
        SecurityIdentifier user = WindowsIdentity.GetCurrent().User!;
        FileSystemAccessRule deny = new(
            user,
            FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.CreateDirectories | FileSystemRights.CreateFiles,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Deny);
        DirectorySecurity security = local.GetAccessControl();
        security.AddAccessRule(deny);
        local.SetAccessControl(security);
        try
        {
            Assert.Equal(1, SelfCheck.Run(paths, checkMediaEngine: false));
        }
        finally
        {
            security.RemoveAccessRule(deny);
            local.SetAccessControl(security);
        }
    }

    [Fact]
    public void StartupToggleAddsAndRemovesTheRunValue()
    {
        string key = @"Software\MediaVerifier.Tests\Run-" + Guid.NewGuid().ToString("N");
        StartupRegistration registration = new(key, @"C:\Apps\MediaVerifier\MediaVerifier.exe");
        try
        {
            Assert.False(registration.IsEnabled);
            registration.Apply(true);
            Assert.True(registration.IsEnabled);
            Assert.True(registration.IsCurrent);
            using (RegistryKey? run = Registry.CurrentUser.OpenSubKey(key))
            {
                Assert.Equal("\"C:\\Apps\\MediaVerifier\\MediaVerifier.exe\" --minimized", run!.GetValue(StartupRegistration.ValueName));
            }

            registration.Apply(false);
            Assert.False(registration.IsEnabled);
            registration.Apply(false);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\MediaVerifier.Tests", throwOnMissingSubKey: false);
        }
    }

    /// <summary>
    /// Start-up maintenance makes the Run value match the setting: an earlier version saved the setting without the value,
    /// and a reinstall to another folder leaves a value pointing at the old one.
    /// </summary>
    [Fact]
    public void StartupReconcileWritesAMissingOrStaleValueAndRemovesAnUnwantedOne()
    {
        string key = @"Software\MediaVerifier.Tests\Run-" + Guid.NewGuid().ToString("N");
        StartupRegistration old = new(key, @"C:\Old\MediaVerifier.exe");
        StartupRegistration registration = new(key, @"C:\Apps\MediaVerifier\MediaVerifier.exe");
        try
        {
            registration.Reconcile(true);
            Assert.True(registration.IsCurrent);

            old.Apply(true);
            Assert.False(registration.IsCurrent);
            registration.Reconcile(true);
            Assert.True(registration.IsCurrent);

            registration.Reconcile(false);
            Assert.False(registration.IsEnabled);
            registration.Reconcile(false);
            Assert.False(registration.IsEnabled);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\MediaVerifier.Tests", throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void PlacementOnADisconnectedMonitorIsNotReachable()
    {
        Rectangle primary = new(0, 0, 1920, 1040);
        Rectangle secondary = new(1920, 0, 2560, 1400);

        Assert.True(WindowPlacementRules.IsReachable(new Rectangle(100, 100, 1280, 800), [primary]));
        Assert.True(WindowPlacementRules.IsReachable(new Rectangle(2000, 50, 1280, 800), [primary, secondary]));
        Assert.False(WindowPlacementRules.IsReachable(new Rectangle(2000, 50, 1280, 800), [primary]));
        Assert.False(WindowPlacementRules.IsReachable(new Rectangle(-5000, -5000, 1280, 800), [primary]));
        Assert.Equal(new Point(320, 120), WindowPlacementRules.CentreIn(new Size(1280, 800), primary));
        Assert.Equal(new Size(1280, 700), WindowPlacementRules.Fit(new Size(1280, 800), new Size(1920, 700)));
    }

    private sealed class Recorder(string name, List<string> log) : IDisposable
    {
        public void Dispose() => log.Add(name);
    }
}
