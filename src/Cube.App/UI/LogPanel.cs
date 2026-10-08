using Godot;

namespace Cube.App.UI;

/// <summary>
/// 로그 패널(Windows → Log, 헬프 라인 오른쪽 Log 버튼): <see cref="LogCapture"/>가 모은 로그를 시간·수준별 색으로 보여 준다.
/// 수준 필터(Info/Warning/Error/Status), 검색, 자동 스크롤, 지우기, 클립보드 복사, 파일 저장. 복사·저장은 현재 필터를 따른다.
/// </summary>
/// <remarks>
/// 갱신은 <see cref="_Process"/>에서 폴링한다: LogCapture의 Version/Total을 마지막으로 보여 준 값과 비교해
/// 새 줄만 덧붙이거나(증분), 필터 변경·지우기·링 버퍼 회전처럼 이전 내용이 무효가 되면 전체를 다시 그린다.
/// 패널이 보이지 않을 때는 아무 작업도 하지 않는다.
/// </remarks>
public partial class LogPanel : FloatingPanel
{
    /// <summary>로그 본문(선택·복사 가능, 자동 줄바꿈 없음, 고정폭 글꼴).</summary>
    private RichTextLabel _text = null!;
    /// <summary>본문 필터 텍스트(대소문자 무시 부분 일치).</summary>
    private LineEdit _search = null!;
    /// <summary>새 줄이 오면 맨 아래로 따라가는지.</summary>
    private CheckBox _autoScroll = null!;
    /// <summary>수준별 표시 체크박스.</summary>
    private readonly Dictionary<LogLevel, CheckBox> _filters = new();
    /// <summary>하단 요약(오류/경고 개수, 보관 줄 수).</summary>
    private Label _summary = null!;
    /// <summary>마지막으로 표시한 시점의 LogCapture 누적 줄 수와 버전(-1 = 아직 없음). 증분 갱신 판단용.</summary>
    private long _shownTotal = -1, _shownVersion = -1;
    /// <summary>다음 프레임에 전체를 다시 그려야 하는지(필터/검색 변경, 다시 보일 때).</summary>
    private bool _rebuild = true;
    /// <summary>버튼이 액션(log.clear/copy/save)을 호출할 셸.</summary>
    private Shell _shell = null!;

    /// <summary>수준별 글자색(오류 빨강, 경고 노랑, 상태 파랑)과 타임스탬프 회색.</summary>
    private static readonly Color ErrorColor = new(1f, 0.42f, 0.38f), WarnColor = new(1f, 0.8f, 0.35f), StatusColor = new(0.55f, 0.75f, 1f), TimeColor = new(0.55f, 0.55f, 0.55f);

    /// <summary>툴바·본문·요약을 만든다. 초기 크기는 화면의 80%×60%를 넘지 않게 정한다.</summary>
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
        // 수준 필터 체크박스: 바꾸면 전체 다시 그리기 예약.
        foreach (var lv in new[] { LogLevel.Info, LogLevel.Warning, LogLevel.Error, LogLevel.Status })
        {
            var cb = new CheckBox { Text = lv.ToString(), ButtonPressed = true, TooltipText = lv == LogLevel.Status ? "Help line messages (operation results)" : $"Show {lv} messages" };
            cb.Toggled += _ => _rebuild = true;
            _filters[lv] = cb;
            bar.AddChild(cb);
        }
        // 검색, 자동 스크롤, 그리고 액션 레지스트리를 통하는 지우기/복사/저장 버튼.
        _search = new LineEdit { PlaceholderText = "Filter text…", CustomMinimumSize = new Vector2(150 * s, 0), ClearButtonEnabled = true };
        _search.TextChanged += _ => _rebuild = true;
        bar.AddChild(_search);
        _autoScroll = new CheckBox { Text = "Auto-scroll", ButtonPressed = true };
        bar.AddChild(_autoScroll);
        bar.AddChild(MakeButton("Clear", "Clear the log", () => shell.Actions.Invoke("log.clear")));
        bar.AddChild(MakeButton("Copy", "Copy the shown log lines to the clipboard", () => shell.Actions.Invoke("log.copy")));
        bar.AddChild(MakeButton("Save…", "Save the shown log lines to a text file", () => shell.Actions.Invoke("log.save")));
        Content.AddChild(bar);

        // 본문: 고정폭 시스템 글꼴(한글 대체 포함), 입력칸 색 배경. BBCode는 끄고 PushColor로 색을 입힌다(로그 텍스트의 [ ]가 해석되지 않도록).
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

        // 요약 줄. 다시 보일 때는 숨겨진 동안 쌓인 변화를 반영하려고 전체 다시 그리기.
        _summary = new Label();
        _summary.AddThemeColorOverride("font_color", MayaTheme.TextDim);
        Content.AddChild(_summary);
        _autoScroll.Toggled += on => _text.ScrollFollowing = on;
        VisibilityChanged += () => { if (IsVisibleInTree()) _rebuild = true; };
    }

    /// <summary>텍스트·툴팁·클릭 동작을 가진 버튼을 만든다.</summary>
    private static Button MakeButton(string text, string tip, Action a)
    {
        var b = new Button { Text = text, TooltipText = tip };
        b.Pressed += a;
        return b;
    }

    /// <summary>
    /// 매 프레임 로그 변화를 반영한다. 버전이 같고 다시 그릴 필요가 없으면 즉시 반환.
    /// 누적 줄 수가 줄었거나(지우기) 줄 수는 같은데 버전만 바뀌었으면(오래된 줄이 밀려남) 전체를 다시 그리고,
    /// 아니면 마지막 표시 이후 추가된 줄만 덧붙인다.
    /// </summary>
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
        // 새로 추가된 줄만 덧붙이는 증분 경로.
        else foreach (var e in log.Since(_shownTotal)) if (Passes(e)) Append(e);
        _shownTotal = total; _shownVersion = ver;
        // 하단 요약 갱신.
        var (err, warn) = log.Counts;
        _summary.Text = $"{err} error(s), {warn} warning(s) — keeps the last {LogCapture.MaxEntries} lines";
    }

    /// <summary>수준 체크박스와 검색어를 모두 통과하는 줄인지.</summary>
    private bool Passes(LogEntry e)
    {
        if (!_filters[e.Level].ButtonPressed) return false;
        var q = _search.Text;
        return string.IsNullOrEmpty(q) || e.Text.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>한 줄을 본문에 추가한다: 회색 타임스탬프(시:분:초.밀리초) + 수준 색 본문. Info가 아니면 [수준] 태그를 붙인다.</summary>
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
    /// <remarks>패널 UI가 아직 만들어지지 않았으면(필터 없음) 모든 줄을 포함한다.</remarks>
    public string FilteredText()
    {
        var log = LogCapture.Instance;
        if (log == null) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var e in log.Snapshot()) if (_filters.Count == 0 || Passes(e)) sb.Append(e.Format()).Append('\n');
        return sb.ToString();
    }

    /// <summary>다음 프레임에 전체를 다시 그리게 한다(외부에서 로그를 지웠을 때 등).</summary>
    public void ForceRebuild() => _rebuild = true;
}
