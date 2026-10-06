using System.Numerics;
using Cube.Core.Mesh;

namespace Cube.Core.Tests.Mesh;

public class PolyMeshTests
{
    [Fact]
    public void AddFace_RejectsNonManifoldThirdFace()
    {
        var m = new PolyMesh();
        int a = m.AddVertex(Vector3.Zero), b = m.AddVertex(Vector3.UnitX), c = m.AddVertex(Vector3.UnitY), d = m.AddVertex(Vector3.UnitZ), e = m.AddVertex(-Vector3.UnitZ);
        Assert.True(m.AddFace(new[] { a, b, c }) >= 0);
        Assert.True(m.AddFace(new[] { b, a, d }) >= 0);   // a-b 엣지 공유(반대 방향)
        Assert.Equal(-1, m.AddFace(new[] { a, b, e }));   // 세 번째 면 → 거부
        Assert.Equal(-1, m.AddFace(new[] { a, b, c }));   // 같은 방향 중복 → 거부
        Assert.Empty(MeshValidator.Check(m));
        Assert.Equal(5, m.AliveEdgeCount);
    }

    [Fact]
    public void RemoveFace_RemovesIsolatedEdgesAndVertices()
    {
        var m = MeshBuilder.Cube();
        m.RemoveFace(0);
        Assert.Empty(MeshValidator.Check(m));
        Assert.Equal(5, m.AliveFaceCount);
        Assert.Equal(12, m.AliveEdgeCount);  // 큐브는 면 하나 지워도 엣지는 모두 남음
        Assert.Equal(8, m.AliveVertexCount);
        int boundary = 0;
        for (int e = 0; e < m.EdgeCount; e++) if (m.Edges[e].Alive && m.IsBoundaryEdge(e)) boundary++;
        Assert.Equal(4, boundary);

        var p = MeshBuilder.Plane();
        p.RemoveFace(0);
        Assert.Equal(0, p.AliveFaceCount);
        Assert.Equal(0, p.AliveEdgeCount);
        Assert.Equal(0, p.AliveVertexCount);
    }

    [Fact]
    public void Ids_AreStableAcrossRemoval_AndCompactRemaps()
    {
        var m = MeshBuilder.Cube();
        var v7 = m.Verts[7].Position;
        m.RemoveFace(2);
        Assert.Equal(v7, m.Verts[7].Position);
        Assert.False(m.Faces[2].Alive);
        Assert.True(m.Faces[3].Alive);
        var remap = m.Compact();
        Assert.Equal(-1, remap.Faces[2]);
        Assert.Equal(2, remap.Faces[3]);
        Assert.Equal(5, m.FaceCount);
        Assert.Empty(MeshValidator.Check(m));
    }

    [Fact]
    public void Clone_IsIndependent()
    {
        var m = MeshBuilder.Cube();
        var c = m.Clone();
        var v = c.Verts[0]; v.Position += Vector3.UnitX; c.Verts[0] = v;
        Assert.NotEqual(m.Verts[0].Position, c.Verts[0].Position);
        c.RemoveFace(0);
        Assert.Equal(6, m.AliveFaceCount);
    }

    [Fact]
    public void VertexAdjacency_OnCubeCorner()
    {
        var m = MeshBuilder.Cube();
        var faces = new List<int>(); var edges = new List<int>();
        m.GetVertexFaces(0, faces); m.GetVertexEdges(0, edges);
        Assert.Equal(3, faces.Count);
        Assert.Equal(3, edges.Count);
        Assert.Equal(3, m.VertexOutgoing(0).Length);
    }
}
