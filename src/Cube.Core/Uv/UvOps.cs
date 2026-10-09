using System.Numerics;
using Cube.Core.Mesh;

namespace Cube.Core.Uv;

/// <summary>UV 점: 같은 정점을 공유하고 심으로 끊기지 않으며 같은 UV를 가진 코너(하프에지)들의 묶음.</summary>
/// <remarks>
/// UV 편집기에서 보이는 "점" 하나에 해당한다. 3D 정점 하나는 심 양쪽이나 UV가 다른 코너마다 여러 UV 점으로 갈라질 수 있다.
/// UV 점을 옮길 때는 HalfEdges의 모든 코너 Uv0을 같은 값으로 써야 연결이 유지된다(<see cref="UvOps.SetPointUv"/>).
/// </remarks>
public sealed class UvPoint
{
    /// <summary>이 UV 점이 속한 메시 정점 ID.</summary>
    public int Vertex;
    /// <summary>이 UV 점으로 묶인 코너(하프에지) ID 목록. 각 하프에지의 Uv0이 이 점의 UV를 담는다.</summary>
    public readonly List<int> HalfEdges = new();
    /// <summary>현재 UV 좌표(하단 원점, Maya 규약). Build 시점 대표 코너 값이며 편집 시 함께 갱신한다.</summary>
    public Vector2 Uv;
    /// <summary>속한 UV 셸(섬) 번호. Build 전에는 −1.</summary>
    public int Shell = -1;
    /// <summary>코너 중 하나라도 PinUv면 고정.</summary>
    public bool Pinned;
}

/// <summary>메시의 UV 점/셸 구조. 위상이나 UV가 바뀌면 다시 만든다.</summary>
/// <remarks>
/// 캐시가 아니라 스냅샷이므로 UV/심/위상을 바꾼 뒤에는 <see cref="Build"/>를 다시 호출해야 한다.
/// UV 점 인덱스는 Build마다 하프에지 순서대로 다시 매겨진다(UV 편집기·뷰포트 UV 모드가 같은 순서를 공유).
/// </remarks>
public sealed class UvTopology
{
    /// <summary>UV 점 목록(인덱스 = UV 점 ID).</summary>
    public readonly List<UvPoint> Points = new();
    /// <summary>halfEdge → UV 점 인덱스.</summary>
    // 죽은 하프에지는 −1.
    public int[] HeToPoint = Array.Empty<int>();
    /// <summary>UV 셸(UV 공간에서 연결된 섬) 개수. 셸 번호는 0..ShellCount−1.</summary>
    public int ShellCount;

    /// <summary>
    /// 메시에서 UV 점과 셸을 만든다.
    /// </summary>
    /// <remarks>
    /// 1) 각 비심 내부 엣지에서, 엣지 양끝 정점별로 두 면의 코너 UV가 eps 이내면 union-find로 합친다.
    /// 2) 같은 대표를 가진 코너들을 UV 점 하나로 만들고 하나라도 PinUv면 Pinned.
    /// 3) 같은 면의 연속 코너(he, he.Next)를 다시 union-find로 합쳐 UV 셸 번호를 매긴다.
    /// </remarks>
    /// <param name="eps">같은 UV로 볼 거리 허용치.</param>
    public static UvTopology Build(PolyMesh m, float eps = 1e-5f)
    {
        // 하프에지 → UV 점 매핑(초기값 −1)
        var t = new UvTopology { HeToPoint = new int[m.HalfEdgeCount] };
        Array.Fill(t.HeToPoint, -1);
        float eps2 = eps * eps;
        // 정점별로 코너를 모으고, 심이 아닌 엣지를 건너 같은 UV인 코너끼리 묶는다(union-find)
        // 1단계: 코너 union-find (경로 압축 Find)
        var parent = new int[m.HalfEdgeCount];
        for (int i = 0; i < parent.Length; i++) parent[i] = i;
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[a] = b; }
        for (int h = 0; h < m.HalfEdgeCount; h++)
        {
            var he = m.Hes[h];
            // 경계 엣지(twin 없음)는 합칠 상대 코너가 없다
            if (!he.Alive || he.Twin < 0) continue;
            var ed = m.Edges[he.Edge];
            // 심 엣지는 UV를 끊는다
            if (ed.Seam) continue;
            // he: a→b, twin: b→a. 정점 a의 코너는 he와 twin.Next, 정점 b의 코너는 he.Next와 twin
            var tw = m.Hes[he.Twin];
            int cornerA1 = h, cornerA2 = tw.Next;
            int cornerB1 = he.Next, cornerB2 = he.Twin;
            if (Vector2.DistanceSquared(m.Hes[cornerA1].Uv0, m.Hes[cornerA2].Uv0) <= eps2) Union(cornerA1, cornerA2);
            if (Vector2.DistanceSquared(m.Hes[cornerB1].Uv0, m.Hes[cornerB2].Uv0) <= eps2) Union(cornerB1, cornerB2);
        }
        // 2단계: union-find 대표마다 UV 점 하나를 만들고 코너를 소속시킨다
        var map = new Dictionary<int, int>();
        for (int h = 0; h < m.HalfEdgeCount; h++)
        {
            if (!m.Hes[h].Alive) continue;
            int r = Find(h);
            if (!map.TryGetValue(r, out int idx))
            {
                idx = t.Points.Count;
                map[r] = idx;
                t.Points.Add(new UvPoint { Vertex = m.Hes[h].Vertex, Uv = m.Hes[h].Uv0 });
            }
            t.Points[idx].HalfEdges.Add(h);
            if (m.Hes[h].PinUv) t.Points[idx].Pinned = true;
            t.HeToPoint[h] = idx;
        }
        // 셸: UV 점이 연결된(같은 면에 속한) 성분
        var pp = new int[t.Points.Count];
        for (int i = 0; i < pp.Length; i++) pp[i] = i;
        int FindP(int x) { while (pp[x] != x) { pp[x] = pp[pp[x]]; x = pp[x]; } return x; }
        for (int h = 0; h < m.HalfEdgeCount; h++)
        {
            var he = m.Hes[h];
            if (!he.Alive) continue;
            int a = FindP(t.HeToPoint[h]), b = FindP(t.HeToPoint[he.Next]);
            if (a != b) pp[a] = b;
        }
        var shellMap = new Dictionary<int, int>();
        for (int i = 0; i < t.Points.Count; i++)
        {
            int r = FindP(i);
            if (!shellMap.TryGetValue(r, out int s)) { s = shellMap.Count; shellMap[r] = s; }
            t.Points[i].Shell = s;
        }
        t.ShellCount = shellMap.Count;
        return t;
    }

    /// <summary>지정 셸에 속한 UV 점 ID를 순서대로 열거한다(전체 선형 탐색).</summary>
    public IEnumerable<int> PointsInShell(int shell)
    {
        for (int i = 0; i < Points.Count; i++) if (Points[i].Shell == shell) yield return i;
    }

    /// <summary>UV 점 집합에 속한 모든 하프에지.</summary>
    public IEnumerable<int> HalfEdgesOf(IEnumerable<int> pointIds)
    {
        foreach (int p in pointIds) foreach (int h in Points[p].HalfEdges) yield return h;
    }
}

