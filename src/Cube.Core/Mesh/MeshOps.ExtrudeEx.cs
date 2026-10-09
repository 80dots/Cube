using System.Numerics;

namespace Cube.Core.Mesh;

/// <summary>Blender Extrude 종류(면 선택).</summary>
public enum ExtrudeType
{
    /// <summary>Extrude Region: 선택 영역의 경계 루프만 옆면이 되고 안쪽은 함께 이동한다.</summary>
    Region,
    /// <summary>Extrude Individual: 면마다 따로, 자기 법선 방향으로.</summary>
    IndividualFaces,
}

/// <summary>오프셋 방향.</summary>
public enum ExtrudeDirection
{
    /// <summary>영역 평균 법선(Blender Region 기본). 엣지는 면 평면에서 바깥쪽.</summary>
    AverageNormal,
    /// <summary>면마다 자기 법선(두께; 여러 방향 면을 함께 밀어도 모양 유지). 엣지는 면 평면에서 바깥쪽.</summary>
    FaceNormals,
    // X, Y, Z: 오브젝트 로컬 축 방향(고정, FixedDir).
    X, Y, Z,
    /// <summary>호출자가 준 방향(뷰 방향 등; 로컬 공간).</summary>
    Custom,
}

/// <summary>Blender Extrude 옵션(Extrude Region / Individual / Edges / Repeat).</summary>
/// <remarks>불변 record. App의 Extrude 옵션 창/이력 파라미터(Offset/Steps/Type/Direction/Flip)가 이 값을 만든다.</remarks>
public sealed record ExtrudeOptions
{
    /// <summary>Region(영역 단위) 또는 IndividualFaces(면마다). 엣지 Extrude에서는 쓰지 않는다.</summary>
    public ExtrudeType Type { get; init; } = ExtrudeType.Region;
    /// <summary>한 단계 이동 거리.</summary>
    public float Offset { get; init; }
    /// <summary>오프셋 방향(<see cref="ExtrudeDirection"/>). Offset이 0이면 의미 없다.</summary>
    public ExtrudeDirection Direction { get; init; } = ExtrudeDirection.AverageNormal;
    /// <summary>Direction = Custom일 때 방향(정규화 불필요).</summary>
    public Vector3 CustomDirection { get; init; } = Vector3.UnitY;
    /// <summary>Extrude Repeat: 이 횟수만큼 반복(매번 Offset만큼).</summary>
    public int Steps { get; init; } = 1;
    /// <summary>결과 형상의 법선을 뒤집는다(하프에지 일관성 때문에 결과가 속한 연결 요소 전체를 뒤집는다).</summary>
    public bool FlipNormals { get; init; }
}

