using System.Numerics;
using Cube.Core.Mesh;

namespace Cube.Core.Tests.Mesh;

/// <summary>모델링 감사(v0.0.57)에서 찾은 버그의 재현 회귀 테스트.</summary>
public class ModelingAuditRegressionTests
{
    /// <summary>오일러 특성 V − E + F.</summary>
    private static int Euler(PolyMesh m) => m.AliveVertexCount - m.AliveEdgeCount + m.AliveFaceCount;
    /// <summary>경계 엣지 수.</summary>
    private static int Boundary(PolyMesh m) => Enumerable.Range(0, m.EdgeCount).Count(e => m.Edges[e].Alive && m.IsBoundaryEdge(e));
    /// <summary>살아 있는 면 ID.</summary>
    private static int[] Faces(PolyMesh m) => Enumerable.Range(0, m.FaceCount).Where(f => m.Faces[f].Alive).ToArray();

    /// <summary>건전성(검증기·고립 정점 없음) + 선택적으로 닫힘/오일러.</summary>
    private static void AssertSound(PolyMesh m, bool closed = false, int? euler = null)
    {
        var p = ModelingAuditFuzzTests.Problems(m);
        Assert.True(p.Count == 0, string.Join("; ", p));
        if (closed) Assert.Equal(0, Boundary(m));
        if (euler != null) Assert.Equal(euler, Euler(m));
    }

    /// <summary>Symmetrize(자르기) 중심 평면: 큐브는 닫힌 큐브(자른 루프가 추가된 10면)로 남는다(전에는 걸친 면이 겹쳐 구멍).</summary>
    [Fact]
    public void Symmetrize_CubeAtCenter_StaysClosedBox()
    {
        var m = MeshBuilder.Cube();
        MeshOps.MirrorGeometry(m, 0, 0f, true, true, 0.001f);
        AssertSound(m, closed: true, euler: 2);
        var (mn, mx) = MeshOps.Bounds(m);
        Assert.Equal(-0.5f, mn.X, 4); Assert.Equal(0.5f, mx.X, 4);
    }

    /// <summary>Symmetrize 비중심 평면: 남길 쪽(x ≥ 0.2)을 반사한 닫힌 상자(-0.1..0.5).</summary>
    [Fact]
    public void Symmetrize_OffCenterPlane_SlicesAndMirrors()
    {
        var m = MeshBuilder.Cube();
        MeshOps.MirrorGeometry(m, 0, 0.2f, true, true, 0.001f);
        AssertSound(m, closed: true, euler: 2);
        var (mn, mx) = MeshOps.Bounds(m);
        Assert.Equal(-0.1f, mn.X, 4); Assert.Equal(0.5f, mx.X, 4);
    }

    /// <summary>Symmetrize: 평면이 기존 정점을 지나는 구(경선)도 닫힌 채로 대칭이 된다.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Symmetrize_SphereThroughVertices_Closed(int axis)
    {
        var m = MeshBuilder.Sphere(0.5f, 8, 6);
        MeshOps.MirrorGeometry(m, axis, 0f, false, true, 0.001f);
        AssertSound(m, closed: true, euler: 2);
    }

    /// <summary>Symmetrize 평면이 메시 바깥(남길 쪽에 아무것도 없음)이면 메시를 바꾸지 않는다.</summary>
    [Fact]
    public void Symmetrize_PlaneOutside_NoChange()
    {
        var m = MeshBuilder.Cube();
        var r = MeshOps.MirrorGeometry(m, 0, 0.5f, true, true, 0.001f);
        Assert.Empty(r);
        Assert.Equal(6, m.AliveFaceCount); AssertSound(m, closed: true, euler: 2);
    }

    /// <summary>Mirror(자르기 없음)를 닫힌 큐브의 면 평면에서 하면 붙은 안쪽 면이 사라지고 2×1×1 닫힌 상자가 된다.</summary>
    [Fact]
    public void Mirror_AtFacePlane_MergesIntoClosedBox()
    {
        var m = MeshBuilder.Cube();
        MeshOps.MirrorGeometry(m, 0, 0.5f, true, false, 0.001f);
        AssertSound(m, closed: true, euler: 2);
        Assert.Equal(10, m.AliveFaceCount);
    }

