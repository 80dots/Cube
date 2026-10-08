using Cube.App.Anim;
using Cube.Core.Scene;
using Godot;

namespace Cube.App.UI.Anim;

/// <summary>
/// Maya Time Slider: 프레임 눈금, 키 표시(빨강: 선택 노드의 키, 선택이 없으면 모든 트랙), 현재 프레임 커서.
/// 클릭/드래그로 스크럽한다. 아래 줄은 클립 선택과 재생 버튼.
/// 구조: 위 = <see cref="Ruler"/>(눈금자, 그리기·스크럽 담당), 아래 = 컨트롤 줄(클립 선택, 처음/이전 키/이전 프레임/재생/다음 프레임/다음 키/끝,
/// Loop, 속도, 프레임 칸, 정보 라벨, Rest Pose, Keys… 버튼). 모든 상태는 <see cref="AnimationPlayback"/>이 갖고 이 위젯은 보여 주기와 요청만 한다.
/// 셸이 클립이 있을 때만 HelpLine 위에 표시한다.
/// </summary>
public partial class TimeSlider : VBoxContainer
{
    /// <summary>재생 상태(클립·시간·재생 여부·루프·속도). 이 위젯의 모든 표시 값의 출처.</summary>
    private AnimationPlayback _pb = null!;
    /// <summary>문서(클립 목록, 선택 변경 구독).</summary>
    private Document _doc = null!;
    /// <summary>위쪽 눈금자 컨트롤(내부 클래스).</summary>
    private Ruler _ruler = null!;
    /// <summary>_clips: 클립(테이크) 선택 드롭다운(인덱스 = Document.Animations 인덱스). _speed: 재생 속도 배율 선택(<see cref="Speeds"/>).</summary>
    private OptionButton _clips = null!, _speed = null!;
    /// <summary>현재 프레임 숫자 칸. 범위는 클립의 시작~끝 프레임으로 매번 맞춘다.</summary>
    private SpinBox _frame = null!;
    /// <summary>프레임 범위·fps·시간·트랙/키 수·rest 포즈 여부 요약 라벨.</summary>
    private Label _info = null!;
    /// <summary>_play: 재생/일시정지(재생 중이면 "||"), _loop: 루프 토글, _rest: rest 포즈로 복귀(이미 rest이고 멈춰 있으면 비활성).</summary>
    private Button _play = null!, _loop = null!, _rest = null!;
    /// <summary>프로그램이 컨트롤 값을 맞추는 중 표시. true면 컨트롤의 변경 이벤트를 사용자 조작으로 처리하지 않는다.</summary>
    private bool _sync;
    /// <summary>"Keys…" 버튼이 부르는 콜백(셸이 Animation Data 창을 열도록 연결).</summary>
    public Action? OpenData;

    /// <summary>재생 속도 드롭다운 항목(배율). 기본 선택은 1x.</summary>
    private static readonly float[] Speeds = { 0.1f, 0.25f, 0.5f, 1f, 2f };

