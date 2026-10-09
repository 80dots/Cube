using System.Numerics;
using Cube.Core.Mesh;
using Cube.Core.Picking;
using Cube.Core.Scene;
using Cube.Core.Selection;

namespace Cube.Core.Tests.Picking;

/// <summary>
/// 뷰포트 피킹(<c>CameraProjection</c> 투영/역투영, <c>RayPicker</c>)을 검증한다: 면 = 레이 교차, 정점/엣지 = 화면 거리 임계,
/// camera-based 가림 판정, 바운드 기반 조기 거절, 마키 선택, 직교 카메라.
/// </summary>
public class RayPickerTests
{
    /// <summary>테스트 공용 뷰포트 크기(1000x800 픽셀).</summary>
    private static readonly Vector2 Vp = new(1000, 800);

    /// <summary>
    /// 원점의 단위 큐브 하나를 피킹 대상으로 만들고, 기본적으로 (0,0,5)에서 원점을 보는 45° 원근 카메라를 함께 돌려준다.
    /// </summary>
    /// <param name="eye">카메라 위치(생략 시 (0,0,5)).</param>
    private static (List<PickTarget> targets, PolyMesh mesh, CameraProjection cam) CubeScene(Vector3? eye = null)
    {
        var mesh = MeshBuilder.Cube();
        var render = MeshTessellator.Build(mesh);
        var tg = new PickTarget { Id = new NodeId(1), Mesh = mesh, Render = render, World = Matrix4x4.Identity };
        var cam = CameraProjection.Perspective(eye ?? new Vector3(0, 0, 5), Vector3.Zero, Vector3.UnitY, 45, Vp);
        return (new List<PickTarget> { tg }, mesh, cam);
    }

    /// <summary>
    /// 월드 점을 화면에 투영한 픽셀로 레이를 역투영하면 그 레이가 원래 점을 지나야 한다.
    /// 원점은 화면 중앙에, 카메라 뒤쪽 점은 투영 결과가 null이어야 한다.
    /// </summary>
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

    /// <summary>
    /// 레이-AABB 교차(피킹 전 조기 거절용): 명중/빗나감, 박스 뒤쪽 방향, 박스 안에서 출발, 축 평행 레이(성분 0),
    /// 두께 0인 평평한 박스(패딩으로 통과)를 모두 올바르게 처리하는지 확인한다.
    /// </summary>
    [Fact]
    public void RayBox_RejectsMissAndAcceptsHit()
    {
        var min = new Vector3(-0.5f); var max = new Vector3(0.5f);
        Assert.True(RayPicker.RayIntersectsBox(new Ray(new Vector3(0, 0, 5), new Vector3(0, 0, -1)), min, max));
        Assert.False(RayPicker.RayIntersectsBox(new Ray(new Vector3(3, 0, 5), new Vector3(0, 0, -1)), min, max));
        Assert.False(RayPicker.RayIntersectsBox(new Ray(new Vector3(0, 0, 5), new Vector3(0, 0, 1)), min, max)); // 뒤쪽
        Assert.True(RayPicker.RayIntersectsBox(new Ray(new Vector3(0, 0, 0), new Vector3(1, 1, 1)), min, max)); // 안에서 출발
        // 축에 평행한 방향(성분 0)도 처리
        Assert.True(RayPicker.RayIntersectsBox(new Ray(new Vector3(-3, 0.2f, 0.1f), new Vector3(1, 0, 0)), min, max));
        Assert.False(RayPicker.RayIntersectsBox(new Ray(new Vector3(-3, 0.8f, 0.1f), new Vector3(1, 0, 0)), min, max));
        // 평평한 박스(두께 0)도 패딩으로 통과
        Assert.True(RayPicker.RayIntersectsBox(new Ray(new Vector3(0, 2, 0), new Vector3(0, -1, 0)), new Vector3(-1, 0, -1), new Vector3(1, 0, 1)));
    }

