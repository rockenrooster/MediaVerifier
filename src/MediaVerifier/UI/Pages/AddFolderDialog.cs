using System.Globalization;
using System.Text.Json;
using MediaVerifier.App;
using MediaVerifier.Core.Engine;
using MediaVerifier.Core.Model;
using MediaVerifier.Core.Storage;
using MediaVerifier.UI.Kit;
using MediaVerifier.UI.Kit.Popups;
using MediaVerifier.UI.Theme;

namespace MediaVerifier.UI.Pages;

/// <summary>
/// Add or edit a folder (subplan 09, Library; reference <c>dialogs</c>, G): the folder must exist and not
/// overlap a watched one; the media files in it are counted in the background while the form is open.
/// </summary>
internal sealed class AddFolderDialog : KitDialog
{
    private readonly UiServices _services;
    private readonly RootRecord? _editing;
    private readonly IReadOnlyList<RootRecord> _roots;
    private readonly PathBox _folder = new() { Inset = true, TabIndex = 0, DialogTitle = Strings.Library.AddTitle };
    private readonly KitTextBox _name = new() { Inset = true, TabIndex = 1 };
    private readonly OptionRow _library = new() { Text = Strings.Library.UseLibrary, Description = Strings.Library.UseLibraryText, GroupName = "use", TabIndex = 2 };
    private readonly OptionRow _backup = new() { Text = Strings.Library.UseBackup, Description = Strings.Library.UseBackupText, GroupName = "use", TabIndex = 3 };
    private readonly KitCheckBox _photos = new() { Text = Strings.Kinds.Photos, Checked = true, TabIndex = 4 };
    private readonly KitCheckBox _videos = new() { Text = Strings.Kinds.Videos, Checked = true, TabIndex = 5 };
    private readonly KitCheckBox _music = new() { Text = Strings.Kinds.Music, Checked = true, TabIndex = 6 };
    private readonly KitTextBox _skip = new() { Inset = true, TabIndex = 7 };
    private readonly InfoBar _count = new() { Tier = Severity.Low, TabIndex = 8 };
    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 300 };
    private readonly KitButton _ok;
    private CancellationTokenSource? _counting;
    private long? _counted;
    private string? _error;
    private bool _nameEdited;
    private string _autoName = string.Empty;

    private AddFolderDialog(UiServices services, RootRecord? editing, IReadOnlyList<RootRecord> roots, string? path)
        : base(editing is null ? Strings.Library.AddTitle : Strings.Library.EditTitle, DialogWidth.Form, closeButton: true)
    {
        _services = services;
        _editing = editing;
        _roots = roots;
        CloseToolTip = Strings.Kit.CloseEsc;
        AddButton(Strings.Kit.Cancel, ButtonKind.Secondary, DialogResult.Cancel);
        _ok = AddButton(editing is null ? Strings.Library.AddFolder : Strings.Kit.Save, ButtonKind.Primary, DialogResult.None);

        // An async void handler: a failure reaches the app's error handler (logged and shown), never a dead button.
        _ok.Click += async (_, _) => await SaveAsync().ConfigureAwait(true);
        _library.Checked = editing?.Role != RootRole.Backup;
        _backup.Checked = editing?.Role == RootRole.Backup;
        if (editing is not null)
        {
            _folder.Text = editing.Path;
            _name.Text = editing.Label ?? Path.GetFileName(editing.Path.TrimEnd('\\'));
            _nameEdited = true;
            _photos.Checked = editing.MediaMask.HasFlag(MediaKindMask.Photo);
            _videos.Checked = editing.MediaMask.HasFlag(MediaKindMask.Video);
            _music.Checked = editing.MediaMask.HasFlag(MediaKindMask.Audio);
            _skip.Text = string.Join("; ", ParsePatterns(editing.ExcludePatternsJson));
        }
        else if (path is not null)
        {
            _folder.Text = path;
        }

        _skip.Placeholder = string.Empty;
        _folder.TextChanged += (_, _) => OnFolderChanged();
        _name.TextChanged += (_, _) => _nameEdited = _name.Text != _autoName;
        foreach (KitCheckBox box in new[] { _photos, _videos, _music })
        {
            box.CheckedChanged += (_, _) => ValidateForm();
        }

        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            StartCount();
        };
        Body.Controls.AddRange([_folder, _name, _library, _backup, _photos, _videos, _music, _skip, _count]);
        Body.Paint += (_, e) => PaintLabels(e.Graphics);
        BodyHeight = 350;
        OnFolderChanged();
    }

    /// <summary>Shows the dialog; resolves after it closes (added or edited roots are saved by then).</summary>
    public static async Task ShowAsync(Form owner, UiServices services, long? rootId, string? path)
    {
        List<RootRecord> roots = await Task.Run(services.Database.Roots.List).ConfigureAwait(true);
        RootRecord? editing = rootId is long id ? roots.FirstOrDefault(r => r.Id == id) : null;
        using AddFolderDialog dialog = new(services, editing, roots, path);
        dialog.ShowOver(owner);
    }

    protected override Control? InitialFocus() => _editing is null ? _folder : _name;

    protected override void LayoutBody()
    {
        int x = S(104);
        int width = Math.Max(0, Body.Width - x);
        int shift = _error is null ? 0 : S(20);
        _folder.Bounds = new Rectangle(x, 0, width, S(32));
        _name.Bounds = new Rectangle(x, S(44) + shift, width, S(32));
        _library.Bounds = new Rectangle(x, S(92) + shift, width, S(52));
        _backup.Bounds = new Rectangle(x, S(152) + shift, width, S(52));
        int boxY = S(216) + shift;
        _photos.Bounds = new Rectangle(new Point(x, boxY), _photos.GetPreferredSize(Size.Empty));
        _videos.Bounds = new Rectangle(new Point(x + S(91), boxY), _videos.GetPreferredSize(Size.Empty));
        _music.Bounds = new Rectangle(new Point(x + S(182), boxY), _music.GetPreferredSize(Size.Empty));
        _skip.Bounds = new Rectangle(x, S(252) + shift, width, S(32));
        _count.Bounds = new Rectangle(0, S(315) + shift, Body.Width, S(36));
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _counting?.Cancel();
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _debounce.Dispose();
            _counting?.Dispose();
        }

        base.Dispose(disposing);
    }

    private static IReadOnlyList<string> ParsePatterns(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void PaintLabels(Graphics g)
    {
        ThemeTokens t = ThemeService.Tokens;
        TypeRamp type = TypeRamp.For(DeviceDpi);
        int shift = _error is null ? 0 : S(20);
        KitPaint.DrawText(g, Strings.Library.FolderLabel, type.Body, t.Text2, 0, S(16), S(96));
        if (_error is not null)
        {
            KitPaint.DrawText(g, _error, type.Caption, t.Critical.Text, S(104), S(44), Body.Width - S(104));
        }

        KitPaint.DrawText(g, Strings.Library.NameLabel, type.Body, t.Text2, 0, S(60) + shift, S(96));
        KitPaint.DrawText(g, Strings.Library.UseAs, type.Body, t.Text2, 0, S(112) + shift, S(96));
        KitPaint.DrawText(g, Strings.Library.Contains, type.Body, t.Text2, 0, S(226) + shift, S(96));
        KitPaint.DrawText(g, Strings.Library.Skip, type.Body, t.Text2, 0, S(268) + shift, S(96));
        KitPaint.DrawText(g, Strings.Library.SkipHelper, type.Caption, t.Text3, S(104), S(296) + shift, Body.Width - S(104));
    }

    private void OnFolderChanged()
    {
        string path = _folder.Text.Trim();
        string name = path.Length == 0 ? string.Empty : Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } leaf ? leaf : path;
        if (!_nameEdited)
        {
            _autoName = name;
            _name.Text = name;
        }

        ValidateForm();
        _debounce.Stop();
        _counting?.Cancel();
        _counted = null;
        _count.Visible = _error is null && path.Length > 0;
        if (_count.Visible)
        {
            _count.Tier = Severity.Low;
            _count.Text = Strings.Library.Counting;
            _debounce.Start();
        }
    }

    private void ValidateForm()
    {
        string path = _folder.Text.Trim();
        string? error = null;
        if (path.Length > 0)
        {
            string full = Normalize(path);
            if (!Directory.Exists(full))
            {
                error = Strings.Library.ErrorMissing;
            }
            else
            {
                foreach (RootRecord root in _roots)
                {
                    if (root.Id == _editing?.Id)
                    {
                        continue;
                    }

                    string watched = Normalize(root.Path);
                    if (string.Equals(full, watched, StringComparison.OrdinalIgnoreCase))
                    {
                        error = Strings.Library.ErrorWatched;
                        break;
                    }

                    if (full.StartsWith(watched + "\\", StringComparison.OrdinalIgnoreCase))
                    {
                        error = string.Format(CultureInfo.CurrentCulture, Strings.Library.ErrorInside, root.Label ?? Path.GetFileName(watched));
                        break;
                    }
                }
            }
        }

        if (error != _error)
        {
            _error = error;
            BodyHeight = error is null ? 350 : 370;
            Body.Invalidate();
        }

        _ok.Enabled = path.Length > 0 && error is null && (_photos.Checked || _videos.Checked || _music.Checked);
    }

    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd('\\');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.TrimEnd('\\');
        }
    }

    private void StartCount()
    {
        string path = Normalize(_folder.Text.Trim());
        CancellationTokenSource cancel = new();
        _counting?.Dispose();
        _counting = cancel;
        Core.Settings.ScanSettings settings = _services.State.Settings.Scanning;
        IReadOnlyList<string> skip = SkipPatterns();
        _ = Task.Run(() => Count(path, settings, skip, cancel.Token), cancel.Token).ContinueWith(
            task =>
            {
                if (task.IsCompletedSuccessfully && !cancel.IsCancellationRequested && !IsDisposed)
                {
                    (long files, long bytes) = task.Result;
                    _counted = files;
                    _count.Tier = files == 0 ? Severity.Medium : Severity.Low;
                    _count.Text = files == 0 ? Strings.Library.NoneFound : string.Format(CultureInfo.CurrentCulture, Strings.Library.Found, UiFormat.Count(files), UiFormat.Size(bytes));
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    private static (long Files, long Bytes) Count(string path, Core.Settings.ScanSettings settings, IReadOnlyList<string> skip, CancellationToken cancel)
    {
        TreeEnumerator enumerator = new(
            WindowsFileSystem.Instance,
            new MediaClassifier(settings.PhotoExtensions, settings.VideoExtensions, settings.AudioExtensions),
            new EnumerationRules { ExcludedDirectories = settings.AlwaysSkip, ExcludeGlobs = skip.Count == 0 ? null : skip },
            Core.SystemClock.Instance);
        long files = 0;
        long bytes = 0;
        foreach (EnumeratedDirectory directory in enumerator.Enumerate(path, cancellationToken: cancel))
        {
            foreach (EnumeratedFile file in directory.Files)
            {
                files++;
                bytes += file.Size;
            }
        }

        return (files, bytes);
    }

    private List<string> SkipPatterns() => [.. _skip.Text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private async Task SaveAsync()
    {
        ValidateForm();
        if (!_ok.Enabled)
        {
            return;
        }

        string path = Normalize(_folder.Text.Trim());
        string name = _name.Text.Trim().Length > 0 ? _name.Text.Trim() : Path.GetFileName(path);
        MediaKindMask mask = (_photos.Checked ? MediaKindMask.Photo : 0) | (_videos.Checked ? MediaKindMask.Video : 0) | (_music.Checked ? MediaKindMask.Audio : 0);
        List<string> skip = SkipPatterns();
        string? skipJson = skip.Count == 0 ? null : JsonSerializer.Serialize(skip);
        RootRole role = _backup.Checked ? RootRole.Backup : RootRole.Library;

        // The dialog stays open until the folder is saved; one save at a time.
        _ok.Enabled = false;
        try
        {
            if (!_services.IsDemo)
            {
                if (_editing is null)
                {
                    await _services.Database.Roots.AddAsync(new RootRecord
                    {
                        Path = path,
                        Label = name,
                        Role = role,
                        MediaMask = mask,
                        ExcludePatternsJson = skipJson,
                        AddedUtc = _services.Clock.UtcNow,
                    }).ConfigureAwait(true);
                }
                else
                {
                    await _services.Database.Roots.UpdateAsync(_editing with { Path = path, Label = name, Role = role, MediaMask = mask, ExcludePatternsJson = skipJson }).ConfigureAwait(true);
                }
            }
        }
        catch
        {
            ValidateForm();
            throw;
        }

        Finish(DialogResult.OK);
        if (_editing is null && role == RootRole.Library && _counted is long counted && counted > 0)
        {
            string waiting = string.Format(CultureInfo.CurrentCulture, Strings.Library.WaitingToast, UiFormat.Count(counted));
            _services.ShowToast?.Invoke(new ToastContent(
                Severity.None,
                string.Format(CultureInfo.CurrentCulture, Strings.Library.AddedToast, name),
                waiting,
                Strings.Library.ScanNow,
                () => _ = ScanNewRootAsync(path, name)));
        }
    }

    private async Task ScanNewRootAsync(string path, string name)
    {
        List<RootRecord> roots = await Task.Run(_services.Database.Roots.List).ConfigureAwait(true);
        if (roots.FirstOrDefault(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase)) is RootRecord root)
        {
            await _services.StartScanAsync([new ScanScope(root.Id)], _services.State.Settings.Scanning.DefaultDepth, name).ConfigureAwait(true);
        }
    }
}