    /// <summary>Add Divisions(Linear, U≠V)를 큐브 전체에 적용: 이웃 면의 U/V가 엇갈려도 이음매가 닫혀 있다(전에는 열렸다).</summary>
    [Theory]
    [InlineData(2, 3)]
    [InlineData(3, 1)]
    [InlineData(4, 2)]
    public void AddDivisionsLinear_WholeCube_Closed(int u, int v)
    {
        var m = MeshBuilder.Cube();
        MeshOps.AddDivisionsLinear(m, Faces(m), u, v);
        AssertSound(m, closed: true, euler: 2);
    }

    /// <summary>열린 평면의 경계 정점 삭제: 주변 면이 사라질 때 고립 정점이 남지 않는다.</summary>
    [Fact]
    public void DeleteVertices_BoundaryVertex_NoIsolatedVertices()
    {
        var m = MeshBuilder.Plane(1, 1, 3, 3);
        // 가장자리 중간 정점(차수 3) 하나
        int v = Enumerable.Range(0, m.VertexCount).First(i => { var outs = m.VertexOutgoing(i); return outs.Length == 2; });
        MeshOps.DeleteVertices(m, new[] { v });
        AssertSound(m);
    }

    /// <summary>n각형 하나의 경계 엣지 삭제: 면이 지워지면 정점도 모두 지워진다.</summary>
    [Fact]
    public void DeleteEdges_BorderOfSingleNgon_RemovesAllVertices()
    {
        var m = MeshBuilder.Polygon(Enumerable.Range(0, 6).Select(i => new Vector3(MathF.Cos(i), 0, MathF.Sin(i))).ToArray(), Vector3.UnitY);
        MeshOps.DeleteEdges(m, new[] { 0 });
        Assert.Equal(0, m.AliveVertexCount);
    }

    /// <summary>Triangulate → Quadrangulate는 원래 쿼드로 돌아온다(전에는 합친 쿼드가 다시 합쳐져 토러스 480삼각형이 35개 n각형이 됐다).</summary>
    [Theory]
    [InlineData("torus")]
    [InlineData("cube")]
    [InlineData("plane")]
    public void TriangulateThenQuadrangulate_RestoresQuads(string kind)
    {
        var m = kind switch { "torus" => MeshBuilder.Torus(), "cube" => MeshBuilder.Cube(), _ => MeshBuilder.Plane(1, 1, 4, 4) };
        int faces = m.AliveFaceCount;
        MeshOps.Triangulate(m, Faces(m));
        Assert.Equal(faces * 2, m.AliveFaceCount);
        MeshOps.Quadrangulate(m, Faces(m), 30f);
        Assert.Equal(faces, m.AliveFaceCount);
        Assert.All(Faces(m), f => Assert.Equal(4, m.FaceDegree(f)));
        AssertSound(m);
    }

    /// <summary>큐브 윗면의 대각 두 모서리를 함께 삭제: 첫 정점은 세 면이 합쳐지고 둘째는 합칠 수 없으니 남는다(전에는 면 1개만 남았다).</summary>
    [Fact]
    public void DeleteVertices_TwoDiagonalCubeCorners_KeepsClosedMesh()
    {
        var m = MeshBuilder.Cube();
        var top = Enumerable.Range(0, m.VertexCount).Where(v => m.Verts[v].Position.Y > 0).ToArray();
        int a = top[0];
        int b = top.First(v => v != a && m.FindEdge(a, v) < 0); // 대각 정점
        MeshOps.DeleteVertices(m, new[] { a, b });
        AssertSound(m, closed: true);
        Assert.True(m.AliveFaceCount >= 4, $"faces {m.AliveFaceCount}");
    }

