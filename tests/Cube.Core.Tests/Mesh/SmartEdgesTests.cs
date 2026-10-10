using System.Numerics;
using Cube.Core.Mesh;

namespace Cube.Core.Tests.Mesh;

/// <summary>Smart Soften/Harden: 각도 임계값으로는 구분할 수 없는 경우(8각 원기둥 45° vs 상자 챔퍼 45°)를 링 문맥·자동 임계값·루프 전파로 풀어내는지.</summary>
public class SmartEdgesTests
{
    private static int HardCount(PolyMesh m) => Enumerable.Range(0, m.EdgeCount).Count(e => m.Edges[e].Alive && m.Edges[e].Hard);
    private static IEnumerable<int> Interior(PolyMesh m) => Enumerable.Range(0, m.EdgeCount).Where(e => m.Edges[e].Alive && m.Edges[e].He1 >= 0);

    /// <summary>원기둥: 옆면 사이 축 방향 엣지(45°)는 소프트, 캡 둘레(90°)는 하드.</summary>
    [Fact]
    public void Cylinder8_SidesSoft_CapsHard()
    {
        var m = MeshBuilder.Cylinder(0.5f, 1f, 8, caps: true);
        var rep = MeshOps.SmartSoftenHarden(m, null, new SmartEdgeOptions());
        foreach (int e in Interior(m))
        {
            var (a, b) = m.EdgeVertices(e);
            bool axial = MathF.Abs(m.Verts[a].Position.Y - m.Verts[b].Position.Y) > 0.5f;
            if (axial) Assert.False(m.Edges[e].Hard, $"axial edge {e} should be soft");
            else { var (f0, f1) = m.EdgeFaces(e); bool capEdge = MathF.Abs(MeshOps.SignedDihedral(m, e)) > 1f; Assert.Equal(capEdge, m.Edges[e].Hard); }
        }
        Assert.Equal(16, HardCount(m));
    }

    /// <summary>단순 각도 30°라면 8각 원기둥 옆면은 전부 하드가 되지만 Smart는 소프트다(동일 메시 비교).</summary>
    [Fact]
    public void Cylinder8_AngleThresholdWouldFail()
    {
        var a = MeshBuilder.Cylinder(0.5f, 1f, 8, caps: false);
        MeshOps.SoftenHardenByAngle(a, Interior(a), 30f);
        Assert.Equal(8, HardCount(a));
        var b = MeshBuilder.Cylinder(0.5f, 1f, 8, caps: false);
        MeshOps.SmartSoftenHarden(b, null, new SmartEdgeOptions());
        Assert.Equal(0, HardCount(b));
    }

    /// <summary>큐브: 모든 모서리 90° → MaxSmoothAngle 규칙으로 전부 하드.</summary>
    [Fact]
    public void Cube_AllHard()
    {
        var m = MeshBuilder.Cube();
        var rep = MeshOps.SmartSoftenHarden(m, null, new SmartEdgeOptions());
        Assert.Equal(12, rep.Hard); Assert.Equal(0, rep.Soft);
    }

    /// <summary>구·토러스(곡면): 하드 엣지 없음.</summary>
    [Fact]
    public void SphereTorus_AllSoft()
    {
        foreach (var m in new[] { MeshBuilder.Sphere(0.5f, 16, 8), MeshBuilder.Torus(0.5f, 0.2f, 16, 8) })
        {
            MeshOps.SmartSoftenHarden(m, null, new SmartEdgeOptions());
            Assert.Equal(0, HardCount(m));
        }
    }

    /// <summary>베벨된 큐브: 베벨 띠 안쪽 엣지는 소프트, 띠와 평면이 만나는 엣지도 띠가 균일하면 소프트(둥근 모서리로 셰이딩).</summary>
    [Fact]
    public void BeveledCube_RoundedEdgesSoft()
    {
        var m = MeshBuilder.Cube();
        MeshOps.Bevel(m, Enumerable.Range(0, m.EdgeCount), new BevelOptions { Width = 0.15f, Segments = 3 });
        Assert.Empty(MeshValidator.Check(m));
        var rep = MeshOps.SmartSoftenHarden(m, null, new SmartEdgeOptions());
        Assert.Equal(0, rep.Hard);
    }

    /// <summary>베벨 1단(챔퍼 45°)이 있는 큐브와 8각 원기둥(45°)이 한 메시에 있어도: 챔퍼는 하드, 원기둥은 소프트.</summary>
    [Fact]
    public void ChamferAndCylinder_SameAngleDifferentResult()
    {
        var m = MeshBuilder.Cube();
        MeshOps.Bevel(m, new[] { 0 }, new BevelOptions { Width = 0.2f, Segments = 1 });
        MeshOps.Append(m, MeshBuilder.Cylinder(0.3f, 1f, 8, caps: false), Matrix4x4.CreateTranslation(3, 0, 0));
        MeshOps.SmartSoftenHarden(m, null, new SmartEdgeOptions());
        foreach (int e in Interior(m))
        {
            var (a, b) = m.EdgeVertices(e);
            bool inCyl = m.Verts[a].Position.X > 2f;
            if (inCyl) Assert.False(m.Edges[e].Hard, "cylinder edge must be soft");
            else { float ang = MathF.Abs(MeshOps.SignedDihedral(m, e)) * 180f / MathF.PI; if (ang > 5f) Assert.True(m.Edges[e].Hard, $"chamfer/corner edge {e} ({ang:0}°) must be hard"); }
        }
    }

