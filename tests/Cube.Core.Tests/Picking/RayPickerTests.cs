using System.Numerics;
using Cube.Core.Mesh;
using Cube.Core.Picking;
using Cube.Core.Scene;
using Cube.Core.Selection;

namespace Cube.Core.Tests.Picking;

public class RayPickerTests
{
    private static readonly Vector2 Vp = new(1000, 800);

    private static (List<PickTarget> targets, PolyMesh mesh, CameraProjection cam) CubeScene(Vector3? eye = null)
    {
        var mesh = MeshBuilder.Cube();
        var render = MeshTessellator.Build(mesh);
        var tg = new PickTarget { Id = new NodeId(1), Mesh = mesh, Render = render, World = Matrix4x4.Identity };
        var cam = CameraProjection.Perspective(eye ?? new Vector3(0, 0, 5), Vector3.Zero, Vector3.UnitY, 45, Vp);
        return (new List<PickTarget> { tg }, mesh, cam);
    }

    [Fact]
    public void Project_Unproject_RoundTrip()
    {
        var (_, _, cam) = CubeScene();
        var world = new Vector3(0.3f, -0.2f, 0.5f);
        var px = cam.Project(world, out float depth)!.Value;
        Assert.True(depth > 0);
        var ray = cam.Unproject(px);
        var closest = ray.At(Vector3.Dot(world - ray.Origin, ray.Direction));
        Assert.True(Vector3.Distance(closest, world) < 1e-3f);
        // 화면 중앙은 원점
        var center = cam.Project(Vector3.Zero, out _)!.Value;
        Assert.True(Vector2.Distance(center, Vp / 2) < 1e-2f);
        // 뒤쪽 점은 null
        Assert.Null(cam.Project(new Vector3(0, 0, 10), out _));
    }

    [Fact]
    public void PickFace_HitsFrontFace()
    {
        var (targets, mesh, cam) = CubeScene();
        var hit = RayPicker.PickFace(targets, cam, Vp / 2);
        Assert.NotNull(hit);
        Assert.True(Vector3.Dot(mesh.Faces[hit.Value.Component].Normal, Vector3.UnitZ) > 0.99f); // +Z 면
        Assert.True(MathF.Abs(hit.Value.Depth - 4.5f) < 1e-3f);
        Assert.Null(RayPicker.PickFace(targets, cam, new Vector2(10, 10)));
    }

    [Fact]
    public void PickVertex_NearestOnScreen_AndCameraBasedOcclusion()
    {
        var (targets, mesh, cam) = CubeScene();
        // 앞면 우상단 정점 (0.5, 0.5, 0.5)
        int v = -1;
        for (int i = 0; i < mesh.VertexCount; i++) if (mesh.Verts[i].Position == new Vector3(0.5f, 0.5f, 0.5f)) v = i;
        var px = cam.Project(mesh.Verts[v].Position, out _)!.Value;
        var hit = RayPicker.PickVertex(targets, cam, px + new Vector2(3, -2), cameraBased: true);
        Assert.NotNull(hit);
        Assert.Equal(v, hit.Value.Component);

        // 뒤쪽 정점 (0.5, 0.5, -0.5)은 앞면에 가려져 camera-based에서는 선택되지 않음
        int back = -1;
        for (int i = 0; i < mesh.VertexCount; i++) if (mesh.Verts[i].Position == new Vector3(0.5f, 0.5f, -0.5f)) back = i;
        var bpx = cam.Project(mesh.Verts[back].Position, out _)!.Value;
        var bhit = RayPicker.PickVertex(targets, cam, bpx, cameraBased: true, thresholdPx: 2f);
        Assert.True(bhit == null || bhit.Value.Component != back);
        var bhit2 = RayPicker.PickVertex(targets, cam, bpx, cameraBased: false, thresholdPx: 2f);
        Assert.NotNull(bhit2);
    }

    [Fact]
    public void PickEdge_FindsTopFrontEdge()
    {
        var (targets, mesh, cam) = CubeScene();
        var a = new Vector3(-0.5f, 0.5f, 0.5f); var b = new Vector3(0.5f, 0.5f, 0.5f);
        var mid = cam.Project((a + b) / 2, out _)!.Value;
        var hit = RayPicker.PickEdge(targets, cam, mid + new Vector2(0, 2), cameraBased: true);
        Assert.NotNull(hit);
        var (va, vb) = mesh.EdgeVertices(hit.Value.Component);
        var pa = mesh.Verts[va].Position; var pb = mesh.Verts[vb].Position;
        Assert.True((pa == a && pb == b) || (pa == b && pb == a));
    }

    [Fact]
    public void Marquee_SelectsFrontVerticesOnly_WhenCameraBased()
    {
        var (targets, _, cam) = CubeScene();
        var items = RayPicker.Marquee(targets, cam, Vector2.Zero, Vp, SelectMode.Vertex, cameraBased: true);
        Assert.Equal(4, items.Count);
        var all = RayPicker.Marquee(targets, cam, Vector2.Zero, Vp, SelectMode.Vertex, cameraBased: false);
        Assert.Equal(8, all.Count);
        var faces = RayPicker.Marquee(targets, cam, Vector2.Zero, Vp, SelectMode.Face, cameraBased: true);
        Assert.Single(faces);
        var objs = RayPicker.Marquee(targets, cam, Vector2.Zero, Vp, SelectMode.Object, cameraBased: false);
        Assert.Single(objs);
        var none = RayPicker.Marquee(targets, cam, Vector2.Zero, new Vector2(50, 50), SelectMode.Object, cameraBased: false);
        Assert.Empty(none);
    }

    [Fact]
    public void Ortho_ProjectAndPick()
    {
        var mesh = MeshBuilder.Cube();
        var render = MeshTessellator.Build(mesh);
        var tg = new PickTarget { Id = new NodeId(1), Mesh = mesh, Render = render, World = Matrix4x4.CreateTranslation(2, 0, 0) };
        var camToWorld = Matrix4x4.CreateTranslation(0, 0, 10); // 원점 앞에서 -Z를 봄
        var cam = new CameraProjection(camToWorld, true, 0, 10f, 0.05f, 100f, Vp);
        var px = cam.Project(new Vector3(2, 0, 0.5f), out float depth)!.Value;
        Assert.True(MathF.Abs(depth - 9.5f) < 1e-4f);
        var hit = RayPicker.PickFace(new[] { tg }, cam, px);
        Assert.NotNull(hit);
        Assert.True(Vector3.Dot(mesh.Faces[hit.Value.Component].Normal, Vector3.UnitZ) > 0.99f);
    }
}
