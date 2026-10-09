using System.Numerics;
using Cube.Core.Mesh;

namespace Cube.Core.Uv;

/// <summary>Maya UV Editor 셸 연산: Orient / Orient to Edges / Randomize / Stack / Stack Similar / Unstack / Distribute / Gather / Snap Together / Snap and Stack / Flip Reversed / Move and Sew / Stitch.</summary>
/// <remarks>
/// UvOps partial의 일부. 셸 단위 연산은 셸에 속한 모든 UV 점에 같은 2D 아핀 변환(<see cref="TransformPoints"/>)을 적용해
/// 셸 모양을 유지한다. 셸 ID는 호출 시점의 UvTopology 기준이며 연산 뒤 위상이 바뀌면 호출자가 다시 Build한다.
/// </remarks>
public static partial class UvOps
{
    /// <summary>셸에 속한 UV 점들의 경계 상자(min, max).</summary>
    public static (Vector2 min, Vector2 max) ShellBounds(UvTopology topo, int shell) => Bounds(topo.PointsInShell(shell).Select(p => topo.Points[p].Uv));

    /// <summary>셸 전체를 delta만큼 평행 이동한다.</summary>
    private static void MoveShell(PolyMesh m, UvTopology topo, int shell, Vector2 delta) => TransformPoints(m, topo, topo.PointsInShell(shell), Matrix3x2.CreateTranslation(delta));

    /// <summary>셸의 UV 면적(부호 있음; 음수 = 뒤집힘).</summary>
    /// <remarks>면의 대표 하프에지가 속한 UV 점의 셸로 면의 셸을 판정하고, 그 셸 면들의 UV 부호 넓이를 더한다.</remarks>
    public static float ShellSignedArea(PolyMesh m, UvTopology topo, int shell)
    {
        float area = 0;
        for (int f = 0; f < m.FaceCount; f++)
        {
            if (!m.Faces[f].Alive) continue;
            int start = m.Faces[f].HalfEdge;
            if (topo.Points[topo.HeToPoint[start]].Shell != shell) continue;
            area += FaceUvSignedArea(m, f);
        }
        return area;
    }

    /// <summary>
    /// 면 하나의 UV 부호 넓이(신발끈 공식). 반시계(앞면)면 양수, 시계(뒤집힘)면 음수.
    /// Back-facing 선택, Flip Reversed, 왜곡·통계 계산의 기본 함수.
    /// </summary>
    public static float FaceUvSignedArea(PolyMesh m, int f)
    {
        float a = 0;
        var hes = FaceHalfEdges(m, f).ToList();
        for (int i = 0; i < hes.Count; i++) { var p = m.Hes[hes[i]].Uv0; var q = m.Hes[hes[(i + 1) % hes.Count]].Uv0; a += p.X * q.Y - q.X * p.Y; }
        return a * 0.5f;
    }

    /// <summary>Orient Shells: 각 셸을 경계 상자 면적이 최소가 되는 각도로 돌린다(0~180°, 2° 간격).</summary>
    /// <remarks>
    /// 셸 중심(점 평균)을 기준으로 0°, 2°, … 178°를 차례로 시험해 회전한 경계 상자 넓이가 가장 작은 각을 고른다(180° 이상은 대칭이라 불필요).
    /// </remarks>
    public static void OrientShells(PolyMesh m, UvTopology topo, IEnumerable<int> shells)
    {
        foreach (int s in shells.Distinct())
        {
            var pts = topo.PointsInShell(s).ToList(); if (pts.Count < 2) continue;
            var uvs = pts.Select(p => topo.Points[p].Uv).ToList();
            var c = uvs.Aggregate(Vector2.Zero, (a, b) => a + b) / uvs.Count;
            // 최적 각도와 그때의 상자 넓이
            float bestA = 0, bestArea = float.MaxValue;
            for (int deg = 0; deg < 180; deg += 2)
            {
                float ang = deg * MathF.PI / 180f; float cs = MathF.Cos(ang), sn = MathF.Sin(ang);
                float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
                foreach (var uv in uvs) { var d = uv - c; float x = d.X * cs - d.Y * sn, y = d.X * sn + d.Y * cs; minX = MathF.Min(minX, x); maxX = MathF.Max(maxX, x); minY = MathF.Min(minY, y); maxY = MathF.Max(maxY, y); }
                float area = (maxX - minX) * (maxY - minY);
                if (area < bestArea - 1e-9f) { bestArea = area; bestA = ang; }
            }
            // 0°가 최적이면(이미 정렬) 변환하지 않는다
            if (bestA != 0) TransformPoints(m, topo, pts, Matrix3x2.CreateRotation(bestA, c));
        }
    }