/// <summary>Blender식 Extrude(면/엣지, 방향·반복·법선 뒤집기 옵션). 기본 위상 연산은 <see cref="ExtrudeFaces"/>/<see cref="ExtrudeEdges(PolyMesh, IEnumerable{int}, out List{int})"/>를 재사용한다.</summary>
public static partial class MeshOps
{
    /// <summary>
    /// Blender식 면 Extrude. 반환값은 마지막 단계의 캡 면(선택할 면).
    /// Region 규칙(연결 영역마다):
    /// ① 경계 루프가 있으면 그 엣지들이 옆면이 되고 영역 안쪽은 캡으로 이동(복제하지 않음).
    /// ② 경계 엣지가 모두 메시의 열린 테두리(면 하나에만 속함)면 선택 면을 복제해 원래 자리에 뒤집어 남긴다 → 사각형이 직육면체가 된다.
    /// ③ 경계가 없는 닫힌 볼륨이면 연결 없이 복제만 한다(새 껍질).
    /// </summary>
    /// <remarks>
    /// Steps번 반복하며 매 단계의 캡을 다음 단계 입력으로 쓴다(Extrude Repeat). Steps는 1..1000으로 클램프.
    /// IndividualFaces: 면마다 ExtrudeFaces 후 그 면 법선(또는 고정 방향)으로 캡 정점을 Offset만큼 이동.
    /// Region: 연결 영역마다 평균 법선을 구하고 ExtrudeRegionComponent 후, FaceNormals면 정점별 마이터 방향(RegionOffsetDirections),
    /// 아니면 평균 법선/고정 방향으로 이동. FlipNormals면 마지막에 결과 요소를 뒤집고 캡 ID를 다시 찾는다.
    /// </remarks>
    public static List<int> Extrude(PolyMesh m, IEnumerable<int> faceIds, ExtrudeOptions o)
    {
        var current = AliveFaces(m, faceIds).ToList();
        int steps = Math.Clamp(o.Steps, 1, 1000);
        for (int s = 0; s < steps && current.Count > 0; s++)
        {
            var next = new List<int>();
            if (o.Type == ExtrudeType.IndividualFaces)
                foreach (int f in current.ToList())
                {
                    if (!m.Faces[f].Alive) continue;
                    // 면을 지우기 전에 법선을 구해 둔다(ExtrudeFaces가 면을 재생성하므로)
                    var n = FaceUnitNormal(m, f);
                    var caps = ExtrudeFaces(m, new[] { f });
                    next.AddRange(caps);
                    var dir = o.Direction is ExtrudeDirection.AverageNormal or ExtrudeDirection.FaceNormals ? n : FixedDir(o);
                    MoveFaceVerts(m, caps, _ => dir * o.Offset);
                }
            else
                foreach (var comp in RegionComponents(m, current))
                {
                    // 영역 평균 법선(면적 가중). 퇴화하면 +Y
                    var avg = Vector3.Zero; foreach (int f in comp) avg += MeshNormals.FaceNormalUnnormalized(m, f);
                    avg = avg.LengthSquared() > 1e-20f ? Vector3.Normalize(avg) : Vector3.UnitY;
                    var caps = ExtrudeRegionComponent(m, comp);
                    next.AddRange(caps);
                    if (o.Offset == 0f) continue;
                    if (o.Direction == ExtrudeDirection.FaceNormals)
                    {
                        var dirs = RegionOffsetDirections(m, caps);
                        MoveFaceVerts(m, caps, v => dirs.TryGetValue(v, out var d) ? d * o.Offset : avg * o.Offset);
                    }
                    else
                    {
                        var dir = o.Direction == ExtrudeDirection.AverageNormal ? avg : FixedDir(o);
                        MoveFaceVerts(m, caps, _ => dir * o.Offset);
                    }
                }
            current = next;
        }
        if (o.FlipNormals && current.Count > 0) current = ReverseKeepingIds(m, current, current);
        m.BumpTopology();
        return current.Where(f => f < m.FaceCount && m.Faces[f].Alive).ToList();
    }

