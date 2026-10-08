using System.Numerics;
using Cube.Core.Mesh;
using Cube.Core.Picking;
using Cube.Core.Scene;
using Cube.Core.Selection;

namespace Cube.Core.Tests.Picking;

/// <summary>
/// 마키(박스) 선택의 엣지 모드 동작을 검증한다. 특히 camera-based(가림 판정) 마키에서
/// 보이는 엣지는 모두 선택되고 가려진 엣지는 빠지는지, 즉 <c>RayPicker.Marquee</c>와 <c>RayPicker.IsOccluded</c>가 일관되는지를 본다.
/// </summary>
public class MarqueeEdgeTests
{
    /// <summary>
    /// Maya 기본 카메라 방향((28,21,28) 정규화, 거리 2.3)에서 뷰포트 전체를 덮는 마키로 큐브 엣지를 선택한다.
    /// 각 엣지 중점의 가림 여부와 선택 여부가 정확히 반대인지(보이면 선택, 가려지면 비선택),
    /// 앞을 향하는 면에 붙은 엣지가 가려졌다고 잘못 판정되지 않는지 확인하고, 최종적으로 9개(실루엣 6 + 안쪽 3)가 선택되는지 본다.
    /// 가림 판정이 자기 면에 걸려 보이는 엣지를 놓치던 회귀를 막는 테스트다.
    /// </summary>
    [Fact]
    public void Marquee_Edges_FromMayaCamera_SelectsAllVisibleEdges()
    {
        // 준비: 단위 큐브와 테셀레이션 결과로 피킹 대상을 만들고, 1100x760 뷰포트의 원근 카메라를 구성한다.
        var mesh = MeshBuilder.Cube();
        var render = MeshTessellator.Build(mesh);
        var tg = new PickTarget { Id = new NodeId(1), Mesh = mesh, Render = render, World = Matrix4x4.Identity };
        var vp = new Vector2(1100, 760);
        var cam = CameraProjection.Perspective(Vector3.Normalize(new Vector3(28, 21, 28)) * 2.3f, Vector3.Zero, Vector3.UnitY, 45, vp);

        // 실행: 뷰포트 전체(0,0)~vp 영역을 camera-based 마키로 엣지 선택한다.
        var edges = RayPicker.Marquee(new[] { tg }, cam, Vector2.Zero, vp, SelectMode.Edge, cameraBased: true);
        // 3면이 보이는 각도: 실루엣 6 + 안쪽 3 = 9
        // 검증: 불일치 엣지를 모아 두었다가 한 번에 메시지로 보여 준다.
        var missing = new List<string>();
        for (int e = 0; e < mesh.EdgeCount; e++)
        {
            var (a, b) = mesh.EdgeVertices(e);
            var mid = (mesh.Verts[a].Position + mesh.Verts[b].Position) * 0.5f;
            bool occluded = RayPicker.IsOccluded(new[] { tg }, cam, mid, tg.Id);
            bool selected = edges.Any(s => s.Component == e);
            if (occluded != !selected) missing.Add($"e{e} mid={mid} occluded={occluded} selected={selected}");
            // 앞을 향하는 면에 붙은 엣지는 가려지지 않아야 한다
            var (f0, f1) = mesh.EdgeFaces(e);
            bool front = Vector3.Dot(mesh.Faces[f0].Normal, cam.Eye - mid) > 0 || Vector3.Dot(mesh.Faces[f1].Normal, cam.Eye - mid) > 0;
            if (front && occluded) missing.Add($"e{e} front-facing but occluded");
        }
        Assert.True(missing.Count == 0, string.Join("\n", missing));
        Assert.Equal(9, edges.Count);
    }
}
