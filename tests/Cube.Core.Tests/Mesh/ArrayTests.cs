using System.Numerics;
using Cube.Core.Mesh;

namespace Cube.Core.Tests.Mesh;

/// <summary>MeshOps.MakeArray(Blender Array): 개수·오프셋·병합·원형 배열·반사·UV 오프셋·Fit Length.</summary>
public class ArrayTests
{
    /// <summary>오일러 특성 V − E + F.</summary>
    private static int Euler(PolyMesh m) => m.AliveVertexCount - m.AliveEdgeCount + m.AliveFaceCount;

    /// <summary>Relative (1,0,0) × 3 = 붙어 있는 큐브 3개(병합 없음): 요소 3배, 원본 ID 유지, 두 번째 큐브는 X로 1만큼.</summary>
    [Fact]
    public void FixedCount_RelativeOffset_CopiesSideBySide()
    {
        var m = MeshBuilder.Cube();
        var p0 = m.Verts[0].Position;
        int n = MeshOps.MakeArray(m, new ArrayOptions { Count = 3 });
        Assert.Equal(3, n);
        Assert.Empty(MeshValidator.Check(m));
        Assert.Equal(24, m.AliveVertexCount);
        Assert.Equal(18, m.AliveFaceCount);
        Assert.Equal(p0, m.Verts[0].Position);
        Assert.Equal(p0 + new Vector3(1, 0, 0), m.Verts[8].Position);
        Assert.Equal(p0 + new Vector3(2, 0, 0), m.Verts[16].Position);
        Assert.Equal(6, Euler(m)); // 닫힌 구 위상 3개
    }

    /// <summary>맞닿은 큐브를 Merge: 마주 보는 면은 사라지고 하나의 닫힌 상자(1×3 칸)가 된다.</summary>
    [Fact]
    public void Merge_TouchingCubes_BecomesOneClosedSolid()
    {
        var m = MeshBuilder.Cube();
        MeshOps.MakeArray(m, new ArrayOptions { Count = 3, Merge = true, MergeDistance = 0.001f });
        Assert.Empty(MeshValidator.Check(m));
        Assert.Equal(16, m.AliveVertexCount);
        Assert.Equal(14, m.AliveFaceCount);
        Assert.Equal(2, Euler(m));
        Assert.Single(MeshOps.ConnectedComponents(m));
    }

    /// <summary>열린 평면을 Merge: 경계 정점이 이어져 한 장(1×3 쿼드)이 된다.</summary>
    [Fact]
    public void Merge_Planes_StitchesBorders()
    {
        var m = MeshBuilder.Plane();
        MeshOps.MakeArray(m, new ArrayOptions { Count = 3, Merge = true });
        Assert.Empty(MeshValidator.Check(m));
        Assert.Equal(8, m.AliveVertexCount);
        Assert.Equal(3, m.AliveFaceCount);
        Assert.Equal(10, m.AliveEdgeCount);
    }

    /// <summary>원형 배열: 상수 오프셋 없이 중심 기준 Y 회전 360/n, First Last 병합으로 고리가 닫힌다(토러스 위상 χ = 0).</summary>
    [Fact]
    public void Circular_WithFirstLastMerge_ClosesRing()
    {
        // 원점에서 X로 2 떨어진, 회전축(Y)에 수직한 쿼드 띠 조각: 양끝이 각도상 이웃 조각과 맞닿도록 만든다
        int n = 8; float a = MathF.PI * 2 / n;
        var inner0 = new Vector3(1.5f, 0, 0); var outer0 = new Vector3(2.5f, 0, 0);
        var rotT = new Cube.Core.Scene.Transform3(Vector3.Zero, new Vector3(0, 360f / n, 0), Vector3.One).ScaleRotationMatrix();
        var inner1 = Vector3.Transform(inner0, rotT); var outer1 = Vector3.Transform(outer0, rotT);
        var m = MeshBuilder.Polygon(new[] { inner0, outer0, outer1, inner1 }, Vector3.UnitY);
        MeshOps.MakeArray(m, new ArrayOptions
        {
            Count = n, UseRelative = false, UseTransform = true, RotateDegrees = new Vector3(0, 360f / n, 0),
            Merge = true, MergeDistance = 0.001f, MergeFirstLast = true,
        });
        Assert.Empty(MeshValidator.Check(m));
        Assert.Equal(n * 2, m.AliveVertexCount);
        Assert.Equal(n, m.AliveFaceCount);
        Assert.Equal(0, Euler(m)); // 고리(원환) = 구멍 하나 있는 원판 → V−E+F = 0
    }

    /// <summary>반사(스케일 −1) 단계는 감김을 뒤집어 면 법선이 바깥을 유지한다(검증기 통과).</summary>
    [Fact]
    public void MirrorScale_StaysValid()
    {
        var m = MeshBuilder.Cube();
        MeshOps.MakeArray(m, new ArrayOptions { Count = 2, UseTransform = true, Scale = new Vector3(-1, 1, 1), UseRelative = false, UseConstant = true, Constant = new Vector3(3, 0, 0) });
        Assert.Empty(MeshValidator.Check(m));
        Assert.Equal(12, m.AliveFaceCount);
        MeshNormals.Recompute(m);
        // 두 번째 큐브(X ≈ 3 근처)의 +X 쪽 면 법선은 +X(바깥)여야 한다
        for (int f = 0; f < m.FaceCount; f++)
        {
            if (!m.Faces[f].Alive) continue;
            var c = m.FaceCentroid(f);
            if (c.X < -2.4f) Assert.True(m.Faces[f].Normal.X < -0.9f);
        }
    }

    /// <summary>UV 오프셋은 복사본 i마다 i배 더해진다.</summary>
    [Fact]
    public void UvOffset_AppliedPerCopy()
    {
        var m = MeshBuilder.Plane();
        int nh = m.HalfEdgeCount;
        var uv0 = m.Hes[0].Uv0;
        MeshOps.MakeArray(m, new ArrayOptions { Count = 3, UvOffset = new Vector2(1, 0) });
        Assert.Equal(uv0 + new Vector2(2, 0), m.Hes[2 * nh].Uv0);
    }

    /// <summary>Fit Length: 길이 2.5, 단계 1 → ⌊2.5⌋ + 1 = 3개.</summary>
    [Fact]
    public void FitLength_ComputesCount()
    {
        var m = MeshBuilder.Cube();
        Assert.Equal(3, MeshOps.ArrayCount(m, new ArrayOptions { FitType = ArrayFitType.FitLength, Length = 2.5f }));
        Assert.Equal(3, MeshOps.ArrayCount(m, new ArrayOptions { FitType = ArrayFitType.FitLength, Length = 2f }));
    }
}
