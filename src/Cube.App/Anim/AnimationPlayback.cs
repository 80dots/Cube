using Cube.Core.Commands;
using Cube.Core.Scene;
using Godot;

namespace Cube.App.Anim;

/// <summary>
/// 가져온 애니메이션 클립 재생기. 문서 데이터(Local)는 건드리지 않고 <see cref="SceneNode.Pose"/>만 바꾼다(Undo·저장 대상 아님).
/// 재생/정지/프레임 이동/스크럽, 루프, 속도. 편집 명령이 들어오면 rest 포즈로 돌아간다(편집은 항상 바인드 포즈 기준).
/// </summary>
public partial class AnimationPlayback : Node
{
    public static AnimationPlayback? Current { get; private set; }

    private Document _doc = null!;
    public int ClipIndex { get; private set; } = -1;
    private AnimationClip? _shown;
    private readonly HashSet<AnimationClip> _known = new();
    /// <summary>현재 시간(초).</summary>
    public float Time { get; private set; }
    public bool Playing { get; private set; }
    /// <summary>포즈가 적용된 상태(정지 중이어도 스크럽한 시간의 포즈를 보여 줄 수 있다).</summary>
    public bool Posed { get; private set; }
    public float Speed = 1f;
    public bool Loop = true;

    public event Action? StateChanged;
    /// <summary>시간이 바뀜(재생 중에는 매 프레임).</summary>
    public event Action? TimeChanged;

    public AnimationClip? Clip => ClipIndex >= 0 && ClipIndex < _doc.Animations.Count ? _doc.Animations[ClipIndex] : null;
    public float FrameRate => Clip?.FrameRate ?? 30f;
    public int Frame => (int)MathF.Round(Time * FrameRate);
    public int EndFrame => Clip == null ? 0 : (int)MathF.Round(Clip.Length * Clip.FrameRate);
    /// <summary>재생 범위 시작(첫 키).</summary>
    public float StartTime => Clip?.StartTime ?? 0f;
    public int StartFrame => Clip == null ? 0 : (int)MathF.Round(StartTime * Clip.FrameRate);

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

    public override void _ExitTree()
    {
        if (Current == this) Current = null;
        _doc.Changed -= OnDocChanged;
        _doc.Undo.Changed -= OnUndoChanged;
        AnimationPose.Clear(_doc);
    }

    private void OnDocChanged(DocChange c)
    {
        if (c.Kind == ChangeKind.Reset)
        {
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
            int idx = _shown != null ? _doc.Animations.IndexOf(_shown) : -1;
            var added = _doc.Animations.Where(a => !_known.Contains(a)).ToList();
            if (idx < 0 || (!Posed && !Playing && added.Count > 0))
            {
                // 보던 클립이 없어졌거나 새로 가져온 클립이 있으면(재생 중이 아닐 때) 새 클립의 첫 번째로
                Rest();
                idx = added.Count > 0 ? _doc.Animations.IndexOf(added[0]) : _doc.Animations.Count - 1;
                Time = idx >= 0 ? _doc.Animations[idx].StartTime : 0;
            }
            _known.Clear(); _known.UnionWith(_doc.Animations);
            ClipIndex = idx;
            _shown = Clip;
            StateChanged?.Invoke(); TimeChanged?.Invoke();
        }
    }

    /// <summary>선택 이외의 명령(편집·Undo/Redo)이 들어오면 rest로 돌아간다.</summary>
    private void OnUndoChanged()
    {
        if (!Posed && !Playing) return;
        if (_doc.Undo.LastCommand is SelectionCommand) return;
        Rest();
    }

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

    public void Play()
    {
        if (Clip == null) return;
        if ((Time >= Clip.Length - 1e-4f && !Loop) || Time < StartTime) Time = StartTime;
        Playing = true;
        Apply();
        StateChanged?.Invoke();
    }

    public void Pause() { if (!Playing) return; Playing = false; StateChanged?.Invoke(); }
    public void TogglePlay() { if (Playing) Pause(); else Play(); }

    /// <summary>재생을 멈추고 rest(바인드) 포즈로 돌아간다. 시간은 유지.</summary>
    public void Rest()
    {
        bool changed = Playing || Posed;
        Playing = false; Posed = false;
        AnimationPose.Clear(_doc);
        if (changed) StateChanged?.Invoke();
    }

    public void SetTime(float t)
    {
        if (Clip == null) return;
        Time = Math.Clamp(t, StartTime, Math.Max(StartTime, Clip.Length));
        Apply();
        TimeChanged?.Invoke();
    }

    public void SetFrame(int frame) => SetTime(frame / FrameRate);
    public void StepFrame(int d) { Pause(); SetFrame(Frame + d); }
    public void GoStart() { Pause(); SetTime(StartTime); }
    public void GoEnd() { Pause(); SetTime(Clip?.Length ?? 0); }

    /// <summary>현재 시간 다음(d=1)/이전(d=-1) 키로 이동. 선택 노드가 있으면 그 노드의 키만.</summary>
    public void StepKey(int d)
    {
        var clip = Clip; if (clip == null) return;
        var times = KeyTimes(clip, SelectedNodes()).ToList();
        if (times.Count == 0) return;
        const float eps = 1e-4f;
        float? target = d > 0 ? times.Where(x => x > Time + eps).DefaultIfEmpty(float.NaN).Min() : times.Where(x => x < Time - eps).DefaultIfEmpty(float.NaN).Max();
        if (target is float f && !float.IsNaN(f)) { Pause(); SetTime(f); }
    }

    public HashSet<NodeId> SelectedNodes()
    {
        var set = new HashSet<NodeId>();
        foreach (var id in _doc.Selection.Objects) set.Add(id);
        return set;
    }

    /// <summary>클립의 키 시간(중복 제거, 정렬). nodes가 비어 있으면 모든 트랙.</summary>
    public static IEnumerable<float> KeyTimes(AnimationClip clip, HashSet<NodeId> nodes)
        => clip.Tracks.Where(t => nodes.Count == 0 || nodes.Contains(t.Node)).SelectMany(t => t.KeyTimes)
            .Select(x => MathF.Round(x * 10000f) / 10000f).Distinct().OrderBy(x => x);

    private void Apply()
    {
        var clip = Clip; if (clip == null) return;
        long t0 = AnimPerf.Begin();
        AnimationPose.Apply(_doc, clip, Time);
        AnimPerf.End("notify.total", t0);
        Posed = true;
    }

    public override void _Process(double delta)
    {
        if (!Playing) return;
        var clip = Clip;
        if (clip == null) { Playing = false; StateChanged?.Invoke(); return; }
        float t = Time + (float)delta * Speed;
        float start = clip.StartTime, len = clip.Length - start;
        if (len <= 0f) t = start;
        else if (t > clip.Length)
        {
            if (Loop) t = start + (t - start) % len;
            else { t = clip.Length; Playing = false; StateChanged?.Invoke(); }
        }
        else if (t < start) t = start;
        Time = t;
        Apply();
        long t1 = AnimPerf.Begin();
        TimeChanged?.Invoke();
        AnimPerf.End("timeslider", t1);
        if (AnimPerf.Enabled) AnimPerf.Frame($"clip={clip.Name} quad={UI.Shell.Instance?.Layout.IsQuad}");
    }
}
