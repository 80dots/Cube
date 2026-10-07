using System.Numerics;

namespace Cube.Core.Mesh;

/// <summary>Maya Mesh / Mesh Display 메뉴의 메시 단위 연산: Fill Hole, Triangulate, Quadrangulate, Mirror, Symmetrize, Cleanup, Conform(표면), 노멀 편집, Soften/Harden by angle, Crease.</summary>
public static partial class MeshOps
{
    // ------------------------------------------------------------ Fill Hole

    /// <summary>경계 엣지가 속한 구멍의 정점 루프(면 방향 반대 = 새 면 순서). 자기 교차 루프면 빈 목록.</summary>
    public static List<int> HoleLoop(PolyMesh m, int boundaryEdge)
    {
        var result = new List<int>();
        if (boundaryEdge < 0 || boundaryEdge >= m.EdgeCount || !m.Edges[boundaryEdge].Alive || !m.IsBoundaryEdge(boundaryEdge)) return result;
        int start = m.Edges[boundaryEdge].He0, cur = start;
        var seen = new HashSet<int>();
        for (int guard = 0; guard < 100000; guard++)
        {
            var h = m.Hes[cur];
            if (!seen.Add(h.Vertex)) return new List<int>();
            result.Add(h.Vertex);
            int to = m.Hes[h.Next].Vertex;
            // to에서 나가는 경계 하프에지
            int next = -1;
            foreach (int he in m.VertexOutgoing(to)) if (m.Hes[he].Twin < 0) { next = he; break; }
            if (next < 0) return new List<int>();
            if (next == start) break;
            cur = next;
        }
        result.Reverse();
        return result;
    }

    /// <summary>Fill Hole: 선택 엣지(경계)가 속한 구멍마다 n각형을 채운다. 반환값은 새 면들.</summary>
    public static List<int> FillHoles(PolyMesh m, IEnumerable<int> edgeIds)
    {
        var result = new List<int>();
        var done = new HashSet<int>();
        foreach (int e in AliveEdges(m, edgeIds).ToArray())
        {
            if (e >= m.EdgeCount || !m.Edges[e].Alive || !m.IsBoundaryEdge(e) || done.Contains(e)) continue;
            var loop = HoleLoop(m, e);
            if (loop.Count < 3) continue;
            for (int i = 0; i < loop.Count; i++) { int be = m.FindEdge(loop[i], loop[(i + 1) % loop.Count]); if (be >= 0) done.Add(be); }
            // 코너 UV: 이웃 면의 같은 정점 코너 UV
            var corners = loop.Select(v =>
            {
                var uv = Vector2.Zero;
                foreach (int he in m.VertexOutgoing(v)) { uv = m.Hes[he].Uv0; break; }
                return new Corner(v, uv, Vector3.Zero);
            }).ToList();
            int nf = AddFaceWithCorners(m, corners);
            if (nf >= 0) result.Add(nf);
        }
        m.BumpTopology();
        return result;
    }

    // ------------------------------------------------------------ Triangulate / Quadrangulate

    /// <summary>Triangulate: n각형을 귀 자르기로 삼각형화한다. 반환값은 결과 삼각형들.</summary>
    public static List<int> Triangulate(PolyMesh m, IEnumerable<int> faceIds)
    {
        var result = new List<int>();
        var faces = AliveFaces(m, faceIds).Where(f => m.FaceDegree(f) > 3).ToList();
        if (faces.Count == 0) return result;
        var rb = new FaceRebuilder(m);
        foreach (int f in faces) rb.Capture(f);
        var plans = new List<(List<Corner> corners, List<int> idx, int material)>();
        foreach (var (f, corners, material) in rb.Captured)
        {
            var n = MeshNormals.FaceNormalUnnormalized(m, f);
            if (n.LengthSquared() < 1e-18f) n = Vector3.UnitY;
            EarClipping.PlaneBasis(Vector3.Normalize(n), out var u, out var w);
            var poly = corners.Select(c => { var p = m.Verts[c.Vertex].Position; return new Vector2(Vector3.Dot(p, u), Vector3.Dot(p, w)); }).ToArray();
            var idx = new List<int>();
            EarClipping.Triangulate(poly, idx);
            plans.Add((corners, idx, material));
        }
        rb.RemoveCaptured();
        foreach (var (corners, idx, material) in plans)
        {
            for (int i = 0; i + 2 < idx.Count; i += 3)
            {
                int nf = rb.AddFace(new[] { corners[idx[i]], corners[idx[i + 1]], corners[idx[i + 2]] }, material);
                if (nf >= 0) result.Add(nf);
            }
        }
        m.BumpTopology();
        return result;
    }