    /// <summary>Orient Shell to Edges: 셸을 최소 각도로 돌려 주어진 엣지가 가장 가까운 축(U 가로 또는 V 세로)과 평행이 되게 한다.</summary>
    /// <remarks>
    /// 엣지의 He0 쪽 코너 두 UV 점(a → b) 방향 각 atan2(d)를 가장 가까운 90° 배수로 반올림한 차이만큼 셸 경계 상자 중심을 기준으로 회전한다.
    /// 결과적으로 엣지가 U 또는 V 축에 평행해진다(Maya와 같이 셸을 45° 넘게 돌리지 않음).
    /// </remarks>
    public static void OrientShellToEdge(PolyMesh m, UvTopology topo, int edge)
    {
        if (edge < 0 || edge >= m.EdgeCount || !m.Edges[edge].Alive) return;
        int he = m.Edges[edge].He0;
        int a = topo.HeToPoint[he], b = topo.HeToPoint[m.Hes[he].Next];
        var d = topo.Points[b].Uv - topo.Points[a].Uv;
        if (d.LengthSquared() < 1e-12f) return;
        // 엣지 각을 가장 가까운 90° 배수(U 또는 V 축)로 돌리는 최소 회전(−45°~45°)
        float cur = MathF.Atan2(d.Y, d.X);
        float ang = MathF.Round(cur / (MathF.PI / 2f)) * (MathF.PI / 2f) - cur;
        if (MathF.Abs(ang) < 1e-7f) return;
        int shell = topo.Points[a].Shell;
        var pts = topo.PointsInShell(shell).ToList();
        var (mn, mx) = Bounds(pts.Select(p => topo.Points[p].Uv));
        TransformPoints(m, topo, pts, Matrix3x2.CreateRotation(ang, (mn + mx) * 0.5f));
    }

    /// <summary>Randomize Shells: 셸마다 임의 이동/회전/스케일.</summary>
    /// <remarks>
    /// 셸 경계 상자 중심을 기준으로 스케일(1 ± scale) → 회전(± rotateDeg) → 이동(± translate)을 균등 난수로 적용한다.
    /// 같은 seed면 같은 결과(재현 가능).
    /// </remarks>
    /// <param name="translate">이동 최대량(UV 단위).</param>
    /// <param name="rotateDeg">회전 최대각(도).</param>
    /// <param name="scale">스케일 변화 비율 최대값(0.2 = ±20%). 결과 스케일은 최소 0.01.</param>
    /// <param name="seed">난수 시드.</param>
    public static void RandomizeShells(PolyMesh m, UvTopology topo, IEnumerable<int> shells, float translate, float rotateDeg, float scale, int seed)
    {
        var rng = new Random(seed);
        foreach (int s in shells.Distinct())
        {
            var pts = topo.PointsInShell(s).ToList(); if (pts.Count == 0) continue;
            var (mn, mx) = Bounds(pts.Select(p => topo.Points[p].Uv)); var c = (mn + mx) * 0.5f;
            float r = (float)(rng.NextDouble() * 2 - 1) * rotateDeg * MathF.PI / 180f;
            float sc = 1f + (float)(rng.NextDouble() * 2 - 1) * scale;
            var t = new Vector2((float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 2 - 1)) * translate;
            var xf = Matrix3x2.CreateScale(MathF.Max(sc, 0.01f), c) * Matrix3x2.CreateRotation(r, c) * Matrix3x2.CreateTranslation(t);
            TransformPoints(m, topo, pts, xf);
        }
    }

