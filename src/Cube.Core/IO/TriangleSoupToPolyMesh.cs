using System.Numerics;
using Cube.Core.Mesh;

namespace Cube.Core.IO;

/// <summary>
/// 삼각형 배열(위치/노멀/UV/인덱스)을 PolyMesh로 변환한다. 같은 위치의 정점은 용접하고 코너 속성(UV/노멀)은 보존한다.
/// 인접 코너 노멀 각도가 임계를 넘으면 하드 엣지로 표시하고, 옵션에 따라 공면 삼각형 쌍을 쿼드로 합친다.
/// </summary>
public static class TriangleSoupToPolyMesh
{
    public sealed class Surface
    /// <summary>
    /// 변환 입력 한 덩어리(Godot ArrayMesh의 서피스 하나에 해당). 인덱스 3개가 삼각형 하나이고
    /// Positions/Normals/Uvs/WeldTag는 같은 인덱스 공간(soup 인덱스)을 공유한다.
    /// </summary>
    {
        public required Vector3[] Positions;
        /// <summary>정점 위치(코어 좌표계, m).</summary>
        public Vector3[]? Normals;
        /// <summary>정점 노멀(선택). 있으면 코너 노멀로 복사하고 하드 엣지 추론에도 쓴다.</summary>
        public Vector2[]? Uvs;
        /// <summary>정점 UV(선택, 코어 규약인 하단 원점이어야 한다).</summary>
        public required int[] Indices;
        /// <summary>삼각형 인덱스 목록(3개씩). 감김 방향은 코어 규약(CCW = 앞면)이어야 한다.</summary>
        public int Material;
        /// <summary>이 서피스의 면들에 줄 머티리얼 인덱스.</summary>
        /// <summary>정점별 용접 태그(예: 스킨 가중치 해시). 위치가 같아도 태그가 다르면 합치지 않는다(맞닿은 다른 부품이 한 정점이 되어 가중치를 잃는 것 방지).</summary>
        public long[]? WeldTag;
    }

    public sealed record Stats(int Welded, int Faces, int SkippedFaces, int HardEdges, int MergedQuads);
/// <summary>
/// 변환 통계. Welded = 기존 정점에 합쳐진 코너 수, Faces = 만든 삼각형 수, SkippedFaces = 퇴화/비매니폴드로 버린 삼각형 수,
/// HardEdges = 노멀 각도로 하드 표시한 엣지 수, MergedQuads = 쿼드로 합친 삼각형 쌍 수.
/// </summary>

    public static PolyMesh Convert(IReadOnlyList<Surface> surfaces, ImportOptions options, out Stats stats) => Convert(surfaces, options, out stats, out _);
/// <summary>정점 맵이 필요 없을 때 쓰는 가장 간단한 오버로드.</summary>

    /// <summary>vertexMap[surface][soupIndex] = 폴리 정점 ID(-1 = 미사용). 스킨 가중치 등 정점별 속성을 옮길 때 쓴다.</summary>
    public static PolyMesh Convert(IReadOnlyList<Surface> surfaces, ImportOptions options, out Stats stats, out int[][] vertexMap)
        /// <remarks>정점 출처(vertexSource)는 버린다.</remarks>
        => Convert(surfaces, options, out stats, out vertexMap, out _);

