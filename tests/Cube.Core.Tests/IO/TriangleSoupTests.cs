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
}
