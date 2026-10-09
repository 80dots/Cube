using Cube.Core.Commands;
using Cube.Core.Scene;
using Godot;

namespace Cube.App.Anim;

/// <summary>
/// 가져온 애니메이션 클립 재생기. 문서 데이터(Local)는 건드리지 않고 <see cref="SceneNode.Pose"/>만 바꾼다(Undo·저장 대상 아님).
/// 재생/정지/프레임 이동/스크럽, 루프, 속도. 편집 명령이 들어오면 rest 포즈로 돌아간다(편집은 항상 바인드 포즈 기준).
/// </summary>
/// <remarks>
/// 셸이 노드로 하나 만들어 <see cref="Bind"/>로 문서에 연결한다. 포즈 적용은 <see cref="AnimationPose.Apply"/>가
/// 모든 노드의 Pose를 채운 뒤 PoseChanged를 한 번 발행하고, SceneView가 뷰 트랜스폼·조인트·스킨을 한꺼번에 갱신한다.
/// UI(TimeSlider, AnimationDataWindow, anim.* 액션)는 <see cref="Current"/>로 접근하고 <see cref="StateChanged"/>/<see cref="TimeChanged"/>를 구독한다.
/// 시간 단위는 초이며 프레임 번호는 클립 FrameRate로 반올림해 계산한다.
/// </remarks>
public partial class AnimationPlayback : Node
{
    /// <summary>현재 살아 있는 재생기 인스턴스(Bind에서 설정, 트리에서 빠지면 null). 액션·UI의 전역 접근점.</summary>
    public static AnimationPlayback? Current { get; private set; }

    /// <summary>연결된 문서. 클립 목록(Animations)과 선택, Undo 스택을 읽는다.</summary>
    private Document _doc = null!;
    /// <summary>선택된 클립 인덱스(Document.Animations 기준, 없으면 -1).</summary>
    public int ClipIndex { get; private set; } = -1;
    /// <summary>
    /// 지금 보고 있는 클립 객체. AnimationsChanged 때 인덱스가 아니라 객체로 같은 클립을 다시 찾기 위해 기억한다.
    /// </summary>
    private AnimationClip? _shown;
    /// <summary>이미 알고 있는 클립 집합. AnimationsChanged 때 새로 추가된(가져온) 클립을 구분하는 데 쓴다.</summary>
    private readonly HashSet<AnimationClip> _known = new();
    /// <summary>현재 시간(초).</summary>
    public float Time { get; private set; }
    /// <summary>재생 중인지(_Process에서 시간이 흐름).</summary>
    public bool Playing { get; private set; }
    /// <summary>포즈가 적용된 상태(정지 중이어도 스크럽한 시간의 포즈를 보여 줄 수 있다).</summary>
    public bool Posed { get; private set; }
    /// <summary>재생 속도 배율(1 = 실시간). 음수는 지원하지 않는다(시작 시간에서 멈춤).</summary>
    public float Speed = 1f;
    /// <summary>끝에 도달하면 시작(첫 키)으로 돌아가 반복할지.</summary>
    public bool Loop = true;

    /// <summary>재생/정지, 클립 선택, 포즈 적용 여부 등 상태가 바뀜(버튼 표시 갱신용).</summary>
    public event Action? StateChanged;
    /// <summary>시간이 바뀜(재생 중에는 매 프레임).</summary>
    public event Action? TimeChanged;

    /// <summary>현재 선택된 클립(인덱스가 범위를 벗어나면 null).</summary>
    public AnimationClip? Clip => ClipIndex >= 0 && ClipIndex < _doc.Animations.Count ? _doc.Animations[ClipIndex] : null;
    /// <summary>현재 클립의 초당 프레임 수(클립이 없으면 30).</summary>
    public float FrameRate => Clip?.FrameRate ?? 30f;
    /// <summary>현재 시간을 프레임 번호로 반올림한 값.</summary>
    public int Frame => (int)MathF.Round(Time * FrameRate);
    /// <summary>클립 끝 프레임(Length × FrameRate 반올림, 클립 없으면 0).</summary>
    public int EndFrame => Clip == null ? 0 : (int)MathF.Round(Clip.Length * Clip.FrameRate);
    /// <summary>재생 범위 시작(첫 키).</summary>
    public float StartTime => Clip?.StartTime ?? 0f;
    /// <summary>재생 범위 시작 프레임(StartTime × FrameRate 반올림).</summary>
    public int StartFrame => Clip == null ? 0 : (int)MathF.Round(StartTime * Clip.FrameRate);

    /// <summary>
    /// 문서에 연결한다. Current로 등록하고 문서 변경·Undo 변경을 구독하며, 클립이 있으면 첫 클립을 고른다.
    /// </summary>
    public void Bind(Document doc)
    {
        Current = this;
        _doc = doc;
        doc.Changed += OnDocChanged;
        doc.Undo.Changed += OnUndoChanged;
        if (doc.Animations.Count > 0) ClipIndex = 0;
        _shown = Clip;
        _known.UnionWith(doc.Animations);
    }

