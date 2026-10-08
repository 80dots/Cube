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
        RenderMeshData.Ensure(ref r.CornerToVertex, corners);
        RenderMeshData.Ensure(ref r.Indices, tris * 3);
        RenderMeshData.Ensure(ref r.TriToFace, tris);
        RenderMeshData.Ensure(ref r.FaceCenters, aliveFaces);
        RenderMeshData.Ensure(ref r.FaceCenterToFace, aliveFaces);
        RenderMeshData.Ensure(ref r.FaceCenterCornerStart, aliveFaces);
        RenderMeshData.Ensure(ref r.FaceCenterDegree, aliveFaces);

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
                r.CornerToHalfEdge[ci] = _loop[i]; r.CornerToVertex[ci] = he.Vertex;
                centroid += p;
                ci++;
            }
            r.FaceCenters[fci] = centroid / n; r.FaceCenterToFace[fci] = f; r.FaceCenterCornerStart[fci] = baseCorner; r.FaceCenterDegree[fci] = n; fci++;

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
        RenderMeshData.Ensure(ref r.LineVertices, aliveEdges * 2);
        int li = 0, le = 0;
        for (int e = 0; e < m.Edges.Count; e++)
        {
            if (!m.Edges[e].Alive) continue;
            var (a, b) = m.EdgeVertices(e);
            r.LineVertices[li] = a; r.LinePositions[li++] = m.Verts[a].Position;
            r.LineVertices[li] = b; r.LinePositions[li++] = m.Verts[b].Position;
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
        UpdateBounds(r);
        r.PositionVersion++;
        return r;
    }

    /// <summary>점 배열에서 로컬 AABB를 다시 잰다.</summary>
    public static void UpdateBounds(RenderMeshData r)
    {
        if (r.PointCount == 0) { r.BoundsMin = r.BoundsMax = Vector3.Zero; return; }
        var mn = r.PointPositions[0]; var mx = mn;
        var pts = r.PointPositions;
        for (int i = 1; i < r.PointCount; i++) { mn = Vector3.Min(mn, pts[i]); mx = Vector3.Max(mx, pts[i]); }
        r.BoundsMin = mn; r.BoundsMax = mx;
    }

    /// <summary>위상은 그대로이고 위치만 바뀐 경우 배열의 위치 값만 갱신한다(드래그 프리뷰).</summary>
    public static void UpdatePositions(PolyMesh m, RenderMeshData r) => UpdatePositions(m, r, null);

    /// <summary>
    /// 위치 갱신. positions가 주어지면(스킨 변형 등) 정점 ID로 그 배열을 쓴다.
    /// Build가 남긴 코너→정점/선분→정점/면 중심→코너 범위 캐시만 읽으므로 메시 위상 구조를 다시 걷지 않는다(재생 중 매 프레임 호출).
    /// </summary>
    public static void UpdatePositions(PolyMesh m, RenderMeshData r, Vector3[]? positions)
    {
        // 정점 ID → 위치 표(메시 위치 또는 변형 위치). 정점 수만큼 한 번만 채워 두고 모든 배열이 이를 읽는다
        int vc = m.Verts.Count;
        if (_vpos == null || _vpos.Length < vc) _vpos = new Vector3[Math.Max(vc, 64)];
        var vp = _vpos;
        if (positions != null && positions.Length >= vc) Array.Copy(positions, vp, vc);
        else
        {
            var verts = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(m.Verts);
            for (int v = 0; v < vc; v++) vp[v] = positions != null && v < positions.Length ? positions[v] : verts[v].Position;
        }
        var pos = r.Positions; var c2v = r.CornerToVertex;
        for (int i = 0; i < r.CornerCount; i++) pos[i] = vp[c2v[i]];
        var lp = r.LinePositions; var lv = r.LineVertices;
        for (int i = 0; i < r.LineVertexCount; i++) lp[i] = vp[lv[i]];
        var pp = r.PointPositions; var p2v = r.PointToVertex;
        for (int i = 0; i < r.PointCount; i++) pp[i] = vp[p2v[i]];
        var fc = r.FaceCenters; var fs = r.FaceCenterCornerStart; var fd = r.FaceCenterDegree;
        for (int i = 0; i < r.FaceCenterCount; i++)
        {
            int start = fs[i], n = fd[i]; var sum = Vector3.Zero;
            for (int k = 0; k < n; k++) sum += pos[start + k];
            fc[i] = sum / Math.Max(n, 1);
        }
        UpdateBounds(r);
        r.PositionVersion++;
    }

    [ThreadStatic] private static Vector3[]? _vpos;
}
