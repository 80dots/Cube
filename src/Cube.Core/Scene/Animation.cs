using System.Numerics;
using Cube.Core.Commands;

namespace Cube.Core.Scene;

/// <summary>키 하나(시간 초, 값).</summary>
/// <typeparam name="T">값 타입(위치/스케일 = Vector3, 회전 = Quaternion).</typeparam>
public readonly record struct AnimKey<T>(float Time, T Value);

/// <summary>
/// 노드 하나의 TRS 키프레임(로컬, 피벗 없는 행렬 의미: M = S·R·T(Position)). 키가 없는 채널은 휴지(rest) 값을 쓴다.
/// 보간은 선형(위치/스케일)과 구면 선형(회전). Cube는 애니메이션을 만들거나 편집하지 않고, 가져온 키를 보고·재생하고·내보내기만 한다.
/// </summary>
/// <remarks>각 키 목록은 시간 오름차순이어야 한다(이진 탐색으로 구간을 찾음).</remarks>
public sealed class NodeTrack
{
    /// <summary>이 트랙이 움직이는 노드.</summary>
    public NodeId Node;
    /// <summary>가져온 원래 이름(디버그/재연결용).</summary>
    public string NodeName = "";
    /// <summary>위치 키(부모 공간, m).</summary>
    public List<AnimKey<Vector3>> Position { get; } = new();
    /// <summary>회전 키(쿼터니언).</summary>
    public List<AnimKey<Quaternion>> Rotation { get; } = new();
    /// <summary>스케일 키.</summary>
    public List<AnimKey<Vector3>> Scale { get; } = new();

    /// <summary>세 채널 키 수의 합.</summary>
    public int KeyCount => Position.Count + Rotation.Count + Scale.Count;
    /// <summary>모든 채널의 키 시간(중복 포함, 정렬 안 됨). 길이 계산·타임 슬라이더 눈금에 쓴다.</summary>
    public IEnumerable<float> KeyTimes => Position.Select(k => k.Time).Concat(Rotation.Select(k => k.Time)).Concat(Scale.Select(k => k.Time));

    /// <summary>벡터 채널을 t초에서 샘플링한다. 범위 밖은 끝 키 값으로 고정(clamp), 키가 없으면 fallback.</summary>
    public static Vector3 Sample(List<AnimKey<Vector3>> keys, float t, Vector3 fallback)
    {
        if (keys.Count == 0) return fallback;
        if (t <= keys[0].Time) return keys[0].Value;
        if (t >= keys[^1].Time) return keys[^1].Value;
        // t를 포함하는 구간 [i, i+1]을 찾아 구간 내 비율 f로 선형 보간.
        int i = FindSegment(keys.Count, j => keys[j].Time, t);
        var a = keys[i]; var b = keys[i + 1];
        float f = b.Time > a.Time ? (t - a.Time) / (b.Time - a.Time) : 0f;
        return Vector3.Lerp(a.Value, b.Value, f);
    }

    /// <summary>회전 채널을 t초에서 샘플링한다(Slerp 후 정규화). 범위 밖은 끝 키 값, 키가 없으면 fallback.</summary>
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

    /// <summary>time(i) &lt;= t 인 가장 큰 i(0 ~ count-2)를 이진 탐색으로 찾는다.</summary>
    private static int FindSegment(int count, Func<int, float> time, float t)
    {
        int lo = 0, hi = count - 2;
        // 위쪽 중간값을 써서 lo = mid 갱신 시 무한 루프를 피한다.
        while (lo < hi) { int mid = (lo + hi + 1) / 2; if (time(mid) <= t) lo = mid; else hi = mid - 1; }
        return lo;
    }

    /// <summary>t초의 로컬 트랜스폼. rest는 키가 없는 채널의 값과 피벗을 준다.</summary>
    /// <remarks>
    /// rest 행렬을 S/R/T로 분해해 키 없는 채널의 기본값으로 쓰고, 샘플링한 S·R·T로 행렬을 만든다.
    /// 피벗이 없으면 TRS를 그대로 Transform3로(회전은 XYZ 오일러 도), 피벗이 있으면 행렬에서 피벗을 유지하며 복원한다.
    /// </remarks>
    public Transform3 Evaluate(float t, Transform3 rest)
    {
        // rest 분해(피벗 포함 행렬 기준) → 채널별 기본값.
        var restM = rest.ToMatrix();
        Matrix4x4.Decompose(restM, out var rs, out var rq, out var rp);
        var p = Sample(Position, t, rp);
        var q = Sample(Rotation, t, rq);
        var s = Sample(Scale, t, rs);
        // 행벡터 규약: S → R → T 순으로 곱한 로컬 행렬.
        var m = Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(q) * Matrix4x4.CreateTranslation(p);
        if (rest.Pivot == Vector3.Zero)
            return new Transform3(p, Transform3.QuaternionToEulerXYZDegrees(q), s);
        return Transform3.FromMatrix(m, rest.Pivot);
    }
}