    /// <summary>트리에서 빠질 때 구독을 해제하고 남아 있는 재생 포즈를 지워 rest 포즈로 되돌린다.</summary>
    public override void _ExitTree()
    {
        if (Current == this) Current = null;
        _doc.Changed -= OnDocChanged;
        _doc.Undo.Changed -= OnUndoChanged;
        AnimationPose.Clear(_doc);
    }

    /// <summary>
    /// 문서 변경 처리. Reset(새 문서/열기)이면 상태를 초기화하고 첫 클립을 고른다.
    /// AnimationsChanged(가져오기·삭제·브리지 교체)면 보던 클립을 유지하거나 새 클립으로 바꾼다.
    /// </summary>
    private void OnDocChanged(DocChange c)
    {
        if (c.Kind == ChangeKind.Reset)
        {
            // 문서가 통째로 바뀜: 재생·포즈 해제, 첫 클립 선택, 시간을 첫 키로, 알려진 클립 집합 재구성
            Playing = false; Posed = false;
            ClipIndex = _doc.Animations.Count > 0 ? 0 : -1;
            _shown = Clip;
            Time = StartTime;
            _known.Clear(); _known.UnionWith(_doc.Animations);
            StateChanged?.Invoke(); TimeChanged?.Invoke();
        }
        else if (c.Kind == ChangeKind.AnimationsChanged)
        {
            // 보던 클립이 남아 있으면 그대로, 없어졌으면(삭제·브리지 교체) rest로 돌아가 마지막(가장 최근에 가져온) 클립을 고른다
            // 보던 클립의 새 인덱스와 이번에 새로 들어온 클립 목록을 구한다
            int idx = _shown != null ? _doc.Animations.IndexOf(_shown) : -1;
            var added = _doc.Animations.Where(a => !_known.Contains(a)).ToList();
            if (idx < 0 || (!Posed && !Playing && added.Count > 0))
            {
                // 보던 클립이 없어졌거나 새로 가져온 클립이 있으면(재생 중이 아닐 때) 새 클립의 첫 번째로
                Rest();
                idx = added.Count > 0 ? _doc.Animations.IndexOf(added[0]) : _doc.Animations.Count - 1;
                Time = idx >= 0 ? _doc.Animations[idx].StartTime : 0;
            }
            // 알려진 집합을 갱신하고 선택 인덱스·보던 클립을 확정한 뒤 UI에 알림
            _known.Clear(); _known.UnionWith(_doc.Animations);
            ClipIndex = idx;
            _shown = Clip;
            StateChanged?.Invoke(); TimeChanged?.Invoke();
        }
    }

    /// <summary>선택 이외의 명령(편집·Undo/Redo)이 들어오면 rest로 돌아간다.</summary>
    /// <remarks>재생 포즈 상태에서 편집하면 rest 기준이 아닌 포즈 기준으로 편집되는 문제를 막기 위함. SelectionCommand는 예외.</remarks>
    private void OnUndoChanged()
    {
        if (!Posed && !Playing) return;
        if (_doc.Undo.LastCommand is SelectionCommand) return;
        Rest();
    }

    /// <summary>
    /// 클립을 고른다(-1 = 선택 해제). 시간을 그 클립의 첫 키로 옮기고, 이전에 포즈를 보이던 중이면 새 클립 포즈를 바로 적용한다.
    /// 범위를 벗어난 인덱스는 무시한다.
    /// </summary>
    public void SelectClip(int index)
    {
        if (index < -1 || index >= _doc.Animations.Count) return;
        bool wasPosed = Posed;
        ClipIndex = index;
        _shown = Clip;
        Time = StartTime;
        if (Clip == null) Rest();
        else if (wasPosed || Playing) Apply();
        StateChanged?.Invoke(); TimeChanged?.Invoke();
    }

    /// <summary>
    /// 재생을 시작한다. 끝에 있고 루프가 아니거나 시간이 범위 앞이면 첫 키로 되감은 뒤 포즈를 적용한다.
    /// </summary>
    public void Play()
    {
        if (Clip == null) return;
        if ((Time >= Clip.Length - 1e-4f && !Loop) || Time < StartTime) Time = StartTime;
        Playing = true;
        Apply();
        StateChanged?.Invoke();
    }

    /// <summary>재생을 멈춘다(포즈는 유지 — 멈춘 시간의 포즈가 그대로 보인다).</summary>
    public void Pause() { if (!Playing) return; Playing = false; StateChanged?.Invoke(); }
    /// <summary>재생 중이면 정지, 아니면 재생(Alt+V).</summary>
    public void TogglePlay() { if (Playing) Pause(); else Play(); }

    /// <summary>재생을 멈추고 rest(바인드) 포즈로 돌아간다. 시간은 유지.</summary>
    public void Rest()
    /// <remarks>Pose를 모두 지우므로 뷰가 문서의 Local 트랜스폼으로 돌아간다. 상태가 실제로 바뀐 경우에만 StateChanged.</remarks>
    {
        bool changed = Playing || Posed;
        Playing = false; Posed = false;
        AnimationPose.Clear(_doc);
        if (changed) StateChanged?.Invoke();
    }

