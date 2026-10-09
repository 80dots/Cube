using System.Numerics;

namespace Cube.Core.Mesh;

/// <summary>Maya Mesh / Mesh Display 메뉴의 메시 단위 연산: Fill Hole, Triangulate, Quadrangulate, Mirror, Symmetrize, Cleanup, Conform(표면), 노멀 편집, Soften/Harden by angle, Crease.</summary>
public static partial class MeshOps
{
    // ------------------------------------------------------------ Fill Hole

    /// <summary>경계 엣지가 속한 구멍의 정점 루프(면 방향 반대 = 새 면 순서). 자기 교차 루프면 빈 목록.</summary>
    /// <remarks>
    /// 경계 엣지의 He0(면이 있는 쪽 하프에지)에서 출발해, 끝 정점에서 나가는 트윈 없는 하프에지(다음 경계)를 따라 한 바퀴 돈다.
    /// 면 쪽 하프에지는 구멍을 반대로 돌므로 마지막에 뒤집어 새 면이 기존 면과 같은 방향(CCW)이 되게 한다.
    /// 같은 정점을 두 번 지나면(나비넥타이형 구멍) 또는 다음 경계를 못 찾으면 빈 목록.
    /// </remarks>
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
    /// <remarks>
    /// 이미 채운 구멍의 경계 엣지는 done에 넣어 같은 구멍을 두 번 채우지 않는다. 새 면 코너 UV는 해당 정점에서 나가는 아무 하프에지의 UV,
    /// 노멀은 Zero(호출자가 재계산).
    /// </remarks>
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
    /// <remarks>
    /// 면 법선으로 평면 기저(u, w)를 만들어 코너를 2D로 투영하고 <see cref="EarClipping.Triangulate"/>로 인덱스 삼중쌍을 얻는다.
    /// 모든 계획을 먼저 세운 뒤 면을 지우고 삼각형으로 재생성한다(둘레 엣지 플래그 유지, 새 대각선은 플래그 없음).
    /// </remarks>
    public static List<int> Triangulate(PolyMesh m, IEnumerable<int> faceIds)
    {
        // 이미 삼각형인 면은 대상이 아님
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
        // 캡처한 모든 면의 변(무향 정점 쌍): 다른 면의 대각선이 이 변을 먼저 차지하면 그 면을 다시 만들 수 없으므로 대각선으로 쓰지 않는다
        var sides = new HashSet<long>(PairKeyComparer.Instance);
        foreach (var (corners, _, _) in plans) for (int i = 0; i < corners.Count; i++) sides.Add(PairKey(corners[i].Vertex, corners[(i + 1) % corners.Count].Vertex));
        rb.RemoveCaptured();
        foreach (var (corners, idx, material) in plans)
        {
            // 귀 자르기 결과 → 실패하면 각 꼭짓점에서의 팬 → 모두 실패하면 원래 다각형을 되살린다.
            // (대각선이 이미 다른 곳의 엣지면(두 면이 엣지 둘 이상을 공유하는 등) 그 삼각형이 엉뚱한 면과 이어지거나 거부되어 구멍이 났다, v0.0.57)
            int n = corners.Count;
            bool done = TryAddTriangles(m, rb, corners, idx, material, result, sides);
            for (int k = 0; !done && k < n; k++)
            {
                var fan = new List<int>();
                for (int i = 1; i + 1 < n; i++) { fan.Add(k); fan.Add((k + i) % n); fan.Add((k + i + 1) % n); }
                done = TryAddTriangles(m, rb, corners, fan, material, result, sides);
            }
            if (!done) rb.AddFace(corners, material);
        }
        m.BumpTopology();
        return result;
    }

