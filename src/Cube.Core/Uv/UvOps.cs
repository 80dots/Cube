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
        // 심이 반영된 셸 구조로 이완 후 다시 구조를 만들어 패킹
        var topo = UvTopology.Build(m);
        UnfoldRelax(m, topo, Enumerable.Range(0, topo.ShellCount), unfoldIterations);
        topo = UvTopology.Build(m);
        Layout(m, topo, Enumerable.Range(0, topo.ShellCount));
        return seams.Count;
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
    /// 초기값은 현재 UV. 저폴리용 단순 ARAP 근사.
    /// </summary>
    /// <remarks>핀 집합 없이 호출하는 오버로드. UvPoint.Pinned만 고정된다.</remarks>
    public static void UnfoldRelax(PolyMesh m, UvTopology topo, IEnumerable<int> shells, int iterations = 60) => UnfoldRelax(m, topo, shells, iterations, null);

    /// <summary>pinned(UV 점 ID)와 Pin된 점은 움직이지 않는다.</summary>
    /// <remarks>
    /// 알고리즘(로컬/글로벌 교대 ARAP의 단순화):
    /// 1) 셸에 속한 렌더 삼각형마다 3D 모양을 자기 평면 2D로 펼친 로컬 좌표(a=(0,0), b=(|ab|,0), c)를 만든다.
    /// 2) 3D 넓이와 현재 UV 넓이 비로 스케일을 정해 전체 크기를 유지한다.
    /// 3) 반복마다 각 삼각형에 대해 현재 UV 삼각형에 로컬 삼각형을 최적 회전(2D 프로크루스테스: atan2(Σ l×q, Σ l·q))으로 맞춘 목표 위치를 계산하고,
    ///    점마다 모든 목표의 평균으로 옮긴다(핀 점은 고정).
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
        // 스케일 정규화: 현재 UV 면적과 3D 면적 비율
        float uvArea = 0, area3 = 0;
        foreach (var (pts, local) in tris)
        {
            uvArea += MathF.Abs(Cross(pos[pts[1]] - pos[pts[0]], pos[pts[2]] - pos[pts[0]]));
            area3 += MathF.Abs(Cross(local[1] - local[0], local[2] - local[0]));
        }
        float scale = uvArea > 1e-12f && area3 > 1e-12f ? MathF.Sqrt(uvArea / area3) : 1f;
        // acc/cnt: 반복마다 점별 목표 위치 합과 개수
        var acc = new Vector2[pos.Length]; var cnt = new int[pos.Length];
        for (int it = 0; it < iterations; it++)
        {
            Array.Clear(acc); Array.Clear(cnt);
            foreach (var (pts, local) in tris)
            {
                // 현재 UV 삼각형에 로컬 삼각형을 강체(회전+이동, 스케일 고정)로 맞춘다
                var cu = (pos[pts[0]] + pos[pts[1]] + pos[pts[2]]) / 3f;
                var cl = (local[0] + local[1] + local[2]) / 3f;
                float sxx = 0, sxy = 0;
                for (int i = 0; i < 3; i++)
                {
                    var q = pos[pts[i]] - cu; var l = (local[i] - cl) * scale;
                    sxx += l.X * q.X + l.Y * q.Y; sxy += l.X * q.Y - l.Y * q.X;
                }
                float ang = MathF.Atan2(sxy, sxx);
                float cs = MathF.Cos(ang), sn = MathF.Sin(ang);
                for (int i = 0; i < 3; i++)
                {
                    var l = (local[i] - cl) * scale;
                    var target = cu + new Vector2(l.X * cs - l.Y * sn, l.X * sn + l.Y * cs);
                    acc[pts[i]] += target; cnt[pts[i]]++;
                }
            }
            // 글로벌 단계: 목표 평균으로 이동(고정점 제외)
            for (int i = 0; i < pos.Length; i++) if (cnt[i] > 0 && !IsPinned(i)) pos[i] = acc[i] / cnt[i];
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
    /// 3) 높이 내림차순으로 정렬해 왼쪽→오른쪽 행을 채우고 넘치면 다음 행(선반 패킹). 0..1을 넘으면 스케일 ×0.9로 최대 12번 재시도.
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
        boxes.Sort((a, b) => b.size.Y.CompareTo(a.size.Y));
        var placed = new Dictionary<int, Vector2>();
        for (int attempt = 0; attempt < 12; attempt++)
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
        // 배치된 셸의 점을 최종 위치로 이동(12번 재시도 후에도 못 넣은 셸은 그대로)
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
