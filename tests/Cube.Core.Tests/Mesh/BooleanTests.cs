using System.Numerics;
using Cube.Core.Mesh;

namespace Cube.Core.Tests.Mesh;

/// <summary>MeshOps.Boolean(BSP CSG): 결과가 닫힌 유효 메시이고 부피·위상(오일러 특성)이 기대값과 같은지.</summary>
public class BooleanTests
{
    /// <summary>오일러 특성 V − E + F.</summary>
    private static int Euler(PolyMesh m) => m.AliveVertexCount - m.AliveEdgeCount + m.AliveFaceCount;

    /// <summary>닫힌 메시의 부피(면 부채꼴 삼각형의 부호 있는 사면체 합).</summary>
    private static float Volume(PolyMesh m)
    {
        float v = 0; var tmp = new List<int>();
        for (int f = 0; f < m.FaceCount; f++)
        {
            if (!m.Faces[f].Alive) continue;
            m.GetFaceVertices(f, tmp);
            var p0 = m.Verts[tmp[0]].Position;
            for (int i = 1; i + 1 < tmp.Count; i++) v += Vector3.Dot(p0, Vector3.Cross(m.Verts[tmp[i]].Position, m.Verts[tmp[i + 1]].Position)) / 6f;
        }
        return v;
    }

    /// <summary>결과 공통 검사: 검증기 통과, 닫힘, 버린 면 없음.</summary>
    private static void AssertSolid(PolyMesh r, BooleanReport rep)
    {
        Assert.Empty(MeshValidator.Check(r));
        Assert.True(MeshOps.IsClosed(r));
        Assert.Equal(0, rep.DroppedFaces);
    }

    [Theory]
    [InlineData(BooleanOperation.Union, 1.875f)]
    [InlineData(BooleanOperation.Difference, 0.875f)]
    [InlineData(BooleanOperation.Intersection, 0.125f)]
    public void OverlappingCubes_VolumeAndTopology(BooleanOperation op, float volume)
    {
        var a = MeshBuilder.Cube(); var b = MeshBuilder.Cube();
        var r = MeshOps.Boolean(a, b, Matrix4x4.CreateTranslation(0.5f, 0.5f, 0.5f), op, out var rep);
        AssertSolid(r, rep);
        Assert.Equal(2, Euler(r));
        Assert.Equal(volume, Volume(r), 3);
    }

    /// <summary>모서리 엣지 일부만 공유하는 상자(같은 높이 평면이 맞닿음)도 깨끗이 합쳐진다: L자 기둥.</summary>
    [Fact]
    public void Union_CoplanarFaces_StaysClean()
    {
        var a = MeshBuilder.Cube(); var b = MeshBuilder.Cube();
        var r = MeshOps.Boolean(a, b, Matrix4x4.CreateTranslation(0.5f, 0f, 0.5f), BooleanOperation.Union, out var rep);
        AssertSolid(r, rep);
        Assert.Equal(2, Euler(r));
        Assert.Equal(1.75f, Volume(r), 3);
        // 위·아래 면은 각각 L자 두 조각(A 면/B 면) 이하로 정리되어야 한다(조각 병합)
        Assert.True(r.AliveFaceCount <= 14, $"faces={r.AliveFaceCount}");
    }

    /// <summary>큐브에 원기둥 구멍 뚫기: 구멍 하나(종수 1) → V−E+F = 0.</summary>
    [Fact]
    public void Difference_CylinderHole_Genus1()
    {
        var a = MeshBuilder.Cube(); var b = MeshBuilder.Cylinder(0.25f, 2f, 16);
        var r = MeshOps.Boolean(a, b, Matrix4x4.Identity, BooleanOperation.Difference, out var rep);
        AssertSolid(r, rep);
        Assert.Equal(0, Euler(r));
        Assert.True(Volume(r) < 1f && Volume(r) > 0.7f);
    }

    /// <summary>구 ∩ 큐브(구가 큐브 모서리를 넘침): 닫힌 유효 메시, 부피가 둘 중 작은 것 이하.</summary>
    [Fact]
    public void Intersection_SphereCube_Closed()
    {
        var a = MeshBuilder.Cube(); var b = MeshBuilder.Sphere(0.65f, 16, 8);
        var r = MeshOps.Boolean(a, b, Matrix4x4.Identity, BooleanOperation.Intersection, out var rep);
        AssertSolid(r, rep);
        Assert.Equal(2, Euler(r));
        Assert.True(Volume(r) < 1f);
    }

