using System.Numerics;
using Cube.Core.Commands;

namespace Cube.Core.Scene;

/// <summary>키 하나(시간 초, 값).</summary>
public readonly record struct AnimKey<T>(float Time, T Value);

/// <summary>
/// 노드 하나의 TRS 키프레임(로컬, 피벗 없는 행렬 의미: M = S·R·T(Position)). 키가 없는 채널은 휴지(rest) 값을 쓴다.
/// 보간은 선형(위치/스케일)과 구면 선형(회전). Cube는 애니메이션을 만들거나 편집하지 않고, 가져온 키를 보고·재생하고·내보내기만 한다.
/// </summary>
public sealed class NodeTrack
{
    public NodeId Node;
    /// <summary>가져온 원래 이름(디버그/재연결용).</summary>
    public string NodeName = "";
    public List<AnimKey<Vector3>> Position { get; } = new();
    public List<AnimKey<Quaternion>> Rotation { get; } = new();
    public List<AnimKey<Vector3>> Scale { get; } = new();

    public int KeyCount => Position.Count + Rotation.Count + Scale.Count;
    public IEnumerable<float> KeyTimes => Position.Select(k => k.Time).Concat(Rotation.Select(k => k.Time)).Concat(Scale.Select(k => k.Time));

    public static Vector3 Sample(List<AnimKey<Vector3>> keys, float t, Vector3 fallback)
    {
        if (keys.Count == 0) return fallback;
        if (t <= keys[0].Time) return keys[0].Value;
        if (t >= keys[^1].Time) return keys[^1].Value;
        int i = FindSegment(keys.Count, j => keys[j].Time, t);
        var a = keys[i]; var b = keys[i + 1];
        float f = b.Time > a.Time ? (t - a.Time) / (b.Time - a.Time) : 0f;
        return Vector3.Lerp(a.Value, b.Value, f);
    }

    public static Quaternion Sample(List<AnimKey<Quaternion>> keys, float t, Quaternion fallback)
    {
        if (keys.Count == 0) return fallback;
        if (t <= keys[0].Time) return keys[0].Value;
        if (t >= keys[^1].Time) return keys[^1].Value;
        int i = FindSegment(keys.Count, j => keys[j].Time, t);
        var a = keys[i]; var b = keys[i + 1];
        float f = b.Time > a.Time ? (t - a.Time) / (b.Time - a.Time) : 0f;
        return Quaternion.Normalize(Quaternion.Slerp(a.Value, b.Value, f));
    }

    private static int FindSegment(int count, Func<int, float> time, float t)
    {
        int lo = 0, hi = count - 2;
        while (lo < hi) { int mid = (lo + hi + 1) / 2; if (time(mid) <= t) lo = mid; else hi = mid - 1; }
        return lo;
    }

    /// <summary>t초의 로컬 트랜스폼. rest는 키가 없는 채널의 값과 피벗을 준다.</summary>
    public Transform3 Evaluate(float t, Transform3 rest)
    {
        var restM = rest.ToMatrix();
        Matrix4x4.Decompose(restM, out var rs, out var rq, out var rp);
        var p = Sample(Position, t, rp);
        var q = Sample(Rotation, t, rq);
        var s = Sample(Scale, t, rs);
        var m = Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(q) * Matrix4x4.CreateTranslation(p);
        if (rest.Pivot == Vector3.Zero)
            return new Transform3(p, Transform3.QuaternionToEulerXYZDegrees(q), s);
        return Transform3.FromMatrix(m, rest.Pivot);
    }
}

/// <summary>애니메이션 클립(가져온 테이크/액션 하나).</summary>
public sealed class AnimationClip
{
    public string Name = "Take";
    /// <summary>길이(초). 키가 없으면 0.</summary>
    public float Length;
    /// <summary>표시·내보내기용 프레임 속도(가져올 때 추정, 기본 30).</summary>
    public float FrameRate = 30f;
    public bool Loop;
    public List<NodeTrack> Tracks { get; } = new();

    public int KeyCount => Tracks.Sum(t => t.KeyCount);

    /// <summary>첫 키 시간(초, 0 이하이면 0). 테이크가 0초가 아닌 곳에서 시작하는 파일(FBX 테이크 범위)의 재생 범위 시작.</summary>
    public float StartTime
    {
        get
        {
            float m = float.MaxValue;
            foreach (var t in Tracks)
            {
                if (t.Position.Count > 0) m = MathF.Min(m, t.Position[0].Time);
                if (t.Rotation.Count > 0) m = MathF.Min(m, t.Rotation[0].Time);
                if (t.Scale.Count > 0) m = MathF.Min(m, t.Scale[0].Time);
            }
            return m == float.MaxValue || m < 0 ? 0 : MathF.Min(m, Length);
        }
    }

    /// <summary>모든 키 시간으로 길이를 다시 계산한다.</summary>
    public void UpdateLength() => Length = Tracks.SelectMany(t => t.KeyTimes).DefaultIfEmpty(0f).Max();

    public AnimationClip Clone()
    {
        var c = new AnimationClip { Name = Name, Length = Length, FrameRate = FrameRate, Loop = Loop };
        foreach (var t in Tracks)
        {
            var n = new NodeTrack { Node = t.Node, NodeName = t.NodeName };
            n.Position.AddRange(t.Position); n.Rotation.AddRange(t.Rotation); n.Scale.AddRange(t.Scale);
            c.Tracks.Add(n);
        }
        return c;
    }
}

/// <summary>애니메이션 클립을 문서에 추가/제거(가져오기·삭제의 Undo 단위).</summary>
public sealed class SetAnimationsCommand : ICommand
{
    private readonly List<AnimationClip> _add; private readonly List<AnimationClip> _remove;
    public string Name { get; }
    public SetAnimationsCommand(string name, IEnumerable<AnimationClip> add, IEnumerable<AnimationClip>? remove = null) { Name = name; _add = add.ToList(); _remove = remove?.ToList() ?? new(); }
    public void Do(Document doc)
    {
        foreach (var c in _remove) doc.Animations.Remove(c);
        foreach (var c in _add) if (!doc.Animations.Contains(c)) doc.Animations.Add(c);
        doc.Notify(new DocChange(ChangeKind.AnimationsChanged, NodeId.None));
    }
    public void Undo(Document doc)
    {
        foreach (var c in _add) doc.Animations.Remove(c);
        foreach (var c in _remove) if (!doc.Animations.Contains(c)) doc.Animations.Add(c);
        doc.Notify(new DocChange(ChangeKind.AnimationsChanged, NodeId.None));
    }
}
