using Cube.Core.Mesh;
using Cube.Core.Uv;

namespace Cube.Core.Selection;

/// <summary>Maya의 Grow(&gt;)/Shrink(&lt;)/Convert Selection에 해당하는 컴포넌트 집합 연산.</summary>
/// <remarks>모두 한 노드의 메시 ID 집합(HashSet)을 직접 다룬다. Grow/Shrink는 집합을 제자리에서 바꾸고, 나머지는 새 집합을 돌려준다.</remarks>
public static class SelectionOps
{
    /// <summary>선택을 한 고리 넓힌다(Maya Grow Selection). 집합을 제자리에서 바꾼다.</summary>
    /// <remarks>정점 = 이웃 정점(같은 면에서 앞뒤 코너), 엣지 = 양 끝 정점에 닿는 모든 엣지, 면 = 면의 정점을 공유하는 모든 면.</remarks>
    public static void Grow(PolyMesh m, SelectMode mode, HashSet<int> set)
    {
        // 반복 중 집합을 바꾸지 않도록 추가분을 따로 모았다가 마지막에 합친다.
        var add = new HashSet<int>();
        var tmp = new List<int>();
        switch (mode)
        {
            case SelectMode.Vertex:
                foreach (int v in set)
                    // 정점에서 나가는 하프에지마다 그 면에서의 다음/이전 코너 정점을 추가.
                    foreach (int he in m.VertexOutgoing(v)) { add.Add(m.Hes[m.Hes[he].Next].Vertex); add.Add(m.Hes[m.Hes[he].Prev].Vertex); }
                break;
            case SelectMode.Edge:
                foreach (int e in set)
                {
                    var (a, b) = m.EdgeVertices(e);
                    m.GetVertexEdges(a, tmp); add.UnionWith(tmp);
                    m.GetVertexEdges(b, tmp); add.UnionWith(tmp);
                }
                break;
            case SelectMode.Face:
                foreach (int f in set)
                {
                    m.GetFaceVertices(f, tmp);
                    // tmp를 GetVertexFaces가 덮어쓰므로 정점 목록은 배열로 복사해 순회한다.
                    foreach (int v in tmp.ToArray()) { m.GetVertexFaces(v, tmp); add.UnionWith(tmp); }
                }
                break;
        }
        set.UnionWith(add);
    }

    /// <summary>선택의 가장자리 한 고리를 깎는다(Maya Shrink Selection). 집합을 제자리에서 바꾼다.</summary>
    /// <remarks>정점 = 이웃 정점 중 하나라도 비선택이면 제거, 엣지 = 양 끝 정점에 닿는 엣지 중 비선택이 있으면 제거, 면 = 정점을 공유하는 면 중 비선택이 있으면 제거.</remarks>
    public static void Shrink(PolyMesh m, SelectMode mode, HashSet<int> set)
    {
        // 판정은 원래 집합 기준으로 하고 제거는 마지막에 한 번에.
        var remove = new HashSet<int>();
        var tmp = new List<int>();
        switch (mode)
        {
            case SelectMode.Vertex:
                foreach (int v in set)
                    foreach (int he in m.VertexOutgoing(v))
                    {
                        if (!set.Contains(m.Hes[m.Hes[he].Next].Vertex) || !set.Contains(m.Hes[m.Hes[he].Prev].Vertex)) { remove.Add(v); break; }
                    }
                break;
            case SelectMode.Edge:
                foreach (int e in set)
                {
                    var (a, b) = m.EdgeVertices(e);
                    bool border = false;
                    m.GetVertexEdges(a, tmp); foreach (int x in tmp) if (!set.Contains(x)) { border = true; break; }
                    if (!border) { m.GetVertexEdges(b, tmp); foreach (int x in tmp) if (!set.Contains(x)) { border = true; break; } }
                    if (border) remove.Add(e);
                }
                break;
            case SelectMode.Face:
                foreach (int f in set)
                {
                    m.GetFaceVertices(f, tmp);
                    bool border = false;
                    foreach (int v in tmp.ToArray())
                    {
                        var faces = new List<int>(); m.GetVertexFaces(v, faces);
                        foreach (int x in faces) if (!set.Contains(x)) { border = true; break; }
                        if (border) break;
                    }
                    if (border) remove.Add(f);
                }
                break;
        }
        set.ExceptWith(remove);
    }

    /// <summary>UV 점 선택을 한 고리 넓힌다(UV 모드 Grow). 같은 면에서 앞뒤 코너의 UV 점을 더한다(심 건너편 UV 점은 이웃이 아님).</summary>
    /// <param name="topo">메시의 현재 UV 토폴로지(UV 점 ID 기준).</param>
    /// <param name="set">UV 점 ID 집합(제자리에서 바뀜). 범위를 벗어난 ID는 무시한다.</param>
    public static void GrowUv(PolyMesh m, UvTopology topo, HashSet<int> set)
    {
        var add = new HashSet<int>();
        foreach (int p in set) foreach (int n in UvNeighbors(m, topo, p)) add.Add(n);
        set.UnionWith(add);
    }

    /// <summary>UV 점 선택의 가장자리 한 고리를 깎는다(UV 모드 Shrink). 이웃 UV 점 중 하나라도 비선택이면 뺀다.</summary>
    public static void ShrinkUv(PolyMesh m, UvTopology topo, HashSet<int> set)
    {
        var remove = new HashSet<int>();
        foreach (int p in set)
        {
            if (p < 0 || p >= topo.Points.Count) { remove.Add(p); continue; }
            foreach (int n in UvNeighbors(m, topo, p)) if (!set.Contains(n)) { remove.Add(p); break; }
        }
        set.ExceptWith(remove);
    }

