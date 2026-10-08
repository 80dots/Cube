using System.Numerics;
using Cube.Core.Mesh;

namespace Cube.Core.Tests.Mesh;

/// <summary>Blender식 Extrude(ExtrudeOptions) 검사.</summary>
public class MeshOpsExtrudeOptionsTests
{
    /// <summary>오일러 특성 V - E + F(닫힌 구 위상 = 2).</summary>
    private static int Euler(PolyMesh m) => m.AliveVertexCount - m.AliveEdgeCount + m.AliveFaceCount;
    /// <summary>살아 있는 엣지 중 경계 엣지가 하나도 없는지(구멍 없이 닫혔는지).</summary>
    private static bool Closed(PolyMesh m) => Enumerable.Range(0, m.EdgeCount).All(e => !m.Edges[e].Alive || !m.IsBoundaryEdge(e));
    /// <summary>법선이 위(+Y)를 향하는 첫 번째 살아 있는 면(큐브 윗면)을 찾는다.</summary>
    private static int TopFace(PolyMesh m) => Enumerable.Range(0, m.FaceCount).First(f => m.Faces[f].Alive && MeshNormals.FaceNormalUnnormalized(m, f).Y > 0.5f);

    /// <summary>
    /// Region 모드로 큐브 윗면을 Offset 0.5만큼 돌출하면 캡 1개, 면 10개(6 + 옆면 4)가 되고 캡 정점이 Y=1에 놓여야 한다.
    /// </summary>
    [Fact]
    public void Region_CubeTopFace_OffsetAlongNormal()
    {
        var m = MeshBuilder.Cube();
        var caps = MeshOps.Extrude(m, new[] { TopFace(m) }, new ExtrudeOptions { Offset = 0.5f });
        Assert.Single(caps);
        Assert.Empty(MeshValidator.Check(m));
        Assert.Equal(2, Euler(m));
        Assert.Equal(10, m.AliveFaceCount);
        var ids = new List<int>(); m.GetFaceVertices(caps[0], ids);
        Assert.All(ids, v => Assert.Equal(1f, m.Verts[v].Position.Y, 4));
    }

    /// <summary>
    /// 경계가 모두 메시 테두리인 열린 판을 Extrude하면 원래 면을 뒤집어 바닥으로 남기므로 닫힌 직육면체(면 6, 정점 8, 오일러 2)가 되어야 한다.
    /// </summary>
    [Fact]
    public void Region_OpenPlane_BecomesClosedBox()
    {
        // 열린 판(모든 경계가 메시 테두리): 원래 면을 뒤집어 남겨 직육면체가 된다
        var m = MeshBuilder.Plane(1, 1, 1, 1);
        var faces = Enumerable.Range(0, m.FaceCount).Where(f => m.Faces[f].Alive).ToList();
        var caps = MeshOps.Extrude(m, faces, new ExtrudeOptions { Offset = 1f });
        Assert.Single(caps);
        Assert.Empty(MeshValidator.Check(m));
        Assert.True(Closed(m));
        Assert.Equal(6, m.AliveFaceCount);
        Assert.Equal(8, m.AliveVertexCount);
        Assert.Equal(2, Euler(m));
    }

    /// <summary>
    /// 경계가 없는 닫힌 볼륨(큐브 전체 면)을 Extrude하면 옆면을 만들 곳이 없으므로 껍질만 복제한다:
    /// 면 12, 정점 16, 연결 요소 2개가 되어야 한다.
    /// </summary>
    [Fact]
    public void Region_ClosedVolume_DuplicatesShell()
    {
        var m = MeshBuilder.Cube();
        var all = Enumerable.Range(0, m.FaceCount).Where(f => m.Faces[f].Alive).ToList();
        var caps = MeshOps.Extrude(m, all, new ExtrudeOptions { Offset = 0f });
        Assert.Equal(6, caps.Count);
        Assert.Equal(12, m.AliveFaceCount);
        Assert.Equal(16, m.AliveVertexCount);
        Assert.Empty(MeshValidator.Check(m));
        Assert.Equal(2, MeshOps.ConnectedComponents(m).Count);
    }

    /// <summary>
    /// IndividualFaces 모드로 인접한 윗면·옆면을 돌출하면 공유 엣지에도 각자 옆면이 생겨 면 14개가 되고,
    /// 각 캡은 자기 법선 방향으로 0.25 이동해 평면 거리 0.75에 놓여야 한다.
    /// </summary>
    [Fact]
    public void Individual_TwoAdjacentFaces_EachAlongOwnNormal()
    {
        var m = MeshBuilder.Cube();
        var top = TopFace(m);
        int side = Enumerable.Range(0, m.FaceCount).First(f => m.Faces[f].Alive && MeshNormals.FaceNormalUnnormalized(m, f).X > 0.5f);
        var caps = MeshOps.Extrude(m, new[] { top, side }, new ExtrudeOptions { Type = ExtrudeType.IndividualFaces, Offset = 0.25f });
        Assert.Equal(2, caps.Count);
        Assert.Empty(MeshValidator.Check(m));
        Assert.Equal(2, Euler(m));
        Assert.True(Closed(m));
        // 6 + 옆면 4 + 4 (공유 엣지에도 각자 옆면이 생긴다)
        Assert.Equal(14, m.AliveFaceCount);
        var ids = new List<int>();
        foreach (int f in caps)
        {
            var n = Vector3.Normalize(MeshNormals.FaceNormalUnnormalized(m, f));
            m.GetFaceVertices(f, ids);
            foreach (int v in ids) Assert.Equal(0.75f, Vector3.Dot(m.Verts[v].Position, n), 4);
        }
    }