/// <summary>애니메이션 클립(가져온 테이크/액션 하나).</summary>
public sealed class AnimationClip
{
    /// <summary>클립 이름(glTF 애니메이션/FBX 테이크 이름).</summary>
    public string Name = "Take";
    /// <summary>길이(초). 키가 없으면 0.</summary>
    public float Length;
    /// <summary>표시·내보내기용 프레임 속도(가져올 때 추정, 기본 30).</summary>
    public float FrameRate = 30f;
    /// <summary>반복 재생 여부.</summary>
    public bool Loop;
    /// <summary>노드별 트랙.</summary>
    public List<NodeTrack> Tracks { get; } = new();

    /// <summary>모든 트랙의 키 수 합.</summary>
    public int KeyCount => Tracks.Sum(t => t.KeyCount);

    /// <summary>첫 키 시간(초, 0 이하이면 0). 테이크가 0초가 아닌 곳에서 시작하는 파일(FBX 테이크 범위)의 재생 범위 시작.</summary>
    public float StartTime
    {
        get
        {
            // 각 채널의 첫 키(정렬되어 있으므로 [0])만 보면 최소값을 얻는다.
            float m = float.MaxValue;
            foreach (var t in Tracks)
            {
                if (t.Position.Count > 0) m = MathF.Min(m, t.Position[0].Time);
                if (t.Rotation.Count > 0) m = MathF.Min(m, t.Rotation[0].Time);
                if (t.Scale.Count > 0) m = MathF.Min(m, t.Scale[0].Time);
            }
            // 키가 없거나 음수면 0, 길이를 넘지 않게 제한.
            return m == float.MaxValue || m < 0 ? 0 : MathF.Min(m, Length);
        }
    }

    /// <summary>모든 키 시간으로 길이를 다시 계산한다.</summary>
    public void UpdateLength() => Length = Tracks.SelectMany(t => t.KeyTimes).DefaultIfEmpty(0f).Max();

    /// <summary>트랙과 키 목록까지 복사한 깊은 복사본(키는 값 타입이라 AddRange로 충분).</summary>
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
/// <remarks>Do = remove 목록 제거 후 add 목록 추가, Undo = 그 반대. 클립 객체 참조로 동작하며 중복 추가는 막는다.</remarks>
public sealed class SetAnimationsCommand : ICommand
{
    /// <summary>추가할 클립과 제거할 클립.</summary>
    private readonly List<AnimationClip> _add; private readonly List<AnimationClip> _remove;
    /// <summary>명령 이름.</summary>
    public string Name { get; }
    /// <summary>이름, 추가할 클립, (선택) 제거할 클립을 받는다.</summary>
    public SetAnimationsCommand(string name, IEnumerable<AnimationClip> add, IEnumerable<AnimationClip>? remove = null) { Name = name; _add = add.ToList(); _remove = remove?.ToList() ?? new(); }
    /// <summary>제거 → 추가 후 AnimationsChanged를 통지한다.</summary>
    public void Do(Document doc)
    {
        foreach (var c in _remove) doc.Animations.Remove(c);
        foreach (var c in _add) if (!doc.Animations.Contains(c)) doc.Animations.Add(c);
        doc.Notify(new DocChange(ChangeKind.AnimationsChanged, NodeId.None));
    }
    /// <summary>추가한 클립을 빼고 제거한 클립을 되돌린다.</summary>
    public void Undo(Document doc)
    {
        foreach (var c in _add) doc.Animations.Remove(c);
        foreach (var c in _remove) if (!doc.Animations.Contains(c)) doc.Animations.Add(c);
        doc.Notify(new DocChange(ChangeKind.AnimationsChanged, NodeId.None));
    }
}
