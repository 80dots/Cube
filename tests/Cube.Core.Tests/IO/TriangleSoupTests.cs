using System.Numerics;
using Cube.Core.IO;
using Cube.Core.Mesh;

namespace Cube.Core.Tests.IO;

public class TriangleSoupTests
{
    /// <summary>PolyMesh → 삼각형 배열(코너 언롤) → 다시 PolyMesh.</summary>
    private static TriangleSoupToPolyMesh.Surface ToSoup(PolyMesh m)
    {
        var r = MeshTessellator.Build(m);
        var s = new TriangleSoupToPolyMesh.Surface
        {
            Positions = r.Positions.Take(r.CornerCount).ToArray(),
            Normals = r.Normals.Take(r.CornerCount).ToArray(),
            Uvs = r.Uvs.Take(r.CornerCount).ToArray(),
            Indices = r.Indices.Take(r.IndexCount).ToArray(),
        };
        return s;
    }

    [Fact]
    public void Cube_RoundTrip_RestoresQuadsAndHardEdges()
    {
        var src = MeshBuilder.Cube();
        var soup = ToSoup(src);
        Assert.Equal(24, soup.Positions.Length);
        var m = TriangleSoupToPolyMesh.Convert(new[] { soup }, ImportOptions.Default, out var stats);
        Assert.Empty(MeshValidator.Check(m));
        Assert.Equal(8, m.AliveVertexCount);
        Assert.Equal(6, m.AliveFaceCount);
        Assert.Equal(12, m.AliveEdgeCount);
        Assert.Equal(6, stats.MergedQuads);
        for (int e = 0; e < m.EdgeCount; e++) if (m.Edges[e].Alive) Assert.True(m.Edges[e].Hard);
        for (int f = 0; f < m.FaceCount; f++) if (m.Faces[f].Alive) Assert.True(Vector3.Dot(m.Faces[f].Normal, m.FaceCentroid(f)) > 0);
    }

    [Fact]
    public void Sphere_RoundTrip_IsSmooth()
    {
        var src = MeshBuilder.Sphere(segments: 12, rings: 6);
        var soup = ToSoup(src);
        var m = TriangleSoupToPolyMesh.Convert(new[] { soup }, ImportOptions.Default, out var stats);
        Assert.Empty(MeshValidator.Check(m));
        Assert.Equal(src.AliveVertexCount, m.AliveVertexCount);
        Assert.Equal(0, stats.HardEdges);
        Assert.Equal(src.AliveFaceCount, m.AliveFaceCount); // 쿼드 복원
        Assert.Equal(2, m.AliveVertexCount - m.AliveEdgeCount + m.AliveFaceCount);
    }