    /// <summary>Manual 모드 + 히스테리시스: 약한 엣지도 확정 하드와 루프로 이어지면 하드, 고립된 약한 엣지는 소프트.</summary>
    [Fact]
    public void Hysteresis_LoopPropagation()
    {
        // 3×3 평면을 접어서: 가운데 행 엣지 루프를 60°로 꺾고 그중 한 칸만 25°로 완만하게
        var m = MeshBuilder.Plane(3, 3, 3, 3);
        foreach (int v in Enumerable.Range(0, m.VertexCount)) { var p = m.Verts[v].Position; if (p.Z > 0.4f) { p.Y += (p.Z - 0.5f) * 1.5f; var vv = m.Verts[v]; vv.Position = p; m.Verts[v] = vv; } }
        m.BumpGeometry();
        var o = new SmartEdgeOptions { ThresholdMode = SmartThresholdMode.Manual, HighAngle = 50f, LowAngle = 15f, ContextWeight = 0f, MinChainLength = 0 };
        var rep = MeshOps.SmartSoftenHarden(m, null, o);
        // 접힌 선 = z≈0.5 행의 엣지 3개 모두 하드
        int fold = Interior(m).Count(e => { var (a, b) = m.EdgeVertices(e); return MathF.Abs(m.Verts[a].Position.Z - 0.5f) < 1e-3f && MathF.Abs(m.Verts[b].Position.Z - 0.5f) < 1e-3f; });
        Assert.Equal(3, fold);
        Assert.Equal(3, rep.Hard);
    }

    /// <summary>UV 규칙: 셸 경계(심)는 하드, 안쪽은 소프트 — 평평한 격자(형태로는 전부 소프트)를 심으로 둘로 갈라 둔 상태.</summary>
    [Fact]
    public void UvRule_BordersHard_InsideSoft()
    {
        var m = MeshBuilder.Plane(1, 1, 2, 2);
        var interior = Interior(m).ToList();
        Assert.Equal(4, interior.Count);
        // 가운데 정점에서 X 방향으로 뻗는 두 엣지를 심으로
        var seamEdges = interior.Where(e => { var (a, b) = m.EdgeVertices(e); return MathF.Abs(m.Verts[a].Position.Z - m.Verts[b].Position.Z) < 1e-5f; }).ToList();
        Assert.Equal(2, seamEdges.Count);
        Cube.Core.Uv.UvOps.CutEdges(m, seamEdges);
        MeshOps.SmartSoftenHarden(m, null, new SmartEdgeOptions { UvRule = SmartUvRule.HardenBordersSoftenInside });
        foreach (int e in interior) Assert.Equal(seamEdges.Contains(e), m.Edges[e].Hard);
        // Ignore면 평면이라 전부 소프트
        MeshOps.SmartSoftenHarden(m, null, new SmartEdgeOptions());
        Assert.Equal(0, HardCount(m));
    }

    /// <summary>KeepExistingHard/Soft 보호와 선택 범위 한정.</summary>
    [Fact]
    public void Protection_AndDomain()
    {
        var m = MeshBuilder.Cylinder(0.5f, 1f, 8, caps: true);
        foreach (int e in Interior(m)) { var ed = m.Edges[e]; ed.Hard = true; m.Edges[e] = ed; }
        var before = HardCount(m);
        MeshOps.SmartSoftenHarden(m, null, new SmartEdgeOptions { KeepExistingHard = true });
        Assert.Equal(before, HardCount(m));
        // 축 방향 엣지 하나만 대상으로 하면 그것만 소프트가 된다
        int axial = Interior(m).First(e => { var (a, b) = m.EdgeVertices(e); return MathF.Abs(m.Verts[a].Position.Y - m.Verts[b].Position.Y) > 0.5f; });
        MeshOps.SmartSoftenHarden(m, new[] { axial }, new SmartEdgeOptions());
        Assert.Equal(before - 1, HardCount(m));
        Assert.False(m.Edges[axial].Hard);
    }

    /// <summary>Weighted Normals: 소프트 코너 노멀이 고정되고 큰 면이 노멀을 지배한다.</summary>
    [Fact]
    public void WeightedNormals_LocksCorners()
    {
        var m = MeshBuilder.Cube();
        MeshOps.Bevel(m, Enumerable.Range(0, m.EdgeCount), new BevelOptions { Width = 0.05f, Segments = 1 });
        MeshOps.SmartSoftenHarden(m, null, new SmartEdgeOptions { ThresholdMode = SmartThresholdMode.Manual, HighAngle = 170f, LowAngle = 170f, WeightedNormals = SmartWeightedNormals.FaceArea });
        MeshNormals.Recompute(m);
        int locked = m.Hes.Count(h => h.Alive && h.NormalLocked);
        Assert.True(locked > 0);
        // 큰 평면(+Y 면)의 코너 노멀은 거의 +Y
        int top = Enumerable.Range(0, m.FaceCount).Where(f => m.Faces[f].Alive).OrderByDescending(f => MeshNormals.FaceNormalUnnormalized(m, f).Y).First();
        var hes = new List<int>(); m.GetFaceHalfEdges(top, hes);
        foreach (int h in hes) Assert.True(m.Hes[h].Normal.Y > 0.95f, $"corner normal {m.Hes[h].Normal}");
    }
}