/// <summary>UV 편집 연산. 모두 코너 UV(HalfEdge.Uv0)와 엣지 심 플래그만 바꾼다(위상 불변).</summary>
/// <remarks>
/// partial 클래스로 UvOps.Modify/Shells/Select/Project.cs에 나뉘어 있다. 이 파일은 투영(Planar/Cylindrical/Spherical),
/// Auto Wrap, Cut/Sew, 점 변환, Unfold(이완), Layout(패킹) 같은 기본 연산을 담는다.
/// 호출자(App의 UvEditCommand)가 실행 전후 코너 UV·심·핀을 스냅샷해 Undo를 만든다.
/// </remarks>
public static partial class UvOps
{
    /// <summary>하프에지 하나의 Uv0을 바꾼다(HalfEdge는 struct라 읽고-수정-쓰기).</summary>
    internal static void SetUv(PolyMesh m, int he, Vector2 uv) { var h = m.Hes[he]; h.Uv0 = uv; m.Hes[he] = h; }

    /// <summary>면 f의 하프에지(코너)를 Next 순서대로 열거한다.</summary>
    internal static IEnumerable<int> FaceHalfEdges(PolyMesh m, int f)
    {
        int start = m.Faces[f].HalfEdge, he = start;
        do { yield return he; he = m.Hes[he].Next; } while (he != start);
    }

    /// <summary>2D 점 집합의 축 정렬 경계 상자(min, max). 빈 집합이면 (MaxValue, MinValue).</summary>
    public static (Vector2 min, Vector2 max) Bounds(IEnumerable<Vector2> pts)
    {
        var min = new Vector2(float.MaxValue); var max = new Vector2(float.MinValue);
        foreach (var p in pts) { min = Vector2.Min(min, p); max = Vector2.Max(max, p); }
        return (min, max);
    }

    /// <summary>선택 면의 평면 투영. 법선 방향으로 투영 평면을 잡고 결과를 0..1 범위로 정규화한다.</summary>
    /// <remarks>
    /// 각 코너의 3D 위치를 <see cref="ProjectionBasis"/>의 u·v 축에 내적해 2D 좌표를 얻고, 종횡비를 유지해 0..1로 맞춘 뒤,
    /// 선택 영역 둘레에 심을 표시해 나머지 UV와 분리한다. 위치는 메시 로컬 좌표 기준이다.
    /// </remarks>
    public static void PlanarProject(PolyMesh m, IEnumerable<int> faces, Vector3 normal)
    {
        // 유효한(살아 있는) 면만 남긴다
        var list = faces.Where(f => f >= 0 && f < m.FaceCount && m.Faces[f].Alive).ToList();
        if (list.Count == 0) return;
        var (u, v) = ProjectionBasis(normal);
        // 코너별 원시 2D 투영 좌표
        var raw = new Dictionary<int, Vector2>();
        foreach (int f in list)
            foreach (int he in FaceHalfEdges(m, f))
            {
                var p = m.Verts[m.Hes[he].Vertex].Position;
                raw[he] = new Vector2(Vector3.Dot(p, u), Vector3.Dot(p, v));
            }
        Normalize(m, raw);
        MarkSeamsAroundSelection(m, list);
    }

