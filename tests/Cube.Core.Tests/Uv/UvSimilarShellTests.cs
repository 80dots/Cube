using System.Numerics;
using Cube.Core.Mesh;
using Cube.Core.Uv;

namespace Cube.Core.Tests.Uv;

/// <summary>Select Identical / Similar Shells(v0.0.69): 위상 같음·모양 같음(Exact/Congruent) 판정.</summary>
public class UvSimilarShellTests
{
    /// <summary>2×2 격자(UV 0..1) 셸 4개를 한 메시에 합친다. 셸 1 = 평행 이동, 셸 2 = 90° 회전, 셸 3 = 찌그러뜨림.</summary>
    private static (PolyMesh m, UvTopology topo) FourShells()
    {
        // 기본 셸은 비대칭 모양(꼭짓점 하나를 당김)이어야 Exact(같은 방향)와 Congruent(회전)가 구분된다
        var basis = MeshBuilder.Plane(1, 1, 2, 2);
        { var bt = UvTopology.Build(basis); int bp = bt.Points.Select((pt, i) => (pt, i)).First(x => x.pt.Uv.X < 0.1f && x.pt.Uv.Y < 0.1f).i; UvOps.SetPointUv(basis, bt, bp, new Vector2(-0.3f, -0.15f)); }
        var m = basis.Clone();
        for (int i = 1; i < 4; i++) MeshOps.Append(m, basis, Matrix4x4.CreateTranslation(0, 0, 2 * i));
        // 그리고 위상이 다른 셸 하나(3×1)
        MeshOps.Append(m, MeshBuilder.Plane(1, 1, 3, 1), Matrix4x4.CreateTranslation(0, 0, 10));
        var topo = UvTopology.Build(m);
        Assert.Equal(5, topo.ShellCount);
        UvOps.TransformPoints(m, topo, topo.PointsInShell(1), Matrix3x2.CreateTranslation(2, 0.5f));
        UvOps.TransformPoints(m, topo, topo.PointsInShell(2), Matrix3x2.CreateRotation(MathF.PI / 2, new Vector2(0.5f, 0.5f)) * Matrix3x2.CreateTranslation(4, 0));
        UvOps.TransformPoints(m, topo, topo.PointsInShell(3), Matrix3x2.CreateTranslation(6, 0));
        int p = topo.PointsInShell(3).First(q => topo.Points[q].Uv.X > 6.9f && topo.Points[q].Uv.Y > 0.9f);
        UvOps.SetPointUv(m, topo, p, topo.Points[p].Uv + new Vector2(-0.2f, -0.1f));
        return (m, topo);
    }

    [Fact]
    public void Similar_FindsAllSameTopology_NotOtherTopology()
    {
        var (m, topo) = FourShells();
        var sim = UvOps.FindSimilarShells(m, topo, 0);
        Assert.Equal(new[] { 1, 2, 3 }, sim.OrderBy(x => x).ToArray());
        Assert.Empty(UvOps.FindSimilarShells(m, topo, 4));
    }

    [Fact]
    public void Identical_Exact_OnlyTranslated_Congruent_AlsoRotated()
    {
        var (m, topo) = FourShells();
        Assert.Equal(new[] { 1 }, UvOps.FindIdenticalShells(m, topo, 0, ShellShapeMatch.Exact).ToArray());
        Assert.Equal(new[] { 1, 2 }, UvOps.FindIdenticalShells(m, topo, 0, ShellShapeMatch.Congruent).OrderBy(x => x).ToArray());
        // 뒤집힌(거울) 셸도 합동으로 잡는다
        UvOps.TransformPoints(m, topo, topo.PointsInShell(1), new Matrix3x2(-1, 0, 0, 1, 0, 0) * Matrix3x2.CreateTranslation(10, 0));
        Assert.DoesNotContain(1, UvOps.FindIdenticalShells(m, topo, 0, ShellShapeMatch.Exact));
        Assert.Contains(1, UvOps.FindIdenticalShells(m, topo, 0, ShellShapeMatch.Congruent));
        // 크기가 다르면 합동이 아니다
        UvOps.TransformPoints(m, topo, topo.PointsInShell(2), Matrix3x2.CreateScale(1.1f, 1.1f, new Vector2(4.5f, 0.5f)));
        Assert.DoesNotContain(2, UvOps.FindIdenticalShells(m, topo, 0, ShellShapeMatch.Congruent));
    }
}
