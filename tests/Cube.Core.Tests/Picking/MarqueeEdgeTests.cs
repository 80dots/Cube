using System.Numerics;
using Cube.Core.Mesh;
using Cube.Core.Picking;
using Cube.Core.Scene;
using Cube.Core.Selection;

namespace Cube.Core.Tests.Picking;

public class MarqueeEdgeTests
{
    [Fact]
    public void Marquee_Edges_FromMayaCamera_SelectsAllVisibleEdges()
    {
        var mesh = MeshBuilder.Cube();
        var render = MeshTessellator.Build(mesh);
        var tg = new PickTarget { Id = new NodeId(1), Mesh = mesh, Render = render, World = Matrix4x4.Identity };
        var vp = new Vector2(1100, 760);
        var cam = CameraProjection.Perspective(Vector3.Normalize(new Vector3(28, 21, 28)) * 2.3f, Vector3.Zero, Vector3.UnitY, 45, vp);

        var edges = RayPicker.Marquee(new[] { tg }, cam, Vector2.Zero, vp, SelectMode.Edge, cameraBased: true);
        // 3면이 보이는 각도: 실루엣 6 + 안쪽 3 = 9
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