    /// <summary>
    /// 투영 평면의 u/v 축: 그 방향에서 바라본 화면의 오른쪽/위와 일치시킨다
    /// (+Z: u=X, v=Y / +X: u=-Z, v=Y / +Y(위에서): u=X, v=-Z / -Y: u=X, v=+Z).
    /// </summary>
    public static (Vector3 u, Vector3 v) ProjectionBasis(Vector3 normal)
    // 거의 수직(위/아래)에서 볼 때는 Y와의 외적이 퇴화하므로 고정 축을 쓴다
    {
        var n = Vector3.Normalize(normal);
        if (MathF.Abs(n.Y) > 0.99f) return (Vector3.UnitX, n.Y > 0 ? -Vector3.UnitZ : Vector3.UnitZ);
        // 그 외: 오른쪽 = Y × n, 위 = n × 오른쪽(화면 오른손 기저)
        var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, n));
        var up = Vector3.Cross(n, right);
        return (right, up);
    }

    /// <summary>선택 면의 평균 법선으로 평면 투영(Maya "Best Plane").</summary>
    public static void PlanarProjectBestFit(PolyMesh m, IEnumerable<int> faces)
    // 면 법선(비정규화 = 면적 가중)을 합산해 평균 방향을 구한다
    {
        var list = faces.ToList();
        var n = Vector3.Zero;
        foreach (int f in list) if (f >= 0 && f < m.FaceCount && m.Faces[f].Alive) n += MeshNormals.FaceNormalUnnormalized(m, f);
        // 법선이 상쇄되면(닫힌 형태) Y축 기본값
        if (n.LengthSquared() < 1e-12f) n = Vector3.UnitY;
        PlanarProject(m, list, n);
    }

    /// <summary>Y축 원통 투영. u = 각도/2π, v = 높이 정규화. 각도 경계(심)에서 u가 0과 1로 갈리는 면은 짧은 쪽으로 맞춘다.</summary>
    public static void CylindricalProject(PolyMesh m, IEnumerable<int> faces) => CylindricalProject(m, faces, Vector3.UnitY, null);

    /// <summary>임의 축 원통 투영. originPoint가 있으면 그 점의 각도가 u=0(랩 경계)이 된다 — 자동 심 경로에 맞출 때 쓴다.</summary>
    public static void CylindricalProject(PolyMesh m, IEnumerable<int> faces, Vector3 axis, Vector3? originPoint)
    /// <remarks>
    /// 중심 = 선택 코너 위치 평균, v = 축 방향 높이를 [hmin, hmax]로 정규화, u = 축에 수직인 평면에서의 각도 / 2π(0..1).
    /// 한 면 안에서 u가 0과 1 근처로 갈리면 첫 코너 기준으로 ±1 해서 면이 UV 전체를 가로지르지 않게 한다.
    /// </remarks>
    {
        var list = faces.Where(f => f >= 0 && f < m.FaceCount && m.Faces[f].Alive).ToList();
        if (list.Count == 0) return;
        axis = axis.LengthSquared() > 1e-12f ? Vector3.Normalize(axis) : Vector3.UnitY;
        var (bu, bv) = ProjectionBasis(axis); // axis에 수직인 두 축
        // 투영 중심 = 선택 면 코너들의 평균 위치
        var center = Vector3.Zero; int cnt = 0;
        foreach (int f in list) foreach (int he in FaceHalfEdges(m, f)) { center += m.Verts[m.Hes[he].Vertex].Position; cnt++; }
        center /= Math.Max(cnt, 1);
        // 축 방향 높이 범위(v 정규화용)
        float hmin = float.MaxValue, hmax = float.MinValue;
        foreach (int f in list) foreach (int he in FaceHalfEdges(m, f)) { float hh = Vector3.Dot(m.Verts[m.Hes[he].Vertex].Position - center, axis); hmin = MathF.Min(hmin, hh); hmax = MathF.Max(hmax, hh); }
        float h = MathF.Max(hmax - hmin, 1e-6f);
        // 축 수직 평면 위 각도
        float Angle(Vector3 p) => MathF.Atan2(Vector3.Dot(p, bv), Vector3.Dot(p, bu));
        // 랩 경계(u = 0)로 삼을 기준 각
        float a0 = originPoint is { } op ? Angle(op - center) : 0f;
        foreach (int f in list)
        {
            var corners = FaceHalfEdges(m, f).ToList();
            var us = new float[corners.Count];
            // 각 코너의 u를 [0, 1)로 (a0 기준 회전, +2로 음수 방지)
            for (int i = 0; i < corners.Count; i++)
            {
                var p = m.Verts[m.Hes[corners[i]].Vertex].Position - center;
                us[i] = ((Angle(p) - a0) / MathF.Tau + 2f) % 1f;
            }
            // 면 내부에서 u 불연속(0.5 이상 차이)이면 작은 쪽을 +1
            float uref = us[0];
            for (int i = 0; i < corners.Count; i++)
            {
                if (us[i] - uref > 0.5f) us[i] -= 1f; else if (uref - us[i] > 0.5f) us[i] += 1f;
                float v = (Vector3.Dot(m.Verts[m.Hes[corners[i]].Vertex].Position - center, axis) - hmin) / h;
                SetUv(m, corners[i], new Vector2(us[i], v));
            }
        }
        MarkSeamsAroundSelection(m, list);
    }

    /// <summary>구면 투영(경도/위도).</summary>
    /// <remarks>
    /// 중심 = 선택 코너 평균. u = 경도 atan2(−z, x)/2π, v = 위도 acos(−y/|p|)/π(아래 극 = 0, 위 극 = 1).
    /// 면 안에서 u 랩 불연속은 원통 투영과 같이 첫 코너 기준으로 보정한다. 중심과 같은 점은 v = 0.5.
    /// </remarks>
    public static void SphericalProject(PolyMesh m, IEnumerable<int> faces)
    {
        var list = faces.Where(f => f >= 0 && f < m.FaceCount && m.Faces[f].Alive).ToList();
        if (list.Count == 0) return;
        var center = Vector3.Zero; int cnt = 0;
        foreach (int f in list) foreach (int he in FaceHalfEdges(m, f)) { center += m.Verts[m.Hes[he].Vertex].Position; cnt++; }
        center /= Math.Max(cnt, 1);
        // 경도 u 계산 후 면 내부 불연속 보정, 위도 v 계산
        foreach (int f in list)
        {
            var corners = FaceHalfEdges(m, f).ToList();
            var us = new float[corners.Count];
            for (int i = 0; i < corners.Count; i++)
            {
                var p = m.Verts[m.Hes[corners[i]].Vertex].Position - center;
                us[i] = (MathF.Atan2(-p.Z, p.X) / MathF.Tau + 1f) % 1f;
            }
            float uref = us[0];
            for (int i = 0; i < corners.Count; i++)
            {
                if (us[i] - uref > 0.5f) us[i] -= 1f; else if (uref - us[i] > 0.5f) us[i] += 1f;
                var p = m.Verts[m.Hes[corners[i]].Vertex].Position - center;
                float len = p.Length();
                float v = len > 1e-9f ? MathF.Acos(Math.Clamp(-p.Y / len, -1f, 1f)) / MathF.PI : 0.5f;
                SetUv(m, corners[i], new Vector2(us[i], v));
            }
        }
        MarkSeamsAroundSelection(m, list);
    }

    /// <summary>원시 2D 좌표를 0..1 사각형 안에 맞춘다(종횡비 유지).</summary>
    /// <remarks>가로/세로 중 긴 쪽 길이로 나눠 최소점을 원점에 둔다.</remarks>
    private static void Normalize(PolyMesh m, Dictionary<int, Vector2> raw)
    {
        var (min, max) = Bounds(raw.Values);
        float size = MathF.Max(MathF.Max(max.X - min.X, max.Y - min.Y), 1e-9f);
        foreach (var (he, p) in raw) SetUv(m, he, (p - min) / size);
    }

    /// <summary>투영된 면 집합의 경계 엣지를 심으로 표시(기존 UV와 분리). 내부 엣지의 심은 해제.</summary>
    /// <remarks>
    /// 선택 면의 각 엣지에 대해: 메시 경계 엣지는 심을 끈다(이미 테두리라 UV가 끊김).
    /// 상대 면이 선택 밖이면 심, 선택 안이라도 양쪽 코너 UV가 다르면(랩 경계) 심, 그 외는 심 해제.
    /// </remarks>
    internal static void MarkSeamsAroundSelection(PolyMesh m, List<int> faces)
    {
        var set = new HashSet<int>(faces);
        foreach (int f in faces)
            foreach (int he in FaceHalfEdges(m, f))
            {
                var h = m.Hes[he];
                var ed = m.Edges[h.Edge];
                bool boundary = h.Twin < 0 || !set.Contains(m.Hes[h.Twin].Face);
                if (h.Twin < 0) { ed.Seam = false; m.Edges[h.Edge] = ed; continue; }
                // 내부 엣지라도 투영 결과 양쪽 코너 UV가 다르면(원통 랩 경계 등) 심으로 남긴다
                var tw = m.Hes[h.Twin];
                bool mismatch = (h.Uv0 - m.Hes[tw.Next].Uv0).LengthSquared() > 1e-8f || (m.Hes[h.Next].Uv0 - tw.Uv0).LengthSquared() > 1e-8f;
                ed.Seam = boundary || mismatch;
                m.Edges[h.Edge] = ed;
            }
    }

    /// <summary>
    /// Auto Wrap: 자동 심 선택 → 심 적용 → 섬마다 최적 평면 투영으로 초기화 → 이완(Unfold) → Layout.
    /// 반환값은 심 엣지 수.
    /// </summary>
    /// <remarks>
    /// 영역별 초기화: 면 법선 합의 크기 / 법선 크기 합 &lt; 0.5이고 면이 6개 이상이면 "둘러싸는"(튜브형) 영역으로 보고
    /// 면 법선과 가장 직교하는 월드 축으로 원통 투영한다. 이때 영역 내부 심 중 이면각이 작고 하드가 아닌 엣지(잘라 낸 경로)의
    /// 중점을 랩 경계로 삼는다. 그 외는 최적 평면 투영. 투영이 심을 다시 쓰므로 자동 심을 복원한 뒤 Unfold → Layout.
    /// </remarks>
    /// <param name="angleDeg">자동 심 이면각 임계값(도).</param>
    /// <param name="unfoldIterations">Unfold 반복 횟수.</param>
    public static int AutoWrap(PolyMesh m, float angleDeg = 55f, int unfoldIterations = 120)
    {
        // 자동 심을 구하고 메시 심 플래그를 그것으로 덮어쓴다
        var seams = AutoSeams.Select(m, angleDeg);
        for (int e = 0; e < m.EdgeCount; e++) { if (!m.Edges[e].Alive) continue; var ed = m.Edges[e]; ed.Seam = seams.Contains(e); m.Edges[e] = ed; }
        float cosLimit = MathF.Cos(angleDeg * MathF.PI / 180f);
        foreach (var region in AutoSeams.Regions(m, seams))
        {
            // 법선이 서로 상쇄되는(둘러싸는) 영역은 원통 투영, 아니면 최적 평면 투영
            var sum = Vector3.Zero; float total = 0;
            foreach (int f in region) { var n = MeshNormals.FaceNormalUnnormalized(m, f); sum += n; total += n.Length(); }
            bool wraps = total > 1e-9f && sum.Length() / total < 0.5f && region.Count >= 6;
            if (!wraps) { PlanarProjectBestFit(m, region); continue; }
            // 축 = 면 법선과 가장 직교하는 월드 축
            Vector3 best = Vector3.UnitY; float bestScore = float.MaxValue;
            foreach (var ax in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
            {
                float score = 0; foreach (int f in region) score += MathF.Abs(Vector3.Dot(MeshNormals.FaceNormalUnnormalized(m, f), ax));
                if (score < bestScore) { bestScore = score; best = ax; }
            }
            // 랩 경계를 잘린(날카롭지 않은) 심 경로에 맞춘다
            Vector3? origin = null;
            var set = new HashSet<int>(region); var hes = new List<int>();
            foreach (int f in region)
            {
                m.GetFaceHalfEdges(f, hes);
                foreach (int he in hes)
                {
                    var hh = m.Hes[he];
                    if (hh.Twin < 0 || !seams.Contains(hh.Edge) || !set.Contains(m.Hes[hh.Twin].Face)) continue;
                    var n0 = Vector3.Normalize(MeshNormals.FaceNormalUnnormalized(m, f)); var n1 = Vector3.Normalize(MeshNormals.FaceNormalUnnormalized(m, m.Hes[hh.Twin].Face));
                    if (Vector3.Dot(n0, n1) >= cosLimit && !m.Edges[hh.Edge].Hard) { origin = (m.Verts[hh.Vertex].Position + m.Verts[m.Hes[hh.Next].Vertex].Position) * 0.5f; break; }
                }
                if (origin != null) break;
            }
            CylindricalProject(m, region, best, origin);
        }
        // 투영이 심을 다시 쓰므로 자동 심을 복원
        for (int e = 0; e < m.EdgeCount; e++) { if (!m.Edges[e].Alive) continue; var ed = m.Edges[e]; ed.Seam = seams.Contains(e); m.Edges[e] = ed; }
        // 투영이 접히거나(앞/뒷면이 섞임) 스스로 겹친 영역은 Tutte 임베딩(경계를 원에, 안쪽은 이웃 평균)으로 다시 초기화한다 —
        // 토러스·구·베벨처럼 휘어진 영역의 투영은 겹친 채 남아 Unfold가 풀지 못하지만 Tutte는 겹침 없는 시작점을 보장한다.
        // (겹침 없이 펼쳐진 평평한 영역의 투영은 왜곡이 없으므로 그대로 둔다)
        var refold = new List<List<int>>();
        foreach (var region in AutoSeams.Regions(m, seams))
        {
            int pos = 0, neg = 0;
            foreach (int f in region) { float a = FaceUvSignedArea(m, f); if (a > 1e-12f) pos++; else neg++; }
            bool folded = pos > 0 && neg > 0 || pos == 0 || OverlappingFaces(m, new HashSet<int>(region)).Count > 0;
            if (!folded) continue;
            // 원통 투영의 랩 불연속이 영역 안에 남으면 셸이 갈라지므로 연속인 평면 투영으로 바꾼 뒤 Tutte
            PlanarProjectBestFit(m, region);
            refold.Add(region);
        }
        if (refold.Count > 0)
        {
            for (int e = 0; e < m.EdgeCount; e++) { if (!m.Edges[e].Alive) continue; var ed = m.Edges[e]; ed.Seam = seams.Contains(e); m.Edges[e] = ed; }
            var t0 = UvTopology.Build(m);
            foreach (var region in refold) TutteDiskInit(m, t0, t0.Points[t0.HeToPoint[m.Faces[region[0]].HalfEdge]].Shell);
        }
        // 심이 반영된 셸 구조로 이완 후 다시 구조를 만들어 패킹
        var topo = UvTopology.Build(m);
        var before = new Vector2[m.HalfEdgeCount];
        for (int h = 0; h < m.HalfEdgeCount; h++) before[h] = m.Hes[h].Uv0;
        var foldedBefore = FoldedShells(m, topo);
        UnfoldRelax(m, topo, Enumerable.Range(0, topo.ShellCount), unfoldIterations);
        // Unfold가 새로 접거나 겹치게 만든 셸(심하게 휜 원반)은 겹침 없는 초기 배치로 되돌린다
        topo = UvTopology.Build(m);
        foreach (int s in FoldedShells(m, topo))
        {
            if (foldedBefore.Contains(s)) continue;
            foreach (int p in topo.PointsInShell(s)) foreach (int h in topo.Points[p].HalfEdges) SetUv(m, h, before[h]);
        }
        topo = UvTopology.Build(m);
        Layout(m, topo, Enumerable.Range(0, topo.ShellCount));
        return seams.Count;
    }

    /// <summary>앞/뒷면 UV가 섞였거나(접힘) 같은 셸 안에서 면끼리 겹치는 셸 번호들.</summary>
    internal static HashSet<int> FoldedShells(PolyMesh m, UvTopology topo)
    {
        var shellFaces = new Dictionary<int, HashSet<int>>();
        for (int f = 0; f < m.FaceCount; f++)
        {
            if (!m.Faces[f].Alive) continue;
            int s = topo.Points[topo.HeToPoint[m.Faces[f].HalfEdge]].Shell;
            if (!shellFaces.TryGetValue(s, out var fs)) shellFaces[s] = fs = new HashSet<int>();
            fs.Add(f);
        }
        var result = new HashSet<int>();
        foreach (var (s, fs) in shellFaces)
        {
            int pos = 0, neg = 0;
            foreach (int f in fs) { float a = FaceUvSignedArea(m, f); if (a > 1e-12f) pos++; else if (a < -1e-12f) neg++; }
            if (pos > 0 && neg > 0 || OverlappingFaces(m, fs).Count > 0) result.Add(s);
        }
        return result;
    }

    /// <summary>
    /// Tutte 임베딩 초기화: 원반 위상(경계 루프 1개, 오일러 특성 1)인 셸의 경계 UV 점을 원 둘레에 3D 호 길이 비례로 놓고
    /// 안쪽 점을 이웃(UV 엣지로 이어진 점) 평균 위치로 푼다(균일 가중 라플라스 방정식, 켤레 기울기법).
    /// 볼록 경계 + 양의 가중이면 뒤집히거나 겹치는 면이 없는 배치가 보장된다(Tutte 정리). 원반이 아니면 아무것도 하지 않는다.
    /// </summary>
    /// <param name="keepBorder">true면 경계 점을 지금 UV 그대로 두고(Map Border 결과 등) 안쪽만 푼다.</param>
    /// <returns>초기화했으면 true.</returns>
    public static bool TutteDiskInit(PolyMesh m, UvTopology topo, int shell, bool keepBorder = false)
    {
        var loops = ShellBorderLoops(m, topo, shell);
        if (loops.Count != 1) return false;
        var loop = loops[0];
        // 셸의 UV 점·UV 엣지(점 쌍)·면 수로 오일러 특성 확인
        var inShell = new HashSet<int>(topo.PointsInShell(shell));
        var adj = new Dictionary<int, HashSet<int>>();
        int faces = 0;
        for (int f = 0; f < m.FaceCount; f++)
        {
            if (!m.Faces[f].Alive || !inShell.Contains(topo.HeToPoint[m.Faces[f].HalfEdge])) continue;
            faces++;
            foreach (int he in FaceHalfEdges(m, f))
            {
                int a = topo.HeToPoint[he], b = topo.HeToPoint[m.Hes[he].Next];
                if (a == b) continue;
                if (!adj.TryGetValue(a, out var la)) adj[a] = la = new HashSet<int>(); la.Add(b);
                if (!adj.TryGetValue(b, out var lb)) adj[b] = lb = new HashSet<int>(); lb.Add(a);
            }
        }
        int uvEdges = adj.Values.Sum(s => s.Count) / 2;
        if (inShell.Count - uvEdges + faces != 1 || loop.Count < 3) return false;
        // 경계: 3D 호 길이 비례로 원 둘레(반시계 = 셸 안쪽이 왼쪽)에 배치
        int n = loop.Count;
        var len = new float[n]; float total = 0;
        for (int i = 0; i < n; i++)
        {
            len[i] = MathF.Max(Vector3.Distance(m.Verts[topo.Points[loop[i]].Vertex].Position, m.Verts[topo.Points[loop[(i + 1) % n]].Vertex].Position), 1e-6f);
            total += len[i];
        }
        var uv = new Dictionary<int, Vector2>();
        float acc = 0;
        for (int i = 0; i < n; i++)
        {
            float t = acc / total * MathF.Tau; acc += len[i];
            uv[loop[i]] = keepBorder ? topo.Points[loop[i]].Uv : new Vector2(0.5f + 0.5f * MathF.Cos(t), 0.5f + 0.5f * MathF.Sin(t));
        }
        // 안쪽 점: 차수·x = 이웃 합(경계 이웃은 우변으로) — 대칭 양의 정부호이므로 켤레 기울기법
        var interior = inShell.Where(p => !uv.ContainsKey(p)).ToList();
        if (interior.Count > 0)
        {
            var index = new Dictionary<int, int>(); for (int i = 0; i < interior.Count; i++) index[interior[i]] = i;
            int k = interior.Count;
            var rhs = new Vector2[k];
            var nbr = new List<int>[k]; var deg = new float[k];
            for (int i = 0; i < k; i++)
            {
                nbr[i] = new List<int>();
                if (!adj.TryGetValue(interior[i], out var ns)) { deg[i] = 1; continue; }
                deg[i] = ns.Count;
                foreach (int q in ns) { if (uv.TryGetValue(q, out var b)) rhs[i] += b; else nbr[i].Add(index[q]); }
            }
            var xs = new Vector2[k]; for (int i = 0; i < k; i++) xs[i] = new Vector2(0.5f, 0.5f);
            SolveLaplacian(deg, nbr.Select(l => l.Select(j => (j, 1f)).ToArray()).ToArray(), rhs, xs, 4 * k + 50);
            for (int i = 0; i < k; i++) uv[interior[i]] = xs[i];
        }
        foreach (var (pt, val) in uv) SetPointUv(m, topo, pt, val);
        return true;
    }

    /// <summary>
    /// 대칭 양의 정부호 희소 시스템 (diag_i·x_i − Σ w_ij·x_j = rhs_i)을 켤레 기울기법으로 푼다(x와 y 성분을 따로, x는 시작값이자 결과).
    /// Tutte 초기화와 Unfold(ARAP) 글로벌 단계가 쓴다.
    /// </summary>
    /// <param name="diag">대각 성분.</param>
    /// <param name="off">행마다 (열, 가중치) — 행렬 성분은 −가중치.</param>
    /// <param name="rhs">우변.</param>
    /// <param name="x">시작값(결과를 덮어씀).</param>
    /// <param name="maxIter">최대 반복 횟수.</param>
    internal static void SolveLaplacian(float[] diag, (int j, float w)[][] off, Vector2[] rhs, Vector2[] x, int maxIter)
    {
        int k = x.Length;
        Vector2[] Mul(Vector2[] v) { var y = new Vector2[k]; for (int i = 0; i < k; i++) { var s = v[i] * diag[i]; foreach (var (j, w) in off[i]) s -= v[j] * w; y[i] = s; } return y; }
        var r = Mul(x); for (int i = 0; i < k; i++) r[i] = rhs[i] - r[i];
        var p = (Vector2[])r.Clone();
        double rrX = 0, rrY = 0; foreach (var v in r) { rrX += v.X * (double)v.X; rrY += v.Y * (double)v.Y; }
        double stop = 1e-14 * Math.Max(1, k);
        for (int it = 0; it < maxIter && (rrX > stop || rrY > stop); it++)
        {
            var ap = Mul(p);
            double pApX = 0, pApY = 0; for (int i = 0; i < k; i++) { pApX += p[i].X * (double)ap[i].X; pApY += p[i].Y * (double)ap[i].Y; }
            float ax = pApX > 1e-30 ? (float)(rrX / pApX) : 0, ay = pApY > 1e-30 ? (float)(rrY / pApY) : 0;
            double nX = 0, nY = 0;
            for (int i = 0; i < k; i++)
            {
                x[i] += new Vector2(ax * p[i].X, ay * p[i].Y);
                r[i] -= new Vector2(ax * ap[i].X, ay * ap[i].Y);
                nX += r[i].X * (double)r[i].X; nY += r[i].Y * (double)r[i].Y;
            }
            float bx = rrX > 1e-30 ? (float)(nX / rrX) : 0, by = rrY > 1e-30 ? (float)(nY / rrY) : 0;
            for (int i = 0; i < k; i++) p[i] = r[i] + new Vector2(bx * p[i].X, by * p[i].Y);
            rrX = nX; rrY = nY;
        }
    }

    /// <summary>Cut UV Edges: 선택 엣지를 심으로 만든다.</summary>
    /// <remarks>메시 경계 엣지(He1 &lt; 0)는 이미 끊겨 있으므로 무시한다. UV 값은 바꾸지 않는다.</remarks>
    public static void CutEdges(PolyMesh m, IEnumerable<int> edges)
    {
        foreach (int e in edges)
        {
            if (e < 0 || e >= m.EdgeCount || !m.Edges[e].Alive || m.Edges[e].He1 < 0) continue;
            var ed = m.Edges[e]; ed.Seam = true; m.Edges[e] = ed;
        }
    }

    /// <summary>내부 엣지가 이미 꿰매져 있는지: 심이 아니고 양쪽 면의 코너 UV가 엣지 양끝에서 같으면 true(경계 엣지는 false).</summary>
    /// <remarks>심 플래그 없이 UV만 갈라진 엣지(투영·Unitize 결과 등)도 Sew 대상이므로, Cut/Sew 툴은 플래그가 아니라 이것으로 건너뛸지 정한다.</remarks>
    public static bool IsEdgeSewn(PolyMesh m, int e)
    {
        if (e < 0 || e >= m.EdgeCount || !m.Edges[e].Alive || m.Edges[e].He1 < 0 || m.Edges[e].Seam) return false;
        var ed = m.Edges[e]; var he = m.Hes[ed.He0]; var tw = m.Hes[ed.He1];
        return Vector2.DistanceSquared(he.Uv0, m.Hes[tw.Next].Uv0) < 1e-10f && Vector2.DistanceSquared(m.Hes[he.Next].Uv0, tw.Uv0) < 1e-10f;
    }

    /// <summary>Sew UV Edges: 심을 해제하고 양쪽 코너 UV를 평균으로 맞춘다.</summary>
    /// <remarks>
    /// 엣지 양끝 정점마다 두 면의 코너 UV 평균을 구해 네 코너에 써서 이음매를 붙인다(셸이 멀리 떨어져 있으면 중간에서 만난다).
    /// 같은 정점의 다른 코너(엣지에 닿지 않는 면)는 건드리지 않으므로 다른 셸 이동은 MoveAndSew를 쓴다.
    /// </remarks>
    public static void SewEdges(PolyMesh m, IEnumerable<int> edges)
    {
        foreach (int e in edges)
        {
            if (e < 0 || e >= m.EdgeCount || !m.Edges[e].Alive || m.Edges[e].He1 < 0) continue;
            var ed = m.Edges[e]; ed.Seam = false; m.Edges[e] = ed;
            var he = m.Hes[ed.He0]; var tw = m.Hes[ed.He1];
            // 정점 a: he, tw.Next / 정점 b: he.Next, tw
            int a1 = ed.He0, a2 = tw.Next, b1 = he.Next, b2 = ed.He1;
            var ua = (m.Hes[a1].Uv0 + m.Hes[a2].Uv0) * 0.5f; var ub = (m.Hes[b1].Uv0 + m.Hes[b2].Uv0) * 0.5f;
            SetUv(m, a1, ua); SetUv(m, a2, ua); SetUv(m, b1, ub); SetUv(m, b2, ub);
        }
    }

    /// <summary>UV 점들을 2D 변환(행벡터 3x2 아핀: uv' = uv·A + t).</summary>
    /// <remarks>UvTopology 점의 Uv와 그 점의 모든 코너 Uv0을 함께 갱신한다. W/E/R UV 조작기 드래그가 쓴다.</remarks>
    public static void TransformPoints(PolyMesh m, UvTopology topo, IEnumerable<int> points, Matrix3x2 xf)
    {
        foreach (int p in points)
        {
            var pt = topo.Points[p];
            var uv = Vector2.Transform(pt.Uv, xf);
            pt.Uv = uv;
            foreach (int he in pt.HalfEdges) SetUv(m, he, uv);
        }
    }

    /// <summary>UV 점 하나를 지정 좌표로 옮긴다(점의 모든 코너를 같은 값으로).</summary>
    public static void SetPointUv(PolyMesh m, UvTopology topo, int point, Vector2 uv)
    {
        var pt = topo.Points[point];
        pt.Uv = uv;
        foreach (int he in pt.HalfEdges) SetUv(m, he, uv);
    }

    /// <summary>Flip U 또는 V (선택 UV 점의 경계 상자 기준).</summary>
    /// <remarks>u' = min + max − u (또는 v). 선택 점들의 경계 상자 중심을 기준으로 거울 반사한다.</remarks>
    public static void Flip(PolyMesh m, UvTopology topo, IEnumerable<int> points, bool flipU)
    {
        var list = points.ToList();
        if (list.Count == 0) return;
        var (min, max) = Bounds(list.Select(p => topo.Points[p].Uv));
        foreach (int p in list)
        {
            var uv = topo.Points[p].Uv;
            uv = flipU ? new Vector2(min.X + max.X - uv.X, uv.Y) : new Vector2(uv.X, min.Y + max.Y - uv.Y);
            SetPointUv(m, topo, p, uv);
        }
    }

    /// <summary>
    /// Unfold(이완): 셸마다 각 삼각형이 3D 모양(프로크루스테스 맞춤)을 따르도록 반복해서 UV 점을 당긴다.
    /// 초기값은 현재 UV. 로컬/글로벌 교대 ARAP(균일 가중).
    /// </summary>
    /// <remarks>핀 집합 없이 호출하는 오버로드. UvPoint.Pinned만 고정된다.</remarks>
    public static void UnfoldRelax(PolyMesh m, UvTopology topo, IEnumerable<int> shells, int iterations = 60) => UnfoldRelax(m, topo, shells, iterations, null);

    /// <summary>pinned(UV 점 ID)와 Pin된 점은 움직이지 않는다.</summary>
    /// <remarks>
    /// 알고리즘(로컬/글로벌 교대 ARAP의 단순화):
    /// 1) 셸에 속한 렌더 삼각형마다 3D 모양을 자기 평면 2D로 펼친 로컬 좌표(a=(0,0), b=(|ab|,0), c)를 만든다.
    /// 2) 3D 넓이와 현재 UV 넓이 비로 스케일을 정해 전체 크기를 유지한다.
    /// 3) 반복마다 로컬 단계에서 각 삼각형에 최적 회전(2D 프로크루스테스: atan2(Σ l×q, Σ l·q))을 구하고,
    ///    글로벌 단계에서 Σ_변 |(u_i − u_j) − R(l_i − l_j)|²를 최소화하는 라플라스 방정식을 켤레 기울기법으로 푼다(핀 점은 고정,
    ///    핀이 없는 셸은 점 하나를 고정). 셸 전체가 거울상(부호 넓이 음수)이면 로컬 삼각형도 뒤집어 맞춘다.
    /// 4) 결과를 셸 내 점에만 기록한다.
    /// </remarks>
    /// <param name="pinned">추가로 고정할 UV 점 ID(Optimize가 경계 고정에 사용). null 가능.</param>
    public static void UnfoldRelax(PolyMesh m, UvTopology topo, IEnumerable<int> shells, int iterations, HashSet<int>? pinned)
    {
        var shellSet = new HashSet<int>(shells);
        bool IsPinned(int p) => topo.Points[p].Pinned || (pinned != null && pinned.Contains(p));
        // 코너 → 하프에지 매핑이 있는 테셀레이션(n각형은 삼각형으로 쪼개짐)
        var render = MeshTessellator.Build(m);
        // 셸별 삼각형 목록: (uv점 3개, 3D 모양을 2D로 펼친 로컬 좌표 3개)
        var tris = new List<(int[] pts, Vector2[] local)>();
        for (int t = 0; t < render.TriangleCount; t++)
        {
            int h0 = render.CornerToHalfEdge[render.Indices[t * 3]], h1 = render.CornerToHalfEdge[render.Indices[t * 3 + 1]], h2 = render.CornerToHalfEdge[render.Indices[t * 3 + 2]];
            int p0 = topo.HeToPoint[h0], p1 = topo.HeToPoint[h1], p2 = topo.HeToPoint[h2];
            if (!shellSet.Contains(topo.Points[p0].Shell)) continue;
            var a = render.Positions[render.Indices[t * 3]]; var b = render.Positions[render.Indices[t * 3 + 1]]; var c = render.Positions[render.Indices[t * 3 + 2]];
            // 삼각형 로컬 2D: a=(0,0), b=(|ab|,0), c=투영
            var ab = b - a; var ac = c - a;
            float lab = ab.Length(); if (lab < 1e-9f) continue;
            var ex = ab / lab;
            float cx = Vector3.Dot(ac, ex);
            float cy = (ac - ex * cx).Length();
            tris.Add((new[] { p0, p1, p2 }, new[] { Vector2.Zero, new Vector2(lab, 0), new Vector2(cx, cy) }));
        }
        if (tris.Count == 0) return;
        // 점별 작업 위치(전체 점 배열; 셸 밖 점은 건드리지 않음)
        var pos = topo.Points.Select(p => p.Uv).ToArray();
        // 거울상(부호 넓이 음수) 셸은 로컬 삼각형도 뒤집어 맞춘다 — 회전만으로는 반사를 맞출 수 없어 셸이 접히거나 무너진다
        var shellArea = new Dictionary<int, float>();
        foreach (var (pts, _) in tris)
        {
            int s = topo.Points[pts[0]].Shell;
            shellArea[s] = shellArea.GetValueOrDefault(s) + Cross(pos[pts[1]] - pos[pts[0]], pos[pts[2]] - pos[pts[0]]);
        }
        for (int i = 0; i < tris.Count; i++)
        {
            if (shellArea[topo.Points[tris[i].pts[0]].Shell] >= 0) continue;
            var l = tris[i].local;
            for (int c = 0; c < 3; c++) l[c] = new Vector2(l[c].X, -l[c].Y);
        }
        // 스케일 정규화: 현재 UV 면적과 3D 면적 비율
        float uvArea = 0, area3 = 0;
        foreach (var (pts, local) in tris)
        {
            uvArea += MathF.Abs(Cross(pos[pts[1]] - pos[pts[0]], pos[pts[2]] - pos[pts[0]]));
            area3 += MathF.Abs(Cross(local[1] - local[0], local[2] - local[0]));
        }
        float scale = uvArea > 1e-12f && area3 > 1e-12f ? MathF.Sqrt(uvArea / area3) : 1f;
        // 글로벌 단계 행렬(균일 가중 ARAP): 삼각형 변 (i, j)마다 A_ii += 1, A_ij −= 1. 고정점은 우변으로 옮긴다.
        // 고정점이 없는 셸은 첫 점 하나를 현재 위치에 고정해 평행 이동 자유도를 없앤다(셸 위치는 Layout이 다시 정한다).
        var fixedPts = new HashSet<int>();
        var shellHasPin = new HashSet<int>();
        foreach (var (pts, _) in tris) foreach (int p in pts) if (IsPinned(p)) { fixedPts.Add(p); shellHasPin.Add(topo.Points[p].Shell); }
        foreach (var (pts, _) in tris)
        {
            int s = topo.Points[pts[0]].Shell;
            if (shellHasPin.Add(s)) fixedPts.Add(pts[0]);
        }
        var index = new Dictionary<int, int>();
        var free = new List<int>();
        foreach (var (pts, _) in tris) foreach (int p in pts) if (!fixedPts.Contains(p) && !index.ContainsKey(p)) { index[p] = free.Count; free.Add(p); }
        int k = free.Count;
        var diag = new float[k];
        var off = new Dictionary<int, float>[k];
        for (int i = 0; i < k; i++) off[i] = new Dictionary<int, float>();
        foreach (var (pts, _) in tris)
            for (int a = 0; a < 3; a++)
                for (int b = 0; b < 3; b++)
                {
                    if (a == b || !index.TryGetValue(pts[a], out int ia)) continue;
                    diag[ia] += 1;
                    if (index.TryGetValue(pts[b], out int ib)) off[ia][ib] = off[ia].GetValueOrDefault(ib) + 1;
                }
        var offList = off.Select(d => d.Select(kv => (kv.Key, kv.Value)).ToArray()).ToArray();
        var rhs = new Vector2[k]; var x = new Vector2[k];
        var rot = new (float cs, float sn)[tris.Count];
        for (int it = 0; it < iterations && k > 0; it++)
        {
            // 로컬 단계: 삼각형마다 현재 UV에 가장 잘 맞는 회전(2D 프로크루스테스)
            for (int t = 0; t < tris.Count; t++)
            {
                var (pts, local) = tris[t];
                var cu = (pos[pts[0]] + pos[pts[1]] + pos[pts[2]]) / 3f;
                var cl = (local[0] + local[1] + local[2]) / 3f;
                float sxx = 0, sxy = 0;
                for (int i = 0; i < 3; i++)
                {
                    var q = pos[pts[i]] - cu; var l = (local[i] - cl) * scale;
                    sxx += l.X * q.X + l.Y * q.Y; sxy += l.X * q.Y - l.Y * q.X;
                }
                float ang = MathF.Atan2(sxy, sxx);
                rot[t] = (MathF.Cos(ang), MathF.Sin(ang));
            }
            // 글로벌 단계: Σ_변 |(u_i − u_j) − R(l_i − l_j)|² 최소화 → 라플라스 방정식(켤레 기울기법, 현재 위치에서 시작)
            Array.Clear(rhs);
            for (int t = 0; t < tris.Count; t++)
            {
                var (pts, local) = tris[t]; var (cs, sn) = rot[t];
                for (int a = 0; a < 3; a++)
                {
                    if (!index.TryGetValue(pts[a], out int ia)) continue;
                    for (int b = 0; b < 3; b++)
                    {
                        if (a == b) continue;
                        var l = (local[a] - local[b]) * scale;
                        rhs[ia] += new Vector2(l.X * cs - l.Y * sn, l.X * sn + l.Y * cs);
                        if (!index.ContainsKey(pts[b])) rhs[ia] += pos[pts[b]];
                    }
                }
            }
            for (int i = 0; i < k; i++) x[i] = pos[free[i]];
            SolveLaplacian(diag, offList, rhs, x, 60);
            // 수렴: 이번 반복에서 점들이 거의 움직이지 않았으면(평균 이동 < 셸 크기의 1e-6) 남은 반복을 건너뛴다
            double moved = 0;
            for (int i = 0; i < k; i++) { moved += Vector2.DistanceSquared(pos[free[i]], x[i]); pos[free[i]] = x[i]; }
            if (moved / k < 1e-12 * MathF.Max(uvArea, 1e-6f)) break;
        }
        // 핀이 없는 셸은 결과를 처음 UV에 가장 잘 맞게 강체 정렬(회전+이동)해 셸이 제자리·원래 방향을 유지하게 한다
        // (고정한 점 하나는 임의로 고른 것이라 ARAP 해가 그 점을 중심으로 돌아가 있을 수 있다)
        var userPinnedShells = new HashSet<int>();
        foreach (var (pts, _) in tris) foreach (int p in pts) if (IsPinned(p)) userPinnedShells.Add(topo.Points[p].Shell);
        var shellPts = new Dictionary<int, HashSet<int>>();
        foreach (var (pts, _) in tris) foreach (int p in pts) { int s = topo.Points[p].Shell; if (!shellPts.TryGetValue(s, out var l)) shellPts[s] = l = new HashSet<int>(); l.Add(p); }
        foreach (var (s, ps) in shellPts)
        {
            if (userPinnedShells.Contains(s)) continue;
            var c0 = Vector2.Zero; var c1 = Vector2.Zero;
            foreach (int p in ps) { c0 += topo.Points[p].Uv; c1 += pos[p]; }
            c0 /= ps.Count; c1 /= ps.Count;
            float sDot = 0, sCross = 0;
            foreach (int p in ps) { var f = pos[p] - c1; var o = topo.Points[p].Uv - c0; sDot += Vector2.Dot(f, o); sCross += Cross(f, o); }
            float ang = MathF.Atan2(sCross, sDot); float cs = MathF.Cos(ang), sn = MathF.Sin(ang);
            foreach (int p in ps) { var f = pos[p] - c1; pos[p] = c0 + new Vector2(f.X * cs - f.Y * sn, f.X * sn + f.Y * cs); }
        }
        // 대상 셸 점만 메시에 반영
        for (int i = 0; i < pos.Length; i++) if (shellSet.Contains(topo.Points[i].Shell) && !IsPinned(i)) SetPointUv(m, topo, i, pos[i]);
    }

    /// <summary>2D 외적(z 성분) a.x·b.y − a.y·b.x. 부호 있는 평행사변형 넓이.</summary>
    public static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

    /// <summary>Layout: 셸들을 0..1 사각형에 선반(shelf) 방식으로 패킹한다. 종횡비 유지, 간격 spacing.</summary>
    public static void Layout(PolyMesh m, UvTopology topo, IEnumerable<int> shells, float spacing = 0.01f) => Layout(m, topo, shells, spacing, false, Vector2.Zero, 1f);

    /// <summary>Layout 옵션: rotateToFit = 셸을 세워(높이 ≤ 폭) 넣기, tileOrigin/tileSize = 대상 타일(UDIM).</summary>
    /// <remarks>
    /// 1) rotateToFit이면 세로가 긴 셸을 −90° 돌려 눕힌다.
    /// 2) 셸마다 UV 경계 상자를 구하고 (간격 포함) 전체 넓이로 첫 스케일 √(0.85/넓이)를 추정한다.
    /// 3) 높이 내림차순으로 정렬해 왼쪽→오른쪽 행을 채우고 넘치면 다음 행(선반 패킹). 0..1을 넘으면 스케일 ×0.9로 최대 80번 재시도(시작 스케일은 가장 큰 셸이 타일에 들어가는 값 이하).
    /// 4) 배치 위치 + (점 − 셸 min)·scale 을 타일 원점/크기로 옮겨 기록한다. 셸 안 모양은 균일 스케일만 되므로 상대 비율이 유지된다.
    /// </remarks>
    public static void Layout(PolyMesh m, UvTopology topo, IEnumerable<int> shells, float spacing, bool rotateToFit, Vector2 tileOrigin, float tileSize)
    {
        var shellList = shells.Distinct().ToList();
        if (shellList.Count == 0) return;
        if (rotateToFit)
            foreach (int s in shellList)
            {
                var pts = topo.PointsInShell(s).ToList();
                if (pts.Count == 0) continue;
                var (mn, mx) = Bounds(pts.Select(p => topo.Points[p].Uv));
                if (mx.Y - mn.Y > mx.X - mn.X) TransformPoints(m, topo, pts, Matrix3x2.CreateRotation(-MathF.PI / 2f, (mn + mx) * 0.5f));
            }
        // 셸별 (셸, 경계 min, 크기). 크기 0 방지로 최소 1e-6
        var boxes = new List<(int shell, Vector2 min, Vector2 size)>();
        foreach (int s in shellList)
        {
            var pts = topo.PointsInShell(s).Select(p => topo.Points[p].Uv).ToList();
            if (pts.Count == 0) continue;
            var (min, max) = Bounds(pts);
            boxes.Add((s, min, Vector2.Max(max - min, new Vector2(1e-6f))));
        }
        // 전체 면적으로 스케일 추정 후 높이 내림차순 선반 패킹; 안 들어가면 스케일을 줄여 재시도
        float total = boxes.Sum(b => (b.size.X + spacing) * (b.size.Y + spacing));
        float scale = total > 0 ? MathF.Sqrt(0.85f / total) : 1f;
        // 가장 긴 셸 하나가 타일 폭/높이를 넘지 않도록 시작 스케일을 제한(가늘고 긴 셸은 넓이 추정만으로는 12번 줄여도 안 들어갔다)
        float maxW = boxes.Count > 0 ? boxes.Max(b => b.size.X) : 0, maxH = boxes.Count > 0 ? boxes.Max(b => b.size.Y) : 0;
        float room = MathF.Max(1f - 2f * spacing, 1e-3f);
        if (maxW > 0) scale = MathF.Min(scale, room / maxW);
        if (maxH > 0) scale = MathF.Min(scale, room / maxH);
        boxes.Sort((a, b) => b.size.Y.CompareTo(a.size.Y));
        var placed = new Dictionary<int, Vector2>();
        for (int attempt = 0; attempt < 80; attempt++)
        {
            placed.Clear();
            float x = spacing, y = spacing, rowH = 0; bool ok = true;
            foreach (var (shell, _, size) in boxes)
            {
                float w = size.X * scale, h = size.Y * scale;
                if (x + w + spacing > 1f) { x = spacing; y += rowH + spacing; rowH = 0; }
                if (y + h + spacing > 1f || x + w + spacing > 1f) { ok = false; break; }
                placed[shell] = new Vector2(x, y);
                x += w + spacing; rowH = MathF.Max(rowH, h);
            }
            if (ok) break;
            scale *= 0.9f;
        }
        // 배치된 셸의 점을 최종 위치로 이동(80번 재시도 후에도 못 넣은 셸은 그대로)
        foreach (var (shell, min, _) in boxes)
        {
            if (!placed.TryGetValue(shell, out var origin)) continue;
            foreach (int p in topo.PointsInShell(shell))
                SetPointUv(m, topo, p, tileOrigin + (origin + (topo.Points[p].Uv - min) * scale) * tileSize);
        }
    }

    /// <summary>UV 점 집합을 포함하는 셸 ID들.</summary>
    public static IEnumerable<int> ShellsOf(UvTopology topo, IEnumerable<int> points) => points.Select(p => topo.Points[p].Shell).Distinct();
}
