using System.Numerics;

namespace Cube.Core.Mesh;

/// <summary>Catmull-Clark 서브디비전(Maya Smooth / Smooth Mesh Preview).</summary>
public static partial class MeshOps
{
    /// <summary>
    /// Catmull-Clark 한 단계를 적용한 새 메시를 돌려준다. n각형·경계 지원. 코너 UV는 면 안에서 보간하므로 심이 유지되고,
    /// 하드 엣지/심 플래그는 자식 엣지에 물려준다(하드 엣지는 노멀에만 영향, 형태는 매끈하게).
    /// </summary>
    public static PolyMesh CatmullClark(PolyMesh m)
    {
        var o = new PolyMesh();
        int nv = m.VertexCount, ne = m.EdgeCount, nf = m.FaceCount;
        var facePt = new int[nf]; var facePos = new Vector3[nf];
        var edgePt = new int[ne]; var edgeMid = new Vector3[ne];
        var vertPt = new int[nv];
        var loop = new List<int>();

        // 1) 면 점
        for (int f = 0; f < nf; f++)
        {
            facePt[f] = -1;
            if (!m.Faces[f].Alive) continue;
            m.GetFaceVertices(f, loop);
            var sum = Vector3.Zero; foreach (int v in loop) sum += m.Verts[v].Position;
            facePos[f] = sum / Math.Max(loop.Count, 1);
        }
        // 2) 엣지 점
        for (int e = 0; e < ne; e++)
        {
            edgePt[e] = -1;
            if (!m.Edges[e].Alive) continue;
            var (a, b) = m.EdgeVertices(e);
            var mid = (m.Verts[a].Position + m.Verts[b].Position) * 0.5f;
            edgeMid[e] = mid;
            var (f0, f1) = m.EdgeFaces(e);
            edgePt[e] = o.AddVertex(f1 < 0 ? mid : (m.Verts[a].Position + m.Verts[b].Position + facePos[f0] + facePos[f1]) * 0.25f);
        }
        for (int f = 0; f < nf; f++) if (m.Faces[f].Alive) facePt[f] = o.AddVertex(facePos[f]);
        // 3) 정점 점
        var edges = new List<int>(); var faces = new List<int>();
        for (int v = 0; v < nv; v++)
        {
            vertPt[v] = -1;
            if (!m.Verts[v].Alive) continue;
            m.GetVertexEdges(v, edges);
            var p = m.Verts[v].Position;
            var boundary = edges.Where(e => m.IsBoundaryEdge(e)).ToList();
            Vector3 np;
            if (boundary.Count >= 2)
            {
                if (boundary.Count > 2 || edges.Count == 2) np = p; // 코너/비정상: 고정
                else np = p * 0.75f + (edgeMid[boundary[0]] + edgeMid[boundary[1]]) * 0.125f;
            }
            else
            {
                m.GetVertexFaces(v, faces);
                int n = edges.Count;
                var F = Vector3.Zero; foreach (int f in faces.Distinct()) F += facePos[f]; F /= Math.Max(faces.Distinct().Count(), 1);
                var R = Vector3.Zero; foreach (int e in edges) R += edgeMid[e]; R /= Math.Max(n, 1);
                np = n > 0 ? (F + R * 2f + p * (n - 3)) / n : p;
            }
            vertPt[v] = o.AddVertex(np);
        }
        // 4) 면: 코너마다 쿼드 [V', E'(out), F', E'(in)]
        var hes = new List<int>();
        for (int f = 0; f < nf; f++)
        {
            if (!m.Faces[f].Alive) continue;
            m.GetFaceHalfEdges(f, hes);
            int n = hes.Count;
            var uvSum = Vector2.Zero; foreach (int he in hes) uvSum += m.Hes[he].Uv0;
            var uvF = uvSum / n;
            for (int i = 0; i < n; i++)
            {
                int heOut = hes[i], heIn = hes[(i + n - 1) % n];
                var hOut = m.Hes[heOut]; var hIn = m.Hes[heIn];
                int v = hOut.Vertex;
                int eOut = hOut.Edge, eIn = hIn.Edge;
                var uvV = hOut.Uv0;
                var uvEOut = (hOut.Uv0 + m.Hes[hOut.Next].Uv0) * 0.5f;
                var uvEIn = (hIn.Uv0 + hOut.Uv0) * 0.5f;
                var corners = new List<Corner>
                {
                    new(vertPt[v], uvV, Vector3.Zero), new(edgePt[eOut], uvEOut, Vector3.Zero),
                    new(facePt[f], uvF, Vector3.Zero), new(edgePt[eIn], uvEIn, Vector3.Zero),
                };
                int q = AddFaceWithCorners(o, corners, m.Faces[f].Material);
                if (q < 0) continue;
                // 자식 엣지 플래그: (V', E'out)는 eOut에서, (E'in, V')는 eIn에서
                CopyEdgeFlags(m, eOut, o, vertPt[v], edgePt[eOut]);
                CopyEdgeFlags(m, eIn, o, edgePt[eIn], vertPt[v]);
            }
        }
        o.BumpTopology();
        return o;
    }

    private static void CopyEdgeFlags(PolyMesh src, int e, PolyMesh dst, int a, int b)
    {
        int ne = dst.FindEdge(a, b);
        if (ne < 0) return;
        var ed = dst.Edges[ne]; ed.Hard = src.Edges[e].Hard; ed.Seam = src.Edges[e].Seam; dst.Edges[ne] = ed;
    }

    /// <summary>levels단계 Catmull-Clark을 적용해 메시를 제자리에서 교체한다.</summary>
    public static void Smooth(PolyMesh m, int levels)
    {
        levels = Math.Clamp(levels, 0, 5);
        if (levels == 0) return;
        PolyMesh cur = m;
        for (int i = 0; i < levels; i++) cur = CatmullClark(cur);
        m.CopyFrom(cur);
        m.BumpTopology();
    }
}
