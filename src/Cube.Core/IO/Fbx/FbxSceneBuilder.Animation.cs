using System.Numerics;
using Cube.Core.Scene;

namespace Cube.Core.IO.Fbx;

/// <summary>
/// FBX 7.4 애니메이션(Blender/Maya 방식): 클립마다 AnimationStack("AnimStack::이름") + AnimationLayer("AnimLayer::BaseLayer"),
/// 내보낸 모델의 채널마다 AnimationCurveNode("T"/"R"/"S", d|X/d|Y/d|Z) + AnimationCurve 3개(선형 키).
/// 연결: Curve →(OP "d|X")→ CurveNode →(OP "Lcl Translation")→ Model, CurveNode →(OO)→ Layer →(OO)→ Stack.
/// 값: 이동 = cm(UnitScale), 회전 = Lcl Rotation과 같은 XYZ 오일러(도, 축마다 이전 키에 가까운 값으로 언랩), 스케일 그대로.
/// 피벗 베이크: 키 행렬 M = S·R·T(p) → 모델 행렬 T(P)·M·T(−Q) = S·R·T(P·S·R + p − Q) (P = 이 노드 피벗, Q = 부모 쪽 오프셋).
/// </summary>
public sealed partial class FbxSceneBuilder
{
    /// <summary>FBX KTime 단위(1초 = 46186158000 틱).</summary>
    public const long KTimePerSecond = 46186158000L;
    public static long ToKTime(double seconds) => (long)Math.Round(seconds * KTimePerSecond);

    private readonly Dictionary<SceneNode, Vector3> _parentShiftOf = new();
    private readonly HashSet<SceneNode> _bakedWorldNodes = new();
    private List<AnimationClip> ExportedClips { get; } = new();

    // KeyAttrFlags 24836 = 선형 보간 + 상수 탄젠트 모드 기본값; KeyAttrDataFloat는 Maya/Blender가 쓰는 비트 패턴(0,0,255790911,0).
    private const int LinearKeyFlags = 24836;
    private static readonly float[] KeyAttrData = { 0f, 0f, BitConverter.Int32BitsToSingle(255790911), 0f };

    private void BuildAnimations()
    {
        foreach (var clip in _doc.Animations)
        {
            ExportedClips.Add(clip);
            var (start, stop) = ClipRange(clip);
            long stackId = NewId("AnimationStack");
            var stack = new FbxNode("AnimationStack", stackId, FbxNode.Id("AnimStack", clip.Name), "");
            var sp = stack.Add("Properties70");
            sp.Add("P", "LocalStart", "KTime", "Time", "", start);
            sp.Add("P", "LocalStop", "KTime", "Time", "", stop);
            sp.Add("P", "ReferenceStart", "KTime", "Time", "", start);
            sp.Add("P", "ReferenceStop", "KTime", "Time", "", stop);
            _objects.Add(stack);

            long layerId = NewId("AnimationLayer");
            var layer = new FbxNode("AnimationLayer", layerId, FbxNode.Id("AnimLayer", "BaseLayer"), "");
            _objects.Add(layer);
            _connections.Add((layerId, stackId, null));

            foreach (var track in clip.Tracks)
            {
                var node = _doc.Find(track.Node);
                if (node == null || !_modelIds.TryGetValue(node, out long modelId) || track.KeyCount == 0) continue;
                foreach (var (chan, prop, times, values) in BakeTrack(node, track))
                    WriteCurveNode(layerId, modelId, chan, prop, times, values);
            }
        }
    }

    private static (long start, long stop) ClipRange(AnimationClip clip)
    {
        float maxKey = clip.Tracks.SelectMany(t => t.KeyTimes).DefaultIfEmpty(0f).Max();
        float minKey = clip.Tracks.SelectMany(t => t.KeyTimes).DefaultIfEmpty(0f).Min();
        float length = MathF.Max(clip.Length, maxKey);
        return (ToKTime(MathF.Min(0f, minKey)), ToKTime(length));
    }

