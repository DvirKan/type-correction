using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace TypeCorrection;

/// <summary>The icon next to the clock and its menu. There is no main window.</summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly Settings _settings;
    private readonly LayoutSet _layouts = new();
    private readonly Spelling _spelling = new();
    private readonly Control _ui = new();
    private readonly Corrector _corrector;
    private readonly NotifyIcon _tray;
    private readonly Icon _iconOn = MakeIcon(true), _iconOff = MakeIcon(false);
    private readonly ToolStripMenuItem _enabledItem, _startupItem, _languagesItem;
    private readonly FileSystemWatcher? _watcher;

    public TrayApp(bool launchedAtStartup)
    {
        _settings = Settings.Load();
        _ = _ui.Handle; // create the handle used to run work on this thread

        _layouts.Refresh();
        foreach (var l in _layouts.All) // load dictionaries now, not mid-typing
            DebugLog.Write($"layout {l}  dictionary={_spelling.HasDictionary(l)}");

        _corrector = new Corrector(_layouts, new WordJudge(_spelling), _settings, _ui) { Enabled = _settings.Enabled };
        _corrector.Start();

        _enabledItem = new ToolStripMenuItem("תיקון אוטומטי פעיל", null, (_, _) => SetEnabled(!_corrector.Enabled));
        _startupItem = new ToolStripMenuItem("הפעלה עם Windows", null, (_, _) => ToggleStartup());
        _languagesItem = new ToolStripMenuItem("שפות שזוהו");

        var menu = new ContextMenuStrip { RightToLeft = RightToLeft.Yes, ShowImageMargin = false };
        menu.Items.AddRange(new ToolStripItem[]
        {
            _enabledItem,
            _startupItem,
            _languagesItem,
            new ToolStripMenuItem("תוכנות מוחרגות…", null, (_, _) => OpenSettingsFile()),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Pause מיד אחרי תיקון = ביטול התיקון") { Enabled = false },
            new ToolStripMenuItem("Pause בכל מצב אחר = המרה ידנית של המילה") { Enabled = false },
            new ToolStripSeparator(),
            new ToolStripMenuItem("יציאה", null, (_, _) => ExitThread()),
        });
        menu.Opening += (_, _) => RefreshMenu();

        _tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => SetEnabled(!_corrector.Enabled);
        UpdateIcon();

        try
        {
            _watcher = new FileSystemWatcher(Settings.Folder, Path.GetFileName(Settings.FilePath))
            {
                EnableRaisingEvents = true,
            };
            _watcher.Changed += (_, _) => _ui.BeginInvoke(ReloadExclusions);
        }
        catch (ArgumentException) { _watcher = null; }

        if (!launchedAtStartup)
            _tray.ShowBalloonTip(4000, "תיקון שפת הקלדה",
                "פועל ברקע. מילה שהוקלדה בשפה הלא נכונה תתוקן אחרי רווח.", ToolTipIcon.Info);
    }

    private void SetEnabled(bool on)
    {
        _corrector.Enabled = on;
        _settings.Enabled = on;
        _settings.Save();
        UpdateIcon();
    }

    private void ToggleStartup()
    {
        try { Settings.StartWithWindows = !Settings.StartWithWindows; }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException)
        {
            MessageBox.Show("לא הצלחתי לשנות את ההפעלה עם Windows.", "תיקון שפה");
        }
    }

    private void RefreshMenu()
    {
        _enabledItem.Checked = _corrector.Enabled;
        _startupItem.Checked = Settings.StartWithWindows;

        _layouts.Refresh();
        _languagesItem.DropDownItems.Clear();
        foreach (var l in _layouts.All)
        {
            string status = _spelling.HasDictionary(l) ? "✓ מילון של Windows" : "✗ אין מילון – תיקון חלקי";
            _languagesItem.DropDownItems.Add(new ToolStripMenuItem($"{l.DisplayName}   {status}") { Enabled = false });
        }
        if (_layouts.All.Count < 2)
            _languagesItem.DropDownItems.Add(new ToolStripMenuItem("צריך לפחות שתי שפות מקלדת") { Enabled = false });
    }

    private void OpenSettingsFile()
    {
        _settings.Save();
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{Settings.FilePath}\"") { UseShellExecute = true });
    }

    private void ReloadExclusions()
    {
        var fresh = Settings.Load();
        _settings.ExcludedApps = fresh.ExcludedApps;
    }

    private void UpdateIcon()
    {
        _tray.Icon = _corrector.Enabled ? _iconOn : _iconOff;
        _tray.Text = _corrector.Enabled ? "תיקון שפה – פעיל" : "תיקון שפה – מושהה";
    }

    private static Icon MakeIcon(bool on)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            using var bg = new SolidBrush(on ? Color.FromArgb(37, 99, 235) : Color.FromArgb(120, 120, 120));
            using var path = new GraphicsPath();
            path.AddArc(0, 0, 12, 12, 180, 90);
            path.AddArc(20, 0, 12, 12, 270, 90);
            path.AddArc(20, 20, 12, 12, 0, 90);
            path.AddArc(0, 20, 12, 12, 90, 90);
            path.CloseFigure();
            g.FillPath(bg, path);
            using var font = new Font("Segoe UI", 13, FontStyle.Bold, GraphicsUnit.Pixel);
            var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("אA", font, Brushes.White, new RectangleF(0, 0, 32, 32), fmt);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    protected override void ExitThreadCore()
    {
        _corrector.Dispose();
        _watcher?.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _ui.Dispose();
        base.ExitThreadCore();
    }
}