    /// <summary>
    /// 위젯을 구성하고 재생기(StateChanged/TimeChanged)·문서(Changed/Selection.Changed)를 구독한다.
    /// 버튼 툴팁의 단축키는 <c>ShellAnimActions</c>의 핫키와 같은 동작을 가리킨다.
    /// </summary>
    /// <param name="pb">재생 상태.</param>
    /// <param name="doc">문서.</param>
    public void Setup(AnimationPlayback pb, Document doc)
    {
        _pb = pb; _doc = doc;
        float s = CubeApp.Instance.UiScale;
        AddThemeConstantOverride("separation", (int)(2 * s));
        // 눈금자: 높이 30px(배율 적용), 가로로 꽉 채움.
        _ruler = new Ruler { Slider = this, CustomMinimumSize = new Vector2(0, 30 * s), SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = "Time Slider: click or drag to scrub" };
        AddChild(_ruler);

        // 아래 컨트롤 줄.
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", (int)(4 * s));
        AddChild(row);
        _clips = new OptionButton { CustomMinimumSize = new Vector2(180 * s, 0), TooltipText = "Animation clip (take)", FitToLongestItem = false, ClipText = true, FocusMode = FocusModeEnum.None };
        // 사용자가 클립을 바꾸면 재생기에 위임(SyncAll 중의 프로그램 변경은 무시).
        _clips.ItemSelected += i => { if (!_sync) _pb.SelectClip((int)i); };
        row.AddChild(_clips);
        // 컨트롤 줄에 버튼을 추가하는 로컬 함수(포커스를 받지 않아 단축키 입력을 빼앗지 않음).
        Button B(string text, string tip, Action a)
        {
            var b = new Button { Text = text, TooltipText = tip, FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(30 * s, 0) };
            b.Pressed += a; row.AddChild(b); return b;
        }
        // 이동/재생 버튼들.
        B("|◀", "Go to start (Shift+Alt+V)", () => _pb.GoStart());
        B("◀◆", "Previous key (Comma)", () => _pb.StepKey(-1));
        B("◀|", "Previous frame (Alt+Comma)", () => _pb.StepFrame(-1));
        _play = B("▶", "Play / Pause (Alt+V)", () => _pb.TogglePlay());
        B("|▶", "Next frame (Alt+Period)", () => _pb.StepFrame(1));
        B("◆▶", "Next key (Period)", () => _pb.StepKey(1));
        B("▶|", "Go to end", () => _pb.GoEnd());
        _loop = new Button { Text = "Loop", ToggleMode = true, ButtonPressed = _pb.Loop, FocusMode = FocusModeEnum.None, TooltipText = "Loop playback" };
        _loop.Toggled += on => _pb.Loop = on;
        row.AddChild(_loop);
        // 재생 속도: 고른 배율을 재생기에 바로 반영.
        _speed = new OptionButton { TooltipText = "Playback speed", FocusMode = FocusModeEnum.None };
        foreach (var sp in Speeds) _speed.AddItem($"{sp:0.##}x");
        _speed.Selected = Array.IndexOf(Speeds, 1f);
        _speed.ItemSelected += i => _pb.Speed = Speeds[i];
        row.AddChild(_speed);
        row.AddChild(new Label { Text = "Frame" });
        _frame = new SpinBox { MinValue = 0, MaxValue = 100000, Step = 1, CustomMinimumSize = new Vector2(80 * s, 0), TooltipText = "Current frame" };
        // 프레임을 직접 입력하면 재생을 멈추고 그 프레임으로 이동.
        _frame.ValueChanged += v => { if (!_sync) { _pb.Pause(); _pb.SetFrame((int)v); } };
        row.AddChild(_frame);
        _info = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true };
        _info.AddThemeColorOverride("font_color", MayaTheme.TextDim);
        row.AddChild(_info);
        _rest = B("Rest Pose", "Stop and return to the bind (rest) pose. Editing, saving and exporting always use the rest pose.", () => _pb.Rest());
        var data = B("Keys…", "Animation Data: clips, tracks and key values", () => OpenData?.Invoke());

