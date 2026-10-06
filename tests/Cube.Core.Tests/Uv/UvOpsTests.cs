using System.Numerics;
using Cube.Core.Commands;
using Cube.Core.Mesh;
using Cube.Core.Uv;

namespace Cube.Core.Tests.Uv;

public class UvOpsTests
{
    private static IEnumerable<int> AllFaces(PolyMesh m) => Enumerable.Range(0, m.FaceCount).Where(f => m.Faces[f].Alive);

    [Fact]
    public void Cube_DefaultUvs_AreSixShells()
    {
        var m = MeshBuilder.Cube();
        var t = UvTopology.Build(m);
        // 각 면이 0..1 UV를 따로 가지므로 코너 UV는 정점마다 달라 6셸, 24 UV 점
        Assert.Equal(24, t.Points.Count);
        Assert.Equal(6, t.ShellCount);
    }

    [Fact]
    public void PlanarProject_MakesOneShell_InUnitSquare()
    {
        var m = MeshBuilder.Plane(2, 1, 2, 1);
        UvOps.PlanarProject(m, AllFaces(m), Vector3.UnitY);
        var t = UvTopology.Build(m);
        Assert.Equal(1, t.ShellCount);
        Assert.Equal(6, t.Points.Count);
        foreach (var p in t.Points) Assert.True(p.Uv.X >= -1e-5f && p.Uv.X <= 1 + 1e-5f && p.Uv.Y >= -1e-5f && p.Uv.Y <= 1 + 1e-5f);
        // 종횡비 유지: 2x1 평면 → u 범위 1, v 범위 0.5
        float vmax = t.Points.Max(p => p.Uv.Y), vmin = t.Points.Min(p => p.Uv.Y);
        Assert.True(MathF.Abs((vmax - vmin) - 0.5f) < 1e-4f);
    }

    [Fact]
    public void Cylindrical_OnCylinderSides_IsContinuous()
    {
        var m = MeshBuilder.Cylinder(segments: 8, caps: false);
        UvOps.CylindricalProject(m, AllFaces(m));
        var t = UvTopology.Build(m);
        // 옆면 8쿼드: 심(경계) 하나 → 셸 1개, 각 면 내부에서 u 불연속 없음
        Assert.Equal(1, t.ShellCount);
        for (int f = 0; f < m.FaceCount; f++)
        {
            var uvs = new List<float>();
            int start = m.Faces[f].HalfEdge, he = start;
            do { uvs.Add(m.Hes[he].Uv0.X); he = m.Hes[he].Next; } while (he != start);
            Assert.True(uvs.Max() - uvs.Min() < 0.5f, $"face {f} u range {uvs.Min()}..{uvs.Max()}");
        }
    }

    [Fact]
    public void CutAndSew_ChangeShellCount()
    {
        var m = MeshBuilder.Plane(2, 2, 2, 2);
        UvOps.PlanarProject(m, AllFaces(m), Vector3.UnitY);
        Assert.Equal(1, UvTopology.Build(m).ShellCount);
        // 중앙 세로선 2개 엣지를 자르면 2셸
        var inner = Enumerable.Range(0, m.EdgeCount).Where(e => !m.IsBoundaryEdge(e)).ToList();
        var cut = inner.Where(e => { var (a, b) = m.EdgeVertices(e); return MathF.Abs(m.Verts[a].Position.X) < 1e-4f && MathF.Abs(m.Verts[b].Position.X) < 1e-4f; }).ToList();
        Assert.Equal(2, cut.Count);
        UvOps.CutEdges(m, cut);
        Assert.Equal(2, UvTopology.Build(m).ShellCount);
        UvOps.SewEdges(m, cut);
        Assert.Equal(1, UvTopology.Build(m).ShellCount);
    }

