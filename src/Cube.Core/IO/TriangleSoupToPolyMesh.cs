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
    {
        public required Vector3[] Positions;
        public Vector3[]? Normals;
        public Vector2[]? Uvs;
        public required int[] Indices;
        public int Material;
    }

    public sealed record Stats(int Welded, int Faces, int SkippedFaces, int HardEdges, int MergedQuads);

    public static PolyMesh Convert(IReadOnlyList<Surface> surfaces, ImportOptions options, out Stats stats)
    {
        var mesh = new PolyMesh();
        var weld = new Dictionary<(long, long, long), int>();
        float inv = options.WeldThreshold > 0 ? 1f / options.WeldThreshold : 1e6f;
        int welded = 0, skipped = 0, faces = 0;

        int VertexFor(Vector3 p)
        {
            var key = ((long)MathF.Round(p.X * inv), (long)MathF.Round(p.Y * inv), (long)MathF.Round(p.Z * inv));
            if (weld.TryGetValue(key, out int v)) { welded++; return v; }
            v = mesh.AddVertex(p);
            weld[key] = v;
            return v;
        }

        // 코너 노멀을 보관해 하드 엣지 판정에 쓴다
        foreach (var s in surfaces)
        {
            for (int t = 0; t + 2 < s.Indices.Length; t += 3)
            {
                int i0 = s.Indices[t], i1 = s.Indices[t + 1], i2 = s.Indices[t + 2];
                int a = VertexFor(s.Positions[i0]), b = VertexFor(s.Positions[i1]), c = VertexFor(s.Positions[i2]);
                if (a == b || b == c || a == c) { skipped++; continue; }
                int f = mesh.AddFace(new[] { a, b, c }, s.Material);
                if (f < 0)
                {
                    // 비매니폴드(같은 방향 엣지 등): 정점을 분리해서라도 면을 살린다
                    int a2 = mesh.AddVertex(s.Positions[i0]), b2 = mesh.AddVertex(s.Positions[i1]), c2 = mesh.AddVertex(s.Positions[i2]);
                    f = mesh.AddFace(new[] { a2, b2, c2 }, s.Material);
                    if (f < 0) { skipped++; continue; }
                }
                faces++;
                int he = mesh.Faces[f].HalfEdge;
                SetCorner(mesh, he, s, i0); he = mesh.Hes[he].Next;
                SetCorner(mesh, he, s, i1); he = mesh.Hes[he].Next;
                SetCorner(mesh, he, s, i2);
            }
        }

        // 하드 엣지: 엣지 양쪽 코너 노멀 비교
        float cosHard = MathF.Cos(options.HardAngleDegrees * MathF.PI / 180f);
        int hard = 0;
        bool hasNormals = surfaces.Any(s => s.Normals != null);
        for (int e = 0; e < mesh.EdgeCount; e++)
        {
            var ed = mesh.Edges[e];
            if (!ed.Alive) continue;
            if (ed.He1 < 0) continue;
            var h0 = mesh.Hes[ed.He0]; var h1 = mesh.Hes[ed.He1];
            Vector3 n0a, n1a;
            if (hasNormals) { n0a = h0.Normal; n1a = mesh.Hes[h1.Next].Normal; }   // 정점 a에서의 두 코너
            else { n0a = MeshNormals.FaceNormalUnnormalized(mesh, h0.Face); n1a = MeshNormals.FaceNormalUnnormalized(mesh, h1.Face); }
            float d = Vector3.Dot(Vector3.Normalize(n0a), Vector3.Normalize(n1a));
            if (d < cosHard) { ed.Hard = true; mesh.Edges[e] = ed; hard++; }
        }

        int merged = 0;
        if (options.MergeTriangleQuads) merged = MergeCoplanarTrianglePairs(mesh);

        MeshNormals.Recompute(mesh);
        mesh.BumpTopology();
        stats = new Stats(welded, faces, skipped, hard, merged);
        return mesh;
    }

    private static void SetCorner(PolyMesh mesh, int he, Surface s, int srcIndex)
    {
        var h = mesh.Hes[he];
        if (s.Normals != null && srcIndex < s.Normals.Length) h.Normal = s.Normals[srcIndex];
        if (s.Uvs != null && srcIndex < s.Uvs.Length) h.Uv0 = s.Uvs[srcIndex];
        mesh.Hes[he] = h;
    }

    /// <summary>두 삼각형이 공면이고 합친 사각형이 볼록하면 공유 엣지를 지워 쿼드로 만든다(소프트 엣지만).</summary>
    public static int MergeCoplanarTrianglePairs(PolyMesh mesh, float coplanarCos = 0.9995f)
    {
        int merged = 0;
        var tmp = new List<int>();
        for (int e = 0; e < mesh.EdgeCount; e++)
        {
            var ed = mesh.Edges[e];
            if (!ed.Alive || ed.He1 < 0 || ed.Hard) continue;
            int f0 = mesh.Hes[ed.He0].Face, f1 = mesh.Hes[ed.He1].Face;
            if (mesh.FaceDegree(f0) != 3 || mesh.FaceDegree(f1) != 3) continue;
            var n0 = Vector3.Normalize(MeshNormals.FaceNormalUnnormalized(mesh, f0));
            var n1 = Vector3.Normalize(MeshNormals.FaceNormalUnnormalized(mesh, f1));
            if (Vector3.Dot(n0, n1) < coplanarCos) continue;
            // 합친 사각형 루프: f0의 he0 다음부터, f1의 he1 다음부터
            var loop = new List<int>();
            int cur = mesh.Hes[ed.He0].Next; while (cur != ed.He0) { loop.Add(mesh.Hes[cur].Vertex); cur = mesh.Hes[cur].Next; }
            cur = mesh.Hes[ed.He1].Next; while (cur != ed.He1) { loop.Add(mesh.Hes[cur].Vertex); cur = mesh.Hes[cur].Next; }
            if (loop.Count != 4 || loop.Distinct().Count() != 4) continue;
            // 볼록 판정(법선 평면에 투영)
            EarClipping.PlaneBasis(n0, out var u, out var v);
            var poly = new Vector2[4];
            for (int i = 0; i < 4; i++) { var p = mesh.Verts[loop[i]].Position; poly[i] = new Vector2(Vector3.Dot(p, u), Vector3.Dot(p, v)); }
            if (!EarClipping.IsConvex(poly)) continue;
            // UV 심이면 합치지 않는다(코너 UV가 엣지 양쪽에서 다름)
            var hA0 = mesh.Hes[ed.He0]; var hB1 = mesh.Hes[mesh.Hes[ed.He1].Next];
            var hB0 = mesh.Hes[mesh.Hes[ed.He0].Next]; var hA1 = mesh.Hes[ed.He1];
            if (Vector2.DistanceSquared(hA0.Uv0, hB1.Uv0) > 1e-8f || Vector2.DistanceSquared(hB0.Uv0, hA1.Uv0) > 1e-8f) continue;
            MeshOps.DeleteEdges(mesh, new[] { e });
            merged++;
        }
        return merged;
    }
}
