using System.Numerics;
using Cube.Core.Mesh;
using Cube.Core.Uv;

namespace Cube.Core.Tests.Uv;

/// <summary>Maya UV 메뉴 추가 연산: Align/Distribute/Normalize/Unitize/Cycle/MatchGrid/Straighten/MapBorder/Optimize/Pin/셸 연산/선택 도우미/Automatic/UV 세트.</summary>
public class UvOpsExtendedTests
{
    private static (PolyMesh m, UvTopology topo) Plane(int n = 2)
    {
        var m = MeshBuilder.Plane(1, 1, n, n);
        return (m, UvTopology.Build(m));
    }

    [Fact]
    public void Align_LinearAlign_Distribute()
    {
        var (m, topo) = Plane(2);
        var all = Enumerable.Range(0, topo.Points.Count).ToList();
        UvOps.Align(m, topo, all, UvOps.AlignMode.MinU);
        Assert.All(topo.Points, p => Assert.True(MathF.Abs(p.Uv.X) < 1e-6f));
        var (m2, t2) = Plane(2);
        var pts = Enumerable.Range(0, t2.Points.Count).ToList();
        UvOps.Distribute(m2, t2, pts, alongU: true);
        var xs = t2.Points.Select(p => p.Uv.X).OrderBy(x => x).ToList();
        Assert.True(xs[0] <= 1e-6f && xs[^1] >= 1 - 1e-6f);
        var (m3, t3) = Plane(2);
        UvOps.LinearAlign(m3, t3, Enumerable.Range(0, t3.Points.Count));
        // 9점이 한 직선 위(주축) → 각 점이 직선에서 0 거리
        var c = t3.Points.Aggregate(Vector2.Zero, (s, p) => s + p.Uv) / t3.Points.Count;
        var dir = Vector2.Normalize(t3.Points[0].Uv - c + new Vector2(1e-12f));
        Assert.All(t3.Points, p => Assert.True(MathF.Abs(UvOps.Cross(dir, p.Uv - c)) < 1e-4f));
    }

    [Fact]
    public void Normalize_Unitize_Cycle_MatchGrid()
    {
        var (m, topo) = Plane(2);
        UvOps.TransformPoints(m, topo, Enumerable.Range(0, topo.Points.Count), Matrix3x2.CreateScale(0.5f) * Matrix3x2.CreateTranslation(0.2f, 0.3f));
        UvOps.Normalize(m, topo, Enumerable.Range(0, topo.Points.Count), preserveAspect: true, collectively: true);
        var (mn, mx) = UvOps.Bounds(topo.Points.Select(p => p.Uv));
        Assert.True(mn.X < 1e-6f && mn.Y < 1e-6f && MathF.Abs(mx.X - 1) < 1e-6f && MathF.Abs(mx.Y - 1) < 1e-6f);
        var cube = MeshBuilder.Cube();
        UvOps.Unitize(cube, Enumerable.Range(0, cube.FaceCount));
        foreach (var h in cube.Hes) if (h.Alive) Assert.True(h.Uv0.X is 0 or 1 && h.Uv0.Y is 0 or 1);
        var before = cube.Hes[cube.Faces[0].HalfEdge].Uv0;
        UvOps.Cycle(cube, new[] { 0 });
        Assert.NotEqual(before, cube.Hes[cube.Faces[0].HalfEdge].Uv0);
        var (m2, t2) = Plane(2);
        UvOps.TransformPoints(m2, t2, Enumerable.Range(0, t2.Points.Count), Matrix3x2.CreateTranslation(0.03f, -0.02f));
        UvOps.MatchGrid(m2, t2, Enumerable.Range(0, t2.Points.Count), 0.25f);
        Assert.All(t2.Points, p => { Assert.True(MathF.Abs(p.Uv.X * 4 - MathF.Round(p.Uv.X * 4)) < 1e-5f); });
    }