    /// <summary>
    /// Region + FaceNormals 방향: 인접 두 면을 함께 돌출해도 마이터 보정으로 각 캡이 자기 법선 방향 평면 0.75에 정확히 놓여야 한다.
    /// </summary>
    [Fact]
    public void Region_TwoAdjacentFaces_FaceNormalsKeepsShape()
    {
        var m = MeshBuilder.Cube();
        var top = TopFace(m);
        int side = Enumerable.Range(0, m.FaceCount).First(f => m.Faces[f].Alive && MeshNormals.FaceNormalUnnormalized(m, f).X > 0.5f);
        var caps = MeshOps.Extrude(m, new[] { top, side }, new ExtrudeOptions { Offset = 0.25f, Direction = ExtrudeDirection.FaceNormals });
        Assert.Equal(2, caps.Count);
        Assert.Empty(MeshValidator.Check(m));
        Assert.Equal(2, Euler(m));
        var ids = new List<int>();
        foreach (int f in caps)
        {
            var n = Vector3.Normalize(MeshNormals.FaceNormalUnnormalized(m, f));
            m.GetFaceVertices(f, ids);
            foreach (int v in ids) Assert.Equal(0.75f, Vector3.Dot(m.Verts[v].Position, n), 3);
        }
    }

    /// <summary>
    /// 방향을 월드 축(X 또는 Z)으로 지정하면 윗면 캡이 그 축으로만 0.3 이동하고 높이(Y=0.5)는 그대로여야 한다.
    /// </summary>
    /// <param name="dir">돌출 방향 옵션.</param>
    /// <param name="axis">검사할 좌표 성분(0 = X, 2 = Z).</param>
    [Theory]
    [InlineData(ExtrudeDirection.X, 0)]
    [InlineData(ExtrudeDirection.Z, 2)]
    public void Region_AxisDirection(ExtrudeDirection dir, int axis)
    {
        var m = MeshBuilder.Cube();
        var caps = MeshOps.Extrude(m, new[] { TopFace(m) }, new ExtrudeOptions { Offset = 0.3f, Direction = dir });
        Assert.Empty(MeshValidator.Check(m));
        var ids = new List<int>(); m.GetFaceVertices(caps[0], ids);
        foreach (int v in ids)
        {
            var p = m.Verts[v].Position;
            Assert.Equal(0.5f, p.Y, 4);
            float c = axis == 0 ? p.X : p.Z;
            Assert.True(MathF.Abs(MathF.Abs(c - 0.3f) - 0.5f) < 1e-4f);
        }
    }

    /// <summary>
    /// Steps = 4(Extrude Repeat)면 0.2씩 네 번 쌓여 옆면이 4단(16면) 생기고 최종 캡이 Y = 0.5 + 0.8 = 1.3에 있어야 한다.
    /// </summary>
    [Fact]
    public void Repeat_StepsStackSegments()
    {
        var m = MeshBuilder.Cube();
        var caps = MeshOps.Extrude(m, new[] { TopFace(m) }, new ExtrudeOptions { Offset = 0.2f, Steps = 4 });
        Assert.Single(caps);
        Assert.Empty(MeshValidator.Check(m));
        Assert.Equal(2, Euler(m));
        Assert.Equal(6 + 4 * 4, m.AliveFaceCount);
        var ids = new List<int>(); m.GetFaceVertices(caps[0], ids);
        Assert.All(ids, v => Assert.Equal(1.3f, m.Verts[v].Position.Y, 4));
    }

    /// <summary>FlipNormals 옵션이면 결과 연결 요소 전체가 뒤집혀 윗면 캡의 법선이 아래(-Y)를 향해야 한다.</summary>
    [Fact]
    public void FlipNormals_InvertsResult()
    {
        var m = MeshBuilder.Cube();
        var caps = MeshOps.Extrude(m, new[] { TopFace(m) }, new ExtrudeOptions { Offset = 0.5f, FlipNormals = true });
        Assert.Empty(MeshValidator.Check(m));
        Assert.True(MeshNormals.FaceNormalUnnormalized(m, caps[0]).Y < -0.5f);
    }

    /// <summary>
    /// 평면의 경계 엣지를 Offset 0.5·Steps 2로 Extrude하면 면 2개와 새 바깥 엣지 1개가 생기고,
    /// 새 엣지는 면 평면(Y=0) 안에서 바깥쪽으로 정확히 1만큼 떨어져 있어야 한다.
    /// </summary>
    [Fact]
    public void Edges_BorderOfPlane_OutwardAndRepeat()
    {
        var m = MeshBuilder.Plane(1, 1, 1, 1);
        int e = Enumerable.Range(0, m.EdgeCount).First(i => m.Edges[i].Alive && m.IsBoundaryEdge(i));
        var (a, b) = m.EdgeVertices(e);
        var mid = (m.Verts[a].Position + m.Verts[b].Position) * 0.5f;
        var faces = MeshOps.ExtrudeEdges(m, new[] { e }, new ExtrudeOptions { Offset = 0.5f, Steps = 2 }, out var newEdges);
        Assert.Equal(2, faces.Count);
        Assert.Single(newEdges);
        Assert.Empty(MeshValidator.Check(m));
        var (x, y) = m.EdgeVertices(newEdges[0]);
        var nmid = (m.Verts[x].Position + m.Verts[y].Position) * 0.5f;
        // 면 평면(y=0) 안에서 바깥쪽으로 1만큼
        Assert.Equal(0f, nmid.Y, 4);
        Assert.Equal(1f, Vector3.Distance(mid, nmid), 4);
        Assert.True(nmid.Length() > mid.Length());
    }
}