    /// <summary>
    /// 시간을 [StartTime, Length]로 잘라 설정하고 그 시간의 포즈를 적용한다(스크럽). 재생 상태는 바꾸지 않는다.
    /// </summary>
    /// <param name="t">초 단위 시간.</param>
    public void SetTime(float t)
    {
        if (Clip == null) return;
        Time = Math.Clamp(t, StartTime, Math.Max(StartTime, Clip.Length));
        Apply();
        TimeChanged?.Invoke();
    }

    /// <summary>프레임 번호로 시간을 설정한다(frame / FrameRate 초).</summary>
    public void SetFrame(int frame) => SetTime(frame / FrameRate);
    /// <summary>정지 후 d프레임 이동(Alt+, / Alt+.).</summary>
    public void StepFrame(int d) { Pause(); SetFrame(Frame + d); }
    /// <summary>정지 후 재생 범위 시작(첫 키)으로.</summary>
    public void GoStart() { Pause(); SetTime(StartTime); }
    /// <summary>정지 후 클립 끝으로.</summary>
    public void GoEnd() { Pause(); SetTime(Clip?.Length ?? 0); }

    /// <summary>현재 시간 다음(d=1)/이전(d=-1) 키로 이동. 선택 노드가 있으면 그 노드의 키만(선택 노드에 키가 없으면 전체 트랙).</summary>
    public void StepKey(int d)
    {
        // 대상 키 시간을 모아(선택 노드 기준) 현재 시간보다 eps 이상 뒤/앞인 가장 가까운 키를 찾는다
        var clip = Clip; if (clip == null) return;
        var times = KeyTimes(clip, SelectedNodes()).ToList();
        // 선택 노드에 키가 없으면(가져온 직후 트랙 없는 루트가 선택된 경우 등) 모든 트랙의 키로 이동한다
        if (times.Count == 0) times = KeyTimes(clip, new HashSet<NodeId>()).ToList();
        if (times.Count == 0) return;
        const float eps = 1e-4f;
        float? target = d > 0 ? times.Where(x => x > Time + eps).DefaultIfEmpty(float.NaN).Min() : times.Where(x => x < Time - eps).DefaultIfEmpty(float.NaN).Max();
        // 찾았으면(NaN이 아니면) 정지하고 그 시간으로 이동
        if (target is float f && !float.IsNaN(f)) { Pause(); SetTime(f); }
    }

    /// <summary>현재 선택된 오브젝트 노드 ID 집합(키 표시·키 이동의 필터). 비어 있으면 전체 트랙 대상.</summary>
    public HashSet<NodeId> SelectedNodes()
    {
        var set = new HashSet<NodeId>();
        foreach (var id in _doc.Selection.Objects) set.Add(id);
        return set;
    }

    /// <summary>클립의 키 시간(중복 제거, 정렬). nodes가 비어 있으면 모든 트랙.</summary>
    public static IEnumerable<float> KeyTimes(AnimationClip clip, HashSet<NodeId> nodes)
        // 1e-4초 단위로 반올림해 부동소수 오차로 생긴 거의 같은 키를 하나로 합친다
        => clip.Tracks.Where(t => nodes.Count == 0 || nodes.Contains(t.Node)).SelectMany(t => t.KeyTimes)
            .Select(x => MathF.Round(x * 10000f) / 10000f).Distinct().OrderBy(x => x);

    /// <summary>
    /// 현재 클립·시간의 포즈를 문서 노드의 Pose에 적용하고 Posed를 켠다. AnimPerf로 알림 비용을 측정한다.
    /// </summary>
    private void Apply()
    {
        var clip = Clip; if (clip == null) return;
        long t0 = AnimPerf.Begin();
        AnimationPose.Apply(_doc, clip, Time);
        AnimPerf.End("notify.total", t0);
        Posed = true;
    }

    /// <summary>
    /// 매 프레임: 재생 중이면 delta × Speed만큼 시간을 진행하고 포즈를 적용한다.
    /// 끝을 넘으면 루프면 [StartTime, Length) 범위로 감고, 아니면 끝에서 멈춘다.
    /// </summary>
    public override void _Process(double delta)
    {
        if (!Playing) return;
        // 재생 중 클립이 사라졌으면 정지
        var clip = Clip;
        if (clip == null) { Playing = false; StateChanged?.Invoke(); return; }
        // 시간 진행 및 범위 처리(길이 0 클립은 시작에 고정)
        float t = Time + (float)delta * Speed;
        float start = clip.StartTime, len = clip.Length - start;
        if (len <= 0f) t = start;
        else if (t > clip.Length)
        {
            if (Loop) t = start + (t - start) % len;
            else { t = clip.Length; Playing = false; StateChanged?.Invoke(); }
        }
        else if (t < start) t = start;
        // 포즈 적용 후 시간 변경 통지(타임 슬라이더 갱신), 성능 계측
        Time = t;
        Apply();
        long t1 = AnimPerf.Begin();
        TimeChanged?.Invoke();
        AnimPerf.End("timeslider", t1);
        if (AnimPerf.Enabled) AnimPerf.Frame($"clip={clip.Name} quad={UI.Shell.Instance?.Layout.IsQuad}");
    }
}