    /// <summary>
    /// vertexSource[polyVertex] = 그 정점을 만든 (surface, soupIndex). 비매니폴드 면을 살리려고 복제한 정점도 포함하므로
    /// 정점별 속성(스킨 가중치)은 vertexMap이 아니라 이것으로 옮겨야 빠지는 정점이 없다.
    /// </summary>
    public static PolyMesh Convert(IReadOnlyList<Surface> surfaces, ImportOptions options, out Stats stats, out int[][] vertexMap, out (int Surface, int Index)[] vertexSource)
    /// <param name="surfaces">입력 서피스 목록(같은 메시로 합쳐진다).</param>
    /// <param name="options">용접 임계·하드 각도·쿼드 병합 옵션.</param>
    /// <param name="stats">변환 통계.</param>
    /// <param name="vertexMap">[서피스][soup 인덱스] → 폴리 정점 ID.</param>
    /// <param name="vertexSource">[폴리 정점 ID] → 만든 (서피스, soup 인덱스).</param>
    /// <returns>노멀이 재계산된 새 PolyMesh.</returns>
    {
        var source = new List<(int, int)>();
        // source = 폴리 정점 ID 순서대로 그 정점을 만든 (서피스, soup 인덱스). AddVertex가 ID를 순서대로 주므로 인덱스가 곧 ID다.
        var mesh = new PolyMesh();
        vertexMap = surfaces.Select(s => Enumerable.Repeat(-1, s.Positions.Length).ToArray()).ToArray();
        // vertexMap은 처음엔 모두 -1(삼각형에 쓰이지 않은 soup 정점은 -1로 남는다). 람다 캡처를 위해 지역 변수 vmap에 둔다.
        var vmap = vertexMap;
        var weld = new Dictionary<(long, long, long, long), int>();
        // 용접 테이블: (양자화 X, Y, Z, 태그) → 정점 ID. 위치를 WeldThreshold 격자로 반올림해 같은 칸이면 같은 정점.
        float inv = options.WeldThreshold > 0 ? 1f / options.WeldThreshold : 1e6f;
        int welded = 0, skipped = 0, faces = 0;

        int NewVertex(Vector3 p, int si, int idx) { source.Add((si, idx)); return mesh.AddVertex(p); }
        // 새 정점을 만들고 출처를 기록한다(용접하지 않는 복제 정점도 이 경로로 만든다).
        int VertexFor(Surface s, int si, int idx)
        // soup 인덱스 idx에 대한 폴리 정점을 찾거나 만든다(위치 격자 + 용접 태그가 같으면 재사용).
        {
            var p = s.Positions[idx];
            long tag = s.WeldTag != null && idx < s.WeldTag.Length ? s.WeldTag[idx] : 0;
            var key = ((long)MathF.Round(p.X * inv), (long)MathF.Round(p.Y * inv), (long)MathF.Round(p.Z * inv), tag);
            if (weld.TryGetValue(key, out int v)) { welded++; return v; }
            v = NewVertex(p, si, idx);
            weld[key] = v;
            return v;
        }

        // 코너 노멀을 보관해 하드 엣지 판정에 쓴다
        for (int si = 0; si < surfaces.Count; si++)
        {
            var s = surfaces[si];
            for (int t = 0; t + 2 < s.Indices.Length; t += 3)
            {
                int i0 = s.Indices[t], i1 = s.Indices[t + 1], i2 = s.Indices[t + 2];
                // 퇴화 삼각형(용접 결과 두 코너가 같은 정점)은 버린다.
                int a = VertexFor(s, si, i0), b = VertexFor(s, si, i1), c = VertexFor(s, si, i2);
                if (a == b || b == c || a == c) { skipped++; continue; }
                int f = mesh.AddFace(new[] { a, b, c }, s.Material);
                if (f < 0)
                {
                    // 비매니폴드(같은 방향 엣지 등): 정점을 분리해서라도 면을 살린다
                    int a2 = NewVertex(s.Positions[i0], si, i0), b2 = NewVertex(s.Positions[i1], si, i1), c2 = NewVertex(s.Positions[i2], si, i2);
                    f = mesh.AddFace(new[] { a2, b2, c2 }, s.Material);
                    if (f < 0) { skipped++; continue; }
                    a = a2; b = b2; c = c2;
                }
                vmap[si][i0] = a; vmap[si][i1] = b; vmap[si][i2] = c;
                // 성공한 코너들을 정점 맵에 기록하고 면의 세 코너에 노멀/UV를 복사한다(AddFace는 첫 코너를 Faces[f].HalfEdge로 둔다).
                faces++;
                int he = mesh.Faces[f].HalfEdge;
                SetCorner(mesh, he, s, i0); he = mesh.Hes[he].Next;
                SetCorner(mesh, he, s, i1); he = mesh.Hes[he].Next;
                SetCorner(mesh, he, s, i2);
            }
        }

        // 하드 엣지: 엣지 양쪽 코너 노멀 비교
        float cosHard = MathF.Cos(options.HardAngleDegrees * MathF.PI / 180f);
        // cosHard보다 내적이 작으면(= 각도가 HardAngleDegrees보다 크면) 하드 엣지.
        int hard = 0;
        bool hasNormals = surfaces.Any(s => s.Normals != null);
        for (int e = 0; e < mesh.EdgeCount; e++)
        {
            var ed = mesh.Edges[e];
            // 경계 엣지(한쪽에만 면)는 비교할 상대가 없으므로 하드 판정을 하지 않는다.
            if (!ed.Alive) continue;
            if (ed.He1 < 0) continue;
            var h0 = mesh.Hes[ed.He0]; var h1 = mesh.Hes[ed.He1];
            Vector3 n0a, n1a;
            // 노멀이 없는 입력이면 두 면의 기하 법선으로 대신 판정한다.
            if (hasNormals) { n0a = h0.Normal; n1a = mesh.Hes[h1.Next].Normal; }   // 정점 a에서의 두 코너
            else { n0a = MeshNormals.FaceNormalUnnormalized(mesh, h0.Face); n1a = MeshNormals.FaceNormalUnnormalized(mesh, h1.Face); }
            float d = Vector3.Dot(Vector3.Normalize(n0a), Vector3.Normalize(n1a));
            if (d < cosHard) { ed.Hard = true; mesh.Edges[e] = ed; hard++; }
        }

        int merged = 0;
        // 공면 삼각형 쌍을 쿼드로 합친다(하드 엣지·UV 심 엣지는 건드리지 않는다).
        if (options.MergeTriangleQuads) merged = MergeCoplanarTrianglePairs(mesh);

        MeshNormals.Recompute(mesh);
        // 최종 코너 노멀을 하드 엣지 규칙대로 다시 계산하고 위상 변경을 알린다.
        mesh.BumpTopology();
        stats = new Stats(welded, faces, skipped, hard, merged);
        vertexSource = source.ToArray();
        return mesh;
    }