    [Fact]
    public void Layout_PacksShellsIntoUnitSquare_WithoutOverlap()
    {
        var m = MeshBuilder.Cube();
        var t = UvTopology.Build(m);
        UvOps.Layout(m, t, Enumerable.Range(0, t.ShellCount));
        t = UvTopology.Build(m);
        var boxes = new List<(Vector2 min, Vector2 max)>();
        for (int s = 0; s < t.ShellCount; s++)
        {
            var pts = t.PointsInShell(s).Select(p => t.Points[p].Uv).ToList();
            var min = new Vector2(pts.Min(p => p.X), pts.Min(p => p.Y)); var max = new Vector2(pts.Max(p => p.X), pts.Max(p => p.Y));
            Assert.True(min.X >= 0 && min.Y >= 0 && max.X <= 1 && max.Y <= 1, $"shell {s} out of range {min}-{max}");
            boxes.Add((min, max));
        }
        for (int i = 0; i < boxes.Count; i++)
            for (int j = i + 1; j < boxes.Count; j++)
            {
                bool overlap = boxes[i].min.X < boxes[j].max.X - 1e-5f && boxes[j].min.X < boxes[i].max.X - 1e-5f && boxes[i].min.Y < boxes[j].max.Y - 1e-5f && boxes[j].min.Y < boxes[i].max.Y - 1e-5f;
                Assert.False(overlap, $"shells {i} and {j} overlap");
            }
    }

    [Fact]
    public void UnfoldRelax_RestoresProportions()
    {
        // 2x1 평면을 1x1로 찌그러뜨린 UV에서 시작 → 이완 후 가로:세로 비율이 2:1로 돌아온다
        var m = MeshBuilder.Plane(2, 1, 4, 2);
        UvOps.PlanarProject(m, AllFaces(m), Vector3.UnitY);
        var t = UvTopology.Build(m);
        foreach (var p in t.Points) UvOps.SetPointUv(m, t, t.Points.IndexOf(p), new Vector2(p.Uv.X, p.Uv.Y * 2f));
        t = UvTopology.Build(m);
        UvOps.UnfoldRelax(m, t, new[] { 0 }, iterations: 100);
        t = UvTopology.Build(m);
        // 이완은 회전 자유도가 있으므로 방향과 무관하게 긴 변/짧은 변 비율로 본다
        float w = t.Points.Max(p => p.Uv.X) - t.Points.Min(p => p.Uv.X);
        float h = t.Points.Max(p => p.Uv.Y) - t.Points.Min(p => p.Uv.Y);
        float ratio = MathF.Max(w, h) / MathF.Min(w, h);
        Assert.True(MathF.Abs(ratio - 2f) < 0.15f, $"ratio {ratio}");
    }

    [Fact]
    public void UvEditCommand_UndoRedo()
    {
        var doc = new Core.Scene.Document();
        var cube = CreatePrimitiveCommand.Cube(doc); doc.Undo.Push(cube);
        var mesh = cube.Node.Mesh!;
        var before = mesh.Hes[0].Uv0;
        doc.Undo.Push(new UvEditCommand("Planar", cube.Node.Id, m => UvOps.PlanarProject(m, AllFaces(m), Vector3.UnitZ)));
        Assert.Equal(1, UvTopology.Build(mesh).ShellCount);
        doc.Undo.Undo();
        Assert.Equal(before, mesh.Hes[0].Uv0);
        Assert.Equal(6, UvTopology.Build(mesh).ShellCount);
        doc.Undo.Redo();
        Assert.Equal(1, UvTopology.Build(mesh).ShellCount);
    }
}

public class UvLayoutCylinderTests
{
    private static IEnumerable<int> AllFaces(PolyMesh m) => Enumerable.Range(0, m.FaceCount).Where(f => m.Faces[f].Alive);

    [Fact]
    public void Cylinder_Cylindrical_Then_Layout_FitsUnitSquare()
    {
        var m = MeshBuilder.Cylinder(segments: 16, caps: true);
        UvOps.CylindricalProject(m, AllFaces(m));
        var t = UvTopology.Build(m);
        var before = t.Points.Select(p => p.Uv).ToList();
        UvOps.Layout(m, t, Enumerable.Range(0, t.ShellCount));
        var t2 = UvTopology.Build(m);
        Assert.True(t2.Points.Any(p => before.All(b => b != p.Uv)), $"layout changed nothing; shells={t.ShellCount} minU={before.Min(p => p.X)} maxU={before.Max(p => p.X)}");
        foreach (var p in t2.Points) Assert.True(p.Uv.X >= -1e-4f && p.Uv.X <= 1 + 1e-4f && p.Uv.Y >= -1e-4f && p.Uv.Y <= 1 + 1e-4f, $"out of range {p.Uv} shells={t.ShellCount}");
    }
}