    /// <summary>Stack Shells: 선택 셸들의 경계 상자 중심을 첫 셸에 맞춘다.</summary>
    public static void StackShells(PolyMesh m, UvTopology topo, IEnumerable<int> shells)
    {
        var list = shells.Distinct().ToList(); if (list.Count < 2) return;
        var (mn0, mx0) = ShellBounds(topo, list[0]); var c0 = (mn0 + mx0) * 0.5f;
        foreach (int s in list.Skip(1)) { var (mn, mx) = ShellBounds(topo, s); MoveShell(m, topo, s, c0 - (mn + mx) * 0.5f); }
    }

    /// <summary>Stack Similar Shells: 점 수·면 수가 같은 셸끼리 묶어 겹친다. 반환값은 묶음 수.</summary>
    /// <remarks>
    /// 셸별 면 수를 먼저 세고 (UV 점 수, 면 수) 키로 묶어 2개 이상인 묶음마다 <see cref="StackShells"/>를 호출한다.
    /// 같은 토폴로지 복제 셸(좌우 대칭 파츠 등)을 겹쳐 텍스처 공간을 공유할 때 쓴다.
    /// </remarks>
    public static int StackSimilarShells(PolyMesh m, UvTopology topo, IEnumerable<int> shells)
    {
        // 셸 → 면 수
        var faceCount = new Dictionary<int, int>();
        for (int f = 0; f < m.FaceCount; f++) { if (!m.Faces[f].Alive) continue; int s = topo.Points[topo.HeToPoint[m.Faces[f].HalfEdge]].Shell; faceCount[s] = faceCount.GetValueOrDefault(s) + 1; }
        int groups = 0;
        foreach (var g in shells.Distinct().GroupBy(s => (topo.PointsInShell(s).Count(), faceCount.GetValueOrDefault(s))))
        {
            var list = g.ToList(); if (list.Count < 2) continue;
            StackShells(m, topo, list); groups++;
        }
        return groups;
    }

    /// <summary>Unstack Shells: 겹친 셸들을 첫 셸 오른쪽으로 간격을 두고 나란히 놓는다.</summary>
    /// <remarks>첫 셸은 그대로 두고 나머지를 첫 셸 바닥(min.Y)에 맞춰 오른쪽으로 차례로 붙인다.</remarks>
    public static void UnstackShells(PolyMesh m, UvTopology topo, IEnumerable<int> shells, float gap = 0.01f)
    {
        var list = shells.Distinct().ToList(); if (list.Count < 2) return;
        var (mn0, mx0) = ShellBounds(topo, list[0]);
        // 다음 셸을 놓을 U 위치
        float x = mx0.X + gap;
        foreach (int s in list.Skip(1))
        {
            var (mn, mx) = ShellBounds(topo, s);
            MoveShell(m, topo, s, new Vector2(x - mn.X, mn0.Y - mn.Y));
            x += (mx.X - mn.X) + gap;
        }
    }

    /// <summary>Distribute Shells: 선택 셸을 축 방향으로 간격 gap을 두고 차례로 놓는다(현재 순서 유지).</summary>
    /// <remarks>
    /// 축 방향 최소 좌표 순으로 정렬해 첫 셸 뒤에 gap 간격으로 이어 붙인다. 다른 축 좌표는 바꾸지 않는다.
    /// </remarks>
    /// <param name="alongU">true면 U(가로), false면 V(세로) 방향으로 배치.</param>
    public static void DistributeShells(PolyMesh m, UvTopology topo, IEnumerable<int> shells, bool alongU, float gap)
    {
        var list = shells.Distinct().OrderBy(s => alongU ? ShellBounds(topo, s).min.X : ShellBounds(topo, s).min.Y).ToList();
        if (list.Count < 2) return;
        var (mn0, mx0) = ShellBounds(topo, list[0]);
        float pos = alongU ? mx0.X + gap : mx0.Y + gap;
        foreach (int s in list.Skip(1))
        {
            var (mn, mx) = ShellBounds(topo, s);
            MoveShell(m, topo, s, alongU ? new Vector2(pos - mn.X, 0) : new Vector2(0, pos - mn.Y));
            pos += (alongU ? mx.X - mn.X : mx.Y - mn.Y) + gap;
        }
    }

