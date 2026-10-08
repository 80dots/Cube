using System.Numerics;
using Cube.Core.Mesh;

namespace Cube.Core.Tests.Mesh;

/// <summary>
/// <c>MeshOps.RegionOffsetDirections</c>(면 Extrude 두께 조작기에서 쓰는 정점별 오프셋 방향)를 검증한다.
/// 정점마다 선택 면 법선 평균에 마이터 보정(1/(dir·n))을 곱해, 각 면이 자기 법선 방향으로 정확히 같은 거리만큼 평행 이동해야 한다.
/// </summary>
public class MeshOpsOffsetTests
{
    /// <summary>
    /// 큐브의 인접한 +X, +Z 면을 함께 Extrude한 뒤 오프셋 방향 × 0.25만큼 정점을 옮긴다.
    /// 두 면이 공유하는 모서리 정점도 마이터 보정 덕분에 +X 면은 X=0.75, +Z 면은 Z=0.75 평면에 정확히 놓여야 하며,
    /// 메시 위상은 유효해야 한다. 여러 방향 면을 한 번에 두껍게 해도 형태가 찌그러지지 않는다는 보장이다.
    /// </summary>
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
        // 실행: 정점별 방향을 구해 0.25만큼 직접 이동(조작기 드래그와 같은 효과).
        var dirs = MeshOps.RegionOffsetDirections(m, faces);
        foreach (var (v, d) in dirs) { var vert = m.Verts[v]; vert.Position += d * 0.25f; m.Verts[v] = vert; }
        Assert.Empty(MeshValidator.Check(m));
        // 검증: 원래 면 평면은 ±0.5이므로 0.25 이동 후 0.75가 되어야 한다.
        var tmp = new List<int>();
        m.GetFaceVertices(fx, tmp); foreach (int v in tmp) Assert.Equal(0.75f, m.Verts[v].Position.X, 4);
        m.GetFaceVertices(fz, tmp); foreach (int v in tmp) Assert.Equal(0.75f, m.Verts[v].Position.Z, 4);
    }

    /// <summary>
    /// 면 하나만 선택하면 네 정점의 오프셋 방향이 모두 그 면의 단위 법선과 같아야 한다(마이터 보정이 1이 되는 기본 경우).
    /// </summary>
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
