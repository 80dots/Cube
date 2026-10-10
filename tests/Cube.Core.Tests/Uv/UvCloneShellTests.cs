using System.Numerics;
using Cube.Core.Mesh;
using Cube.Core.Uv;

namespace Cube.Core.Tests.Uv;

/// <summary>Clone UV Shell(v0.0.68): 같은 위상 셸에 UV 복사, 위치 유지 옵션, 대칭 셸의 최적 대응, 위상이 다른 셸 건너뛰기.</summary>
public class UvCloneShellTests
{
    /// <summary>4×1 격자 평면을 가운데 엣지에서 잘라 2×1 셸 두 개로 만든다.</summary>
    private static (PolyMesh m, UvTopology topo) TwoShells()
    {
        var m = MeshBuilder.Plane(1, 1, 4, 1);
        int cut = Enumerable.Range(0, m.EdgeCount).First(e => { var (a, b) = m.EdgeVertices(e); return MathF.Abs(m.Verts[a].Position.X) < 1e-5f && MathF.Abs(m.Verts[b].Position.X) < 1e-5f; });
        UvOps.CutEdges(m, new[] { cut });
        var topo = UvTopology.Build(m);
        Assert.Equal(2, topo.ShellCount);
        return (m, topo);
    }

    [Fact]
    public void Clone_CopiesUvsToSimilarShell_Stacked()
    {
        var (m, topo) = TwoShells();
        // 셸 0을 찌그러뜨린다(점 하나 이동)
        int src = 0, dst = 1;
        int p = topo.PointsInShell(src).First();
        UvOps.SetPointUv(m, topo, p, topo.Points[p].Uv + new Vector2(0.03f, 0.07f));
        // 스택: 셸 1을 셸 0 위로
        UvOps.StackShells(m, topo, new[] { src, dst });
        var rep = UvOps.CloneToSimilarShells(m, topo, src, CloneShellTarget.Stacked, keepPosition: false);
        Assert.Equal(1, rep.Cloned); Assert.Equal(0, rep.Mismatched);
        // 대응되는 점의 UV가 완전히 같다: 셸 1의 모든 UV 점 위치 집합 == 셸 0의 집합
        var a = topo.PointsInShell(src).Select(q => topo.Points[q].Uv).OrderBy(v => v.X).ThenBy(v => v.Y).ToArray();
        var b = topo.PointsInShell(dst).Select(q => topo.Points[q].Uv).OrderBy(v => v.X).ThenBy(v => v.Y).ToArray();
        Assert.Equal(a.Length, b.Length);
        for (int i = 0; i < a.Length; i++) Assert.True(Vector2.Distance(a[i], b[i]) < 1e-5f, $"{a[i]} vs {b[i]}");
        // 코너 UV도 반영되었는지(점 → 코너)
        foreach (int q in topo.PointsInShell(dst)) foreach (int he in topo.Points[q].HalfEdges) Assert.Equal(topo.Points[q].Uv, m.Hes[he].Uv0);
    }

    [Fact]
    public void Clone_KeepPosition_OffsetsByCenter_AllSimilarFindsUnstacked()
    {
        var (m, topo) = TwoShells();
        int src = 0, dst = 1;
        int p = topo.PointsInShell(src).First();
        UvOps.SetPointUv(m, topo, p, topo.Points[p].Uv + new Vector2(0.02f, 0.05f));
        var (dmn, dmx) = UvOps.ShellBounds(topo, dst); var dstCenter = (dmn + dmx) * 0.5f;
        // 스택하지 않은 상태: Stacked 대상은 겹치지 않으므로 0, AllSimilar는 복사
        Assert.Equal(0, UvOps.CloneToSimilarShells(m, topo, src, CloneShellTarget.Stacked, false).Candidates);
        var rep = UvOps.CloneToSimilarShells(m, topo, src, CloneShellTarget.AllSimilar, keepPosition: true);
        Assert.Equal(1, rep.Cloned);
        var (mn2, mx2) = UvOps.ShellBounds(topo, dst);
        Assert.True(Vector2.Distance((mn2 + mx2) * 0.5f, dstCenter) < 1e-4f);
        // 모양은 원본과 같다(중심 이동 후 비교)
        var (smn, smx) = UvOps.ShellBounds(topo, src); var off = (mn2 + mx2) * 0.5f - (smn + smx) * 0.5f;
        var a = topo.PointsInShell(src).Select(q => topo.Points[q].Uv + off).OrderBy(v => v.X).ThenBy(v => v.Y).ToArray();
        var b = topo.PointsInShell(dst).Select(q => topo.Points[q].Uv).OrderBy(v => v.X).ThenBy(v => v.Y).ToArray();
        for (int i = 0; i < a.Length; i++) Assert.True(Vector2.Distance(a[i], b[i]) < 1e-4f);
    }