    /// <summary>Gather Shells: 셸의 중심이 0..1 타일 안에 오도록 정수 단위로 옮긴다.</summary>
    /// <remarks>경계 상자 중심의 정수 부분(floor)만큼 빼므로 UDIM 타일 밖으로 나간 셸이 0..1 타일로 돌아온다(타일 안 위치는 유지).</remarks>
    public static void GatherShells(PolyMesh m, UvTopology topo, IEnumerable<int> shells)
    {
        foreach (int s in shells.Distinct())
        {
            var (mn, mx) = ShellBounds(topo, s); var c = (mn + mx) * 0.5f;
            var d = new Vector2(-MathF.Floor(c.X), -MathF.Floor(c.Y));
            if (d != Vector2.Zero) MoveShell(m, topo, s, d);
        }
    }

    /// <summary>Snap Together: 점 a가 속한 셸을 옮겨 a가 b 위에 오게 한다.</summary>
    /// <remarks>두 점이 같은 셸이면 아무것도 하지 않는다(셸을 옮겨도 상대 위치가 같으므로).</remarks>
    public static void SnapTogether(PolyMesh m, UvTopology topo, int pointA, int pointB)
    {
        if (topo.Points[pointA].Shell == topo.Points[pointB].Shell) return;
        MoveShell(m, topo, topo.Points[pointA].Shell, topo.Points[pointB].Uv - topo.Points[pointA].Uv);
    }

    /// <summary>Snap and Stack: 선택 셸들의 경계 상자 최소점을 첫 셸에 맞춘다.</summary>
    /// <remarks>Stack Shells와 달리 중심이 아니라 좌하단(min)을 맞춘다.</remarks>
    public static void SnapAndStack(PolyMesh m, UvTopology topo, IEnumerable<int> shells)
    {
        var list = shells.Distinct().ToList(); if (list.Count < 2) return;
        var (mn0, _) = ShellBounds(topo, list[0]);
        foreach (int s in list.Skip(1)) { var (mn, _) = ShellBounds(topo, s); MoveShell(m, topo, s, mn0 - mn); }
    }

    /// <summary>Flip reversed UV shells: UV 면적이 음수(뒤집힌 와인딩)인 셸을 U로 뒤집는다. 반환값은 뒤집은 셸 수.</summary>
    /// <remarks>경계 상자 중심 기준으로 U를 뒤집으면 와인딩이 반대가 되어 부호 넓이가 양수가 된다.</remarks>
    public static int FlipReversedShells(PolyMesh m, UvTopology topo, IEnumerable<int> shells)
    {
        int n = 0;
        foreach (int s in shells.Distinct())
        {
            if (ShellSignedArea(m, topo, s) >= 0) continue;
            Flip(m, topo, topo.PointsInShell(s), flipU: true); n++;
        }
        return n;
    }