    [Fact]
    public void Plane_WithUvSeam_KeepsSeamTriangles()
    {
        // 두 삼각형이 공면이지만 UV가 다르면 쿼드로 합치지 않는다
        var s = new TriangleSoupToPolyMesh.Surface
        {
            Positions = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 0, -1), new Vector3(0, 0, 0), new Vector3(1, 0, -1), new Vector3(0, 0, -1) },
            Normals = Enumerable.Repeat(Vector3.UnitY, 6).ToArray(),
            Uvs = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0.5f, 0), new Vector2(1, 1), new Vector2(0, 1) },
            Indices = new[] { 0, 1, 2, 3, 4, 5 },
        };
        var m = TriangleSoupToPolyMesh.Convert(new[] { s }, ImportOptions.Default, out var stats);
        Assert.Equal(2, m.AliveFaceCount);
        Assert.Equal(0, stats.MergedQuads);
        Assert.Equal(4, m.AliveVertexCount);
    }

    /// <summary>
    /// 회귀(v0.0.37): 큰 메시 가져오기가 수 분 멈췄다(AddVertex가 엣지 캐시를 무효화, 쌍 병합이 엣지마다 캐시 재구성,
    /// long 키 기본 해시가 a^b라 충돌). 8만 삼각형 격자가 선형 시간 안에 변환되어야 한다.
    /// </summary>
    [Fact]
    public void LargeGrid_ConvertsInLinearTime()
    {
        const int n = 200;
        var pos = new List<Vector3>(); var nrm = new List<Vector3>(); var uv = new List<Vector2>(); var idx = new List<int>();
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                // 살짝 휜 면이라 일부만 쿼드로 합쳐진다(병합·비병합 경로 모두)
                Vector3 P(int i, int j) => new(i * 0.01f, 0.02f * MathF.Sin(i * 0.3f) * MathF.Sin(j * 0.2f), j * 0.01f);
                int b = pos.Count;
                foreach (var (i, j) in new[] { (x, y), (x + 1, y), (x + 1, y + 1), (x, y + 1) })
                { pos.Add(P(i, j)); nrm.Add(Vector3.UnitY); uv.Add(new Vector2(i / (float)n, j / (float)n)); }
                idx.AddRange(new[] { b, b + 2, b + 1, b, b + 3, b + 2 });
            }
        var soup = new TriangleSoupToPolyMesh.Surface { Positions = pos.ToArray(), Normals = nrm.ToArray(), Uvs = uv.ToArray(), Indices = idx.ToArray() };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var m = TriangleSoupToPolyMesh.Convert(new[] { soup }, ImportOptions.Default, out var stats);
        sw.Stop();
        Assert.Equal((n + 1) * (n + 1), m.AliveVertexCount);
        Assert.True(stats.MergedQuads > 0);
        Assert.Empty(MeshValidator.Check(m));
        Assert.True(sw.ElapsedMilliseconds < 5000, $"80k tris took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void PairKeyComparer_SpreadsNeighbouringPairs()
    {
        // 기본 long 해시(a^b)는 (a, a+1) 쌍이 몇 개 값으로 몰린다. 섞은 해시는 거의 모두 달라야 한다.
        var hashes = new HashSet<int>();
        for (int a = 0; a < 10000; a++) hashes.Add(PairKeyComparer.Instance.GetHashCode(((long)a << 32) | (uint)(a + 1)));
        Assert.True(hashes.Count > 9900);
    }

    [Fact]
    public void WeldTag_KeepsTouchingPartsApart_AndVertexSourceCoversEveryVertex()
    {
        // 정점 하나를 공유하는 두 삼각형(다른 부품): 태그가 다르면 용접하지 않는다
        var pos = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, 0), new Vector3(-1, 0, 0), new Vector3(0, -1, 0) };
        var soup = new TriangleSoupToPolyMesh.Surface { Positions = pos, Indices = new[] { 0, 1, 2, 3, 4, 5 }, WeldTag = new long[] { 1, 1, 1, 2, 2, 2 } };
        var m = TriangleSoupToPolyMesh.Convert(new[] { soup }, ImportOptions.Default, out _, out _, out var src);
        Assert.Equal(6, m.AliveVertexCount);
        soup.WeldTag = null;
        m = TriangleSoupToPolyMesh.Convert(new[] { soup }, ImportOptions.Default, out _, out _, out src);
        Assert.Equal(5, m.AliveVertexCount);
        Assert.Equal(m.VertexCount, src.Length);
        Assert.All(src, x => Assert.InRange(x.Index, 0, 5));
    }

    [Fact]
    public void NonManifoldDuplicates_HaveVertexSource()
    {
        // 같은 방향 엣지를 가진 세 번째 삼각형은 정점을 복제해 살린다 → 복제 정점도 출처가 있어야 한다(스킨 가중치 누락 방지)
        var pos = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 0, 1) };
        var soup = new TriangleSoupToPolyMesh.Surface { Positions = pos, Indices = new[] { 0, 1, 2, 3, 4, 5 } };
        var m = TriangleSoupToPolyMesh.Convert(new[] { soup }, ImportOptions.Default with { MergeTriangleQuads = false }, out _, out var vmap, out var src);
        Assert.Equal(m.VertexCount, src.Length);
        for (int v = 0; v < m.VertexCount; v++) Assert.Equal(pos[src[v].Index], m.Verts[v].Position);
    }
}
