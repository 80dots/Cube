using Godot;

namespace Cube.App.UI;

/// <summary>로그 패널(Windows → Log, 헬프 라인 오른쪽 끝 Log 버튼)과 log.* 액션.</summary>
public partial class Shell
{
    public LogPanel? LogWindow { get; private set; }
    private Button _logButton = null!;
    private string _lastHelp = "";
    private int _seenErrors, _seenWarnings;
    private (int e, int w) _shownCounts = (-1, -1);

    /// <summary>헬프 라인 줄: 왼쪽 메시지 + 오른쪽 끝 Log 버튼(오류/경고 수 표시).</summary>
    private Control BuildHelpRow(float s)
    {
        var row = new HBoxContainer { Name = "HelpRow" };
        HelpLine.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        HelpLine.ClipText = true;
        row.AddChild(HelpLine);
        _logButton = new Button { Name = "LogButton", Text = "Log", Flat = true, TooltipText = "Show the log panel (messages, warnings and errors)", FocusMode = FocusModeEnum.None };
        _logButton.AddThemeFontSizeOverride("font_size", (int)(12 * s));
        _logButton.Pressed += () => Actions.Invoke("windows.log");
        row.AddChild(_logButton);
        return row;
    }

    private void RegisterLogActions()
    {
        Actions.Register("windows.log", "Log", () => TogglePanel(EnsureLog()), isChecked: () => LogWindow?.IsOpen ?? false);
        Actions.Register("log.copy", "Copy Log to Clipboard", CopyLog);
        Actions.Register("log.save", "Save Log to File...", ShowSaveLogDialog);
        Actions.Register("log.clear", "Clear Log", () => { LogCapture.Instance?.Clear(); _seenErrors = _seenWarnings = 0; LogWindow?.ForceRebuild(); });
    }

    private LogPanel EnsureLog()
    {
        if (LogWindow == null)
        {
            LogWindow = new LogPanel { Name = "LogPanel", Visible = false, PanelId = "log" };
            AddChild(LogWindow);
            LogWindow.Setup(this);
            LogWindow.Closed += RefreshShelf;
            Dock.Register(LogWindow);
        }
        return LogWindow;
    }

    private string LogText() => LogWindow != null ? LogWindow.FilteredText() : string.Join("\n", (LogCapture.Instance?.Snapshot() ?? new()).Select(e => e.Format())) + "\n";

    private void CopyLog()
    {
        var text = LogText();
        DisplayServer.ClipboardSet(text);
        int lines = text.Count(c => c == '\n');
        HelpLine.Text = $"Log: copied {lines} line(s) to the clipboard.";
    }

    private void ShowSaveLogDialog()
    {
        var fd = new FileDialog
        {
            FileMode = FileDialog.FileModeEnum.SaveFile, Access = FileDialog.AccessEnum.Filesystem, UseNativeDialog = true,
            Title = "Save Log", CurrentFile = $"cube-log-{DateTime.Now:yyyyMMdd-HHmmss}.log",
            CurrentDir = Settings.LastExportDir ?? OS.GetSystemDir(OS.SystemDir.Documents),
        };
        fd.AddFilter("*.log,*.txt", "Log / text");
        AddChild(fd);
        fd.FileSelected += p => { SaveLog(p); fd.QueueFree(); };
        fd.Canceled += fd.QueueFree;
        fd.PopupCentered();
    }

    public bool SaveLog(string path)
    {
        try
        {
            var header = $"Cube {ProjectSettings.GetSetting("application/config/version")} — Godot {Engine.GetVersionInfo()["string"]} — {OS.GetName()} — saved {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n\n";
            System.IO.File.WriteAllText(path, header + LogText(), new System.Text.UTF8Encoding(false));
            HelpLine.Text = $"Log: saved to {path}";
            return true;
        }
        catch (Exception ex) { HelpLine.Text = $"Log: save failed — {ex.Message}"; return false; }
    }

    /// <summary>_Process: 헬프 라인 메시지를 Status로 기록하고 Log 버튼에 오류/경고 수를 표시한다(패널을 열면 '본 것'으로 처리).</summary>
    private void UpdateLog()
    {
        var log = LogCapture.Instance;
        if (log == null || _logButton == null) return;
        if (HelpLine.Text != _lastHelp) { _lastHelp = HelpLine.Text; if (!string.IsNullOrWhiteSpace(_lastHelp)) LogCapture.Write(LogLevel.Status, _lastHelp); }
        var (err, warn) = log.Counts;
        if (LogWindow?.IsOpen == true) { _seenErrors = err; _seenWarnings = warn; }
        int newErr = Math.Max(0, err - _seenErrors), newWarn = Math.Max(0, warn - _seenWarnings);
        if (_shownCounts == (newErr, newWarn)) return;
        _shownCounts = (newErr, newWarn);
        _logButton.Text = newErr > 0 ? $"Log  ● {newErr} error(s)" : newWarn > 0 ? $"Log  ● {newWarn} warning(s)" : "Log";
        var col = newErr > 0 ? new Color(1f, 0.45f, 0.4f) : newWarn > 0 ? new Color(1f, 0.8f, 0.35f) : MayaTheme.Text;
        _logButton.AddThemeColorOverride("font_color", col);
        _logButton.AddThemeColorOverride("font_hover_color", col);
    }
}
