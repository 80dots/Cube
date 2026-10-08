using Godot;

namespace Cube.App.UI;

/// <summary>로그 패널(Windows → Log, 헬프 라인 오른쪽 끝 Log 버튼)과 log.* 액션.</summary>
public partial class Shell
{
    /// <summary>로그 패널(처음 열 때 생성). LogCapture가 모은 메시지/경고/오류를 보여 준다.</summary>
    public LogPanel? LogWindow { get; private set; }
    /// <summary>헬프 라인 오른쪽 끝 Log 버튼. 아직 보지 않은 오류/경고 수를 글자와 색으로 표시한다.</summary>
    private Button _logButton = null!;
    /// <summary>마지막으로 로그에 기록한 헬프 라인 텍스트(같은 메시지를 매 프레임 중복 기록하지 않기 위함).</summary>
    private string _lastHelp = "";
    /// <summary>사용자가 '본' 것으로 간주한 오류/경고 누적 수. 로그 패널이 열려 있으면 현재 수로 따라잡는다.</summary>
    private int _seenErrors, _seenWarnings;
    /// <summary>Log 버튼에 마지막으로 표시한 (새 오류, 새 경고) 수. 같으면 버튼을 다시 꾸미지 않는다(-1 = 아직 없음).</summary>
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

    /// <summary>
    /// log 관련 액션 등록: windows.log(패널 토글), log.copy(클립보드 복사), log.save(파일로 저장),
    /// log.clear(기록 비우기 + '본' 카운터 초기화 + 패널 다시 그리기).
    /// </summary>
    private void RegisterLogActions()
    {
        Actions.Register("windows.log", "Log", () => TogglePanel(EnsureLog()), isChecked: () => LogWindow?.IsOpen ?? false);
        Actions.Register("log.copy", "Copy Log to Clipboard", CopyLog);
        Actions.Register("log.save", "Save Log to File...", ShowSaveLogDialog);
        Actions.Register("log.clear", "Clear Log", () => { LogCapture.Instance?.Clear(); _seenErrors = _seenWarnings = 0; LogWindow?.ForceRebuild(); });
    }

    /// <summary>로그 패널을 지연 생성하고 DockManager에 등록한다(레이아웃 복원 "log"에서도 호출).</summary>
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

    /// <summary>
    /// 복사/저장할 로그 텍스트: 패널이 있으면 패널의 현재 필터가 적용된 텍스트, 없으면 전체 기록을 한 줄씩 포맷한 텍스트.
    /// </summary>
    private string LogText() => LogWindow != null ? LogWindow.FilteredText() : string.Join("\n", (LogCapture.Instance?.Snapshot() ?? new()).Select(e => e.Format())) + "\n";

    /// <summary>로그 텍스트를 클립보드에 복사하고 줄 수를 헬프 라인에 알린다.</summary>
    private void CopyLog()
    {
        var text = LogText();
        DisplayServer.ClipboardSet(text);
        int lines = text.Count(c => c == '\n');
        HelpLine.Text = $"Log: copied {lines} line(s) to the clipboard.";
    }

    /// <summary>
    /// 로그 저장 대화상자(네이티브 파일 대화상자, 기본 파일명 cube-log-날짜시각.log, 마지막 내보내기 폴더 또는 문서 폴더)를 띄운다.
    /// 선택/취소 후 대화상자는 해제한다.
    /// </summary>
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

    /// <summary>
    /// 로그를 파일로 저장한다. 첫 줄에 앱 버전·Godot 버전·OS·저장 시각 헤더를 붙이고 BOM 없는 UTF-8로 쓴다.
    /// </summary>
    /// <param name="path">저장할 파일의 절대 경로.</param>
    /// <returns>성공하면 true, 예외가 나면 헬프 라인에 실패 메시지를 쓰고 false.</returns>
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
        // ① 헬프 라인이 바뀌었으면 그 메시지를 Status 수준으로 로그에 남긴다
        if (HelpLine.Text != _lastHelp) { _lastHelp = HelpLine.Text; if (!string.IsNullOrWhiteSpace(_lastHelp)) LogCapture.Write(LogLevel.Status, _lastHelp); }
        // ② 패널이 열려 있으면 지금까지의 오류/경고를 모두 본 것으로 처리하고, 새로 생긴 수만 계산
        var (err, warn) = log.Counts;
        if (LogWindow?.IsOpen == true) { _seenErrors = err; _seenWarnings = warn; }
        int newErr = Math.Max(0, err - _seenErrors), newWarn = Math.Max(0, warn - _seenWarnings);
        // ③ 표시할 수가 바뀌었을 때만 버튼 텍스트와 색(오류 = 빨강, 경고 = 노랑, 없음 = 기본)을 갱신
        if (_shownCounts == (newErr, newWarn)) return;
        _shownCounts = (newErr, newWarn);
        _logButton.Text = newErr > 0 ? $"Log  ● {newErr} error(s)" : newWarn > 0 ? $"Log  ● {newWarn} warning(s)" : "Log";
        var col = newErr > 0 ? new Color(1f, 0.45f, 0.4f) : newWarn > 0 ? new Color(1f, 0.8f, 0.35f) : MayaTheme.Text;
        _logButton.AddThemeColorOverride("font_color", col);
        _logButton.AddThemeColorOverride("font_hover_color", col);
    }
}