    /// <summary>무향 정점 쌍 키(작은 ID 상위 32비트).</summary>
    private static long PairKey(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

    /// <summary>
    /// 삼각형 인덱스 목록(코너 로컬 번호 3개씩)을 면으로 추가한다. 다각형 변이 아닌 대각선이 이미 메시에 엣지로 있으면 시도하지 않고,
    /// 추가 도중 하나라도 거부되면 이번에 넣은 삼각형을 모두 지우고 false를 돌려준다(정점은 남긴다).
    /// </summary>
    /// <param name="sides">다시 만들 면들의 변(대각선으로 쓰면 안 되는 정점 쌍).</param>
    private static bool TryAddTriangles(PolyMesh m, FaceRebuilder rb, List<Corner> corners, List<int> idx, int material, List<int> result, HashSet<long> sides)
    {
        int n = corners.Count;
        if (idx.Count != (n - 2) * 3) return false;
        // 대각선(이웃하지 않은 코너 쌍)이 이미 있는 엣지면 이 분할은 쓸 수 없다
        for (int i = 0; i + 2 < idx.Count; i += 3)
            for (int k = 0; k < 3; k++)
            {
                int x = idx[i + k], y = idx[i + (k + 1) % 3];
                bool side = (x + 1) % n == y || (y + 1) % n == x;
                if (!side && (m.FindEdge(corners[x].Vertex, corners[y].Vertex) >= 0 || sides.Contains(PairKey(corners[x].Vertex, corners[y].Vertex)))) return false;
            }
        var added = new List<int>();
        for (int i = 0; i + 2 < idx.Count; i += 3)
        {
            int nf = rb.AddFace(new[] { corners[idx[i]], corners[idx[i + 1]], corners[idx[i + 2]] }, material);
            if (nf < 0) { foreach (int f in added) m.RemoveFace(f, removeIsolated: false); return false; }
            added.Add(nf);
        }
        result.AddRange(added);
        return true;
    }

    /// <summary>Quadrangulate: 공유 엣지로 맞닿은 삼각형 쌍을 각도 임계 안에서 쿼드로 합친다. 반환값은 새 쿼드들.</summary>
    /// <param name="angleDegrees">두 삼각형 법선 사이 각이 이 값보다 작을 때만 합친다.</param>
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
                // 양쪽 모두 선택 삼각형인 내부 엣지만, f0 < f1 조건으로 엣지당 한 번만 후보에 넣는다(하프에지 두 개로 두 번 보이므로)
                if (f1 < 0 || !set.Contains(f0) || !set.Contains(f1) || f0 > f1) continue;
                var n0 = Vector3.Normalize(MeshNormals.FaceNormalUnnormalized(m, f0) + new Vector3(1e-12f)); var n1 = Vector3.Normalize(MeshNormals.FaceNormalUnnormalized(m, f1) + new Vector3(1e-12f));
                if (Vector3.Dot(n0, n1) < cosT) continue;
                var (a, b) = m.EdgeVertices(e);
                cands.Add((e, Vector3.Distance(m.Verts[a].Position, m.Verts[b].Position)));
            }
        }
        // 탐욕 병합: 긴 엣지부터, 이미 쓴 삼각형은 다시 쓰지 않는다
        var used = new HashSet<int>();
        foreach (var (e, _) in cands.Distinct().OrderByDescending(c => c.len))
        {
            if (e >= m.EdgeCount || !m.Edges[e].Alive) continue;
            var (f0, f1) = m.EdgeFaces(e);
            // 양쪽 모두 원래 선택 삼각형이어야 한다: 방금 합친 쿼드(새 ID라 used에 없음)가 다시 이웃 삼각형과 합쳐져
            // 오각형·큰 n각형으로 불어나던 문제(v0.0.57: 토러스 480 삼각형 → 35면)
            if (f1 < 0 || used.Contains(f0) || used.Contains(f1) || !set.Contains(f0) || !set.Contains(f1)) continue;
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
    /// <param name="axis">대칭 축(0=X, 1=Y, 2=Z).</param>
    /// <param name="planeOffset">대칭 평면의 오브젝트 공간 위치(축 좌표).</param>
    /// <param name="keepPositive">cut일 때 양수 쪽을 남길지(true) 음수 쪽을 남길지.</param>
    /// <param name="cut">true면 버릴 쪽 면을 먼저 지운다(면 중심 기준).</param>
    /// <param name="mergeThreshold">0보다 크면 평면에서 이 거리 안의 정점을 2배 임계로 병합해 이음매를 닫는다.</param>
    public static List<int> MirrorGeometry(PolyMesh m, int axis, float planeOffset, bool keepPositive, bool cut, float mergeThreshold)
    {
        // 평면 판정 허용 오차: 메시 크기에 비례(최소 1e-6)
        var (bmin, bmax) = Bounds(m);
        float eps = MathF.Max(1e-5f * (bmax - bmin).Length(), 1e-6f);
        float D(Vector3 p) => GetAxis(p, axis) - planeOffset;
        if (cut)
        {
            // Maya Symmetrize: 평면을 가로지르는 면을 평면에서 잘라(v0.0.57; 전에는 면 중심으로만 골라 걸친 면이 통째로 남아 반사본과 겹쳤다)
            // 버릴 쪽 면을 지운다. 남는 면이 없으면(평면이 메시 바깥) 아무것도 바꾸지 않는다.
            var work = m.Clone();
            SplitAlongAxisPlane(work, axis, planeOffset, eps);
            var remove = new List<int>();
            for (int f = 0; f < work.FaceCount; f++)
            {
                if (!work.Faces[f].Alive) continue;
                float c = D(work.FaceCentroid(f));
                if (keepPositive ? c < -eps : c > eps) remove.Add(f);
            }
            foreach (int f in remove) work.RemoveFace(f);
            // 남길 쪽에 평면 위가 아닌 면이 하나도 없으면(평면이 메시 바깥이거나 경계에 닿기만 함) 그대로 둔다
            var loopTmp = new List<int>();
            bool anyOff = false;
            for (int f = 0; f < work.FaceCount && !anyOff; f++)
            {
                if (!work.Faces[f].Alive) continue;
                work.GetFaceVertices(f, loopTmp);
                anyOff = loopTmp.Any(v => MathF.Abs(D(work.Verts[v].Position)) > eps);
            }
            if (!anyOff) return new List<int>();
            m.CopyFrom(work);
        }
        // 평면 위에 놓인 면(모든 정점이 평면 위)은 반사본과 정확히 겹쳐 안쪽 이중 면이 되므로 병합할 때는 지운다
        // (닫힌 메시를 그 면에서 미러하면 붙은 면이 남아 병합 뒤 비매니폴드로 구멍이 났다)
        if (mergeThreshold > 0f || cut)
        {
            var onPlane = new List<int>(); var tmp = new List<int>();
            for (int f = 0; f < m.FaceCount; f++)
            {
                if (!m.Faces[f].Alive) continue;
                m.GetFaceVertices(f, tmp);
                if (tmp.All(v => MathF.Abs(D(m.Verts[v].Position)) <= MathF.Max(eps, mergeThreshold))) onPlane.Add(f);
            }
            if (onPlane.Count > 0 && onPlane.Count < m.AliveFaceCount) foreach (int f in onPlane) m.RemoveFace(f);
        }
        // 현재 메시를 복제해 반사 행렬 T(−o)·S(축 −1)·T(o)로 덧붙인다(Append가 음의 행렬식이면 면 방향을 뒤집어 준다)
        var src = m.Clone();
        var scale = Vector3.One; SetAxis(ref scale, axis, -1f);
        var offset = Vector3.Zero; SetAxis(ref offset, axis, planeOffset);
        var reflect = Matrix4x4.CreateTranslation(-offset) * Matrix4x4.CreateScale(scale) * Matrix4x4.CreateTranslation(offset);
        // 덧붙인 면은 기존 면 수 이후 슬롯에 생긴다(ID 재사용 없음)
        int before = m.FaceCount;
        Append(m, src, reflect);
        var result = new List<int>();
        for (int f = before; f < m.FaceCount; f++) if (m.Faces[f].Alive) result.Add(f);
        float mergeDist = cut ? MathF.Max(mergeThreshold, eps) : mergeThreshold;
        if (mergeDist > 0f)
        {
            var near = new List<int>();
            for (int v = 0; v < m.VertexCount; v++) if (m.Verts[v].Alive && MathF.Abs(D(m.Verts[v].Position)) <= mergeDist) near.Add(v);
            MergeVertices(m, near, mergeDist * 2f);
            // 병합된 이음매 정점은 정확히 평면 위로
            foreach (int v in near) if (m.Verts[v].Alive) { var vt = m.Verts[v]; SetAxis(ref vt.Position, axis, planeOffset); m.Verts[v] = vt; }
            result = result.Where(f => f < m.FaceCount && m.Faces[f].Alive).ToList();
        }
        m.BumpTopology();
        return result;
    }

    /// <summary>
    /// 축 평면(axis 좌표 = offset)에서 메시를 자른다(Symmetrize 전처리). 평면에서 eps 안의 정점은 평면 위로 붙이고,
    /// 평면을 가로지르는 엣지는 교점에서 나누며, 양쪽에 정점이 있는 면은 평면 위 정점 쌍(루프 순서로 둘씩)을 이어 나눈다.
    /// 기존 정점을 지나는 평면(구의 경선 등)도 면이 갈라진다는 점이 <see cref="SliceWithPlane"/>과 다르다.
    /// </summary>
    private static void SplitAlongAxisPlane(PolyMesh m, int axis, float offset, float eps)
    {
        float D(int v) => GetAxis(m.Verts[v].Position, axis) - offset;
        for (int v = 0; v < m.VertexCount; v++)
            if (m.Verts[v].Alive && MathF.Abs(D(v)) <= eps) { var vt = m.Verts[v]; SetAxis(ref vt.Position, axis, offset); m.Verts[v] = vt; }
        int edgeCount = m.EdgeCount;
        for (int e = 0; e < edgeCount; e++)
        {
            if (!m.Edges[e].Alive) continue;
            var (a, b) = m.EdgeVertices(e);
            float da = D(a), db = D(b);
            if (!(da > eps && db < -eps || da < -eps && db > eps)) continue;
            int nv = SplitEdge(m, e, da / (da - db));
            if (nv >= 0) { var vt = m.Verts[nv]; SetAxis(ref vt.Position, axis, offset); m.Verts[nv] = vt; }
        }
        int faceCount = m.FaceCount;
        var loop = new List<int>();
        for (int f = 0; f < faceCount; f++)
        {
            if (!m.Faces[f].Alive) continue;
            m.GetFaceVertices(f, loop);
            bool pos = false, neg = false;
            foreach (int v in loop) { float d = D(v); if (d > eps) pos = true; else if (d < -eps) neg = true; }
            if (!pos || !neg) continue;
            var on = loop.Where(v => MathF.Abs(D(v)) <= eps).ToList();
            for (int i = 0; i + 1 < on.Count; i += 2) SplitFaceBetween(m, on[i], on[i + 1]);
        }
        m.BumpTopology();
    }

    // ------------------------------------------------------------ Cleanup

    /// <summary>Cleanup: 면적 0 면, 라미나(정점 집합이 같은 두 면), 고립 정점을 지운다. 반환값은 지운 면 수.</summary>
    /// <param name="zeroAreaEps">면 넓이(법선 길이의 절반) 임계. 비정규 법선 길이² &lt; eps²이면 넓이 0으로 본다.</param>
    public static int Cleanup(PolyMesh m, float zeroAreaEps = 1e-10f)
    {
        int removed = 0;
        // keys: 정렬된 정점 ID 목록 문자열 → 처음 본 면. 같은 키가 또 나오면 라미나 면으로 보고 지운다.
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
        // 면 삭제 후 남은 고립 정점 정리
        for (int v = 0; v < m.VertexCount; v++) if (m.Verts[v].Alive) m.RemoveVertexIfIsolated(v);
        m.BumpTopology();
        return removed;
    }

    // ------------------------------------------------------------ Conform (wrap onto surface)

    /// <summary>Conform: 정점들을 대상 메시 표면의 가장 가까운 점으로 옮긴다. targetToLocal = 대상 월드 → 이 메시 로컬.</summary>
    /// <remarks>
    /// 대상 메시를 모두 삼각형 팬으로 바꿔(로컬 좌표로 변환) 정점마다 전수 탐색으로 최근접점을 찾는다(O(V·T), 가속 구조 없음).
    /// 위치만 바뀌므로 BumpGeometry.
    /// </remarks>
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
        // 보로노이 영역 판정: 꼭짓점 a/b/c 영역, 엣지 ab/ac/bc 영역, 내부 순으로 검사하고 해당 영역의 최근접점을 돌려준다
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
        // 내부: 무게중심 좌표(v2, w2)로 면 위 투영점
        float denom = 1f / (va + vb + vc);
        float v2 = vb * denom, w2 = vc * denom;
        return a + ab * v2 + ac * w2;
    }

    // ------------------------------------------------------------ Normals (Mesh Display)

    /// <summary>Soften/Harden Edge(각도): 이면각이 angle보다 크면 하드, 아니면 소프트.</summary>
    /// <remarks>경계 엣지와 넓이 0 면에 닿은 엣지는 소프트. 위상은 바꾸지 않는다(호출자가 노멀 재계산).</remarks>
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
    /// <remarks>
    /// 요소 중심에서 각 면 중심으로 가는 벡터와 (면적 가중) 법선의 내적 합이 음수면 대부분이 안쪽을 향한 것으로 보고
    /// <see cref="ReverseFaces"/>로 그 요소 전체를 뒤집는다.
    /// </remarks>
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
    /// <remarks>잠근 노멀은 PolyMesh.LockedNormals(정점 → 노멀)에 저장되고 MeshNormals.Recompute가 그 정점의 코너를 고정한다.</remarks>
    public static void LockNormals(PolyMesh m, IEnumerable<int> vertIds)
    {
        foreach (int v in AliveVerts(m, vertIds))
        {
            var sum = Vector3.Zero; foreach (int he in m.VertexOutgoing(v)) sum += m.Hes[he].Normal;
            if (sum.LengthSquared() > 1e-12f) m.LockedNormals[v] = Vector3.Normalize(sum);
        }
    }

    /// <summary>정점 잠금과 그 정점의 코너 고정(Bevel Harden Normals 등)을 모두 푼다.</summary>
    public static void UnlockNormals(PolyMesh m, IEnumerable<int> vertIds)
    {
        // 죽은 정점이라도 잠금 표는 정리하고, 살아 있는 정점은 코너 고정(NormalLocked)도 해제한다
        foreach (int v in vertIds)
        {
            m.LockedNormals.Remove(v);
            if (v < 0 || v >= m.VertexCount || !m.Verts[v].Alive) continue;
            var outs = m.VertexOutgoing(v).ToArray();
            foreach (int he in outs) { var h = m.Hes[he]; if (h.NormalLocked) { h.NormalLocked = false; m.Hes[he] = h; } }
        }
    }

    /// <summary>Set Vertex Normal: 정점 노멀을 지정 방향으로 잠근다.</summary>
    /// <remarks>영벡터 방향이면 아무것도 하지 않는다.</remarks>
    public static void SetVertexNormal(PolyMesh m, IEnumerable<int> vertIds, Vector3 normal)
    {
        if (normal.LengthSquared() < 1e-12f) return;
        normal = Vector3.Normalize(normal);
        foreach (int v in AliveVerts(m, vertIds)) m.LockedNormals[v] = normal;
    }

    /// <summary>Set to Face: 정점 노멀을 인접 면 법선 평균으로 잠근다(면을 넘겼으면 그 면들만).</summary>
    /// <remarks>인접 면 법선을 정규화해 같은 가중치로 평균한다(면적 무관).</remarks>
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
    /// <remarks>비정규 면 법선을 합하므로 면적 가중 평균이다.</remarks>
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
    /// <param name="point">평면 위의 한 점(오브젝트 공간).</param>
    /// <param name="normal">평면 법선(정규화하지 않아도 됨).</param>
    /// <param name="faceFilter">지정하면 이 면들에 속한 엣지/면만 자른다.</param>
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
            // 두 끝점의 부호 거리 da/db가 같은 부호면(평면 한쪽) 건너뛰고, 교차하면 t = da/(da−db)에서 엣지를 나눈다. 끝점에 너무 가까운 교차는 무시.
            float da = Vector3.Dot(m.Verts[a].Position - point, normal), db = Vector3.Dot(m.Verts[b].Position - point, normal);
            if ((da > 1e-7f && db > 1e-7f) || (da < -1e-7f && db < -1e-7f) || MathF.Abs(da - db) < 1e-12f) continue;
            float t = da / (da - db);
            if (t <= 0.001f || t >= 0.999f) continue;
            int nv = SplitEdge(m, e, t);
            if (nv >= 0) newVerts.Add(nv);
        }
        if (newVerts.Count < 2) return result;
        // 새 정점을 포함한 면마다 면 루프 순서의 새 정점을 두 개씩 짝지어 면을 가로지르는 엣지로 잇는다
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
    /// <remarks>
    /// 실제 구현: points의 각 점은 existingVertexIds[i]가 살아 있는 정점 ID(≥ 0)면 그 정점을 쓰고, 아니면 points[i] 위치에 새 정점을 만든다.
    /// 새 면 루프 = (b, a, 점들…) — 경계 엣지 a→b의 반대 방향으로 시작해 기존 면과 감김이 맞는다. 실패하면 새로 만든 고립 정점을 지운다.
    /// </remarks>
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

    /// <summary>
    /// 엣지 크리즈 값을 설정한다(0..10으로 클램프). Catmull-Clark 서브디비전에서 크리즈 엣지는 날카롭게 유지되며 레벨마다 1씩 줄어든다.
    /// 위상은 바꾸지 않는다.
    /// </summary>
    public static void SetCrease(PolyMesh m, IEnumerable<int> edgeIds, float crease)
    {
        crease = Math.Clamp(crease, 0f, 10f);
        foreach (int e in AliveEdges(m, edgeIds)) { var ed = m.Edges[e]; ed.Crease = crease; m.Edges[e] = ed; }
    }
}
