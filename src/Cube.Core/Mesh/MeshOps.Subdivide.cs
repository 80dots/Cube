using System.Numerics;

namespace Cube.Core.Mesh;

/// <summary>Catmull-Clark 서브디비전(Maya Smooth / Smooth Mesh Preview).</summary>
public static partial class MeshOps
{
    /// <summary>
    /// Catmull-Clark 한 단계를 적용한 새 메시를 돌려준다. n각형·경계 지원. 코너 UV는 면 안에서 보간하므로 심이 유지되고,
    /// 하드 엣지/심 플래그는 자식 엣지에 물려준다(하드 엣지는 노멀에만 영향, 형태는 매끈하게).
    /// </summary>
    /// <remarks>
    /// 원본 m은 바꾸지 않는다. 결과 메시의 정점 = 면 점(F) + 엣지 점(E) + 정점 점(V').
    /// 면 점 = 면 정점 평균. 엣지 점 = (양끝 + 양쪽 면 점)/4(경계는 중점)를 크리즈 세기(0..1)만큼 중점으로 블렌드.
    /// 정점 점 = (F + 2R + (n−3)P)/n(F = 인접 면 점 평균, R = 인접 엣지 중점 평균, n = 가수), 경계 정점은 3/4 P + 1/8(경계 엣지 중점 두 개),
    /// 경계 엣지가 3개 이상이거나 가수 2인 경계 정점은 고정. 원래 면의 각 코너는 쿼드 [V', E'out, F, E'in]이 된다.
    /// 노멀은 Zero로 두고 호출자가 재계산한다.
    /// </remarks>
    public static PolyMesh CatmullClark(PolyMesh m)
    {
        var o = new PolyMesh();
        // 원본 슬롯 인덱스 → 결과 메시 정점 ID 매핑 표(죽은 요소는 -1). facePos/edgeMid는 위치 캐시.
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
            var smooth = f1 < 0 ? mid : (m.Verts[a].Position + m.Verts[b].Position + facePos[f0] + facePos[f1]) * 0.25f;
            float crease = Math.Clamp(m.Edges[e].Crease, 0f, 1f); // 크리즈: 날카로운 엣지 점(중점)과 블렌드
            edgePt[e] = o.AddVertex(Vector3.Lerp(smooth, mid, crease));
        }
        // 면 점 정점은 엣지 점 다음에 추가(ID 순서는 결과에 영향 없음)
        for (int f = 0; f < nf; f++) if (m.Faces[f].Alive) facePt[f] = o.AddVertex(facePos[f]);
        // 3) 정점 점
        var edges = new List<int>(); var faces = new List<int>();
        for (int v = 0; v < nv; v++)
        {
            vertPt[v] = -1;
            if (!m.Verts[v].Alive) continue;
            m.GetVertexEdges(v, edges);
            var p = m.Verts[v].Position;
            // boundary: 경계 엣지, creased: 크리즈 값이 있는 엣지
            var boundary = edges.Where(e => m.IsBoundaryEdge(e)).ToList();
            var creased = edges.Where(e => m.Edges[e].Crease > 0f).ToList();
            Vector3 np;
            if (boundary.Count < 2 && creased.Count >= 2)
            {
                // 크리즈 정점 규칙: 2개면 크리즈 곡선(3/4 v + 1/8 양쪽), 3개 이상이면 코너 고정. 크리즈 세기로 매끈한 결과와 블렌드
                m.GetVertexFaces(v, faces);
                int n = edges.Count;
                var F = Vector3.Zero; foreach (int f in faces.Distinct()) F += facePos[f]; F /= Math.Max(faces.Distinct().Count(), 1);
                var R = Vector3.Zero; foreach (int e in edges) R += edgeMid[e]; R /= Math.Max(n, 1);
                var smoothP = n > 0 ? (F + R * 2f + p * (n - 3)) / n : p;
                var sharpP = creased.Count == 2 ? p * 0.75f + (edgeMid[creased[0]] + edgeMid[creased[1]]) * 0.125f : p;
                float w = Math.Clamp(creased.Min(e => m.Edges[e].Crease), 0f, 1f);
                np = Vector3.Lerp(smoothP, sharpP, w);
            }
            else if (boundary.Count >= 2)
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
            // 면 점 UV = 코너 UV 평균, 엣지 점 UV = 양끝 코너 UV 중점(면 안에서만 보간하므로 심 양쪽 UV가 섞이지 않는다)
            var uvSum = Vector2.Zero; foreach (int he in hes) uvSum += m.Hes[he].Uv0;
            var uvF = uvSum / n;
            for (int i = 0; i < n; i++)
            {
                // 코너 i: 이 코너에서 나가는 하프에지(heOut)와 들어오는 하프에지(heIn)
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

    /// <summary>
    /// 원본 엣지 e의 하드/심 플래그를 결과 메시의 자식 엣지 a-b로 복사한다. 크리즈는 레벨마다 1씩 줄여(최소 0) 점점 부드러워지게 한다.
    /// </summary>
    private static void CopyEdgeFlags(PolyMesh src, int e, PolyMesh dst, int a, int b)
    {
        int ne = dst.FindEdge(a, b);
        if (ne < 0) return;
        var ed = dst.Edges[ne]; ed.Hard = src.Edges[e].Hard; ed.Seam = src.Edges[e].Seam; ed.Crease = MathF.Max(0f, src.Edges[e].Crease - 1f); dst.Edges[ne] = ed;
    }

    /// <summary>levels단계 Catmull-Clark을 적용해 메시를 제자리에서 교체한다.</summary>
    /// <remarks>
    /// levels는 0..5로 클램프(0이면 아무것도 안 함). 매 단계 새 메시를 만들고 마지막에 <see cref="PolyMesh.CopyFrom"/>으로 m 내용을 통째로 교체한다
    /// (ID가 모두 바뀌므로 호출자는 컴포넌트 선택을 비운다).
    /// </remarks>
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
