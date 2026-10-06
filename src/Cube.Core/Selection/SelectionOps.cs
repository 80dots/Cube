using Cube.Core.Mesh;

namespace Cube.Core.Selection;

/// <summary>Maya의 Grow(&gt;)/Shrink(&lt;)/Convert Selection에 해당하는 컴포넌트 집합 연산.</summary>
public static class SelectionOps
{
    public static void Grow(PolyMesh m, SelectMode mode, HashSet<int> set)
    {
        var add = new HashSet<int>();
        var tmp = new List<int>();
        switch (mode)
        {
            case SelectMode.Vertex:
                foreach (int v in set)
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
                    foreach (int v in tmp.ToArray()) { m.GetVertexFaces(v, tmp); add.UnionWith(tmp); }
                }
                break;
        }
        set.UnionWith(add);
    }

    public static void Shrink(PolyMesh m, SelectMode mode, HashSet<int> set)
    {
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

    /// <summary>면 집합의 바깥 경계 엣지(인접 면 중 하나만 집합에 속하거나 메시 경계인 엣지).</summary>
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
                // 정점/엣지 → 면: 선택 정점을 하나라도 포함하는 면(Maya 기본)
                foreach (int v in verts) { m.GetVertexFaces(v, tmp); result.UnionWith(tmp); }
                break;
        }
        return result;
    }
}