    /// <summary>
    /// Blender식 엣지 Extrude(열린 테두리 엣지 → 면). 오프셋 방향은 AverageNormal/FaceNormals면 면 평면에서 바깥쪽(테두리를 넓힘), 축/Custom이면 그 방향.
    /// 반환값은 새 면, newEdges는 마지막 단계의 바깥 엣지(선택할 엣지).
    /// </summary>
    /// <remarks>
    /// 매 단계: ① 현재 테두리 엣지마다 면 평면 안에서 엣지에 수직이고 면 중심에서 멀어지는 방향을 구해 양끝 정점에 누적
    /// ② 기본 ExtrudeEdges로 쿼드를 붙이고 새 바깥 엣지(created)를 얻음 ③ Offset이 있으면 새 바깥 정점을 이동
    /// (평균/면 법선 모드는 같은 위치의 원래 테두리 정점의 누적 방향을 정규화해 사용) ④ created를 다음 단계 입력으로.
    /// FlipNormals면 면을 뒤집은 뒤 바깥 엣지를 정점 쌍으로 다시 찾는다.
    /// </remarks>
    public static List<int> ExtrudeEdges(PolyMesh m, IEnumerable<int> edgeIds, ExtrudeOptions o, out List<int> newEdges)
    {
        var all = new List<int>();
        newEdges = AliveEdges(m, edgeIds).ToList();
        int steps = Math.Clamp(o.Steps, 1, 1000);
        for (int s = 0; s < steps && newEdges.Count > 0; s++)
        {
            // 바깥쪽 방향(면 평면 안에서 엣지에 수직, 면에서 멀어지는 쪽)을 미리 구한다
            var outward = new Dictionary<int, Vector3>();
            foreach (int e in newEdges)
            {
                // 테두리 엣지만 처리. He0는 면 쪽 하프에지(테두리 엣지는 He1이 없음)
                if (!m.IsBoundaryEdge(e)) continue;
                var h = m.Hes[m.Edges[e].He0];
                int a = h.Vertex, b = m.Hes[h.Next].Vertex;
                var pa = m.Verts[a].Position; var pb = m.Verts[b].Position;
                var n = FaceUnitNormal(m, h.Face);
                // 엣지 방향 × 면 법선 = 면 평면 안의 수직 방향. 면 중심 반대쪽을 향하도록 부호를 맞춘다.
                var d = Vector3.Cross(pb - pa, n);
                if (Vector3.Dot(d, (pa + pb) * 0.5f - m.FaceCentroid(h.Face)) < 0) d = -d;
                if (d.LengthSquared() < 1e-20f) continue;
                d = Vector3.Normalize(d);
                outward[a] = outward.GetValueOrDefault(a) + d; outward[b] = outward.GetValueOrDefault(b) + d;
            }
            var faces = ExtrudeEdges(m, newEdges, out var created);
            all.AddRange(faces);
            if (o.Offset != 0f)
            {
                // moved: 이어진 엣지가 공유하는 정점을 두 번 움직이지 않도록
                var moved = new HashSet<int>();
                foreach (int e in created)
                {
                    var (a2, b2) = m.EdgeVertices(e);
                    foreach (int v in new[] { a2, b2 })
                    {
                        if (!moved.Add(v)) continue;
                        Vector3 dir;
                        if (o.Direction is ExtrudeDirection.AverageNormal or ExtrudeDirection.FaceNormals)
                        {
                            // 복제 정점의 원래 정점 = 같은 위치의 테두리 정점
                            var src = outward.Keys.FirstOrDefault(k => Vector3.DistanceSquared(m.Verts[k].Position, m.Verts[v].Position) < 1e-12f, -1);
                            dir = src >= 0 && outward[src].LengthSquared() > 1e-20f ? Vector3.Normalize(outward[src]) : Vector3.Zero;
                        }
                        else dir = FixedDir(o);
                        var vt = m.Verts[v]; vt.Position += dir * o.Offset; m.Verts[v] = vt;
                    }
                }
            }
            newEdges = created;
        }
        if (o.FlipNormals && all.Count > 0)
        {
            // 뒤집기 전에 바깥 엣지를 정점 쌍으로 기억(면 재생성으로 엣지 ID가 바뀔 수 있음)
            var keep = newEdges.Select(e => m.EdgeVertices(e)).ToList();
            all = ReverseKeepingIds(m, all, all);
            newEdges = keep.Select(p => m.FindEdge(p.Item1, p.Item2)).Where(e => e >= 0).ToList();
        }
        m.BumpTopology();
        return all.Where(f => f < m.FaceCount && m.Faces[f].Alive).ToList();
    }

    /// <summary>ReverseFaces는 면을 다시 만들어 ID가 바뀌므로, 추적할 면을 정점 집합으로 다시 찾는다.</summary>
    /// <remarks>
    /// 정렬된 정점 ID 문자열을 키로 쓰므로 같은 정점 집합의 면(라미나)이 있으면 둘 다 잡힐 수 있다.
    /// </remarks>
    /// <param name="toReverse">뒤집을 면(그 연결 요소 전체가 뒤집힌다).</param>
    /// <param name="track">뒤집은 뒤 새 ID를 알고 싶은 면.</param>
    /// <returns>track 면들의 새 ID.</returns>
    private static List<int> ReverseKeepingIds(PolyMesh m, List<int> toReverse, List<int> track)
    {
        var tmp = new List<int>();
        var keys = track.Where(f => m.Faces[f].Alive).Select(f => { m.GetFaceVertices(f, tmp); return string.Join(",", tmp.OrderBy(x => x)); }).ToHashSet();
        ReverseFaces(m, toReverse);
        var res = new List<int>();
        for (int f = 0; f < m.FaceCount; f++)
        {
            if (!m.Faces[f].Alive) continue;
            m.GetFaceVertices(f, tmp);
            if (keys.Contains(string.Join(",", tmp.OrderBy(x => x)))) res.Add(f);
        }
        return res;
    }