    /// <summary>
    /// 화면 바운드 필터: 큐브가 보이는 화면 중앙은 포함 가능, 먼 구석은 제외되어야 한다.
    /// 카메라가 메시 안/가까이에 있어 바운드 코너가 카메라 뒤로 넘어가면 투영이 불가능하므로 보수적으로 true여야 한다.
    /// </summary>
    [Fact]
    public void ScreenBounds_FilterTargets()
    {
        var (targets, _, cam) = CubeScene();
        Assert.True(RayPicker.ScreenBoundsMayContain(targets[0], cam, Vp / 2, 6f));
        Assert.False(RayPicker.ScreenBoundsMayContain(targets[0], cam, new Vector2(10, 10), 6f));
        // 카메라 뒤에 코너가 있으면 보수적으로 true
        var near = CameraProjection.Perspective(new Vector3(0, 0, 0.2f), new Vector3(0, 0, -1), Vector3.UnitY, 45, Vp);
        Assert.True(RayPicker.ScreenBoundsMayContain(targets[0], near, new Vector2(10, 10), 6f));
    }

    /// <summary>
    /// 스킨 변형 등으로 <c>UpdatePositions</c>가 위치를 바꾸면 피킹 바운드도 따라가야 한다.
    /// X로 1.5 옮긴 뒤에는 원래 자리(화면 중앙)에서는 안 집히고 새 위치에서는 집혀야 한다.
    /// </summary>
    [Fact]
    public void PickFace_UsesBounds_AfterDeformUpdate()
    {
        // 변형으로 메시가 옮겨 가면(UpdatePositions) 바운드도 따라가 새 위치에서 집힌다
        var (targets, mesh, cam) = CubeScene();
        var moved = new Vector3[mesh.VertexCount];
        for (int v = 0; v < mesh.VertexCount; v++) moved[v] = mesh.Verts[v].Position + new Vector3(1.5f, 0, 0);
        MeshTessellator.UpdatePositions(mesh, targets[0].Render, moved);
        Assert.Null(RayPicker.PickFace(targets, cam, Vp / 2));
        var px = cam.Project(new Vector3(1.5f, 0, 0.5f), out _)!.Value;
        Assert.NotNull(RayPicker.PickFace(targets, cam, px));
    }

    /// <summary>화면 중앙을 클릭하면 카메라를 향한 +Z 면이 깊이 4.5(=5-0.5)로 집히고, 빈 곳은 null이어야 한다.</summary>
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