    [Fact]
    public void Straighten_MapBorder_Optimize_RespectPins()
    {
        var (m, topo) = Plane(3);
        // 안쪽 점들을 흐트러뜨린 뒤 경계를 사각형에 맞추고 Optimize하면 안쪽이 다시 고르게 퍼진다
        var rng = new Random(1);
        foreach (var p in topo.Points) if (p.Uv.X > 0.1f && p.Uv.X < 0.9f && p.Uv.Y > 0.1f && p.Uv.Y < 0.9f) UvOps.SetPointUv(m, topo, topo.Points.IndexOf(p), p.Uv + new Vector2((float)rng.NextDouble() * 0.1f, (float)rng.NextDouble() * 0.1f));
        Assert.True(UvOps.MapBorder(m, topo, 0, square: true));
        var border = UvOps.ShellBorderLoops(m, topo, 0).OrderByDescending(l => l.Count).First();
        Assert.Equal(12, border.Count);
        Assert.All(border, p => Assert.True(topo.Points[p].Uv.X is <= 1e-5f or >= 1 - 1e-5f || topo.Points[p].Uv.Y is <= 1e-5f or >= 1 - 1e-5f));
        UvOps.Optimize(m, topo, new[] { 0 }, 80);
        // 중앙 점(1/3,1/3 격자의 (1,1)) 근처로 돌아왔는지
        var inner = topo.Points.Where(p => !border.Contains(topo.Points.IndexOf(p))).ToList();
        Assert.Equal(4, inner.Count);
        Assert.All(inner, p => Assert.True(p.Uv.X > 0.2f && p.Uv.X < 0.8f && p.Uv.Y > 0.2f && p.Uv.Y < 0.8f, $"{p.Uv}"));
        // Pin: 고정 점은 Optimize에서 움직이지 않는다
        int pinId = topo.Points.IndexOf(inner[0]);
        UvOps.SetPins(m, topo, new[] { pinId }, true);
        var t2 = UvTopology.Build(m);
        Assert.True(t2.Points[pinId].Pinned);
        UvOps.SetPointUv(m, t2, pinId, new Vector2(0.45f, 0.45f));
        UvOps.Optimize(m, t2, new[] { 0 }, 50);
        Assert.Equal(new Vector2(0.45f, 0.45f), t2.Points[pinId].Uv);
        var (m3, t3) = Plane(2);
        UvOps.TransformPoints(m3, t3, Enumerable.Range(0, t3.Points.Count), Matrix3x2.CreateRotation(0.05f, new Vector2(0.5f, 0.5f)));
        UvOps.StraightenUvs(m3, t3, Enumerable.Range(0, t3.Points.Count), 10f, iterations: 40);
        // 가로/세로 엣지가 축에 정렬됨
        var ys = t3.Points.Select(p => MathF.Round(p.Uv.Y, 3)).Distinct().Count();
        Assert.True(ys <= 3, $"distinct rows {ys}");
    }

    [Fact]
    public void ShellOps_Stack_Unstack_Distribute_Gather_FlipReversed()
    {
        var cube = MeshBuilder.Cube();
        var topo = UvTopology.Build(cube);
        Assert.Equal(6, topo.ShellCount);
        UvOps.StackShells(cube, topo, Enumerable.Range(0, 6));
        var c0 = UvOps.ShellBounds(topo, 0);
        for (int s = 1; s < 6; s++) { var c = UvOps.ShellBounds(topo, s); Assert.True(Vector2.Distance((c.min + c.max) * 0.5f, (c0.min + c0.max) * 0.5f) < 1e-5f); }
        UvOps.UnstackShells(cube, topo, Enumerable.Range(0, 6), 0.05f);
        Assert.Empty(UvOps.OverlappingFaces(cube));
        UvOps.DistributeShells(cube, topo, Enumerable.Range(0, 6), alongU: false, 0.1f);
        Assert.Empty(UvOps.OverlappingFaces(cube));
        UvOps.GatherShells(cube, topo, Enumerable.Range(0, 6));
        foreach (int s in Enumerable.Range(0, 6)) { var b = UvOps.ShellBounds(topo, s); var c = (b.min + b.max) * 0.5f; Assert.True(c.X >= 0 && c.X < 1 && c.Y >= 0 && c.Y < 1); }
        // 셸 하나를 U로 뒤집으면 뒤집힌 셸이 생기고 FlipReversed가 되돌린다
        UvOps.Flip(cube, topo, topo.PointsInShell(2), flipU: true);
        Assert.Single(UvOps.BackFacingFaces(cube));
        Assert.Equal(1, UvOps.FlipReversedShells(cube, topo, Enumerable.Range(0, 6)));
        Assert.Empty(UvOps.BackFacingFaces(cube));
        Assert.Equal(6, UvOps.Statistics(cube, topo).shells);
    }

    [Fact]
    public void MoveAndSew_JoinsTwoShells()
    {
        var cube = MeshBuilder.Cube();
        var topo = UvTopology.Build(cube);
        int e = Enumerable.Range(0, cube.EdgeCount).First(x => cube.Edges[x].Alive && cube.Edges[x].Seam);
        int done = UvOps.MoveAndSew(cube, topo, new[] { e });
        Assert.Equal(1, done);
        var t2 = UvTopology.Build(cube);
        Assert.Equal(5, t2.ShellCount);
        Assert.Empty(MeshValidator.Check(cube));
    }