    /// <summary>축(X/Y/Z) 또는 Custom 방향 단위 벡터. Custom이 영벡터면 +Y.</summary>
    private static Vector3 FixedDir(ExtrudeOptions o) => o.Direction switch
    {
        ExtrudeDirection.X => Vector3.UnitX,
        ExtrudeDirection.Y => Vector3.UnitY,
        ExtrudeDirection.Z => Vector3.UnitZ,
        _ => o.CustomDirection.LengthSquared() > 1e-20f ? Vector3.Normalize(o.CustomDirection) : Vector3.UnitY,
    };

    /// <summary>면 단위 법선(퇴화 면이면 +Y).</summary>
    private static Vector3 FaceUnitNormal(PolyMesh m, int f)
    {
        var n = MeshNormals.FaceNormalUnnormalized(m, f);
        return n.LengthSquared() > 1e-20f ? Vector3.Normalize(n) : Vector3.UnitY;
    }

    /// <summary>면들이 쓰는 정점을 중복 없이 모아 각 정점에 delta(정점 ID)만큼 더한다(공유 정점은 한 번만 이동).</summary>
    private static void MoveFaceVerts(PolyMesh m, IEnumerable<int> faces, Func<int, Vector3> delta)
    {
        var verts = new HashSet<int>(); var tmp = new List<int>();
        foreach (int f in faces) { if (!m.Faces[f].Alive) continue; m.GetFaceVertices(f, tmp); verts.UnionWith(tmp); }
        foreach (int v in verts) { var vt = m.Verts[v]; vt.Position += delta(v); m.Verts[v] = vt; }
    }

    /// <summary>선택 면을 엣지로 이어진 묶음으로 나눈다.</summary>
    /// <remarks>스택 DFS. 엣지(트윈)를 공유하는 선택 면끼리만 같은 묶음이다(정점만 맞닿은 면은 별개).</remarks>
    private static List<List<int>> RegionComponents(PolyMesh m, List<int> faces)
    {
        var set = new HashSet<int>(faces); var seen = new HashSet<int>(); var res = new List<List<int>>(); var hes = new List<int>();
        foreach (int f0 in faces)
        {
            if (!seen.Add(f0)) continue;
            var comp = new List<int>(); var stack = new Stack<int>(); stack.Push(f0);
            while (stack.Count > 0)
            {
                int f = stack.Pop(); comp.Add(f);
                m.GetFaceHalfEdges(f, hes);
                foreach (int he in hes.ToArray()) { int tw = m.Hes[he].Twin; if (tw < 0) continue; int g = m.Hes[tw].Face; if (set.Contains(g) && seen.Add(g)) stack.Push(g); }
            }
            res.Add(comp);
        }
        return res;
    }