    /// <summary>UV 점의 이웃 UV 점(그 점의 코너마다 같은 면의 다음/이전 코너).</summary>
    private static IEnumerable<int> UvNeighbors(PolyMesh m, UvTopology topo, int p)
    {
        if (p < 0 || p >= topo.Points.Count) yield break;
        foreach (int he in topo.Points[p].HalfEdges)
        {
            int nx = topo.HeToPoint[m.Hes[he].Next], pv = topo.HeToPoint[m.Hes[he].Prev];
            if (nx >= 0) yield return nx;
            if (pv >= 0) yield return pv;
        }
    }

    /// <summary>면 집합의 바깥 경계 엣지(인접 면 중 하나만 집합에 속하거나 메시 경계인 엣지).</summary>
    /// <remarks>죽었거나 범위 밖 면 ID는 무시한다. 트윈이 없는 하프에지(메시 경계) 또는 트윈 면이 집합 밖이면 경계.</remarks>
    public static HashSet<int> BoundaryEdgesOfFaces(PolyMesh m, IEnumerable<int> faces)
    {
        var set = new HashSet<int>(faces);
        var result = new HashSet<int>();
        var tmp = new List<int>();
        foreach (int f in set)
        {
            if (f < 0 || f >= m.FaceCount || !m.Faces[f].Alive) continue;
            m.GetFaceHalfEdges(f, tmp);
            foreach (int he in tmp)
            {
                var h = m.Hes[he];
                if (h.Twin < 0 || !set.Contains(m.Hes[h.Twin].Face)) result.Add(h.Edge);
            }
        }
        return result;
    }

    /// <summary>정점 집합과 관련된 엣지: 양 끝이 모두 집합에 있는 엣지. 하나도 없으면 집합 정점에 닿는 모든 엣지.</summary>
    /// <remarks>both = 양 끝이 선택된 엣지, any = 선택 정점에 닿는 모든 엣지. 정점 하나만 선택한 경우에도 결과가 비지 않도록 any로 대체한다.</remarks>
    public static HashSet<int> EdgesOfVertices(PolyMesh m, IEnumerable<int> vertIds)
    {
        var verts = new HashSet<int>(vertIds);
        var both = new HashSet<int>(); var any = new HashSet<int>();
        var tmp = new List<int>();
        foreach (int v in verts)
        {
            if (v < 0 || v >= m.VertexCount || !m.Verts[v].Alive) continue;
            m.GetVertexEdges(v, tmp);
            foreach (int e in tmp) { any.Add(e); var (a, b) = m.EdgeVertices(e); if (verts.Contains(a) && verts.Contains(b)) both.Add(e); }
        }
        return both.Count > 0 ? both : any;
    }

    /// <summary>현재 컴포넌트 선택(모든 타입)을 대상 모드의 집합으로 변환한다.</summary>
    /// <remarks>출발 선택을 먼저 정점 집합으로 바꾼 뒤 대상 모드 규칙을 적용한다(면 → 엣지만 예외로 면의 엣지를 직접 쓴다). UV 모드는 다루지 않는다.</remarks>
    /// <returns>대상 모드의 ID 집합.</returns>
    public static HashSet<int> Convert(PolyMesh m, ComponentSet comps, SelectMode from, SelectMode to)
    {
        var verts = new HashSet<int>();
        var tmp = new List<int>();
        // 1) 출발 모드의 선택을 정점 집합으로
        switch (from)
        {
            case SelectMode.Vertex: verts.UnionWith(comps.Verts); break;
            case SelectMode.Edge: foreach (int e in comps.Edges) { var (a, b) = m.EdgeVertices(e); verts.Add(a); verts.Add(b); } break;
            case SelectMode.Face: foreach (int f in comps.Faces) { m.GetFaceVertices(f, tmp); verts.UnionWith(tmp); } break;
        }
        // 2) 정점 집합(또는 원래 면)을 대상 모드로
        var result = new HashSet<int>();
        switch (to)
        {
            case SelectMode.Vertex: result.UnionWith(verts); break;
            case SelectMode.Edge:
                if (from == SelectMode.Face)
                {
                    // 면 → 엣지: 면의 모든 엣지
                    foreach (int f in comps.Faces) { m.GetFaceHalfEdges(f, tmp); foreach (int he in tmp) result.Add(m.Hes[he].Edge); }
                }
                else
                {
                    // 정점 → 엣지: 양 끝이 모두 선택된 엣지
                    foreach (int v in verts) { m.GetVertexEdges(v, tmp); foreach (int e in tmp) { var (a, b) = m.EdgeVertices(e); if (verts.Contains(a) && verts.Contains(b)) result.Add(e); } }
                }
                break;
            case SelectMode.Face:
                if (from == SelectMode.Edge)
                {
                    // 엣지 → 면: 엣지 양쪽에 붙은 면(Maya polyListComponentConversion -fe -tf). 예전에는 양끝 정점에 닿는 모든 면(격자에서 6개)이었다.
                    foreach (int e in comps.Edges)
                    {
                        if (e < 0 || e >= m.EdgeCount || !m.Edges[e].Alive) continue;
                        var (f0, f1) = m.EdgeFaces(e);
                        if (f0 >= 0) result.Add(f0);
                        if (f1 >= 0) result.Add(f1);
                    }
                    break;
                }
                // 정점 → 면: 선택 정점을 하나라도 포함하는 면(Maya 기본)
                foreach (int v in verts) { m.GetVertexFaces(v, tmp); result.UnionWith(tmp); }
                break;
        }
        return result;
    }
}