    private static void SetCorner(PolyMesh mesh, int he, Surface s, int srcIndex)
    /// <summary>
    /// 하프에지 <paramref name="he"/>(= 면의 한 코너)에 원본 soup 정점 <paramref name="srcIndex"/>의 노멀/UV를 복사한다.
    /// 배열이 없거나 짧으면 해당 속성은 건드리지 않는다.
    /// </summary>
    {
        var h = mesh.Hes[he];
        if (s.Normals != null && srcIndex < s.Normals.Length) h.Normal = s.Normals[srcIndex];
        if (s.Uvs != null && srcIndex < s.Uvs.Length) h.Uv0 = s.Uvs[srcIndex];
        mesh.Hes[he] = h;
    }

    /// <summary>
    /// 두 삼각형이 공면이고 합친 사각형이 볼록하면 공유 엣지를 지워 쿼드로 만든다(소프트 엣지, UV 연속만).
    /// 짝을 먼저 모두 고른 뒤(탐욕적, 면마다 한 번) 메시를 한 번에 다시 만든다 — 엣지를 하나씩 지우면 매번 캐시를 다시 만들어 O(면²)이 된다.
    /// 정점 ID는 그대로라 가져오기의 정점 맵이 유효하다.
    /// </summary>
    public static int MergeCoplanarTrianglePairs(PolyMesh mesh, float coplanarCos = 0.9995f)
    /// <param name="mesh">대상 메시(제자리에서 바뀐다).</param>
    /// <param name="coplanarCos">두 면 법선 내적이 이 값 이상이어야 공면으로 본다(기본 ≈ 1.8°).</param>
    /// <returns>합친 쌍의 수.</returns>
    {
        var partner = new Dictionary<int, (int other, int edge)>();
        // 1단계: 면 → (짝 면, 공유 엣지) 표. 한 면은 한 번만 짝지어진다.
        for (int e = 0; e < mesh.EdgeCount; e++)
        {
            var ed = mesh.Edges[e];
            if (!ed.Alive || ed.He1 < 0 || ed.Hard) continue;
            // 내부의 소프트 엣지만 후보. 이미 짝이 있는 면, 삼각형이 아닌 면은 제외.
            int f0 = mesh.Hes[ed.He0].Face, f1 = mesh.Hes[ed.He1].Face;
            if (partner.ContainsKey(f0) || partner.ContainsKey(f1)) continue;
            if (mesh.FaceDegree(f0) != 3 || mesh.FaceDegree(f1) != 3) continue;
            var n0 = MeshNormals.FaceNormalUnnormalized(mesh, f0); var n1 = MeshNormals.FaceNormalUnnormalized(mesh, f1);
            // 법선이 퇴화했거나 각도 차가 크면(비공면) 제외.
            if (n0.LengthSquared() < 1e-20f || n1.LengthSquared() < 1e-20f) continue;
            n0 = Vector3.Normalize(n0); n1 = Vector3.Normalize(n1);
            if (Vector3.Dot(n0, n1) < coplanarCos) continue;
            var loop = QuadLoop(mesh, ed);
            // 합친 사각형의 네 코너가 서로 다른 정점이어야 한다.
            if (loop[0] == loop[2] || loop[1] == loop[3] || loop.Distinct().Count() != 4) continue;
            EarClipping.PlaneBasis(n0, out var u, out var v);
            // 사각형을 면 평면에 투영해 볼록성 검사(오목 쿼드는 렌더링/편집에서 문제가 되므로 합치지 않는다).
            var poly = new Vector2[4];
            for (int i = 0; i < 4; i++) { var p = mesh.Verts[mesh.Hes[loop[i]].Vertex].Position; poly[i] = new Vector2(Vector3.Dot(p, u), Vector3.Dot(p, v)); }
            if (!EarClipping.IsConvex(poly)) continue;
            // UV 심이면 합치지 않는다(코너 UV가 엣지 양쪽에서 다름)
            var hA0 = mesh.Hes[ed.He0]; var hB1 = mesh.Hes[mesh.Hes[ed.He1].Next];
            var hB0 = mesh.Hes[mesh.Hes[ed.He0].Next]; var hA1 = mesh.Hes[ed.He1];
            if (Vector2.DistanceSquared(hA0.Uv0, hB1.Uv0) > 1e-8f || Vector2.DistanceSquared(hB0.Uv0, hA1.Uv0) > 1e-8f) continue;
            partner[f0] = (f1, e); partner[f1] = (f0, e);
        }
        if (partner.Count == 0) return 0;
// 2단계: 정점 슬롯을 그대로(죽은 정점도 죽은 채로) 복사해 정점 ID를 유지한다.

        // 같은 정점 ID로 다시 만든다: 짝이면 쿼드(앞 면에서 한 번), 아니면 그대로
        var rebuilt = new PolyMesh();
        foreach (var vt in mesh.Verts) { int id = rebuilt.AddVertex(vt.Position); if (!vt.Alive) { var d = rebuilt.Verts[id]; d.Alive = false; rebuilt.Verts[id] = d; } }
        int merged = 0;
        var tmp = new List<int>();
        // 3단계: 면을 원래 순서대로 다시 만든다. 짝이 있으면 ID가 작은 면에서 쿼드 하나를 만들고 큰 면은 건너뛴다.
        for (int f = 0; f < mesh.FaceCount; f++)
        {
            if (!mesh.Faces[f].Alive) continue;
            List<int> corners;
            if (partner.TryGetValue(f, out var pr))
            {
                if (pr.other < f) continue; // 짝의 앞 면에서 이미 만듦
                corners = QuadLoop(mesh, mesh.Edges[pr.edge]);
                merged++;
            }
            else { mesh.GetFaceHalfEdges(f, tmp); corners = new List<int>(tmp); }
            var ids = corners.Select(h => mesh.Hes[h].Vertex).ToArray();
            int nf = rebuilt.AddFace(ids, mesh.Faces[f].Material);
            if (nf < 0) continue;
            int he = rebuilt.Faces[nf].HalfEdge;
            // 새 면의 코너에 원래 코너의 UV/노멀을 순서대로 복사한다.
            foreach (int src in corners)
            {
                var h = rebuilt.Hes[he]; h.Uv0 = mesh.Hes[src].Uv0; h.Normal = mesh.Hes[src].Normal; rebuilt.Hes[he] = h;
                he = rebuilt.Hes[he].Next;
            }
        }
        for (int e = 0; e < mesh.EdgeCount; e++)
        // 4단계: 하드 엣지 플래그를 정점 쌍으로 찾아 새 메시로 옮긴다(엣지 ID는 바뀌었기 때문).
        {
            var ed = mesh.Edges[e];
            if (!ed.Alive || !ed.Hard) continue;
            var (a, b) = mesh.EdgeVertices(e);
            int ne = rebuilt.FindEdge(a, b);
            if (ne >= 0) { var x = rebuilt.Edges[ne]; x.Hard = true; rebuilt.Edges[ne] = x; }
        }
        mesh.CopyFrom(rebuilt);
        // 결과를 원래 메시 객체에 덮어쓴다(호출자의 참조 유지).
        return merged;
    }

    /// <summary>엣지 ed를 공유하는 두 삼각형을 합친 사각형의 하프에지 순서(코너 = 하프에지의 출발 정점).</summary>
    private static List<int> QuadLoop(PolyMesh mesh, Edge ed)
    {
        var loop = new List<int>(4);
        int cur = mesh.Hes[ed.He0].Next; while (cur != ed.He0) { loop.Add(cur); cur = mesh.Hes[cur].Next; }
        // 각 삼각형에서 공유 엣지를 제외한 나머지 두 하프에지를 이어 붙이면 사각형 루프가 된다.
        cur = mesh.Hes[ed.He1].Next; while (cur != ed.He1) { loop.Add(cur); cur = mesh.Hes[cur].Next; }
        return loop;
    }
}