    /// <summary>Quadrangulate: 공유 엣지로 맞닿은 삼각형 쌍을 각도 임계 안에서 쿼드로 합친다. 반환값은 새 쿼드들.</summary>
    public static List<int> Quadrangulate(PolyMesh m, IEnumerable<int> faceIds, float angleDegrees)
    {
        var result = new List<int>();
        var set = new HashSet<int>(AliveFaces(m, faceIds).Where(f => m.FaceDegree(f) == 3));
        float cosT = MathF.Cos(angleDegrees * MathF.PI / 180f);
        // 후보 엣지: 양쪽 모두 선택 삼각형, 법선 각도 작음. 긴 엣지(대각선일 가능성)부터
        var cands = new List<(int e, float len)>();
        var hes = new List<int>();
        foreach (int f in set)
        {
            m.GetFaceHalfEdges(f, hes);
            foreach (int he in hes)
            {
                int e = m.Hes[he].Edge;
                var (f0, f1) = m.EdgeFaces(e);
                if (f1 < 0 || !set.Contains(f0) || !set.Contains(f1) || f0 > f1) continue;
                var n0 = Vector3.Normalize(MeshNormals.FaceNormalUnnormalized(m, f0) + new Vector3(1e-12f)); var n1 = Vector3.Normalize(MeshNormals.FaceNormalUnnormalized(m, f1) + new Vector3(1e-12f));
                if (Vector3.Dot(n0, n1) < cosT) continue;
                var (a, b) = m.EdgeVertices(e);
                cands.Add((e, Vector3.Distance(m.Verts[a].Position, m.Verts[b].Position)));
            }
        }
        var used = new HashSet<int>();
        foreach (var (e, _) in cands.Distinct().OrderByDescending(c => c.len))
        {
            if (e >= m.EdgeCount || !m.Edges[e].Alive) continue;
            var (f0, f1) = m.EdgeFaces(e);
            if (f1 < 0 || used.Contains(f0) || used.Contains(f1)) continue;
            var (ok, nf) = MergeFacesAcrossEdgeReturning(m, e);
            if (!ok) continue;
            used.Add(f0); used.Add(f1); result.Add(nf);
        }
        m.BumpTopology();
        return result;
    }

    // ------------------------------------------------------------ Mirror / Symmetrize (mesh)

    /// <summary>
    /// Mirror: 메시를 축 평면(axis 0=X/1=Y/2=Z, planeOffset 위치)에 대해 복제·반사해 덧붙인다. direction이 +면 양쪽이 없는 쪽(음수 쪽 원본을 양수로)…
    /// cut이면 반대편(keepPositive가 아닌 쪽) 원본 면을 먼저 지운다(Maya Symmetrize). mergeThreshold>0이면 평면 근처 정점을 합친다. 반환값은 새 면들.
    /// </summary>
    public static List<int> MirrorGeometry(PolyMesh m, int axis, float planeOffset, bool keepPositive, bool cut, float mergeThreshold)
    {
        if (cut)
        {
            var remove = new List<int>();
            for (int f = 0; f < m.FaceCount; f++)
            {
                if (!m.Faces[f].Alive) continue;
                float c = GetAxis(m.FaceCentroid(f), axis) - planeOffset;
                if (keepPositive ? c < -1e-6f : c > 1e-6f) remove.Add(f);
            }
            foreach (int f in remove) m.RemoveFace(f);
        }
        var src = m.Clone();
        var scale = Vector3.One; SetAxis(ref scale, axis, -1f);
        var offset = Vector3.Zero; SetAxis(ref offset, axis, planeOffset);
        var reflect = Matrix4x4.CreateTranslation(-offset) * Matrix4x4.CreateScale(scale) * Matrix4x4.CreateTranslation(offset);
        int before = m.FaceCount;
        Append(m, src, reflect);
        var result = new List<int>();
        for (int f = before; f < m.FaceCount; f++) if (m.Faces[f].Alive) result.Add(f);
        if (mergeThreshold > 0f)
        {
            var near = new List<int>();
            for (int v = 0; v < m.VertexCount; v++) if (m.Verts[v].Alive && MathF.Abs(GetAxis(m.Verts[v].Position, axis) - planeOffset) <= mergeThreshold) near.Add(v);
            MergeVertices(m, near, mergeThreshold * 2f);
            result = result.Where(f => f < m.FaceCount && m.Faces[f].Alive).ToList();
        }
        m.BumpTopology();
        return result;
    }

