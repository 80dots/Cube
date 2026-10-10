using System.Numerics;
using Cube.Core.Mesh;
using Cube.Core.Selection;

namespace Cube.Core.Tests.Selection;

/// <summary>Symmetry(v0.0.64): 거울 짝 테이블, 컴포넌트 확장, 대칭 변형 규칙, 월드 평면 변환, 엣지 비율 거울.</summary>
public class SymmetryTests
{
    [Fact]
    public void Cube_EveryVertexHasMirror_AndEdgesFaces()
    {
        var m = MeshBuilder.Cube();
        var map = SymmetryMap.Get(m, SymmetryPlane.Object(0));
        for (int v = 0; v < m.VertexCount; v++)
        {
            int mv = map.MirrorVertex(v);
            Assert.True(mv >= 0 && mv != v);
            var p = m.Verts[v].Position; var q = m.Verts[mv].Position;
            Assert.Equal(-p.X, q.X, 5); Assert.Equal(p.Y, q.Y, 5); Assert.Equal(p.Z, q.Z, 5);
        }
        for (int e = 0; e < m.EdgeCount; e++) Assert.True(map.MirrorEdge(m, e) >= 0);
        for (int f = 0; f < m.FaceCount; f++) Assert.True(map.MirrorFace(m, f) >= 0);
        // +X 면의 짝은 −X 면
        int px = Enumerable.Range(0, 6).First(f => MeshNormals.FaceNormalUnnormalized(m, f).X > 0.5f);
        Assert.True(MeshNormals.FaceNormalUnnormalized(m, map.MirrorFace(m, px)).X < -0.5f);
    }

    [Fact]
    public void OnPlaneVertices_MapToThemselves_AsymmetricHaveNone()
    {
        var m = MeshBuilder.Plane(1, 1, 2, 2); // 가운데 열 정점 x = 0
        var map = SymmetryMap.Get(m, SymmetryPlane.Object(0));
        int onPlane = 0, paired = 0;
        for (int v = 0; v < m.VertexCount; v++) { if (map.MirrorVertex(v) == v) onPlane++; else if (map.MirrorVertex(v) >= 0) paired++; }
        Assert.Equal(3, onPlane); Assert.Equal(6, paired);
        // 한 정점을 비대칭으로 옮기면 그 짝이 사라진다
        var vv = m.Verts[0]; vv.Position += new Vector3(0, 0.3f, 0); m.Verts[0] = vv; m.BumpGeometry();
        var map2 = SymmetryMap.Get(m, SymmetryPlane.Object(0));
        Assert.NotSame(map, map2);
        Assert.Equal(-1, map2.MirrorVertex(0));
    }

    [Fact]
    public void Expand_AddsMirrorsPerMode()
    {
        var m = MeshBuilder.Cube();
        var map = SymmetryMap.Get(m, SymmetryPlane.Object(0));
        int v = Enumerable.Range(0, 8).First(i => m.Verts[i].Position.X > 0);
        var set = new HashSet<int> { v };
        Assert.Equal(0, map.Expand(m, SelectMode.Vertex, set));
        Assert.Equal(2, set.Count);
        int px = Enumerable.Range(0, 6).First(f => MeshNormals.FaceNormalUnnormalized(m, f).X > 0.5f);
        var fs = new HashSet<int> { px }; map.Expand(m, SelectMode.Face, fs); Assert.Equal(2, fs.Count);
        var es = new HashSet<int> { 0 }; map.Expand(m, SelectMode.Edge, es); Assert.True(es.Count is 1 or 2); // 평면을 가로지르는 엣지는 자기 자신이 짝
    }

    [Fact]
    public void Transform_MirrorsNegativeSide_KeepsPlaneVertices()
    {
        var plane = SymmetryPlane.Object(0);
        var move = Matrix4x4.CreateTranslation(0.2f, 0.1f, 0f);
        var pos = SymmetryOps.Transform(new Vector3(0.5f, 0, 0), move, plane);
        var neg = SymmetryOps.Transform(new Vector3(-0.5f, 0, 0), move, plane);
        var on = SymmetryOps.Transform(new Vector3(0, 0, 0), move, plane);
        Assert.Equal(new Vector3(0.7f, 0.1f, 0), pos);
        Assert.Equal(new Vector3(-0.7f, 0.1f, 0), neg);
        Assert.Equal(new Vector3(0, 0.1f, 0), on); // X 성분은 잘림
        // 회전: +쪽을 Y축 +30° 돌리면 −쪽은 −30°(거울 회전)
        var rot = Matrix4x4.CreateRotationY(30f * MathF.PI / 180f);
        var a = SymmetryOps.Transform(new Vector3(1, 0, 0.5f), rot, plane);
        var b = SymmetryOps.Transform(new Vector3(-1, 0, 0.5f), rot, plane);
        Assert.Equal(a.X, -b.X, 4); Assert.Equal(a.Z, b.Z, 4);
    }

    [Fact]
    public void WorldPlane_TransformsIntoObjectSpace()
    {
        // 노드가 X로 1 이동·Y 90° 회전: 월드 X 평면(x_w = 0)은 오브젝트 공간에서 x_w = z_o·(…)… 정점으로 검증
        var world = Matrix4x4.CreateRotationY(MathF.PI / 2) * Matrix4x4.CreateTranslation(1, 0, 0);
        var plane = SymmetryPlane.World(0, world);
        // 오브젝트 점 p의 월드 x = 0이면 평면 위여야 한다
        var p = new Vector3(0, 0.3f, 1f); // 회전 후 (1,0,0)·… 계산 대신 월드로 보내 검사
        var pw = Vector3.Transform(p, world);
        Assert.Equal(MathF.Abs(pw.X), MathF.Abs(plane.Signed(p)), 4);
        var pw0 = Vector3.Transform(new Vector3(0, 0, 0), world); // 월드 x = 1
        Assert.Equal(1f, MathF.Abs(plane.Signed(Vector3.Zero)), 4);
        _ = pw0;
    }

    [Fact]
    public void MirrorParam_ProjectsOntoMirrorEdge()
    {
        var m = MeshBuilder.Cube();
        var map = SymmetryMap.Get(m, SymmetryPlane.Object(0));
        // 평면을 가로지르지 않는 엣지(양끝 x 같은 부호) 하나
        int e = Enumerable.Range(0, m.EdgeCount).First(i => { var (a, b) = m.EdgeVertices(i); return m.Verts[a].Position.X > 0 && m.Verts[b].Position.X > 0; });
        int me = map.MirrorEdge(m, e);
        float t2 = SymmetryOps.MirrorParam(m, map.Plane, e, 0.3f, me);
        var (a1, b1) = m.EdgeVertices(e); var (a2, b2) = m.EdgeVertices(me);
        var p = Vector3.Lerp(m.Verts[a1].Position, m.Verts[b1].Position, 0.3f);
        var q = Vector3.Lerp(m.Verts[a2].Position, m.Verts[b2].Position, t2);
        Assert.True(Vector3.Distance(map.Plane.Reflect(p), q) < 1e-5f);
    }
}
