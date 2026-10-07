using Cube.App.Anim;
using Cube.Core.Scene;
using Godot;

namespace Cube.App.UI.Anim;

/// <summary>
/// Maya Time Slider: 프레임 눈금, 키 표시(빨강: 선택 노드의 키, 선택이 없으면 모든 트랙), 현재 프레임 커서.
/// 클릭/드래그로 스크럽한다. 아래 줄은 클립 선택과 재생 버튼.
/// </summary>
public partial class TimeSlider : VBoxContainer
{
    private AnimationPlayback _pb = null!;
    private Document _doc = null!;
    private Ruler _ruler = null!;
    private OptionButton _clips = null!, _speed = null!;
    private SpinBox _frame = null!;
    private Label _info = null!;
    private Button _play = null!, _loop = null!, _rest = null!;
    private bool _sync;
    public Action? OpenData;

    private static readonly float[] Speeds = { 0.1f, 0.25f, 0.5f, 1f, 2f };

    public void Setup(AnimationPlayback pb, Document doc)
    {
        _pb = pb; _doc = doc;
        float s = CubeApp.Instance.UiScale;
        AddThemeConstantOverride("separation", (int)(2 * s));
        _ruler = new Ruler { Slider = this, CustomMinimumSize = new Vector2(0, 30 * s), SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = "Time Slider: click or drag to scrub" };
        AddChild(_ruler);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", (int)(4 * s));
        AddChild(row);
        _clips = new OptionButton { CustomMinimumSize = new Vector2(180 * s, 0), TooltipText = "Animation clip (take)", FitToLongestItem = false, ClipText = true, FocusMode = FocusModeEnum.None };
        _clips.ItemSelected += i => { if (!_sync) _pb.SelectClip((int)i); };
        row.AddChild(_clips);
        Button B(string text, string tip, Action a)
        {
            var b = new Button { Text = text, TooltipText = tip, FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(30 * s, 0) };
            b.Pressed += a; row.AddChild(b); return b;
        }
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
        _speed = new OptionButton { TooltipText = "Playback speed", FocusMode = FocusModeEnum.None };
        foreach (var sp in Speeds) _speed.AddItem($"{sp:0.##}x");
        _speed.Selected = Array.IndexOf(Speeds, 1f);
        _speed.ItemSelected += i => _pb.Speed = Speeds[i];
        row.AddChild(_speed);
        row.AddChild(new Label { Text = "Frame" });
        _frame = new SpinBox { MinValue = 0, MaxValue = 100000, Step = 1, CustomMinimumSize = new Vector2(80 * s, 0), TooltipText = "Current frame" };
        _frame.ValueChanged += v => { if (!_sync) { _pb.Pause(); _pb.SetFrame((int)v); } };
        row.AddChild(_frame);
        _info = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true };
        _info.AddThemeColorOverride("font_color", MayaTheme.TextDim);
        row.AddChild(_info);
        _rest = B("Rest Pose", "Stop and return to the bind (rest) pose. Editing, saving and exporting always use the rest pose.", () => _pb.Rest());
        var data = B("Keys…", "Animation Data: clips, tracks and key values", () => OpenData?.Invoke());

        _pb.StateChanged += SyncAll;
        _pb.TimeChanged += SyncTime;
        doc.Selection.Changed += OnSelectionChanged;
        doc.Changed += OnDocChanged;
        SyncAll();
    }

    private void OnSelectionChanged() => _ruler.QueueRedraw();
    private void OnDocChanged(DocChange c) { if (c.Kind is ChangeKind.AnimationsChanged or ChangeKind.Reset) SyncAll(); }

    /// <summary>도킹/떼어 내기로 트리를 옮겨도 구독을 유지하고, 실제로 지워질 때만 해제한다.</summary>
    public override void _Notification(int what)
    {
        if (what != (int)NotificationPredelete) return;
        if (_pb == null) return;
        _pb.StateChanged -= SyncAll; _pb.TimeChanged -= SyncTime;
        _doc.Selection.Changed -= OnSelectionChanged; _doc.Changed -= OnDocChanged;
    }

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

    private void SyncTime()
    {
        _sync = true;
        _frame.MinValue = _pb.StartFrame;
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

    private partial class Ruler : Control
    {
        public TimeSlider Slider = null!;
        private bool _drag;
        private static float Margin => 12 * CubeApp.Instance.UiScale;

        private int _start;
        private float XOf(float frame, int end) => Margin + (Size.X - 2 * Margin) * (end > _start ? (frame - _start) / (end - _start) : 0f);
        private float FrameOf(float x, int end) => end <= _start ? _start : _start + Math.Clamp((x - Margin) / (Size.X - 2 * Margin), 0f, 1f) * (end - _start);

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

        public override void _Draw()
        {
            float s = CubeApp.Instance.UiScale;
            var pb = Slider._pb;
            var font = GetThemeDefaultFont();
            int fs = (int)(11 * s);
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
            int step = 1;
            foreach (int m in new[] { 1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000 }) { step = m; if (m * pxPerFrame >= 50 * s) break; }
            int minor = step >= 10 ? step / 5 : 1;
            if (minor * pxPerFrame < 3 * s) minor = step;
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
            foreach (var t in AnimationPlayback.KeyTimes(clip, any ? sel : new HashSet<NodeId>()))
            {
                float x = XOf(t * clip.FrameRate, end);
                DrawRect(new Rect2(x - 1 * s, 2 * s, 2 * s, Size.Y * 0.45f), keyCol);
            }
            // 현재 프레임 커서 + 번호
            float cx = XOf(pb.Time * clip.FrameRate, end);
            DrawRect(new Rect2(cx - 1.5f * s, 0, 3 * s, Size.Y), new Color(0.85f, 0.85f, 0.85f, 0.85f));
            string label = pb.Frame.ToString();
            var ls = font.GetStringSize(label, HorizontalAlignment.Left, -1, fs);
            float lx = Math.Min(cx + 4 * s, Size.X - ls.X - 2 * s);
            DrawRect(new Rect2(lx - 2 * s, 1 * s, ls.X + 4 * s, ls.Y), new Color(0.15f, 0.15f, 0.15f, 0.85f));
            DrawString(font, new Vector2(lx, 1 * s + ls.Y * 0.8f), label, HorizontalAlignment.Left, -1, fs, Colors.White);
        }
    }
}
