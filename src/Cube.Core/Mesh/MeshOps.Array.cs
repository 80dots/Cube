using System.Numerics;

namespace Cube.Core.Mesh;

/// <summary>Array의 개수 결정 방식(Blender Fit Type). Fit Curve는 커브가 없어 지원하지 않는다.</summary>
public enum ArrayFitType
{
    /// <summary>Count 개수만큼.</summary>
    FixedCount,
    /// <summary>한 단계 이동 거리로 Length를 채울 수 있는 만큼(Count = ⌊Length / 이동 거리⌋ + 1).</summary>
    FitLength,
}

/// <summary>
/// Array 옵션(Blender Array 모디파이어 기준). 단계 변환 = 오프셋 변환(Center 기준 Scale·Rotate) 뒤 Relative + Constant 이동.
/// i번째 복사본은 단계 변환을 i번 적용한 것(오브젝트 로컬 공간, 행벡터 규약 v·Step^i).
/// </summary>
public sealed class ArrayOptions
{
    /// <summary>개수 결정 방식.</summary>
    public ArrayFitType FitType = ArrayFitType.FixedCount;
    /// <summary>FixedCount일 때 전체 개수(원본 포함, 1 이상).</summary>
    public int Count = 2;
    /// <summary>FitLength일 때 채울 길이(로컬 단위).</summary>
    public float Length = 4f;
    /// <summary>Relative Offset 사용 여부.</summary>
    public bool UseRelative = true;
    /// <summary>Relative Offset 배율(원본 경계 상자 크기 × 이 값, 축별). 기본 (1,0,0) = X로 딱 붙여서 나열.</summary>
    public Vector3 Relative = new(1, 0, 0);
    /// <summary>Constant Offset 사용 여부.</summary>
    public bool UseConstant;
    /// <summary>Constant Offset 거리(로컬 단위).</summary>
    public Vector3 Constant = new(0.1f, 0, 0);
    /// <summary>Offset Transform 사용 여부(Blender Object Offset 대용: 복사본마다 회전·스케일이 누적).</summary>
    public bool UseTransform;
    /// <summary>단계당 회전(도, XYZ 오일러 = Transform3 규약).</summary>
    public Vector3 RotateDegrees;
    /// <summary>단계당 스케일.</summary>
    public Vector3 Scale = Vector3.One;
    /// <summary>회전·스케일 중심(로컬). 원 둘레로 나열하려면 원의 중심을 준다.</summary>
    public Vector3 Center;
    /// <summary>이웃한 복사본끼리 가까운 정점을 합칠지(Blender Merge).</summary>
    public bool Merge;
    /// <summary>합칠 거리.</summary>
    public float MergeDistance = 0.01f;
    /// <summary>마지막 복사본과 첫 복사본도 합칠지(Blender First Last; 원형 배열을 닫을 때).</summary>
    public bool MergeFirstLast;
    /// <summary>복사본마다 현재 UV 세트에 더할 UV 오프셋(Blender Offset U/V).</summary>
    public Vector2 UvOffset;
    /// <summary>개수 상한(실수로 큰 값을 넣어 멈추지 않도록).</summary>
    public const int MaxCount = 1000;
}

