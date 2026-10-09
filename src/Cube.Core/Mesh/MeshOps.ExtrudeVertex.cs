using System.Numerics;

namespace Cube.Core.Mesh;

/// <summary>Extrude Vertex 옵션(Maya Edit Mesh → Extrude, 정점 선택 / polyExtrudeVertex).</summary>
public sealed class ExtrudeVertexOptions
{
    /// <summary>밑면 크기: 정점에서 각 이웃 엣지를 따라 이 비율(0.01~0.95, 엣지 길이 대비)만큼 떨어진 곳에 밑면 꼭짓점을 만든다.</summary>
    public float Width = 0.25f;
    /// <summary>뾰족한 끝(꼭짓점)을 정점 노멀 방향으로 들어 올리는 거리(로컬 단위, 음수면 안쪽으로 파인다).</summary>
    public float Length = 0.2f;
    /// <summary>옆면을 높이 방향으로 나누는 단수(1 = 삼각형 옆면, 2 이상이면 아래쪽은 쿼드 띠).</summary>
    public int Divisions = 1;
}

public static partial class MeshOps
{
    /// <summary>
    /// 정점 Extrude(스파이크, v0.0.59). 하프에지 구조에서는 면 없는 와이어 엣지를 만들 수 없으므로 Maya처럼 정점 주변을 깎아 피라미드를 세운다:
    /// ① 선택 정점 v의 이웃 엣지 v–u마다 v에서 Width 비율 지점에 밑면 점 r(v,u)를 만든다(엣지 플래그는 r–u가 물려받음)
    /// ② v를 쓰던 면은 코너 v를 r(v,들어오는 엣지), r(v,나가는 엣지) 두 점으로 바꿔 모서리를 깎는다
    /// ③ 깎인 자리마다 옆면 삼각형 (r_in, 꼭짓점, r_out)을 붙인다 — 꼭짓점은 v 슬롯을 그대로 써서(ID 유지) 정점 노멀 방향으로 Length만큼 옮긴다
    /// ④ Divisions ≥ 2면 옆면을 높이 방향 단으로 나눈다(이웃 옆면과 단 정점을 공유해 매니폴드 유지).
    /// 경계 정점(열린 부채꼴)도 동작하며 면이 없는 고립 정점은 건너뛴다. 양끝이 모두 선택된 엣지가 있으면 Width를 0.49로 제한해 두 밑면 점이 겹치지 않게 한다.
    /// 코너 UV는 엣지를 따라 보간하고 꼭짓점 UV는 그 면에서의 원래 정점 UV. 노멀 재계산은 호출자 몫.
    /// </summary>
    /// <returns>꼭짓점(스파이크 끝) 정점 ID 목록(원래 선택 정점 ID와 같다).</returns>
    public static List<int> ExtrudeVertices(PolyMesh m, IEnumerable<int> vertIds, ExtrudeVertexOptions o)
    {
        var sel = new HashSet<int>(vertIds.Where(v => v >= 0 && v < m.VertexCount && m.Verts[v].Alive && m.VertexOutgoing(v).Length > 0));
        var apexes = new List<int>();
        if (sel.Count == 0) return apexes;
        float w = Math.Clamp(o.Width, 0.01f, 0.95f);
        int div = Math.Clamp(o.Divisions, 1, 100);
        // 양끝이 모두 선택된 엣지가 있으면 두 밑면 점이 엇갈리지 않게 절반 미만으로
        for (int e = 0; e < m.EdgeCount && w > 0.49f; e++)
        {
            if (!m.Edges[e].Alive) continue;
            var (a, b) = m.EdgeVertices(e);
            if (sel.Contains(a) && sel.Contains(b)) w = 0.49f;
        }

        // 정점 노멀(이웃 면 뉴웰 법선 합 = 면적 가중)과 원래 위치
        var normal = new Dictionary<int, Vector3>(); var origin = new Dictionary<int, Vector3>();
        var faces = new HashSet<int>(); var tmp = new List<int>();
        foreach (int v in sel)
        {
            m.GetVertexFaces(v, tmp);
            var n = Vector3.Zero;
            foreach (int f in tmp.Distinct()) { faces.Add(f); n += FaceNewell(m, f); }
            normal[v] = n.LengthSquared() > 1e-20f ? Vector3.Normalize(n) : Vector3.UnitY;
            origin[v] = m.Verts[v].Position;
        }

        var rb = new FaceRebuilder(m);
        foreach (int f in faces) rb.Capture(f);
        // 밑면 점 r(v,u)와 단 정점(v,u,k): 이웃 옆면끼리 공유하도록 사전으로 만든다
        var ring = new Dictionary<(int v, int u, int k), int>();
        // 밑면 점 r(v,u)가 놓인 원래 엣지 v–u의 플래그. FaceRebuilder.SetParent를 쓰면 r–꼭짓점(v) 엣지까지 상속되므로 직접 복원한다.
        var baseFlags = new Dictionary<(int v, int u), EdgeFlags>();
        int Ring(int v, int u, int k)
        {
            if (ring.TryGetValue((v, u, k), out int id)) return id;
            var basePos = Vector3.Lerp(origin[v], m.Verts[u].Position, w);
            var apexPos = origin[v] + normal[v] * o.Length;
            id = m.AddVertex(Vector3.Lerp(basePos, apexPos, (float)k / div));
            if (k == 0) baseFlags[(v, u)] = GetFlags(m, v, u); // r–u(밑면 조각)가 물려받을 원래 엣지 v–u의 플래그(면을 지우기 전에 읽는다)
            ring[(v, u, k)] = id;
            return id;
        }
        // 새 면 루프(깎인 원래 면 + 옆면) 계산
        var newFaces = new List<(List<Corner> loop, int material)>();
        foreach (var (_, corners, material) in rb.Captured)
        {
            var loop = new List<Corner>();
            int n = corners.Count;
            for (int i = 0; i < n; i++)
            {
                var c = corners[i];
                if (!sel.Contains(c.Vertex)) { loop.Add(c); continue; }
                var p = corners[(i - 1 + n) % n]; var q = corners[(i + 1) % n];
                int v = c.Vertex;
                var uvIn = Vector2.Lerp(c.Uv, p.Uv, w); var uvOut = Vector2.Lerp(c.Uv, q.Uv, w);
                int rIn = Ring(v, p.Vertex, 0), rOut = Ring(v, q.Vertex, 0);
                loop.Add(new Corner(rIn, uvIn, Vector3.Zero));
                loop.Add(new Corner(rOut, uvOut, Vector3.Zero));
                // 옆면: (in_k, in_k+1, out_k+1, out_k) 쿼드 띠 + 맨 위 (in_top, 꼭짓점, out_top) 삼각형 — 원래 모서리 (r_in, v, r_out)와 같은 감김
                for (int k = 0; k < div - 1; k++)
                {
                    float t0 = (float)k / div, t1 = (float)(k + 1) / div;
                    newFaces.Add((new List<Corner>
                    {
                        new(Ring(v, p.Vertex, k), Vector2.Lerp(uvIn, c.Uv, t0), Vector3.Zero),
                        new(Ring(v, p.Vertex, k + 1), Vector2.Lerp(uvIn, c.Uv, t1), Vector3.Zero),
                        new(Ring(v, q.Vertex, k + 1), Vector2.Lerp(uvOut, c.Uv, t1), Vector3.Zero),
                        new(Ring(v, q.Vertex, k), Vector2.Lerp(uvOut, c.Uv, t0), Vector3.Zero),
                    }, material));
                }
                float tt = (float)(div - 1) / div;
                newFaces.Add((new List<Corner>
                {
                    new(Ring(v, p.Vertex, div - 1), Vector2.Lerp(uvIn, c.Uv, tt), Vector3.Zero),
                    new(v, c.Uv, Vector3.Zero),
                    new(Ring(v, q.Vertex, div - 1), Vector2.Lerp(uvOut, c.Uv, tt), Vector3.Zero),
                }, material));
            }
            newFaces.Add((loop, material));
        }
        rb.RemoveCaptured();
        // 꼭짓점: 원래 정점 슬롯을 들어 올린다(면을 모두 지운 뒤라 v는 잠시 고립 상태)
        foreach (int v in sel)
        {
            var vv = m.Verts[v]; vv.Position = origin[v] + normal[v] * o.Length; m.Verts[v] = vv;
            m.LockedNormals.Remove(v);
            apexes.Add(v);
        }
        foreach (var (loop, material) in newFaces) rb.AddFace(loop, material);
        // 원래 엣지 위에 남은 조각: r(v,u)–u, 양끝이 모두 선택됐으면 r(v,u)–r(u,v)
        foreach (var ((v, u), f) in baseFlags)
        {
            int r = ring[(v, u, 0)];
            SetFlags(m, r, ring.TryGetValue((u, v, 0), out int ru) ? ru : u, f);
        }
        m.BumpTopology();
        return apexes;
    }

    /// <summary>면의 뉴웰 법선(정규화하지 않음 = 길이가 면적의 2배).</summary>
    private static Vector3 FaceNewell(PolyMesh m, int f)
    {
        var n = Vector3.Zero;
        int start = m.Faces[f].HalfEdge, he = start;
        do
        {
            var a = m.Verts[m.Hes[he].Vertex].Position; var b = m.Verts[m.Hes[m.Hes[he].Next].Vertex].Position;
            n += new Vector3((a.Y - b.Y) * (a.Z + b.Z), (a.Z - b.Z) * (a.X + b.X), (a.X - b.X) * (a.Y + b.Y));
            he = m.Hes[he].Next;
        } while (he != start);
        return n;
    }
}