    /// <summary>
    /// 정점 피킹은 커서에서 몇 픽셀 떨어져도 화면상 가장 가까운 정점을 골라야 한다.
    /// 앞면에 가려진 뒤쪽 정점은 camera-based 모드에서는 선택되지 않고, camera-based를 끄면 선택되어야 한다.
    /// </summary>
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
        // 뒤쪽 정점: 앞면 뒤에 있어 camera-based에서는 가려진다. 임계를 2px로 좁혀 다른 정점이 대신 집히지 않게 한다.
        int back = -1;
        for (int i = 0; i < mesh.VertexCount; i++) if (mesh.Verts[i].Position == new Vector3(0.5f, 0.5f, -0.5f)) back = i;
        var bpx = cam.Project(mesh.Verts[back].Position, out _)!.Value;
        var bhit = RayPicker.PickVertex(targets, cam, bpx, cameraBased: true, thresholdPx: 2f);
        Assert.True(bhit == null || bhit.Value.Component != back);
        var bhit2 = RayPicker.PickVertex(targets, cam, bpx, cameraBased: false, thresholdPx: 2f);
        Assert.NotNull(bhit2);
    }

    /// <summary>앞면 위쪽 엣지 중점 근처(2px 아래)를 클릭하면 바로 그 엣지(양 끝 (-0.5,0.5,0.5)~(0.5,0.5,0.5))가 집혀야 한다.</summary>
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

    /// <summary>
    /// 화면 전체 마키: camera-based면 보이는 앞면 정점 4개만, 아니면 8개 모두가 선택되어야 한다.
    /// 면 모드는 보이는 면 1개, 오브젝트 모드는 오브젝트 1개, 아무것도 없는 작은 영역은 빈 결과여야 한다.
    /// </summary>
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

    /// <summary>
    /// 직교 카메라(Z=10에서 -Z를 봄)로 X=2에 놓인 큐브의 앞면 점을 투영하면 깊이가 9.5이고,
    /// 그 픽셀에서 면 피킹이 오브젝트 월드 행렬을 반영해 +Z 면을 집어야 한다.
    /// </summary>
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

    /// <summary>
    /// Lasso 다각형 판정: 오목한 L자 다각형의 안/밖(움푹 들어간 부분은 밖), 자기 교차(8자) 곡선은 짝홀 규칙으로 처리되는지,
    /// 선분이 다각형을 가로지르기만 해도(끝점이 모두 밖이어도) 걸리는지 확인한다.
    /// </summary>
    [Fact]
    public void LassoPolygon_InsideAndSegmentTests()
    {
        var l = new List<Vector2> { new(0, 0), new(10, 0), new(10, 4), new(4, 4), new(4, 10), new(0, 10) };
        Assert.True(RayPicker.InsidePolygon(new Vector2(2, 8), l));
        Assert.True(RayPicker.InsidePolygon(new Vector2(8, 2), l));
        Assert.False(RayPicker.InsidePolygon(new Vector2(8, 8), l)); // L자의 빈 모서리
        Assert.False(RayPicker.InsidePolygon(new Vector2(-1, 5), l));
        // 끝점이 둘 다 밖이지만 L자를 가로지르는 선분
        Assert.True(RayPicker.SegmentIntersectsPolygon(new Vector2(-5, 2), new Vector2(15, 2), l));
        Assert.False(RayPicker.SegmentIntersectsPolygon(new Vector2(6, 6), new Vector2(9, 9), l));
        // 8자(꼬인) 곡선: 두 고리 안은 안, 교차점 바깥은 밖
        var eight = new List<Vector2> { new(0, 0), new(10, 10), new(10, 0), new(0, 10) };
        Assert.True(RayPicker.InsidePolygon(new Vector2(1, 5), eight));
        Assert.True(RayPicker.InsidePolygon(new Vector2(9, 5), eight));
        Assert.False(RayPicker.InsidePolygon(new Vector2(5, 1), eight));
    }

    /// <summary>
    /// Lasso 선택: 뷰포트 전체를 감싸는 사각형 곡선은 마키와 같은 결과(앞면 정점 4개 / 전체 8개 / 오브젝트 1개)이고,
    /// 화면 왼쪽 절반만 감싸면 투영 X가 중앙보다 왼쪽인 정점만 고른다. 꼭짓점이 3개 미만이면 빈 결과.
    /// </summary>
    [Fact]
    public void Lasso_MatchesMarqueeAndSelectsInsideOnly()
    {
        var (targets, _, cam) = CubeScene();
        var full = new List<Vector2> { Vector2.Zero, new(Vp.X, 0), Vp, new(0, Vp.Y) };
        Assert.Equal(4, RayPicker.Lasso(targets, cam, full, SelectMode.Vertex, cameraBased: true).Count);
        Assert.Equal(8, RayPicker.Lasso(targets, cam, full, SelectMode.Vertex, cameraBased: false).Count);
        Assert.Single(RayPicker.Lasso(targets, cam, full, SelectMode.Object, cameraBased: false));
        var left = new List<Vector2> { Vector2.Zero, new(Vp.X / 2, 0), new(Vp.X / 2, Vp.Y), new(0, Vp.Y) };
        var items = RayPicker.Lasso(targets, cam, left, SelectMode.Vertex, cameraBased: false);
        Assert.NotEmpty(items);
        Assert.True(items.Count < 8);
        foreach (var it in items)
        {
            var sp = cam.Project(Vector3.Transform(targets[0].Mesh.Verts[it.Component].Position, targets[0].World), out _);
            Assert.True(sp!.Value.X <= Vp.X / 2);
        }
        Assert.Empty(RayPicker.Lasso(targets, cam, new List<Vector2> { Vector2.Zero, Vp }, SelectMode.Vertex, cameraBased: false));
    }
}