    [Fact]
    public void Clone_SymmetricShell_PicksMappingClosestToCurrentUvs()
    {
        // 2×2 격자(UV 0..1 정사각형) 두 개를 한 메시에 합친다(이미 스택됨, 4겹 회전 대칭). 셸 1을 90° 돌려 두면 돌린 대응을 골라 점이 움직이지 않는다.
        var m = MeshBuilder.Plane(1, 1, 2, 2);
        MeshOps.Append(m, MeshBuilder.Plane(1, 1, 2, 2), Matrix4x4.CreateTranslation(0, 0, 2));
        var topo = UvTopology.Build(m);
        Assert.Equal(2, topo.ShellCount);
        UvOps.TransformPoints(m, topo, topo.PointsInShell(1), Matrix3x2.CreateRotation(MathF.PI / 2, new Vector2(0.5f, 0.5f)));
        var before = topo.PointsInShell(1).ToDictionary(q => q, q => topo.Points[q].Uv);
        Assert.True(UvOps.CloneShellUvs(m, topo, 0, 1, keepPosition: false));
        float moved = topo.PointsInShell(1).Max(q => Vector2.Distance(before[q], topo.Points[q].Uv));
        Assert.True(moved < 1e-4f, $"moved {moved}");
        // 원본 셸을 찌그러뜨리고 다시 클론하면 돌린 대응 그대로 모양만 따라온다(점 집합이 같아짐)
        int p = topo.PointsInShell(0).First(q => topo.Points[q].Uv.X < 0.1f && topo.Points[q].Uv.Y < 0.1f);
        UvOps.SetPointUv(m, topo, p, new Vector2(-0.1f, -0.05f));
        Assert.True(UvOps.CloneShellUvs(m, topo, 0, 1, keepPosition: false));
        var a = topo.PointsInShell(0).Select(q => topo.Points[q].Uv).OrderBy(v => v.X).ThenBy(v => v.Y).ToArray();
        var b = topo.PointsInShell(1).Select(q => topo.Points[q].Uv).OrderBy(v => v.X).ThenBy(v => v.Y).ToArray();
        for (int i = 0; i < a.Length; i++) Assert.True(Vector2.Distance(a[i], b[i]) < 1e-5f);
    }

    [Fact]
    public void Clone_SkipsShellWithDifferentTopology()
    {
        var m = MeshBuilder.Plane(1, 1, 3, 1); // 3칸: 가운데 양쪽 엣지를 자르면 1칸/1칸/1칸 셸
        var cuts = Enumerable.Range(0, m.EdgeCount).Where(e => { var (a, b) = m.EdgeVertices(e); float xa = m.Verts[a].Position.X, xb = m.Verts[b].Position.X; return MathF.Abs(xa - xb) < 1e-5f && MathF.Abs(MathF.Abs(xa) - 1f / 6f) < 1e-4f; }).ToArray();
        UvOps.CutEdges(m, cuts.Take(1).ToArray()); // 한쪽만 자름 → 1칸 셸 + 2칸 셸
        var topo = UvTopology.Build(m);
        Assert.Equal(2, topo.ShellCount);
        var rep = UvOps.CloneToSimilarShells(m, topo, 0, CloneShellTarget.AllSimilar, false);
        Assert.Equal(1, rep.Candidates); Assert.Equal(0, rep.Cloned); Assert.Equal(1, rep.Mismatched);
    }
}