    /// <summary>면 Bridge: 떨어진 두 큐브(Combine)의 마주 보는 면을 이으면 하나의 닫힌 메시(오일러 2)가 된다.</summary>
    [Fact]
    public void BridgeFaces_TwoCombinedCubes_JoinsIntoOneClosedMesh()
    {
        var m = MeshBuilder.Cube();
        MeshOps.Append(m, MeshBuilder.Cube(), System.Numerics.Matrix4x4.CreateTranslation(3, 0, 0));
        MeshNormals.Recompute(m);
        var facing = Faces(m).Where(f => { var c = m.FaceCentroid(f); return MathF.Abs(c.X - 0.5f) < 1e-4f || MathF.Abs(c.X - 2.5f) < 1e-4f; }).ToArray();
        Assert.Equal(2, facing.Length);
        var made = MeshOps.BridgeFaces(m, facing);
        Assert.Equal(4, made.Count);
        AssertSound(m, closed: true, euler: 2);
        Assert.Single(MeshOps.ConnectedComponents(m));
    }

    /// <summary>면 Bridge: 영역이 하나뿐이면 메시를 바꾸지 않는다.</summary>
    [Fact]
    public void BridgeFaces_SingleRegion_NoChange()
    {
        var m = MeshBuilder.Cube();
        var made = MeshOps.BridgeFaces(m, Faces(m).Take(2));
        Assert.Empty(made);
        Assert.Equal(6, m.AliveFaceCount);
    }