    /// <summary>트랙 하나를 FBX 채널(T/R/S) 키 배열로 바꾼다. 키가 없는 채널은 내보내지 않는다(임포터는 Lcl 기본값을 쓴다).</summary>
    private IEnumerable<(string chan, string prop, long[] times, Vector3[] values)> BakeTrack(SceneNode n, NodeTrack track)
    {
        bool worldBake = _bakedWorldNodes.Contains(n);
        // 피벗 보정 항: 이동 = xt + P·S·R − Q
        Vector3 P, Q;
        if (_opt.BakePivots) { P = _bakedPivot.GetValueOrDefault(n); Q = _parentShiftOf.GetValueOrDefault(n); }
        else { P = n.Local.Pivot; Q = n.Local.Pivot; }
        var parentWorld = worldBake && n.Parent != null ? RestWorld(n.Parent) : Matrix4x4.Identity;

        // 휴지 값(키가 없는 채널): Evaluate와 같이 피벗을 포함한 로컬 행렬을 분해한다
        var restM = n.Local.ToMatrix();
        if (!Matrix4x4.Decompose(restM, out var rs, out var rq, out var rp)) { rs = n.Local.Scale; rq = n.Local.Rotation; rp = restM.Translation; }

        bool hasP = track.Position.Count > 0, hasR = track.Rotation.Count > 0, hasS = track.Scale.Count > 0;
        bool pivotTerm = P != Vector3.Zero;
        bool needT = worldBake || hasP || (pivotTerm && (hasR || hasS));
        bool needR = worldBake || hasR;
        bool needS = worldBake || hasS;

        float[] Union(bool p, bool r, bool s)
        {
            var set = new SortedSet<float>();
            if (p) foreach (var k in track.Position) set.Add(k.Time);
            if (r) foreach (var k in track.Rotation) set.Add(k.Time);
            if (s) foreach (var k in track.Scale) set.Add(k.Time);
            return set.ToArray();
        }

        (Vector3 s, Quaternion q, Vector3 t) Pose(float time)
        {
            var p = NodeTrack.Sample(track.Position, time, rp);
            var q = NodeTrack.Sample(track.Rotation, time, rq);
            var s = NodeTrack.Sample(track.Scale, time, rs);
            if (worldBake)
            {
                var m = Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(q) * Matrix4x4.CreateTranslation(p) * parentWorld;
                if (Matrix4x4.Decompose(m, out var ws, out var wq, out var wt)) { s = ws; q = wq; p = wt; }
                else p = m.Translation;
            }
            var sr = Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(q);
            var t = p + (pivotTerm ? Vector3.Transform(P, sr) : Vector3.Zero) - Q;
            return (s, q, t);
        }

        if (needT)
        {
            var times = worldBake ? Union(true, true, true) : pivotTerm ? Union(true, hasR, hasS) : Union(true, false, false);
            var vals = times.Select(tm => Pose(tm).t * S).ToArray();
            yield return ("T", "Lcl Translation", times.Select(tm => ToKTime(tm)).ToArray(), vals);
        }
        if (needR)
        {
            var times = worldBake ? Union(true, true, true) : Union(false, true, false);
            var vals = new Vector3[times.Length];
            for (int i = 0; i < times.Length; i++)
            {
                var e = EulerXYZDegrees(Pose(times[i]).q);
                vals[i] = i == 0 ? e : UnwrapEuler(e, vals[i - 1]);
            }
            yield return ("R", "Lcl Rotation", times.Select(tm => ToKTime(tm)).ToArray(), vals);
        }
        if (needS)
        {
            var times = worldBake ? Union(true, true, true) : Union(false, false, true);
            yield return ("S", "Lcl Scaling", times.Select(tm => ToKTime(tm)).ToArray(), times.Select(tm => Pose(tm).s).ToArray());
        }
    }

    /// <summary>
    /// Transform3.QuaternionToEulerXYZDegrees와 같은 규약(R = Rx·Ry·Rz 행벡터, 짐벌락이면 rz = 0)을 double로 계산한다.
    /// ry를 asin 대신 atan2(−M13, √(M11²+M12²))로 구해 ±90° 근처에서도 정밀하다.
    /// </summary>
    public static Vector3 EulerXYZDegrees(Quaternion q)
    {
        double n = Math.Sqrt((double)q.X * q.X + (double)q.Y * q.Y + (double)q.Z * q.Z + (double)q.W * q.W);
        if (n < 1e-12) return Vector3.Zero;
        double x = q.X / n, y = q.Y / n, z = q.Z / n, w = q.W / n;
        double m11 = 1 - 2 * (y * y + z * z), m12 = 2 * (x * y + z * w), m13 = 2 * (x * z - y * w);
        double m22 = 1 - 2 * (x * x + z * z), m23 = 2 * (y * z + x * w);
        double m32 = 2 * (y * z - x * w), m33 = 1 - 2 * (x * x + y * y);
        double cy = Math.Sqrt(m11 * m11 + m12 * m12);
        double ry = Math.Atan2(-m13, cy), rx, rz;
        if (cy > 1e-9) { rx = Math.Atan2(m23, m33); rz = Math.Atan2(m12, m11); }
        else { rx = Math.Atan2(-m32, m22); rz = 0; }
        const double r2d = 180.0 / Math.PI;
        return new Vector3((float)(rx * r2d), (float)(ry * r2d), (float)(rz * r2d));
    }

    /// <summary>축마다 prev에 가장 가까운 등가 각(±360°k)을 고른다.</summary>
    public static Vector3 UnwrapEuler(Vector3 e, Vector3 prev)
    {
        static float U(float v, float p) => v + 360f * MathF.Round((p - v) / 360f);
        return new Vector3(U(e.X, prev.X), U(e.Y, prev.Y), U(e.Z, prev.Z));
    }

    /// <summary>재생 포즈를 무시한 휴지(Local) 월드 행렬.</summary>
    private static Matrix4x4 RestWorld(SceneNode n)
    {
        var m = Matrix4x4.Identity;
        for (var p = n; p != null && !p.IsRoot; p = p.Parent) m *= p.Local.ToMatrix();
        return m;
    }