        // 상태 변경(클립·재생·루프) = 전체 동기화, 시간 변경 = 프레임/정보/커서만 동기화.
        _pb.StateChanged += SyncAll;
        _pb.TimeChanged += SyncTime;
        doc.Selection.Changed += OnSelectionChanged;
        doc.Changed += OnDocChanged;
        SyncAll();
    }

    /// <summary>선택이 바뀌면 키 표시(선택 노드의 키 강조)가 달라지므로 눈금자만 다시 그린다.</summary>
    private void OnSelectionChanged() => _ruler.QueueRedraw();
    /// <summary>애니메이션 목록 변경·문서 리셋 때 클립 드롭다운 등 전체를 다시 맞춘다.</summary>
    private void OnDocChanged(DocChange c) { if (c.Kind is ChangeKind.AnimationsChanged or ChangeKind.Reset) SyncAll(); }

    /// <summary>도킹/떼어 내기로 트리를 옮겨도 구독을 유지하고, 실제로 지워질 때만 해제한다.</summary>
    public override void _Notification(int what)
    {
        if (what != (int)NotificationPredelete) return;
        if (_pb == null) return;
        _pb.StateChanged -= SyncAll; _pb.TimeChanged -= SyncTime;
        _doc.Selection.Changed -= OnSelectionChanged; _doc.Changed -= OnDocChanged;
    }

    /// <summary>
    /// 클립 드롭다운·재생 버튼 글자·루프 토글을 재생기 상태에 맞추고 <see cref="SyncTime"/>을 부른다.
    /// _sync를 켜 두어 드롭다운 Clear/AddItem이 클립 선택 요청을 일으키지 않게 한다.
    /// </summary>
    private void SyncAll()
    {
        _sync = true;
        _clips.Clear();
        for (int i = 0; i < _doc.Animations.Count; i++) _clips.AddItem(_doc.Animations[i].Name, i);
        if (_pb.ClipIndex >= 0) _clips.Selected = _pb.ClipIndex;
        _play.Text = _pb.Playing ? "||" : "▶";
        _loop.SetPressedNoSignal(_pb.Loop);
        _sync = false;
        SyncTime();
    }

    /// <summary>
    /// 시간 관련 표시를 맞춘다: 프레임 칸 범위·값(신호 없이), 정보 라벨, Rest Pose 버튼 활성 여부, 눈금자 다시 그리기.
    /// 재생 중에는 매 프레임 불리므로 가벼운 작업만 한다.
    /// </summary>
    private void SyncTime()
    {
        _sync = true;
        _frame.MinValue = _pb.StartFrame;
        // 최소 1프레임 폭을 보장해 SpinBox 범위가 비지 않게 한다.
        _frame.MaxValue = Math.Max(_pb.StartFrame + 1, _pb.EndFrame);
        _frame.SetValueNoSignal(_pb.Frame);
        var c = _pb.Clip;
        _info.Text = c == null ? "No animation"
            : $"{_pb.StartFrame}–{_pb.EndFrame}   {c.FrameRate:0.##} fps   {_pb.Time:0.000}s / {c.Length:0.000}s   {c.Tracks.Count} track(s), {c.KeyCount} key(s)" + (_pb.Posed ? "" : "   [rest pose]");
        _rest.Disabled = !_pb.Posed && !_pb.Playing;
        _sync = false;
        _ruler.QueueRedraw();
    }

    // ---------------------------------------------------------------- 눈금자

    /// <summary>
    /// 타임 슬라이더의 눈금자. 좌우 <see cref="Margin"/>을 뺀 폭에 [시작 프레임, 끝 프레임]을 선형으로 매핑한다.
    /// 좌클릭/드래그 = 스크럽(재생을 멈추고 가장 가까운 정수 프레임으로 이동), 그리기 = 배경·눈금·키 막대·현재 프레임 커서.
    /// 키 시간 목록은 클립/선택/키 수가 바뀔 때만 다시 계산해 캐시한다.
    /// </summary>
    private partial class Ruler : Control
    {
        /// <summary>소유 TimeSlider(재생기 접근용). 생성 시 객체 이니셜라이저로 넣는다.</summary>
        public TimeSlider Slider = null!;
        /// <summary>좌버튼을 누른 채 드래그 중인지.</summary>
        private bool _drag;
        /// <summary>좌우 여백(px, UI 배율 적용). 이 안쪽이 프레임 축이다.</summary>
        private static float Margin => 12 * CubeApp.Instance.UiScale;

        /// <summary>현재 클립의 시작 프레임(입력/그리기 때마다 재생기에서 다시 읽는다).</summary>
        private int _start;
        /// <summary>캐시된 키 시간(초) 배열. 중복 제거·정렬된 값.</summary>
        private float[] _keys = Array.Empty<float>();
        /// <summary>캐시를 만든 클립(참조 비교로 변경 감지).</summary>
        private object? _keysClip;
        /// <summary>캐시를 만든 선택 노드 ID 목록 문자열(정렬 후 쉼표로 이음; 선택 키를 쓰지 않으면 빈 문자열).</summary>
        private string _keysSel = "";
        /// <summary>캐시를 만든 시점의 클립 키 수(-1 = 아직 없음). 클립 내용이 바뀐 경우를 감지한다.</summary>
        private int _keysCount = -1;
        /// <summary>프레임 → 눈금자 로컬 X(px). 끝 프레임이 시작 이하이면 여백 위치(0 비율).</summary>
        private float XOf(float frame, int end) => Margin + (Size.X - 2 * Margin) * (end > _start ? (frame - _start) / (end - _start) : 0f);
        /// <summary>눈금자 로컬 X(px) → 프레임(실수). 범위를 벗어난 X는 [시작, 끝]으로 고정된다.</summary>
        private float FrameOf(float x, int end) => end <= _start ? _start : _start + Math.Clamp((x - Margin) / (Size.X - 2 * Margin), 0f, 1f) * (end - _start);

        /// <summary>
        /// 스크럽 입력. 좌버튼 누름 = 재생 일시정지 + 클릭 위치 프레임으로 이동하고 드래그 시작, 뗌 = 드래그 종료.
        /// 드래그 중 이동 = 계속 프레임 이동. 처리한 이벤트는 AcceptEvent로 소비한다.
        /// </summary>
        public override void _GuiInput(InputEvent e)
        {
            var pb = Slider._pb;
            _start = pb.StartFrame;
            if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } mb)
            {
                _drag = mb.Pressed;
                if (mb.Pressed) { pb.Pause(); pb.SetFrame((int)MathF.Round(FrameOf(mb.Position.X, pb.EndFrame))); }
                AcceptEvent();
            }
            else if (e is InputEventMouseMotion mm && _drag)
            {
                pb.SetFrame((int)MathF.Round(FrameOf(mm.Position.X, pb.EndFrame)));
                AcceptEvent();
            }
        }

        /// <summary>
        /// 눈금자 그리기: 배경 → (클립 없으면 안내 문구) → 프레임 눈금/라벨 → 키 막대 → 현재 프레임 커서와 번호 상자.
        /// </summary>
        public override void _Draw()
        {
            float s = CubeApp.Instance.UiScale;
            var pb = Slider._pb;
            var font = GetThemeDefaultFont();
            int fs = (int)(11 * s);
            // 배경.
            DrawRect(new Rect2(Vector2.Zero, Size), MayaTheme.Field);
            var clip = pb.Clip;
            int end = pb.EndFrame;
            _start = pb.StartFrame;
            if (clip == null || end <= 0)
            {
                DrawString(font, new Vector2(Margin, Size.Y * 0.65f), "No animation (import a glTF/FBX file that contains animation)", HorizontalAlignment.Left, -1, fs, MayaTheme.TextDim);
                return;
            }
            float w = Size.X - 2 * Margin;
            // 눈금 간격: 라벨이 50px 이상 떨어지도록 1/2/5×10^n 프레임
            float pxPerFrame = w / Math.Max(1, end - _start);
            // 라벨 간격이 50px 이상이 되는 가장 작은 단계를 고른다.
            int step = 1;
            foreach (int m in new[] { 1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000 }) { step = m; if (m * pxPerFrame >= 50 * s) break; }
            // 보조 눈금: 큰 단계면 1/5 간격, 3px보다 촘촘하면 주 눈금만.
            int minor = step >= 10 ? step / 5 : 1;
            if (minor * pxPerFrame < 3 * s) minor = step;
            // 시작 프레임 이상의 첫 보조 눈금 배수부터 끝까지.
            for (int f = (_start + minor - 1) / minor * minor; f <= end; f += minor)
            {
                float x = XOf(f, end);
                bool major = f % step == 0;
                DrawLine(new Vector2(x, Size.Y), new Vector2(x, Size.Y - (major ? 10 : 5) * s), MayaTheme.TextDim, 1);
                if (major) DrawString(font, new Vector2(x + 2 * s, Size.Y - 12 * s), f.ToString(), HorizontalAlignment.Left, -1, fs, MayaTheme.TextDim);
            }
            // 키: 선택 노드의 키(빨강), 선택에 키가 없으면 모든 트랙(어두운 빨강)
            var sel = pb.SelectedNodes();
            bool any = sel.Count > 0 && clip.Tracks.Any(t => sel.Contains(t.Node));
            var keyCol = any ? new Color(0.95f, 0.2f, 0.2f) : new Color(0.65f, 0.25f, 0.25f);
            string selKey = any ? string.Join(",", sel.Select(n => n.Value).OrderBy(v => v)) : "";
            // 클립 참조·선택·키 수 중 하나라도 바뀌면 캐시를 다시 만든다.
            if (!ReferenceEquals(_keysClip, clip) || _keysSel != selKey || _keysCount != clip.KeyCount)
            {
                // 키가 수만 개인 클립(BrainStem 등)에서 매 프레임 중복 제거·정렬하지 않도록 클립·선택이 바뀔 때만 계산
                _keys = AnimationPlayback.KeyTimes(clip, any ? sel : new HashSet<NodeId>()).ToArray();
                _keysClip = clip; _keysSel = selKey; _keysCount = clip.KeyCount;
            }
            // 키 시간(초) × fps = 프레임 → X. 1px 안에 겹치는 키는 하나만 그린다.
            float lastX = float.NegativeInfinity;
            foreach (var t in _keys)
            {
                float x = XOf(t * clip.FrameRate, end);
                if (x - lastX < 1f) continue; // 같은 픽셀 열은 한 번만
                lastX = x;
                DrawRect(new Rect2(x - 1 * s, 2 * s, 2 * s, Size.Y * 0.45f), keyCol);
            }
            // 현재 프레임 커서 + 번호
            float cx = XOf(pb.Time * clip.FrameRate, end);
            DrawRect(new Rect2(cx - 1.5f * s, 0, 3 * s, Size.Y), new Color(0.85f, 0.85f, 0.85f, 0.85f));
            string label = pb.Frame.ToString();
            var ls = font.GetStringSize(label, HorizontalAlignment.Left, -1, fs);
            // 번호 상자가 오른쪽 끝을 넘지 않게 왼쪽으로 붙인다.
            float lx = Math.Min(cx + 4 * s, Size.X - ls.X - 2 * s);
            DrawRect(new Rect2(lx - 2 * s, 1 * s, ls.X + 4 * s, ls.Y), new Color(0.15f, 0.15f, 0.15f, 0.85f));
            DrawString(font, new Vector2(lx, 1 * s + ls.Y * 0.8f), label, HorizontalAlignment.Left, -1, fs, Colors.White);
        }
    }
}