    /// <summary>
    /// Move and Sew: 심 엣지마다, 작은 쪽 셸을 강체(회전+이동)로 옮겨 엣지 양끝이 큰 쪽 셸의 대응 점과 겹치게 한 뒤 Sew한다.
    /// 반환값은 처리한 엣지 수.
    /// </summary>
    /// <remarks>
    /// 반복: 처리할 엣지 하나를 꺼내 → 양쪽 면의 UV 점 대응(면0 a→b, 면1 b→a)을 구하고 →
    /// 이미 같은 셸이면 그냥 Sew, 아니면 UV 점 수가 적은 셸(mover)을 유사 변환(이동·균일 스케일·회전)으로 큰 셸(anchor)의 엣지에 맞춘 뒤 Sew →
    /// 같은 두 셸 사이의 다른 심 엣지도 Sew → 셸이 합쳐졌으므로 UvTopology를 다시 만들어 인자로 받은 topo 객체 내용을 교체한다.
    /// </remarks>
    /// <param name="edges">이을 엣지 ID(메시 경계 엣지는 제외됨).</param>
    public static int MoveAndSew(PolyMesh m, UvTopology topo, IEnumerable<int> edges)
    {
        int done = 0;
        // 처리 대상: 살아 있는 내부 엣지
        var remaining = new HashSet<int>(edges.Where(e => e >= 0 && e < m.EdgeCount && m.Edges[e].Alive && m.Edges[e].He1 >= 0));
        while (remaining.Count > 0)
        {
            int e = remaining.First(); remaining.Remove(e);
            var ed = m.Edges[e];
            int h0 = ed.He0, h1 = ed.He1;
            int a0 = topo.HeToPoint[h0], b0 = topo.HeToPoint[m.Hes[h0].Next];   // 면0 쪽: a→b
            int b1 = topo.HeToPoint[h1], a1 = topo.HeToPoint[m.Hes[h1].Next];   // 면1 쪽: b→a
            int s0 = topo.Points[a0].Shell, s1 = topo.Points[a1].Shell;
            if (s0 == s1) { SewEdges(m, new[] { e }); done++; continue; }
            int n0 = topo.PointsInShell(s0).Count(), n1 = topo.PointsInShell(s1).Count();
            // 작은 셸(mover)을 큰 셸(anchor)에 맞춘다
            bool moveS1 = n1 <= n0;
            var (ma, mb, ta, tb) = moveS1 ? (a1, b1, a0, b0) : (a0, b0, a1, b1);
            // mover 쪽 엣지 벡터 d0을 anchor 쪽 d1에 맞추는 회전각과 스케일
            var pa = topo.Points[ma].Uv; var pb = topo.Points[mb].Uv; var qa = topo.Points[ta].Uv; var qb = topo.Points[tb].Uv;
            var d0 = pb - pa; var d1 = qb - qa;
            // 길이가 0인 엣지는 방향을 정할 수 없으니 그냥 Sew
            if (d0.LengthSquared() < 1e-14f || d1.LengthSquared() < 1e-14f) { SewEdges(m, new[] { e }); done++; continue; }
            float ang = MathF.Atan2(d1.Y, d1.X) - MathF.Atan2(d0.Y, d0.X);
            float scale = MathF.Sqrt(d1.LengthSquared() / d0.LengthSquared());
            // pa를 원점으로 → 스케일 → 회전 → qa로 이동 (행벡터 규약이라 왼쪽부터 적용)
            var xf = Matrix3x2.CreateTranslation(-pa) * Matrix3x2.CreateScale(scale) * Matrix3x2.CreateRotation(ang) * Matrix3x2.CreateTranslation(qa);
            int mover = moveS1 ? s1 : s0;
            TransformPoints(m, topo, topo.PointsInShell(mover), xf);
            SewEdges(m, new[] { e }); done++;
            // 같은 두 셸 사이의 나머지 심 엣지는 이제 겹치므로 바로 Sew
            foreach (int e2 in remaining.ToArray())
            {
                var ed2 = m.Edges[e2];
                int p = topo.HeToPoint[ed2.He0], q = topo.HeToPoint[ed2.He1];
                var sp = topo.Points[p].Shell; var sq = topo.Points[q].Shell;
                if ((sp == s0 && sq == s1) || (sp == s1 && sq == s0)) { SewEdges(m, new[] { e2 }); remaining.Remove(e2); done++; }
            }
            // 셸이 합쳐졌으므로 위상 ID를 갱신
            var fresh = UvTopology.Build(m);
            topo.Points.Clear(); topo.Points.AddRange(fresh.Points); topo.HeToPoint = fresh.HeToPoint; topo.ShellCount = fresh.ShellCount;
        }
        return done;
    }
}