    [Fact]
    public void Select_Helpers_And_SplitMerge()
    {
        var (m, topo) = Plane(2);
        var border = UvOps.TextureBorderPoints(m, topo);
        Assert.Equal(8, border.Count);
        var path = UvOps.ShortestEdgePath(m, 0, 8);
        Assert.Equal(4, path.Count);
        // 루프 성장/축소: 4×4 평면의 안쪽 엣지(양 끝이 4가 정점)에서
        var big = MeshBuilder.Plane(1, 1, 4, 4);
        var tmpE = new List<int>();
        int inner = Enumerable.Range(0, big.EdgeCount).First(e => { var (a, b) = big.EdgeVertices(e); big.GetVertexEdges(a, tmpE); int da = tmpE.Count; big.GetVertexEdges(b, tmpE); return da == 4 && tmpE.Count == 4; });
        var edges = new HashSet<int> { inner };
        UvOps.GrowAlongLoop(big, edges);
        Assert.Equal(3, edges.Count);
        UvOps.ShrinkAlongLoop(big, edges);
        Assert.Single(edges);
        Assert.Equal(4, UvOps.FacesOfPoints(m, topo, Enumerable.Range(0, topo.Points.Count), all: true).Count);
        // 중앙 점을 Split하면 4개 점으로 갈라진다
        int center = topo.Points.FindIndex(p => Vector2.Distance(p.Uv, new Vector2(0.5f, 0.5f)) < 1e-5f);
        UvOps.SplitUvs(m, topo, new[] { center });
        var t2 = UvTopology.Build(m);
        Assert.Equal(4, t2.Points.Count(p => Vector2.Distance(p.Uv, new Vector2(0.5f, 0.5f)) < 1e-5f));
        int merged = UvOps.MergeUvs(m, t2, Enumerable.Range(0, t2.Points.Count), 1e-4f);
        Assert.True(merged > 0);
        Assert.Equal(1, UvTopology.Build(m).ShellCount);
    }

    [Fact]
    public void Automatic_Camera_Contour_Projections()
    {
        var cube = MeshBuilder.Cube();
        int shells = UvOps.AutomaticProject(cube, Enumerable.Range(0, cube.FaceCount), 6);
        Assert.Equal(6, shells);
        var topo = UvTopology.Build(cube);
        Assert.Equal(6, topo.ShellCount);
        Assert.Empty(UvOps.OverlappingFaces(cube));
        var (mn, mx) = UvOps.Bounds(topo.Points.Select(p => p.Uv));
        Assert.True(mn.X >= -1e-4f && mx.X <= 1 + 1e-4f && mn.Y >= -1e-4f && mx.Y <= 1 + 1e-4f);
        var plane = MeshBuilder.Plane(1, 1, 2, 2);
        UvOps.CameraProject(plane, Enumerable.Range(0, plane.FaceCount), Vector3.UnitX, Vector3.UnitZ);
        var pt = UvTopology.Build(plane);
        Assert.Equal(9, pt.Points.Count);
        UvOps.ContourStretch(plane, Enumerable.Range(0, plane.FaceCount));
        var (cmn, cmx) = UvOps.Bounds(UvTopology.Build(plane).Points.Select(p => p.Uv));
        Assert.True(cmn.X < 1e-5f && cmx.X > 1 - 1e-5f);
        var sphere = MeshBuilder.Sphere();
        int s3 = UvOps.AutomaticProject(sphere, Enumerable.Range(0, sphere.FaceCount), 3);
        Assert.True(s3 >= 3 && s3 <= 12, $"{s3}");
    }

    [Fact]
    public void UvSets_Switch_Copy_Remove_And_SaveLoad()
    {
        var m = MeshBuilder.Cube();
        var first = m.Hes[0].Uv0;
        int idx = m.AddUvSet("lightmap", copyCurrent: false);
        Assert.Equal(1, idx); Assert.Equal(2, m.UvSets.Count);
        m.SwitchUvSet(1);
        Assert.Equal(Vector2.Zero, m.Hes[0].Uv0);
        var topo = UvTopology.Build(m);
        UvOps.Unitize(m, Enumerable.Range(0, m.FaceCount));
        m.SwitchUvSet(0);
        Assert.Equal(first, m.Hes[0].Uv0);
        var c = m.Clone();
        Assert.Equal(2, c.UvSets.Count);
        // .cube 왕복
        var doc = new Core.Scene.Document();
        var node = new Core.Scene.SceneNode { Name = "cube", Shape = new Core.Scene.MeshShape(m) };
        doc.AddNode(node, doc.Root);
        var json = Core.IO.CubeFileFormat.Serialize(doc);
        var doc2 = new Core.Scene.Document(); Core.IO.CubeFileFormat.Deserialize(doc2, json);
        var m2 = doc2.MeshNodes().First().Mesh!;
        Assert.Equal(2, m2.UvSets.Count); Assert.Equal("lightmap", m2.UvSets[1].Name);
        m2.SwitchUvSet(1);
        Assert.True(m2.Hes.Where(h => h.Alive).All(h => h.Uv0.X is 0 or 1));
        m.RemoveUvSet(1);
        Assert.Single(m.UvSets);
    }
}