    /// <summary>연결 영역 하나의 Region Extrude(위 ①②③ 규칙). 반환값은 캡 면.</summary>
    private static List<int> ExtrudeRegionComponent(PolyMesh m, List<int> comp)
    {
        // boundary = 영역 경계 하프에지 수, meshBorder = 그중 트윈이 없는(메시 테두리) 것의 수
        var set = new HashSet<int>(comp); var hes = new List<int>();
        int boundary = 0, meshBorder = 0;
        foreach (int f in comp)
        {
            m.GetFaceHalfEdges(f, hes);
            foreach (int he in hes)
            {
                int tw = m.Hes[he].Twin;
                if (tw < 0) { boundary++; meshBorder++; }
                else if (!set.Contains(m.Hes[tw].Face)) boundary++;
            }
        }
        if (boundary > 0 && meshBorder < boundary) return ExtrudeFaces(m, comp); // ① 일반

        // ②③: 모든 정점을 복제한 캡(원래 방향)
        // captured: 각 면의 코너/머티리얼/둘레 하드 플래그. D(v): 정점 복제(필요할 때 한 번만)
        var captured = comp.Select(f => (corners: CaptureCorners(m, f), material: m.Faces[f].Material, hard: HardFlags(m, f))).ToList();
        var dup = new Dictionary<int, int>();
        int D(int v) { if (!dup.TryGetValue(v, out int d)) { d = m.AddVertex(m.Verts[v].Position); dup[v] = d; } return d; }
        if (boundary == 0)
        {
            // ③ 닫힌 볼륨: 새 껍질로 복제만
            var caps = new List<int>();
            foreach (var (corners, material, hard) in captured)
            {
                var mapped = corners.Select(c => c with { Vertex = D(c.Vertex) }).ToList();
                int nf = AddFaceWithCorners(m, mapped, material);
                if (nf < 0) continue;
                caps.Add(nf);
                for (int i = 0; i < mapped.Count; i++) SetFlags(m, mapped[i].Vertex, mapped[(i + 1) % mapped.Count].Vertex, hard[i]);
            }
            return caps;
        }
        // ② 열린 판: 원래 면은 뒤집어 바닥으로 남기고, 복제 캡과 테두리 옆면으로 닫는다
        var borderHes = new List<(int a, int b, Vector2 uvA, Vector2 uvB, bool hard)>();
        foreach (int f in comp)
        {
            m.GetFaceHalfEdges(f, hes);
            foreach (int he in hes)
            {
                var h = m.Hes[he];
                if (h.Twin >= 0) continue;
                borderHes.Add((h.Vertex, m.Hes[h.Next].Vertex, h.Uv0, m.Hes[h.Next].Uv0, m.Edges[h.Edge].Hard));
            }
        }
        foreach (int f in comp) m.RemoveFace(f, removeIsolated: false);
        var result = new List<int>();
        foreach (var (corners, material, hard) in captured)
        {
            // 바닥(뒤집음)
            var rev = corners.AsEnumerable().Reverse().Select(c => c.Flipped()).ToList();
            AddFaceWithCorners(m, rev, material);
            // 캡(원래 방향, 복제 정점)
            var mapped = corners.Select(c => c with { Vertex = D(c.Vertex) }).ToList();
            int nf = AddFaceWithCorners(m, mapped, material);
            if (nf >= 0)
            {
                result.Add(nf);
                for (int i = 0; i < mapped.Count; i++) SetFlags(m, mapped[i].Vertex, mapped[(i + 1) % mapped.Count].Vertex, hard[i]);
            }
            for (int i = 0; i < corners.Count; i++) SetFlags(m, corners[i].Vertex, corners[(i + 1) % corners.Count].Vertex, hard[i]);
        }
        // 테두리 엣지 a→b와 복제 엣지를 잇는 옆면 쿼드(a, b, b', a'). 상자 모서리이므로 네 엣지 모두 하드.
        foreach (var (a, b, uvA, uvB, hard) in borderHes)
        {
            int a2 = D(a), b2 = D(b);
            AddFaceWithCorners(m, new List<Corner> { new(a, uvA, Vector3.Zero), new(b, uvB, Vector3.Zero), new(b2, uvB, Vector3.Zero), new(a2, uvA, Vector3.Zero) });
            SetHard(m, a, a2, true); SetHard(m, b, b2, true); SetHard(m, a, b, true); SetHard(m, a2, b2, true);
        }
        return result;
    }

    /// <summary>면 f의 둘레 엣지 플래그(하드/심/크리즈)를 코너 순서대로(코너 i → i+1 엣지) 돌려준다.</summary>
    private static List<EdgeFlags> HardFlags(PolyMesh m, int f)
    {
        var c = CaptureCorners(m, f); var res = new List<EdgeFlags>();
        for (int i = 0; i < c.Count; i++) res.Add(GetFlags(m, c[i].Vertex, c[(i + 1) % c.Count].Vertex));
        return res;
    }
}
