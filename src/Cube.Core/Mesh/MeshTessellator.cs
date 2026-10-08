using System.Numerics;

namespace Cube.Core.Mesh;

/// <summary>
/// PolyMesh를 표시용 삼각형/선/점 배열로 변환한다. 노멀은 호출 전에 계산되어 있어야 한다.
/// 결과 <see cref="RenderMeshData"/>는 뷰포트 MeshView가 Godot 메시로 올리고, 피킹이 역매핑(삼각형→면, 선분→엣지, 점→정점)에 쓴다.
/// 위상이 바뀌면 <see cref="Build"/>, 위치만 바뀌면(드래그 프리뷰·스킨 변형) 더 싼 <see cref="UpdatePositions(PolyMesh, RenderMeshData, Vector3[])"/>를 쓴다.
/// </summary>
public static class MeshTessellator
{
    /// <summary>면 하나의 하프에지 루프 임시 버퍼(스레드별, 할당 재사용).</summary>
    [ThreadStatic] private static List<int>? _loop;
    /// <summary>귀 자르기 결과 로컬 삼각형 인덱스 임시 버퍼(스레드별).</summary>
    [ThreadStatic] private static List<int>? _tris;
    /// <summary>면 평면에 투영한 2D 다각형 임시 버퍼(스레드별, 필요하면 키움).</summary>
    [ThreadStatic] private static Vector2[]? _poly2d;

    /// <summary>
    /// 메시 전체를 테셀레이션한다. 순서: ① 코너·삼각형·면 수 선계산 후 배열 용량 확보 ② 면마다 코너 언롤 + 면 중심 +
    /// 삼각분할(삼각형은 그대로, 볼록 n각형은 팬, 오목은 귀 자르기) ③ 살아 있는 엣지마다 선분 ④ 살아 있는 정점마다 점
    /// ⑤ AABB 갱신, <see cref="RenderMeshData.PositionVersion"/> 증가.
    /// </summary>
    /// <param name="m">대상 메시(면/코너 노멀이 계산되어 있어야 함).</param>
    /// <param name="reuse">재사용할 결과 객체(배열 재할당을 줄임). null이면 새로 만든다.</param>
    /// <returns>채워진 결과(<paramref name="reuse"/>가 있으면 같은 객체).</returns>
    public static RenderMeshData Build(PolyMesh m, RenderMeshData? reuse = null)
    {
        var r = reuse ?? new RenderMeshData();
        _loop ??= new List<int>(16);
        _tris ??= new List<int>(32);

        // --- 코너 수와 삼각형 수 선계산
        // n각형 하나 = 코너 n개, 삼각형 n-2개
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

        // ci = 코너 쓰기 위치, ii = 인덱스 쓰기 위치, ti = 삼각형 번호, fci = 면 중심 번호
        int ci = 0, ii = 0, ti = 0, fci = 0;
        for (int f = 0; f < m.Faces.Count; f++)
        {
            if (!m.Faces[f].Alive) continue;
            int n = m.GetFaceHalfEdges(f, _loop);
            int baseCorner = ci;
            var centroid = Vector3.Zero;
            // 면의 코너를 루프 순서대로 연속 배치(위치·노멀·UV와 역매핑 캐시)
            for (int i = 0; i < n; i++)
            {
                var he = m.Hes[_loop[i]];
                var p = m.Verts[he.Vertex].Position;
                r.Positions[ci] = p; r.Normals[ci] = he.Normal; r.Uvs[ci] = he.Uv0;
                r.CornerToHalfEdge[ci] = _loop[i]; r.CornerToVertex[ci] = he.Vertex;
                centroid += p;
                ci++;
            }
            // 면 중심 = 코너 위치 평균. UpdatePositions가 같은 코너 범위로 다시 계산할 수 있게 시작·차수를 기록
            r.FaceCenters[fci] = centroid / n; r.FaceCenterToFace[fci] = f; r.FaceCenterCornerStart[fci] = baseCorner; r.FaceCenterDegree[fci] = n; fci++;

            if (n == 3)
            {
                // 삼각형은 그대로
                r.Indices[ii++] = baseCorner; r.Indices[ii++] = baseCorner + 1; r.Indices[ii++] = baseCorner + 2;
                r.TriToFace[ti++] = f;
            }
            else
            {
                // 면 평면에 투영 후 볼록이면 팬, 아니면 귀 자르기
                if (_poly2d == null || _poly2d.Length < n) _poly2d = new Vector2[Math.Max(n, 16)];
                // 캐시된 면 노멀이 없으면(Recompute 전) Newell로 직접 계산
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
                    // 볼록: 첫 코너 기준 팬
                    for (int i = 1; i + 1 < n; i++)
                    {
                        r.Indices[ii++] = baseCorner; r.Indices[ii++] = baseCorner + i; r.Indices[ii++] = baseCorner + i + 1;
                        r.TriToFace[ti++] = f;
                    }
                }
                else
                {
                    // 오목: 귀 자르기(로컬 인덱스 → 코너 번호로 오프셋)
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
        // 살아 있는 엣지마다 두 끝점(He0 방향 기준)을 기록
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
        // 살아 있는 정점마다 점 하나(고립 정점도 포함)
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
    /// 노멀·UV는 갱신하지 않는다. 마지막 Build 이후 위상이 바뀌었다면 반드시 Build를 다시 불러야 한다.
    /// </summary>
    /// <param name="positions">정점 ID로 인덱싱되는 대체 위치(LBS 변형 결과 등). 짧으면 모자란 정점은 메시 위치를 쓴다.</param>
    public static void UpdatePositions(PolyMesh m, RenderMeshData r, Vector3[]? positions)
    {
        // 정점 ID → 위치 표(메시 위치 또는 변형 위치). 정점 수만큼 한 번만 채워 두고 모든 배열이 이를 읽는다
        int vc = m.Verts.Count;
        if (_vpos == null || _vpos.Length < vc) _vpos = new Vector3[Math.Max(vc, 64)];
        var vp = _vpos;
        if (positions != null && positions.Length >= vc) Array.Copy(positions, vp, vc);
        else
        {
            // List 내부 배열을 Span으로 직접 읽어 구조체 복사·경계 검사 비용을 줄인다
            var verts = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(m.Verts);
            for (int v = 0; v < vc; v++) vp[v] = positions != null && v < positions.Length ? positions[v] : verts[v].Position;
        }
        // 코너 위치
        var pos = r.Positions; var c2v = r.CornerToVertex;
        for (int i = 0; i < r.CornerCount; i++) pos[i] = vp[c2v[i]];
        // 와이어 선분 끝점
        var lp = r.LinePositions; var lv = r.LineVertices;
        for (int i = 0; i < r.LineVertexCount; i++) lp[i] = vp[lv[i]];
        // 정점 점
        var pp = r.PointPositions; var p2v = r.PointToVertex;
        for (int i = 0; i < r.PointCount; i++) pp[i] = vp[p2v[i]];
        // 면 중심: 방금 갱신한 코너 위치의 [start, start+n) 평균
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

    /// <summary>UpdatePositions의 정점 ID → 위치 임시 표(스레드별, 필요하면 키움).</summary>
    [ThreadStatic] private static Vector3[]? _vpos;
}
