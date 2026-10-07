using System.Numerics;

namespace Cube.Core.Mesh;

/// <summary>PolyMesh를 표시용 삼각형/선/점 배열로 변환한다. 노멀은 호출 전에 계산되어 있어야 한다.</summary>
public static class MeshTessellator
{
    [ThreadStatic] private static List<int>? _loop;
    [ThreadStatic] private static List<int>? _tris;
    [ThreadStatic] private static Vector2[]? _poly2d;

    public static RenderMeshData Build(PolyMesh m, RenderMeshData? reuse = null)
    {
        var r = reuse ?? new RenderMeshData();
        _loop ??= new List<int>(16);
        _tris ??= new List<int>(32);

        // --- 코너 수와 삼각형 수 선계산
        int corners = 0, tris = 0, aliveFaces = 0;
        for (int f = 0; f < m.Faces.Count; f++)
        {
            if (!m.Faces[f].Alive) continue;
            int d = m.FaceDegree(f);
            corners += d; tris += d - 2; aliveFaces++;
        }
        RenderMeshData.Ensure(ref r.Positions, corners);
        RenderMeshData.Ensure(ref r.Normals, corners);
        RenderMeshData.Ensure(ref r.Uvs, corners);
        RenderMeshData.Ensure(ref r.CornerToHalfEdge, corners);
        RenderMeshData.Ensure(ref r.Indices, tris * 3);
        RenderMeshData.Ensure(ref r.TriToFace, tris);
        RenderMeshData.Ensure(ref r.FaceCenters, aliveFaces);
        RenderMeshData.Ensure(ref r.FaceCenterToFace, aliveFaces);

        int ci = 0, ii = 0, ti = 0, fci = 0;
        for (int f = 0; f < m.Faces.Count; f++)
        {
            if (!m.Faces[f].Alive) continue;
            int n = m.GetFaceHalfEdges(f, _loop);
            int baseCorner = ci;
            var centroid = Vector3.Zero;
            for (int i = 0; i < n; i++)
            {
                var he = m.Hes[_loop[i]];
                var p = m.Verts[he.Vertex].Position;
                r.Positions[ci] = p; r.Normals[ci] = he.Normal; r.Uvs[ci] = he.Uv0;
                r.CornerToHalfEdge[ci] = _loop[i];
                centroid += p;
                ci++;
            }
            r.FaceCenters[fci] = centroid / n; r.FaceCenterToFace[fci] = f; fci++;

            if (n == 3)
            {
                r.Indices[ii++] = baseCorner; r.Indices[ii++] = baseCorner + 1; r.Indices[ii++] = baseCorner + 2;
                r.TriToFace[ti++] = f;
            }
            else
            {
                // 면 평면에 투영 후 볼록이면 팬, 아니면 귀 자르기
                if (_poly2d == null || _poly2d.Length < n) _poly2d = new Vector2[Math.Max(n, 16)];
                var normal = m.Faces[f].Normal;
                if (normal.LengthSquared() < 1e-12f) normal = Vector3.Normalize(MeshNormals.FaceNormalUnnormalized(m, f));
                EarClipping.PlaneBasis(normal, out var u, out var v);
                for (int i = 0; i < n; i++)
                {
                    var p = r.Positions[baseCorner + i];
                    _poly2d[i] = new Vector2(Vector3.Dot(p, u), Vector3.Dot(p, v));
                }
                var span = new ReadOnlySpan<Vector2>(_poly2d, 0, n);
                if (EarClipping.IsConvex(span))
                {
                    for (int i = 1; i + 1 < n; i++)
                    {
                        r.Indices[ii++] = baseCorner; r.Indices[ii++] = baseCorner + i; r.Indices[ii++] = baseCorner + i + 1;
                        r.TriToFace[ti++] = f;
                    }
                }
                else
                {
                    _tris.Clear();
                    EarClipping.Triangulate(span, _tris);
                    for (int i = 0; i + 2 < _tris.Count; i += 3)
                    {
                        r.Indices[ii++] = baseCorner + _tris[i]; r.Indices[ii++] = baseCorner + _tris[i + 1]; r.Indices[ii++] = baseCorner + _tris[i + 2];
                        r.TriToFace[ti++] = f;
                    }
                }
            }
        }
        r.CornerCount = ci; r.IndexCount = ii; r.FaceCenterCount = fci;

        // --- 선
        int aliveEdges = m.AliveEdgeCount;
        RenderMeshData.Ensure(ref r.LinePositions, aliveEdges * 2);
        RenderMeshData.Ensure(ref r.LineToEdge, aliveEdges);
        int li = 0, le = 0;
        for (int e = 0; e < m.Edges.Count; e++)
        {
            if (!m.Edges[e].Alive) continue;
            var (a, b) = m.EdgeVertices(e);
            r.LinePositions[li++] = m.Verts[a].Position;
            r.LinePositions[li++] = m.Verts[b].Position;
            r.LineToEdge[le++] = e;
        }
        r.LineVertexCount = li;

        // --- 점
        int aliveVerts = m.AliveVertexCount;
        RenderMeshData.Ensure(ref r.PointPositions, aliveVerts);
        RenderMeshData.Ensure(ref r.PointToVertex, aliveVerts);
        int pi = 0;
        for (int v = 0; v < m.Verts.Count; v++)
        {
            if (!m.Verts[v].Alive) continue;
            r.PointPositions[pi] = m.Verts[v].Position; r.PointToVertex[pi] = v; pi++;
        }
        r.PointCount = pi;
        return r;
    }

    /// <summary>위상은 그대로이고 위치만 바뀐 경우 배열의 위치 값만 갱신한다(드래그 프리뷰).</summary>
    public static void UpdatePositions(PolyMesh m, RenderMeshData r) => UpdatePositions(m, r, null);

    /// <summary>위치 갱신. positions가 주어지면(스킨 변형 등) 정점 ID로 그 배열을 쓴다.</summary>
    public static void UpdatePositions(PolyMesh m, RenderMeshData r, Vector3[]? positions)
    {
        Vector3 P(int v) => positions != null && v < positions.Length ? positions[v] : m.Verts[v].Position;
        for (int i = 0; i < r.CornerCount; i++) r.Positions[i] = P(m.Hes[r.CornerToHalfEdge[i]].Vertex);
        for (int i = 0; i < r.LineCount; i++)
        {
            var (a, b) = m.EdgeVertices(r.LineToEdge[i]);
            r.LinePositions[i * 2] = P(a); r.LinePositions[i * 2 + 1] = P(b);
        }
        for (int i = 0; i < r.PointCount; i++) r.PointPositions[i] = P(r.PointToVertex[i]);
        for (int i = 0; i < r.FaceCenterCount; i++)
        {
            if (positions == null) { r.FaceCenters[i] = m.FaceCentroid(r.FaceCenterToFace[i]); continue; }
            int start = m.Faces[r.FaceCenterToFace[i]].HalfEdge, he = start; var sum = Vector3.Zero; int n = 0;
            do { sum += P(m.Hes[he].Vertex); n++; he = m.Hes[he].Next; } while (he != start);
            r.FaceCenters[i] = sum / Math.Max(n, 1);
        }
    }
}
