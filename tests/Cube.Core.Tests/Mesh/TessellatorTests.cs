using System.Numerics;
using Cube.Core.Mesh;

namespace Cube.Core.Tests.Mesh;

public class TessellatorTests
{
    [Fact]
    public void Cube_Produces12TrianglesAnd24Corners()
    {
        var m = MeshBuilder.Cube();
        var r = MeshTessellator.Build(m);
        Assert.Equal(24, r.CornerCount);
        Assert.Equal(12, r.TriangleCount);
        Assert.Equal(12, r.LineCount);
        Assert.Equal(8, r.PointCount);
        Assert.Equal(6, r.FaceCenterCount);
        for (int t = 0; t < r.TriangleCount; t++)
        {
            int f = r.TriToFace[t];
            var a = r.Positions[r.Indices[t * 3]]; var b = r.Positions[r.Indices[t * 3 + 1]]; var c = r.Positions[r.Indices[t * 3 + 2]];
            var n = Vector3.Normalize(Vector3.Cross(b - a, c - a));
            Assert.True(Vector3.Dot(n, m.Faces[f].Normal) > 0.99f, $"tri {t} winding mismatch");
        }
    }

    [Fact]
    public void Cylinder_NgonCaps_TriangulateFully()
    {
        var m = MeshBuilder.Cylinder(segments: 8);
        var r = MeshTessellator.Build(m);
        // 옆면 8쿼드 = 16 tri, 캡 2개 × (8-2) = 12 tri
        Assert.Equal(28, r.TriangleCount);
    }

    [Fact]
    public void ConcaveNgon_UsesEarClipping()
    {
        // L자형 6각형 (XZ 평면, 반시계 from +Y)
        var m = new PolyMesh();
        int[] v =
        {
            m.AddVertex(new(0, 0, 0)), m.AddVertex(new(2, 0, 0)), m.AddVertex(new(2, 0, -1)),
            m.AddVertex(new(1, 0, -1)), m.AddVertex(new(1, 0, -2)), m.AddVertex(new(0, 0, -2)),
        };
        int f = m.AddFace(v);
        Assert.True(f >= 0);
        MeshNormals.Recompute(m);
        Assert.True(Vector3.Dot(m.Faces[f].Normal, Vector3.UnitY) > 0.99f);
        var r = MeshTessellator.Build(m);
        Assert.Equal(4, r.TriangleCount);
        // 모든 삼각형이 면 노멀과 같은 방향이고 면적 합이 3
        float area = 0;
        for (int t = 0; t < r.TriangleCount; t++)
        {
            var a = r.Positions[r.Indices[t * 3]]; var b = r.Positions[r.Indices[t * 3 + 1]]; var c = r.Positions[r.Indices[t * 3 + 2]];
            var cr = Vector3.Cross(b - a, c - a);
            Assert.True(cr.Y > 0, $"tri {t} flipped");
            area += cr.Length() / 2;
        }
        Assert.True(MathF.Abs(area - 3f) < 1e-4f, $"area {area}");
    }

    [Fact]
    public void UpdatePositions_TracksVertexMoves()
    {
        var m = MeshBuilder.Cube();
        var r = MeshTessellator.Build(m);
        var v = m.Verts[0]; v.Position = new Vector3(5, 5, 5); m.Verts[0] = v; m.BumpGeometry();
        MeshTessellator.UpdatePositions(m, r);
        int hits = 0;
        for (int i = 0; i < r.CornerCount; i++) if (r.Positions[i] == new Vector3(5, 5, 5)) hits++;
        Assert.Equal(3, hits); // 정점 0은 3개 면의 코너
        Assert.Contains(new Vector3(5, 5, 5), r.PointPositions.Take(r.PointCount));
    }
}