    private void WriteCurveNode(long layerId, long modelId, string chan, string prop, long[] times, Vector3[] values)
    {
        if (times.Length == 0) return;
        long cnId = NewId("AnimationCurveNode");
        var cn = new FbxNode("AnimationCurveNode", cnId, FbxNode.Id("AnimCurveNode", chan), "");
        var p = cn.Add("Properties70");
        p.Add("P", "d|X", "Number", "", "A", (double)values[0].X);
        p.Add("P", "d|Y", "Number", "", "A", (double)values[0].Y);
        p.Add("P", "d|Z", "Number", "", "A", (double)values[0].Z);
        _objects.Add(cn);
        _connections.Add((cnId, layerId, null));
        _connections.Add((cnId, modelId, prop));

        for (int axis = 0; axis < 3; axis++)
        {
            var vals = values.Select(v => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z).ToArray();
            long cid = NewId("AnimationCurve");
            var c = new FbxNode("AnimationCurve", cid, FbxNode.Id("AnimCurve", ""), "");
            c.Add("Default", (double)vals[0]);
            c.Add("KeyVer", 4009);
            c.Add("KeyTime", times);
            c.Add("KeyValueFloat", vals);
            c.Add("KeyAttrFlags", new[] { LinearKeyFlags });
            c.Add("KeyAttrDataFloat", (float[])KeyAttrData.Clone());
            c.Add("KeyAttrRefCount", new[] { times.Length });
            _objects.Add(c);
            _connections.Add((cid, cnId, axis == 0 ? "d|X" : axis == 1 ? "d|Y" : "d|Z"));
        }
    }

    // ---------------------------------------------------------------- Takes / 시간 설정 / 템플릿

    private FbxNode Takes()
    {
        var takes = new FbxNode("Takes");
        takes.Add("Current", ExportedClips.Count > 0 ? ExportedClips[0].Name : "");
        foreach (var clip in ExportedClips)
        {
            var (start, stop) = ClipRange(clip);
            var take = takes.Add("Take", clip.Name);
            take.Add("FileName", clip.Name + ".tak");
            take.Add("LocalTime", start, stop);
            take.Add("ReferenceTime", start, stop);
        }
        return takes;
    }

    /// <summary>GlobalSettings의 TimeMode/CustomFrameRate/TimeSpan. 클립이 없으면 기존 기본값(24fps, 0~1초).</summary>
    private (int mode, double custom, long start, long stop) TimeSettings()
    {
        if (ExportedClips.Count == 0) return (11, 24.0, 0L, KTimePerSecond);
        long start = 0, stop = 0;
        foreach (var c in ExportedClips) { var (s, e) = ClipRange(c); start = Math.Min(start, s); stop = Math.Max(stop, e); }
        double fps = ExportedClips[0].FrameRate;
        int mode = Math.Round(fps, 3) switch
        {
            120.0 => 1, 100.0 => 2, 60.0 => 3, 50.0 => 4, 48.0 => 5, 30.0 => 6, 25.0 => 10, 24.0 => 11, 1000.0 => 12, 96.0 => 15, 72.0 => 16,
            _ => 14,
        };
        return (mode, fps > 0 ? fps : 24.0, start, stop);
    }

    private static void AnimationTemplate(string type, FbxNode ot)
    {
        switch (type)
        {
            case "AnimationStack":
                {
                    var p = ot.Add("PropertyTemplate", "FbxAnimStack").Add("Properties70");
                    p.Add("P", "Description", "KString", "", "", "");
                    p.Add("P", "LocalStart", "KTime", "Time", "", 0L);
                    p.Add("P", "LocalStop", "KTime", "Time", "", 0L);
                    p.Add("P", "ReferenceStart", "KTime", "Time", "", 0L);
                    p.Add("P", "ReferenceStop", "KTime", "Time", "", 0L);
                    break;
                }
            case "AnimationLayer":
                {
                    var p = ot.Add("PropertyTemplate", "FbxAnimLayer").Add("Properties70");
                    p.Add("P", "Weight", "Number", "", "A", 100.0);
                    p.Add("P", "Mute", "bool", "", "", 0);
                    p.Add("P", "Solo", "bool", "", "", 0);
                    p.Add("P", "Lock", "bool", "", "", 0);
                    p.Add("P", "Color", "ColorRGB", "Color", "", 0.8, 0.8, 0.8);
                    p.Add("P", "BlendMode", "enum", "", "", 0);
                    p.Add("P", "RotationAccumulationMode", "enum", "", "", 0);
                    p.Add("P", "ScaleAccumulationMode", "enum", "", "", 0);
                    p.Add("P", "BlendModeBypass", "ULongLong", "", "", 0L);
                    break;
                }
            case "AnimationCurveNode":
                {
                    var p = ot.Add("PropertyTemplate", "FbxAnimCurveNode").Add("Properties70");
                    p.Add("P", "d", "Compound", "", "");
                    break;
                }
        }
    }
}
