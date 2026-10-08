using Godot;

namespace Cube.App.UI;

/// <summary>
/// 로그 패널(Windows → Log, 헬프 라인 오른쪽 Log 버튼): <see cref="LogCapture"/>가 모은 로그를 시간·수준별 색으로 보여 준다.
/// 수준 필터(Info/Warning/Error/Status), 검색, 자동 스크롤, 지우기, 클립보드 복사, 파일 저장. 복사·저장은 현재 필터를 따른다.
/// </summary>
public partial class LogPanel : FloatingPanel
{
    private RichTextLabel _text = null!;
    private LineEdit _search = null!;
    private CheckBox _autoScroll = null!;
    private readonly Dictionary<LogLevel, CheckBox> _filters = new();
    private Label _summary = null!;
    private long _shownTotal = -1, _shownVersion = -1;
    private bool _rebuild = true;
    private Shell _shell = null!;

    private static readonly Color ErrorColor = new(1f, 0.42f, 0.38f), WarnColor = new(1f, 0.8f, 0.35f), StatusColor = new(0.55f, 0.75f, 1f), TimeColor = new(0.55f, 0.55f, 0.55f);

    public void Setup(Shell shell)
    {
        _shell = shell;
        float s = CubeApp.Instance.UiScale;
        Title = "Log";
        var host = shell.GetViewport().GetVisibleRect().Size;
        Size = new Vector2(MathF.Min(760 * s, host.X * 0.8f), MathF.Min(360 * s, host.Y * 0.6f));
        MinPanelSize = new Vector2(300 * s, 160 * s);

        // 툴바(좁으면 여러 줄로 넘어감)
        var bar = new HFlowContainer();
        foreach (var lv in new[] { LogLevel.Info, LogLevel.Warning, LogLevel.Error, LogLevel.Status })
        {
            var cb = new CheckBox { Text = lv.ToString(), ButtonPressed = true, TooltipText = lv == LogLevel.Status ? "Help line messages (operation results)" : $"Show {lv} messages" };
            cb.Toggled += _ => _rebuild = true;
            _filters[lv] = cb;
            bar.AddChild(cb);
        }
        _search = new LineEdit { PlaceholderText = "Filter text…", CustomMinimumSize = new Vector2(150 * s, 0), ClearButtonEnabled = true };
        _search.TextChanged += _ => _rebuild = true;
        bar.AddChild(_search);
        _autoScroll = new CheckBox { Text = "Auto-scroll", ButtonPressed = true };
        bar.AddChild(_autoScroll);
        bar.AddChild(MakeButton("Clear", "Clear the log", () => shell.Actions.Invoke("log.clear")));
        bar.AddChild(MakeButton("Copy", "Copy the shown log lines to the clipboard", () => shell.Actions.Invoke("log.copy")));
        bar.AddChild(MakeButton("Save…", "Save the shown log lines to a text file", () => shell.Actions.Invoke("log.save")));
        Content.AddChild(bar);

        _text = new RichTextLabel
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SelectionEnabled = true, ScrollFollowing = true, BbcodeEnabled = false, ContextMenuEnabled = true,
            AutowrapMode = TextServer.AutowrapMode.Off,
        };
        _text.AddThemeFontSizeOverride("normal_font_size", (int)(12 * s));
        var mono = new SystemFont { FontNames = new[] { "Consolas", "Cascadia Mono", "D2Coding", "Malgun Gothic", "monospace" } };
        _text.AddThemeFontOverride("normal_font", mono);
        var bg = new StyleBoxFlat { BgColor = MayaTheme.Field };
        bg.SetContentMarginAll(4 * s);
        _text.AddThemeStyleboxOverride("normal", bg);
        Content.AddChild(_text);

        _summary = new Label();
        _summary.AddThemeColorOverride("font_color", MayaTheme.TextDim);
        Content.AddChild(_summary);
        _autoScroll.Toggled += on => _text.ScrollFollowing = on;
        VisibilityChanged += () => { if (IsVisibleInTree()) _rebuild = true; };
    }

    private static Button MakeButton(string text, string tip, Action a)
    {
        var b = new Button { Text = text, TooltipText = tip };
        b.Pressed += a;
        return b;
    }

    public override void _Process(double delta)
    {
        base._Process(delta);
        var log = LogCapture.Instance;
        if (log == null || !IsVisibleInTree()) return;
        long ver = log.Version;
        if (!_rebuild && ver == _shownVersion) return;
        long total = log.Total;
        if (_rebuild || total < _shownTotal || ver != _shownVersion && total == _shownTotal)
        {
            // 필터 변경·지우기 등: 전체 다시 그리기
            _text.Clear();
            foreach (var e in log.Snapshot()) if (Passes(e)) Append(e);
            _rebuild = false;
        }
        else foreach (var e in log.Since(_shownTotal)) if (Passes(e)) Append(e);
        _shownTotal = total; _shownVersion = ver;
        var (err, warn) = log.Counts;
        _summary.Text = $"{err} error(s), {warn} warning(s) — keeps the last {LogCapture.MaxEntries} lines";
    }

    private bool Passes(LogEntry e)
    {
        if (!_filters[e.Level].ButtonPressed) return false;
        var q = _search.Text;
        return string.IsNullOrEmpty(q) || e.Text.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void Append(LogEntry e)
    {
        _text.PushColor(TimeColor);
        _text.AddText($"{e.Time:HH:mm:ss.fff} ");
        _text.Pop();
        var c = e.Level switch { LogLevel.Error => ErrorColor, LogLevel.Warning => WarnColor, LogLevel.Status => StatusColor, _ => MayaTheme.Text };
        _text.PushColor(c);
        _text.AddText((e.Level == LogLevel.Info ? "" : $"[{LogEntry.LevelTag(e.Level)}] ") + e.Text + "\n");
        _text.Pop();
    }

    /// <summary>현재 필터를 통과한 줄(복사·저장용).</summary>
    public string FilteredText()
    {
        var log = LogCapture.Instance;
        if (log == null) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var e in log.Snapshot()) if (_filters.Count == 0 || Passes(e)) sb.Append(e.Format()).Append('\n');
        return sb.ToString();
    }

    public void ForceRebuild() => _rebuild = true;
}