    /// <summary>
    /// 성능 회귀: 면을 대량으로 지웠다 다시 만드는 연산이 면마다 전체 하프에지를 훑지 않는다(전에는 Add Divisions 4단계 토러스 107초,
    /// 엣지 32단계 분할 구 5분 이상 = 앱 정지). 넉넉한 상한으로 O(n²) 회귀만 잡는다.
    /// </summary>
    [Fact]
    public void LargeSplitOperations_AreNotQuadratic()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var t = MeshBuilder.Torus();
        MeshOps.AddDivisions(t, Faces(t), 3, MeshOps.DivisionMode.Triangles);
        var s = MeshBuilder.Sphere();
        MeshOps.DivideEdges(s, Enumerable.Range(0, s.EdgeCount).Where(e => s.Edges[e].Alive).ToArray(), 32);
        var c = MeshBuilder.Plane(1, 1, 60, 60);
        MeshOps.DeleteFaces(c, Faces(c).Where(f => f % 2 == 0));
        Assert.True(sw.ElapsedMilliseconds < 20000, $"took {sw.ElapsedMilliseconds} ms");
        AssertSound(s); AssertSound(c);
    }

    /// <summary>Bevel 폭 0은 아무것도 바꾸지 않는다(전에는 넓이 0 면이 생겼다).</summary>
    [Fact]
    public void Bevel_ZeroWidth_NoChange()
    {
        var m = MeshBuilder.Cube();
        var r = MeshOps.Bevel(m, Enumerable.Range(0, m.EdgeCount).ToArray(), new BevelOptions { Width = 0f });
        Assert.Empty(r);
        Assert.Equal(6, m.AliveFaceCount);
    }

    /// <summary>모든 엣지에 심·크리즈, 모든 코너에 UV 핀·노멀 고정을 건 큐브.</summary>
    private static PolyMesh FlaggedCube()
    {
        var m = MeshBuilder.Cube(); MeshNormals.Recompute(m);
        for (int e = 0; e < m.EdgeCount; e++) { var ed = m.Edges[e]; ed.Seam = true; ed.Crease = 2f; m.Edges[e] = ed; }
        for (int h = 0; h < m.Hes.Count; h++) { var he = m.Hes[h]; he.PinUv = true; he.NormalLocked = true; m.Hes[h] = he; }
        return m;
    }

    /// <summary>
    /// 면을 다시 만드는 연산이 엣지의 심·크리즈와 코너의 UV 핀·노멀 고정을 잃지 않는다(전에는 하드 플래그만 옮겨
    /// Reverse/Separate/Extract/Mirror/Combine/Merge/Insert Edge Loop 뒤 UV 심이 사라져 셸이 합쳐졌다).
    /// </summary>
    [Theory]
    [InlineData("reverse")]
    [InlineData("extract")]
    [InlineData("mirror")]
    [InlineData("append")]
    [InlineData("insertLoop")]
    [InlineData("merge")]
    public void RebuildingOps_KeepSeamsCreasesPinsAndLockedNormals(string op)
    {
        var m = FlaggedCube();
        int edgesBefore = m.AliveEdgeCount;
        switch (op)
        {
            case "reverse": MeshOps.ReverseFaces(m, Faces(m)); break;
            case "extract": m = MeshOps.ExtractFaces(m, Faces(m)); break;
            case "mirror": MeshOps.MirrorGeometry(m, 0, 0.5f, true, false, 0.001f); break;
            case "append": { var t = new PolyMesh(); MeshOps.Append(t, m, System.Numerics.Matrix4x4.CreateTranslation(2, 0, 0)); m = t; break; }
            case "insertLoop": MeshOps.InsertEdgeLoop(m, 0, 0.5f); break;
            case "merge": MeshOps.MergeVertices(m, new[] { 0, 1 }, 2f); break;
        }
        AssertSound(m);
        // 원래 있던 종류의 엣지(새로 만든 분할/절단 엣지 제외)는 심·크리즈를 유지
        int seams = Enumerable.Range(0, m.EdgeCount).Count(e => m.Edges[e].Alive && m.Edges[e].Seam && m.Edges[e].Crease == 2f);
        int expected = op switch { "mirror" => 2 * edgesBefore - 4 - 4, "insertLoop" => edgesBefore + 4, "merge" => m.AliveEdgeCount, _ => edgesBefore };
        Assert.True(seams >= expected, $"{op}: seam+crease edges {seams} < {expected}");
        if (op is "reverse" or "extract" or "append")
            Assert.All(Enumerable.Range(0, m.Hes.Count).Where(h => m.Hes[h].Alive), h => { Assert.True(m.Hes[h].PinUv, "pin"); Assert.True(m.Hes[h].NormalLocked, "locked"); });
    }

    /// <summary>Combine 결과는 첫 원본의 머티리얼을 이어받는다(전에는 lambert1로 바뀌었다).</summary>
    [Fact]
    public void Combine_KeepsFirstSourceMaterial()
    {
        var doc = new Cube.Core.Scene.Document();
        var a = Cube.Core.Commands.CreatePrimitiveCommand.Cube(doc); doc.Undo.Push(a);
        var b = Cube.Core.Commands.CreatePrimitiveCommand.Sphere(doc); doc.Undo.Push(b);
        a.Node.MaterialId = 7; b.Node.MaterialId = 3;
        var cmd = new Cube.Core.Commands.CombineCommand(new[] { a.Node.Id, b.Node.Id });
        doc.Undo.Push(cmd);
        Assert.Equal(7, cmd.Result!.MaterialId);
    }

    /// <summary>Merge Vertices가 격자로 이웃만 비교한다: 모든 면을 떼어 낸 100×100 평면(4만 정점)을 다시 병합(전에는 O(n²)).</summary>
    [Fact]
    public void MergeVertices_LargeMesh_IsFastAndRestoresGrid()
    {
        var m = MeshBuilder.Plane(1, 1, 100, 100);
        MeshOps.DetachVertices(m, Enumerable.Range(0, m.VertexCount).ToArray());
        Assert.Equal(40000, m.AliveVertexCount);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        MeshOps.MergeVertices(m, Enumerable.Range(0, m.VertexCount).Where(v => m.Verts[v].Alive), 0.001f);
        Assert.True(sw.ElapsedMilliseconds < 10000, $"took {sw.ElapsedMilliseconds} ms");
        Assert.Equal(101 * 101, m.AliveVertexCount);
        AssertSound(m);
    }

    /// <summary>맞붙은 두 큐브(병합 없는 Mirror)를 Merge Vertices로 합치면 접촉면 두 장이 사라진 닫힌 상자가 된다(전에는 면 5장이 사라져 구멍).</summary>
    [Fact]
    public void MergeVertices_TouchingBoxes_DropsInnerLaminaAndStaysClosed()
    {
        var m = MeshBuilder.Cube();
        MeshOps.MirrorGeometry(m, 0, 0.5f, true, false, 0f);
        Assert.Equal(12, m.AliveFaceCount);
        MeshOps.MergeVertices(m, Enumerable.Range(0, m.VertexCount).Where(v => m.Verts[v].Alive), 0.001f);
        Assert.Equal(10, m.AliveFaceCount);
        AssertSound(m, closed: true, euler: 2);
    }
}