    // ------------------------------------------------------------ Cleanup

    /// <summary>Cleanup: 면적 0 면, 라미나(정점 집합이 같은 두 면), 고립 정점을 지운다. 반환값은 지운 면 수.</summary>
    public static int Cleanup(PolyMesh m, float zeroAreaEps = 1e-10f)
    {
        int removed = 0;
        var keys = new Dictionary<string, int>();
        var verts = new List<int>();
        for (int f = 0; f < m.FaceCount; f++)
        {
            if (!m.Faces[f].Alive) continue;
            if (MeshNormals.FaceNormalUnnormalized(m, f).LengthSquared() < zeroAreaEps * zeroAreaEps) { m.RemoveFace(f); removed++; continue; }
            m.GetFaceVertices(f, verts);
            string key = string.Join(",", verts.OrderBy(v => v));
            if (keys.ContainsKey(key)) { m.RemoveFace(f); removed++; continue; }
            keys[key] = f;
        }
        for (int v = 0; v < m.VertexCount; v++) if (m.Verts[v].Alive) m.RemoveVertexIfIsolated(v);
        m.BumpTopology();
        return removed;
    }

    // ------------------------------------------------------------ Conform (wrap onto surface)

    /// <summary>Conform: 정점들을 대상 메시 표면의 가장 가까운 점으로 옮긴다. targetToLocal = 대상 월드 → 이 메시 로컬.</summary>
    public static void ConformToSurface(PolyMesh m, IEnumerable<int> vertIds, PolyMesh target, Matrix4x4 targetToLocal)
    {
        var tris = new List<(Vector3 a, Vector3 b, Vector3 c)>();
        var loop = new List<int>();
        for (int f = 0; f < target.FaceCount; f++)
        {
            if (!target.Faces[f].Alive) continue;
            target.GetFaceVertices(f, loop);
            var p0 = Vector3.Transform(target.Verts[loop[0]].Position, targetToLocal);
            for (int i = 1; i + 1 < loop.Count; i++)
                tris.Add((p0, Vector3.Transform(target.Verts[loop[i]].Position, targetToLocal), Vector3.Transform(target.Verts[loop[i + 1]].Position, targetToLocal)));
        }
        if (tris.Count == 0) return;
        foreach (int v in AliveVerts(m, vertIds))
        {
            var p = m.Verts[v].Position;
            var best = p; float bestD = float.MaxValue;
            foreach (var (a, b, c) in tris)
            {
                var q = ClosestPointOnTriangle(p, a, b, c);
                float d = Vector3.DistanceSquared(p, q);
                if (d < bestD) { bestD = d; best = q; }
            }
            var vv = m.Verts[v]; vv.Position = best; m.Verts[v] = vv;
        }
        m.BumpGeometry();
    }

