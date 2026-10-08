using System.Numerics;
using Cube.Core.Mesh;

namespace Cube.Core.Tests.Mesh;

/// <summary>Blender식 Extrude(ExtrudeOptions) 검사.</summary>
public class MeshOpsExtrudeOptionsTests
{
    private static int Euler(PolyMesh m) => m.AliveVertexCount - m.AliveEdgeCount + m.AliveFaceCount;
    private static bool Closed(PolyMesh m) => Enumerable.Range(0, m.EdgeCount).All(e => !m.Edges[e].Alive || !m.IsBoundaryEdge(e));
    private static int TopFace(PolyMesh m) => Enumerable.Range(0, m.FaceCount).First(f => m.Faces[f].Alive && MeshNormals.FaceNormalUnnormalized(m, f).Y > 0.5f);

    [Fact]
    public void Region_CubeTopFace_OffsetAlongNormal()
    {
        var m = MeshBuilder.Cube();
        var caps = MeshOps.Extrude(m, new[] { TopFace(m) }, new ExtrudeOptions { Offset = 0.5f });
        Assert.Single(caps);
        Assert.Empty(MeshValidator.Check(m));
        Assert.Equal(2, Euler(m));
        Assert.Equal(10, m.AliveFaceCount);
        var ids = new List<int>(); m.GetFaceVertices(caps[0], ids);
        Assert.All(ids, v => Assert.Equal(1f, m.Verts[v].Position.Y, 4));
    }

    [Fact]
    public void Region_OpenPlane_BecomesClosedBox()
    {
        // 열린 판(모든 경계가 메시 테두리): 원래 면을 뒤집어 남겨 직육면체가 된다
        var m = MeshBuilder.Plane(1, 1, 1, 1);
        var faces = Enumerable.Range(0, m.FaceCount).Where(f => m.Faces[f].Alive).ToList();
        var caps = MeshOps.Extrude(m, faces, new ExtrudeOptions { Offset = 1f });
        Assert.Single(caps);
        Assert.Empty(MeshValidator.Check(m));
        Assert.True(Closed(m));
        Assert.Equal(6, m.AliveFaceCount);
        Assert.Equal(8, m.AliveVertexCount);
        Assert.Equal(2, Euler(m));
    }

    [Fact]
    public void Region_ClosedVolume_DuplicatesShell()
    {
        var m = MeshBuilder.Cube();
        var all = Enumerable.Range(0, m.FaceCount).Where(f => m.Faces[f].Alive).ToList();
        var caps = MeshOps.Extrude(m, all, new ExtrudeOptions { Offset = 0f });
        Assert.Equal(6, caps.Count);
        Assert.Equal(12, m.AliveFaceCount);
        Assert.Equal(16, m.AliveVertexCount);
        Assert.Empty(MeshValidator.Check(m));
        Assert.Equal(2, MeshOps.ConnectedComponents(m).Count);
    }

    [Fact]
    public void Individual_TwoAdjacentFaces_EachAlongOwnNormal()
    {
        var m = MeshBuilder.Cube();
        var top = TopFace(m);
        int side = Enumerable.Range(0, m.FaceCount).First(f => m.Faces[f].Alive && MeshNormals.FaceNormalUnnormalized(m, f).X > 0.5f);
        var caps = MeshOps.Extrude(m, new[] { top, side }, new ExtrudeOptions { Type = ExtrudeType.IndividualFaces, Offset = 0.25f });
        Assert.Equal(2, caps.Count);
        Assert.Empty(MeshValidator.Check(m));
        Assert.Equal(2, Euler(m));
        Assert.True(Closed(m));
        // 6 + 옆면 4 + 4 (공유 엣지에도 각자 옆면이 생긴다)
        Assert.Equal(14, m.AliveFaceCount);
        var ids = new List<int>();
        foreach (int f in caps)
        {
            var n = Vector3.Normalize(MeshNormals.FaceNormalUnnormalized(m, f));
            m.GetFaceVertices(f, ids);
            foreach (int v in ids) Assert.Equal(0.75f, Vector3.Dot(m.Verts[v].Position, n), 4);
        }
    }

