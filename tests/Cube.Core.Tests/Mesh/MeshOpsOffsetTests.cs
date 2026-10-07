using System.Numerics;
using Cube.Core.Mesh;

namespace Cube.Core.Tests.Mesh;

public class MeshOpsOffsetTests
{
    [Fact]
    public void RegionOffset_TwoAdjacentFaces_MovesEachFacePlaneByDistance()
    {
        var m = MeshBuilder.Cube();
        // +X, +Z 면 찾기
        int fx = -1, fz = -1;
        for (int f = 0; f < m.FaceCount; f++)
        {
            var n = Vector3.Normalize(MeshNormals.FaceNormalUnnormalized(m, f));
            if (n.X > 0.9f) fx = f; if (n.Z > 0.9f) fz = f;
        }
        var faces = MeshOps.ExtrudeFaces(m, new[] { fx, fz });
        Assert.NotEmpty(faces);
        // 돌출된 캡(반환된 면)을 법선으로 다시 찾는다
        foreach (int f in faces)
        {
            var n = Vector3.Normalize(MeshNormals.FaceNormalUnnormalized(m, f));
            if (n.X > 0.9f) fx = f; if (n.Z > 0.9f) fz = f;
        }
        var dirs = MeshOps.RegionOffsetDirections(m, faces);
        foreach (var (v, d) in dirs) { var vert = m.Verts[v]; vert.Position += d * 0.25f; m.Verts[v] = vert; }
        Assert.Empty(MeshValidator.Check(m));
        var tmp = new List<int>();
        m.GetFaceVertices(fx, tmp); foreach (int v in tmp) Assert.Equal(0.75f, m.Verts[v].Position.X, 4);
        m.GetFaceVertices(fz, tmp); foreach (int v in tmp) Assert.Equal(0.75f, m.Verts[v].Position.Z, 4);
    }

    [Fact]
    public void RegionOffset_SingleFace_IsFaceNormal()
    {
        var m = MeshBuilder.Cube();
        var dirs = MeshOps.RegionOffsetDirections(m, new[] { 0 });
        var n = Vector3.Normalize(MeshNormals.FaceNormalUnnormalized(m, 0));
        Assert.Equal(4, dirs.Count);
        foreach (var d in dirs.Values) Assert.True((d - n).Length() < 1e-5f);
    }
}
