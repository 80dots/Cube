namespace Cube.Core.Mesh;

/// <summary>하프에지 불변식을 검사한다. 테스트와 디버그 빌드에서 사용.</summary>
public static class MeshValidator
{
    public static List<string> Check(PolyMesh m)
    {
        var errors = new List<string>();
        for (int h = 0; h < m.Hes.Count; h++)
        {
            var he = m.Hes[h];
            if (!he.Alive) continue;
            if ((uint)he.Vertex >= (uint)m.Verts.Count || !m.Verts[he.Vertex].Alive) errors.Add($"he{h}: dead vertex {he.Vertex}");
            if ((uint)he.Next >= (uint)m.Hes.Count || !m.Hes[he.Next].Alive) errors.Add($"he{h}: bad next {he.Next}");
            else if (m.Hes[he.Next].Prev != h) errors.Add($"he{h}: next.prev != self");
            if ((uint)he.Prev >= (uint)m.Hes.Count || !m.Hes[he.Prev].Alive) errors.Add($"he{h}: bad prev {he.Prev}");
            else if (m.Hes[he.Prev].Next != h) errors.Add($"he{h}: prev.next != self");
            if ((uint)he.Face >= (uint)m.Faces.Count || !m.Faces[he.Face].Alive) errors.Add($"he{h}: dead face {he.Face}");
            if ((uint)he.Edge >= (uint)m.Edges.Count || !m.Edges[he.Edge].Alive) errors.Add($"he{h}: dead edge {he.Edge}");
            else
            {
                var e = m.Edges[he.Edge];
                if (e.He0 != h && e.He1 != h) errors.Add($"he{h}: edge {he.Edge} does not reference it");
            }
            if (he.Twin >= 0)
            {
                if (he.Twin >= m.Hes.Count || !m.Hes[he.Twin].Alive) errors.Add($"he{h}: dead twin {he.Twin}");
                else
                {
                    var t = m.Hes[he.Twin];
                    if (t.Twin != h) errors.Add($"he{h}: twin.twin != self");
                    if (t.Edge != he.Edge) errors.Add($"he{h}: twin edge mismatch");
                    if (t.Vertex != m.Hes[he.Next].Vertex || he.Vertex != m.Hes[t.Next].Vertex) errors.Add($"he{h}: twin direction mismatch");
                }
            }
        }
        for (int f = 0; f < m.Faces.Count; f++)
        {
            if (!m.Faces[f].Alive) continue;
            int start = m.Faces[f].HalfEdge, he = start, n = 0;
            do
            {
                if (!m.Hes[he].Alive || m.Hes[he].Face != f) { errors.Add($"face{f}: loop he{he} invalid"); break; }
                he = m.Hes[he].Next; n++;
                if (n > 100000) { errors.Add($"face{f}: loop not closed"); break; }
            } while (he != start);
            if (n < 3) errors.Add($"face{f}: degree {n}");
        }
        for (int e = 0; e < m.Edges.Count; e++)
        {
            var ed = m.Edges[e];
            if (!ed.Alive) continue;
            if (ed.He0 < 0 || !m.Hes[ed.He0].Alive) errors.Add($"edge{e}: dead he0");
            if (ed.He1 >= 0 && !m.Hes[ed.He1].Alive) errors.Add($"edge{e}: dead he1");
            if (ed.He1 >= 0 && m.Hes[ed.He0].Twin != ed.He1) errors.Add($"edge{e}: he0.twin != he1");
        }
        for (int v = 0; v < m.Verts.Count; v++)
        {
            var vt = m.Verts[v];
            if (!vt.Alive) continue;
            if (vt.HalfEdge >= 0 && (!m.Hes[vt.HalfEdge].Alive || m.Hes[vt.HalfEdge].Vertex != v)) errors.Add($"vertex{v}: bad halfedge ref");
        }
        return errors;
    }

    public static bool IsValid(PolyMesh m) => Check(m).Count == 0;
}