    [Fact]
    public void Region_TwoAdjacentFaces_FaceNormalsKeepsShape()
    {
        var m = MeshBuilder.Cube();
        var top = TopFace(m);
        int side = Enumerable.Range(0, m.FaceCount).First(f => m.Faces[f].Alive && MeshNormals.FaceNormalUnnormalized(m, f).X > 0.5f);
        var caps = MeshOps.Extrude(m, new[] { top, side }, new ExtrudeOptions { Offset = 0.25f, Direction = ExtrudeDirection.FaceNormals });
        Assert.Equal(2, caps.Count);
        Assert.Empty(MeshValidator.Check(m));
        Assert.Equal(2, Euler(m));
        var ids = new List<int>();
        foreach (int f in caps)
        {
            var n = Vector3.Normalize(MeshNormals.FaceNormalUnnormalized(m, f));
            m.GetFaceVertices(f, ids);
            foreach (int v in ids) Assert.Equal(0.75f, Vector3.Dot(m.Verts[v].Position, n), 3);
        }
    }

    [Theory]
    [InlineData(ExtrudeDirection.X, 0)]
    [InlineData(ExtrudeDirection.Z, 2)]
    public void Region_AxisDirection(ExtrudeDirection dir, int axis)
    {
        var m = MeshBuilder.Cube();
        var caps = MeshOps.Extrude(m, new[] { TopFace(m) }, new ExtrudeOptions { Offset = 0.3f, Direction = dir });
        Assert.Empty(MeshValidator.Check(m));
        var ids = new List<int>(); m.GetFaceVertices(caps[0], ids);
        foreach (int v in ids)
        {
            var p = m.Verts[v].Position;
            Assert.Equal(0.5f, p.Y, 4);
            float c = axis == 0 ? p.X : p.Z;
            Assert.True(MathF.Abs(MathF.Abs(c - 0.3f) - 0.5f) < 1e-4f);
        }
    }

    [Fact]
    public void Repeat_StepsStackSegments()
    {
        var m = MeshBuilder.Cube();
        var caps = MeshOps.Extrude(m, new[] { TopFace(m) }, new ExtrudeOptions { Offset = 0.2f, Steps = 4 });
        Assert.Single(caps);
        Assert.Empty(MeshValidator.Check(m));
        Assert.Equal(2, Euler(m));
        Assert.Equal(6 + 4 * 4, m.AliveFaceCount);
        var ids = new List<int>(); m.GetFaceVertices(caps[0], ids);
        Assert.All(ids, v => Assert.Equal(1.3f, m.Verts[v].Position.Y, 4));
    }

    [Fact]
    public void FlipNormals_InvertsResult()
    {
        var m = MeshBuilder.Cube();
        var caps = MeshOps.Extrude(m, new[] { TopFace(m) }, new ExtrudeOptions { Offset = 0.5f, FlipNormals = true });
        Assert.Empty(MeshValidator.Check(m));
        Assert.True(MeshNormals.FaceNormalUnnormalized(m, caps[0]).Y < -0.5f);
    }

    [Fact]
    public void Edges_BorderOfPlane_OutwardAndRepeat()
    {
        var m = MeshBuilder.Plane(1, 1, 1, 1);
        int e = Enumerable.Range(0, m.EdgeCount).First(i => m.Edges[i].Alive && m.IsBoundaryEdge(i));
        var (a, b) = m.EdgeVertices(e);
        var mid = (m.Verts[a].Position + m.Verts[b].Position) * 0.5f;
        var faces = MeshOps.ExtrudeEdges(m, new[] { e }, new ExtrudeOptions { Offset = 0.5f, Steps = 2 }, out var newEdges);
        Assert.Equal(2, faces.Count);
        Assert.Single(newEdges);
        Assert.Empty(MeshValidator.Check(m));
        var (x, y) = m.EdgeVertices(newEdges[0]);
        var nmid = (m.Verts[x].Position + m.Verts[y].Position) * 0.5f;
        // 면 평면(y=0) 안에서 바깥쪽으로 1만큼
        Assert.Equal(0f, nmid.Y, 4);
        Assert.Equal(1f, Vector3.Distance(mid, nmid), 4);
        Assert.True(nmid.Length() > mid.Length());
    }
}