    /// <summary>떨어진 두 메시의 Union = 두 덩어리 그대로, Intersection = 빈 메시.</summary>
    [Fact]
    public void Disjoint_UnionKeepsBoth_IntersectionEmpty()
    {
        var a = MeshBuilder.Cube(); var b = MeshBuilder.Cube();
        var u = MeshOps.Boolean(a, b, Matrix4x4.CreateTranslation(3, 0, 0), BooleanOperation.Union, out var rep);
        AssertSolid(u, rep);
        Assert.Equal(12, u.AliveFaceCount);
        var i = MeshOps.Boolean(a, b, Matrix4x4.CreateTranslation(3, 0, 0), BooleanOperation.Intersection, out _);
        Assert.Equal(0, i.AliveFaceCount);
    }

    /// <summary>두 입력의 경계 엣지는 하드, 큐브 원래 모서리도 하드로 유지된다.</summary>
    [Fact]
    public void HardEdges_OnSeamAndOriginalCorners()
    {
        var a = MeshBuilder.Cube(); var b = MeshBuilder.Cube();
        var r = MeshOps.Boolean(a, b, Matrix4x4.CreateTranslation(0.5f, 0.5f, 0.5f), BooleanOperation.Difference, out _);
        MeshNormals.Recompute(r);
        // 꺾인 엣지(두 면 법선이 다름)는 모두 하드, 같은 평면 조각 사이 엣지는 소프트
        foreach (var e in r.Edges)
        {
            if (!e.Alive || e.He1 < 0) continue;
            var n0 = r.Faces[r.Hes[e.He0].Face].Normal; var n1 = r.Faces[r.Hes[e.He1].Face].Normal;
            Assert.Equal(Vector3.Dot(n0, n1) < 0.99f, e.Hard);
        }
        // 큐브 − 모서리 큐브 = L자 면 3 + 온전한 면 3 + 파인 면 3
        Assert.Equal(9, r.AliveFaceCount);
    }

    /// <summary>퍼즈: 큐브/구/원기둥/원뿔/토러스를 무작위 회전·이동·스케일로 겹쳐 세 연산을 모두 돌린다 — 결과가 항상 닫힌 유효 메시이고 부피가 포함 관계를 지킨다.</summary>
    [Fact]
    public void Fuzz_RandomPrimitives_StaySolid()
    {
        var rnd = new Random(1234);
        Func<PolyMesh>[] makers = { () => MeshBuilder.Cube(), () => MeshBuilder.Sphere(0.6f, 12, 6), () => MeshBuilder.Cylinder(0.4f, 1.2f, 10), () => MeshBuilder.Cone(0.5f, 1f, 9), () => MeshBuilder.Torus(0.5f, 0.2f, 12, 8) };
        var failures = new List<string>();
        for (int iter = 0; iter < 60; iter++)
        {
            int ia = rnd.Next(makers.Length), ib = rnd.Next(makers.Length);
            var a = makers[ia](); var b = makers[ib]();
            var xf = Matrix4x4.CreateScale(0.6f + (float)rnd.NextDouble())
                   * Matrix4x4.CreateFromYawPitchRoll((float)rnd.NextDouble() * 6, (float)rnd.NextDouble() * 6, (float)rnd.NextDouble() * 6)
                   * Matrix4x4.CreateTranslation((float)rnd.NextDouble() - 0.5f, (float)rnd.NextDouble() - 0.5f, (float)rnd.NextDouble() - 0.5f);
            float va = Volume(a), vb = Volume(b) * xf.GetDeterminant();
            foreach (var op in new[] { BooleanOperation.Union, BooleanOperation.Difference, BooleanOperation.Intersection })
            {
                var r = MeshOps.Boolean(a, b, xf, op, out var rep);
                var errs = MeshValidator.Check(r);
                bool closed = r.AliveFaceCount == 0 || MeshOps.IsClosed(r);
                float v = Volume(r);
                bool volOk = op switch
                {
                    BooleanOperation.Union => v >= MathF.Max(va, vb) - 0.02f && v <= va + vb + 0.02f,
                    BooleanOperation.Difference => v <= va + 0.02f && v >= va - vb - 0.02f,
                    _ => v <= MathF.Min(va, vb) + 0.02f && v >= -0.02f,
                };
                if (errs.Count > 0 || !closed || !volOk)
                    failures.Add($"iter{iter} {ia}x{ib} {op}: errs={errs.Count} closed={closed} dropped={rep.DroppedFaces} filled={rep.FilledHoles} vol={v:0.###} (a={va:0.###} b={vb:0.###})");
            }
        }
        Assert.True(failures.Count == 0, string.Join(" | ", failures));
    }
}