public static partial class MeshOps
{
    /// <summary>살아 있는 정점의 로컬 경계 상자(정점이 없으면 0 상자).</summary>
    public static (Vector3 min, Vector3 max) Bounds(PolyMesh m)
    {
        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue); bool any = false;
        foreach (var v in m.Verts) { if (!v.Alive) continue; min = Vector3.Min(min, v.Position); max = Vector3.Max(max, v.Position); any = true; }
        return any ? (min, max) : (Vector3.Zero, Vector3.Zero);
    }

    /// <summary>
    /// 한 단계 변환(로컬, 행벡터): Center 기준 Scale → Rotate(Offset Transform을 켰을 때) 뒤 Relative(경계 상자 크기 × 배율) + Constant 이동.
    /// </summary>
    public static Matrix4x4 ArrayStep(PolyMesh m, ArrayOptions o)
    {
        var (min, max) = Bounds(m);
        var move = Vector3.Zero;
        if (o.UseRelative) move += (max - min) * o.Relative;
        if (o.UseConstant) move += o.Constant;
        var step = Matrix4x4.Identity;
        if (o.UseTransform)
        {
            var sr = new Scene.Transform3(Vector3.Zero, o.RotateDegrees, o.Scale).ScaleRotationMatrix();
            step = Matrix4x4.CreateTranslation(-o.Center) * sr * Matrix4x4.CreateTranslation(o.Center);
        }
        return step * Matrix4x4.CreateTranslation(move);
    }

    /// <summary>
    /// 실제 개수(원본 포함). FitLength면 단계 이동 거리로 Length를 채우는 개수 ⌊Length/거리 + ε⌋ + 1(거리 0이면 1),
    /// 아니면 Count. 1 ~ <see cref="ArrayOptions.MaxCount"/>로 제한.
    /// </summary>
    public static int ArrayCount(PolyMesh m, ArrayOptions o)
    {
        int n = o.Count;
        if (o.FitType == ArrayFitType.FitLength)
        {
            float dist = ArrayStep(m, o).Translation.Length();
            n = dist > 1e-6f ? (int)MathF.Floor(o.Length / dist + 1e-4f) + 1 : 1;
        }
        return Math.Clamp(n, 1, ArrayOptions.MaxCount);
    }

    /// <summary>
    /// 메시를 Array로 바꾼다(제자리; 원본 = 복사본 0이라 원래 컴포넌트 ID가 그대로 유지된다). 반환값은 복사본 수.
    /// </summary>
    /// <remarks>
    /// ① 원본 요소 배열을 슬롯째(죽은 슬롯 포함) i번 이어 붙이고 모든 참조 ID를 i×개수만큼 민다 — 코너 UV/노멀, 핀, 노멀 고정,
    ///    UV 세트, 잠긴 노멀, 엣지 하드/심/크리즈가 그대로 복사된다. 위치는 Step^i, 노멀은 그 역전치로 변환한다.
    /// ② 반사 변환(행렬식 &lt; 0)인 복사본은 감김을 맞추려 그 복사본의 면을 뒤집는다.
    /// ③ UV 오프셋은 현재 UV 세트(Uv0와 UvSets[Current])에 i배 더한다.
    /// ④ Merge면 복사본 k의 정점을 복사본 k−1의 가장 가까운(거리 이하) 정점으로 합치고, First Last면 마지막 → 첫 복사본도.
    /// 노멀 재계산은 호출자 몫.
    /// </remarks>
    public static int MakeArray(PolyMesh m, ArrayOptions o)
    {
        int count = ArrayCount(m, o);
        if (count <= 1) return 1;
        var step = ArrayStep(m, o);
        int nv = m.Verts.Count, nh = m.Hes.Count, ne = m.Edges.Count, nf = m.Faces.Count;
        var verts = m.Verts.ToArray(); var hes = m.Hes.ToArray(); var edges = m.Edges.ToArray(); var faces = m.Faces.ToArray();
        var locked = m.LockedNormals.ToArray();
        var setUvs = m.UvSets.Select(s => s.Uvs).ToArray();
        int cur = m.CurrentUvSet;
        static int Sh(int id, int off) => id < 0 ? id : id + off;

        var xf = Matrix4x4.Identity;
        var flipped = new List<int>();
        for (int i = 1; i < count; i++)
        {
            xf *= step;
            Matrix4x4.Invert(xf, out var inv);
            var nxf = Matrix4x4.Transpose(inv);
            int vo = i * nv, ho = i * nh, eo = i * ne, fo = i * nf;
            var uvOff = o.UvOffset * i;
            foreach (var v0 in verts) { var v = v0; v.Position = Vector3.Transform(v.Position, xf); v.HalfEdge = Sh(v.HalfEdge, ho); m.Verts.Add(v); }
            foreach (var h0 in hes)
            {
                var h = h0;
                h.Vertex = Sh(h.Vertex, vo); h.Next = Sh(h.Next, ho); h.Prev = Sh(h.Prev, ho); h.Twin = Sh(h.Twin, ho);
                h.Face = Sh(h.Face, fo); h.Edge = Sh(h.Edge, eo);
                var n = Vector3.TransformNormal(h.Normal, nxf); h.Normal = n.LengthSquared() > 1e-20f ? Vector3.Normalize(n) : h.Normal;
                h.Uv0 += uvOff;
                m.Hes.Add(h);
            }
            foreach (var e0 in edges) { var e = e0; e.He0 = Sh(e.He0, ho); e.He1 = Sh(e.He1, ho); m.Edges.Add(e); }
            foreach (var f0 in faces) { var f = f0; f.HalfEdge = Sh(f.HalfEdge, ho); m.Faces.Add(f); }
            foreach (var kv in locked) { var n = Vector3.TransformNormal(kv.Value, nxf); m.LockedNormals[kv.Key + vo] = n.LengthSquared() > 1e-20f ? Vector3.Normalize(n) : kv.Value; }
            if (Det3(xf) < 0) flipped.Add(i);
        }
        // UV 세트: 하프에지 슬롯별 배열을 같은 순서로 이어 붙인다(현재 세트에만 UV 오프셋)
        for (int s = 0; s < m.UvSets.Count; s++)
        {
            var src = setUvs[s]; var dst = new Vector2[nh * count];
            for (int i = 0; i < count; i++)
                for (int k = 0; k < Math.Min(nh, src.Length); k++) dst[i * nh + k] = src[k] + (s == cur ? o.UvOffset * i : Vector2.Zero);
            m.UvSets[s].Uvs = dst;
        }
        m.BumpTopology();
        // 반사된 복사본은 감김을 뒤집는다(복사본은 아직 서로 떨어진 연결 요소이므로 그 복사본만 뒤집힌다)
        foreach (int i in flipped)
        {
            var fs = Enumerable.Range(i * nf, nf).Where(f => m.Faces[f].Alive).ToList();
            if (fs.Count > 0) ReverseFaces(m, fs);
        }
        if (o.Merge && o.MergeDistance > 0) MergeArrayCopies(m, nv, count, o.MergeDistance, o.MergeFirstLast);
        return count;
    }

    /// <summary>
    /// 이웃한 복사본(k−1, k)끼리 거리 이하인 정점을 앞 복사본 쪽으로 합친다(위치는 앞 복사본 정점 그대로 = Blender).
    /// 복사본 k의 정점 범위는 [k·nv, (k+1)·nv). 공간 해시(셀 = 거리)로 이웃 셀만 찾는다.
    /// </summary>
    private static void MergeArrayCopies(PolyMesh m, int nv, int count, float dist, bool firstLast)
    {
        var map = new Dictionary<int, int>();
        float d2 = dist * dist;
        int Find(int v) { while (map.TryGetValue(v, out int r)) v = r; return v; }
        (int, int, int) Cell(Vector3 p) => ((int)MathF.Floor(p.X / dist), (int)MathF.Floor(p.Y / dist), (int)MathF.Floor(p.Z / dist));

        void MergeInto(int fromCopy, int toCopy)
        {
            // toCopy 정점(이미 합쳐진 것은 대표로)을 해시에 넣고 fromCopy 정점마다 가장 가까운 것을 찾는다
            var grid = new Dictionary<(int, int, int), List<int>>();
            for (int v = toCopy * nv; v < (toCopy + 1) * nv; v++)
            {
                if (!m.Verts[v].Alive) continue;
                int r = Find(v); var c = Cell(m.Verts[r].Position);
                if (!grid.TryGetValue(c, out var l)) grid[c] = l = new List<int>();
                if (!l.Contains(r)) l.Add(r);
            }
            for (int v = fromCopy * nv; v < (fromCopy + 1) * nv; v++)
            {
                if (!m.Verts[v].Alive || map.ContainsKey(v)) continue;
                var p = m.Verts[v].Position; var (cx, cy, cz) = Cell(p);
                int best = -1; float bd = d2;
                for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) for (int dz = -1; dz <= 1; dz++)
                {
                    if (!grid.TryGetValue((cx + dx, cy + dy, cz + dz), out var l)) continue;
                    foreach (int r in l)
                    {
                        if (r == v) continue;
                        float dd = Vector3.DistanceSquared(m.Verts[r].Position, p);
                        if (dd <= bd) { bd = dd; best = r; }
                    }
                }
                if (best >= 0 && Find(best) != v) map[v] = Find(best);
            }
        }

        for (int k = 1; k < count; k++) MergeInto(k, k - 1);
        if (firstLast && count > 2) MergeInto(count - 1, 0);
        if (map.Count == 0) return;
        // 체인을 대표로 정리해 RebuildFacesWithVertexMap에 넘긴다
        var resolved = new Dictionary<int, int>();
        foreach (var k in map.Keys) { int r = Find(k); if (r != k) resolved[k] = r; }
        // 합친 뒤 같은 정점 집합이 되는 면 쌍(맞닿은 두 복사본의 마주 보는 면)은 둘 다 지운다 —
        // 남기면 엣지 하나에 면이 넷인 비매니폴드가 되어 다시 만들 수 없다(Blender는 내부 면을 남기지만 Cube 메시는 매니폴드만 허용)
        var reps = new HashSet<int>(resolved.Values);
        var byKey = new Dictionary<string, List<int>>();
        var tmp = new List<int>();
        for (int f = 0; f < m.Faces.Count; f++)
        {
            if (!m.Faces[f].Alive) continue;
            m.GetFaceVertices(f, tmp);
            // 합쳐지는 정점(키)이나 대표 정점(값)에 닿은 면만 후보(앞 복사본의 마주 보는 면은 대표 정점만 가짐)
            if (!tmp.Any(v => resolved.ContainsKey(v) || reps.Contains(v))) continue;
            var key = string.Join(",", tmp.Select(v => resolved.TryGetValue(v, out int r) ? r : v).OrderBy(v => v));
            if (!byKey.TryGetValue(key, out var l)) byKey[key] = l = new List<int>();
            l.Add(f);
        }
        foreach (var l in byKey.Values) if (l.Count == 2) foreach (int f in l) m.RemoveFace(f, removeIsolated: false);
        RebuildFacesWithVertexMap(m, resolved);
        m.BumpTopology();
    }
}