    /// <summary>삼각형 위 최근접점(Ericson, Real-Time Collision Detection).</summary>
    public static Vector3 ClosestPointOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        var ab = b - a; var ac = c - a; var ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return a;
        var bp = p - b; float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return b;
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) { float v = d1 / (d1 - d3); return a + ab * v; }
        var cp = p - c; float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return c;
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) { float w = d2 / (d2 - d6); return a + ac * w; }
        float va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) { float w = (d4 - d3) / ((d4 - d3) + (d5 - d6)); return b + (c - b) * w; }
        float denom = 1f / (va + vb + vc);
        float v2 = vb * denom, w2 = vc * denom;
        return a + ab * v2 + ac * w2;
    }

    // ------------------------------------------------------------ Normals (Mesh Display)

    /// <summary>Soften/Harden Edge(각도): 이면각이 angle보다 크면 하드, 아니면 소프트.</summary>
    public static void SoftenHardenByAngle(PolyMesh m, IEnumerable<int> edgeIds, float angleDegrees)
    {
        float cosT = MathF.Cos(angleDegrees * MathF.PI / 180f);
        foreach (int e in AliveEdges(m, edgeIds))
        {
            var (f0, f1) = m.EdgeFaces(e);
            bool hard;
            if (f1 < 0) hard = false;
            else
            {
                var n0 = MeshNormals.FaceNormalUnnormalized(m, f0); var n1 = MeshNormals.FaceNormalUnnormalized(m, f1);
                if (n0.LengthSquared() < 1e-18f || n1.LengthSquared() < 1e-18f) hard = false;
                else hard = Vector3.Dot(Vector3.Normalize(n0), Vector3.Normalize(n1)) < cosT;
            }
            var ed = m.Edges[e]; ed.Hard = hard; m.Edges[e] = ed;
        }
    }

    /// <summary>Conform(노멀): 연결 요소마다 면 대부분이 바깥(중심에서 멀어지는 쪽)을 향하도록 뒤집는다. 반환값은 뒤집은 요소 수.</summary>
    public static int ConformNormals(PolyMesh m)
    {
        int flipped = 0;
        foreach (var comp in ConnectedComponents(m))
        {
            var center = comp.Aggregate(Vector3.Zero, (s, f) => s + m.FaceCentroid(f)) / comp.Count;
            float score = 0;
            foreach (int f in comp) score += Vector3.Dot(MeshNormals.FaceNormalUnnormalized(m, f), m.FaceCentroid(f) - center);
            if (score < 0) { ReverseFaces(m, new[] { comp[0] }); flipped++; }
        }
        return flipped;
    }

    /// <summary>Lock Normals: 현재 코너 노멀의 평균을 정점 노멀로 잠근다.</summary>
    public static void LockNormals(PolyMesh m, IEnumerable<int> vertIds)
    {
        foreach (int v in AliveVerts(m, vertIds))
        {
            var sum = Vector3.Zero; foreach (int he in m.VertexOutgoing(v)) sum += m.Hes[he].Normal;
            if (sum.LengthSquared() > 1e-12f) m.LockedNormals[v] = Vector3.Normalize(sum);
        }
    }

    public static void UnlockNormals(PolyMesh m, IEnumerable<int> vertIds) { foreach (int v in vertIds) m.LockedNormals.Remove(v); }

    /// <summary>Set Vertex Normal: 정점 노멀을 지정 방향으로 잠근다.</summary>
    public static void SetVertexNormal(PolyMesh m, IEnumerable<int> vertIds, Vector3 normal)
    {
        if (normal.LengthSquared() < 1e-12f) return;
        normal = Vector3.Normalize(normal);
        foreach (int v in AliveVerts(m, vertIds)) m.LockedNormals[v] = normal;
    }

    /// <summary>Set to Face: 정점 노멀을 인접 면 법선 평균으로 잠근다(면을 넘겼으면 그 면들만).</summary>
    public static void SetNormalsToFace(PolyMesh m, IEnumerable<int> vertIds, IEnumerable<int>? faceFilter = null)
    {
        var filter = faceFilter == null ? null : new HashSet<int>(faceFilter);
        var faces = new List<int>();
        foreach (int v in AliveVerts(m, vertIds))
        {
            m.GetVertexFaces(v, faces);
            var sum = Vector3.Zero;
            foreach (int f in faces.Distinct()) if (filter == null || filter.Contains(f)) sum += Vector3.Normalize(MeshNormals.FaceNormalUnnormalized(m, f) + new Vector3(1e-12f));
            if (sum.LengthSquared() > 1e-12f) m.LockedNormals[v] = Vector3.Normalize(sum);
        }
    }

    /// <summary>Average Normals: 정점의 모든 코너 노멀(하드 엣지 무시)을 평균해 잠근다.</summary>
    public static void AverageNormals(PolyMesh m, IEnumerable<int> vertIds)
    {
        var faces = new List<int>();
        foreach (int v in AliveVerts(m, vertIds))
        {
            m.GetVertexFaces(v, faces);
            var sum = Vector3.Zero;
            foreach (int f in faces.Distinct()) sum += MeshNormals.FaceNormalUnnormalized(m, f);
            if (sum.LengthSquared() > 1e-12f) m.LockedNormals[v] = Vector3.Normalize(sum);
        }
    }

    // ------------------------------------------------------------ Slice (Multi-Cut) / Append to Polygon

    /// <summary>평면으로 메시를 자른다(Multi-Cut 슬라이스): 평면을 가로지르는 엣지를 나누고 같은 면의 새 정점끼리 잇는다. 반환값은 새 엣지들.</summary>
    public static List<int> SliceWithPlane(PolyMesh m, Vector3 point, Vector3 normal, IEnumerable<int>? faceFilter = null)
    {
        var result = new List<int>();
        if (normal.LengthSquared() < 1e-12f) return result;
        normal = Vector3.Normalize(normal);
        var filter = faceFilter == null ? null : new HashSet<int>(AliveFaces(m, faceFilter));
        var newVerts = new List<int>();
        for (int e = 0; e < m.EdgeCount; e++)
        {
            if (!m.Edges[e].Alive) continue;
            if (filter != null) { var (f0, f1) = m.EdgeFaces(e); if (!filter.Contains(f0) && (f1 < 0 || !filter.Contains(f1))) continue; }
            var (a, b) = m.EdgeVertices(e);
            float da = Vector3.Dot(m.Verts[a].Position - point, normal), db = Vector3.Dot(m.Verts[b].Position - point, normal);
            if ((da > 1e-7f && db > 1e-7f) || (da < -1e-7f && db < -1e-7f) || MathF.Abs(da - db) < 1e-12f) continue;
            float t = da / (da - db);
            if (t <= 0.001f || t >= 0.999f) continue;
            int nv = SplitEdge(m, e, t);
            if (nv >= 0) newVerts.Add(nv);
        }
        if (newVerts.Count < 2) return result;
        var set = new HashSet<int>(newVerts);
        var faces = new List<int>(); var loop = new List<int>();
        var seen = new HashSet<int>();
        var pairs = new List<(int, int)>();
        foreach (int v in newVerts)
        {
            m.GetVertexFaces(v, faces);
            foreach (int f in faces)
            {
                if (!seen.Add(f)) continue;
                if (filter != null && !filter.Contains(f)) continue;
                m.GetFaceVertices(f, loop);
                var sel = loop.Where(set.Contains).ToList();
                for (int i = 0; i + 1 < sel.Count; i += 2) if (m.FindEdge(sel[i], sel[i + 1]) < 0) pairs.Add((sel[i], sel[i + 1]));
            }
        }
        foreach (var (a, b) in pairs) { int ne = SplitFaceBetween(m, a, b); if (ne >= 0) result.Add(ne); }
        m.BumpTopology();
        return result;
    }

    /// <summary>
    /// Append to Polygon: 경계 엣지에서 시작해 점들을 거쳐 돌아오는 새 면을 붙인다. 점은 새 정점 위치(또는 기존 정점 ID를 음수-1로 인코딩: -(id+1)).
    /// 반환값은 새 면 ID(-1 = 실패).
    /// </summary>
    public static int AppendPolygon(PolyMesh m, int boundaryEdge, IReadOnlyList<Vector3> points, IReadOnlyList<int>? existingVertexIds = null)
    {
        if (boundaryEdge < 0 || boundaryEdge >= m.EdgeCount || !m.Edges[boundaryEdge].Alive || !m.IsBoundaryEdge(boundaryEdge)) return -1;
        int he = m.Edges[boundaryEdge].He0;
        int a = m.Hes[he].Vertex, b = m.Hes[m.Hes[he].Next].Vertex;
        var ids = new List<int> { b, a };
        for (int i = 0; i < points.Count; i++)
        {
            int ex = existingVertexIds != null && i < existingVertexIds.Count ? existingVertexIds[i] : -1;
            ids.Add(ex >= 0 && ex < m.VertexCount && m.Verts[ex].Alive ? ex : m.AddVertex(points[i]));
        }
        if (ids.Distinct().Count() < 3) return -1;
        var uvA = m.Hes[he].Uv0; var uvB = m.Hes[m.Hes[he].Next].Uv0;
        var corners = ids.Select((v, i) => new Corner(v, i == 0 ? uvB : i == 1 ? uvA : Vector2.Zero, Vector3.Zero)).ToList();
        int nf = AddFaceWithCorners(m, corners, m.Faces[m.Hes[he].Face].Material);
        if (nf < 0) { foreach (int v in ids.Skip(2)) m.RemoveVertexIfIsolated(v); }
        m.BumpTopology();
        return nf;
    }

    // ------------------------------------------------------------ Crease

    public static void SetCrease(PolyMesh m, IEnumerable<int> edgeIds, float crease)
    {
        crease = Math.Clamp(crease, 0f, 10f);
        foreach (int e in AliveEdges(m, edgeIds)) { var ed = m.Edges[e]; ed.Crease = crease; m.Edges[e] = ed; }
    }
}
