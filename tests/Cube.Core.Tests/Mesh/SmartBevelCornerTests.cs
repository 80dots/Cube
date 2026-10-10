using System.Numerics;
using Cube.Core.Mesh;

namespace Cube.Core.Tests.Mesh;

/// <summary>Smart Bevel D(셋백 코너 패치)·E(비대칭 폭, 사용자 프로파일 점).</summary>
public class SmartBevelCornerTests
{
    private static int Euler(PolyMesh m) => m.AliveVertexCount - m.AliveEdgeCount + m.AliveFaceCount;
    private static void AssertSolid(PolyMesh m) { Assert.Empty(MeshValidator.Check(m)); Assert.True(MeshOps.IsClosed(m)); Assert.Equal(2, Euler(m)); }

    /// <summary>D: 큐브 전체 베벨(세그먼트 짝수/홀수)에서 코너가 쿼드 그리드(+홀수면 가운데 삼각형)로 채워지고 닫힌 유효 메시다.</summary>
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(3)]
    [InlineData(5)]
    public void CubeCorners_SetbackPatch_Closed(int segments)
    {
        var m = MeshBuilder.Cube();
        var faces = MeshOps.Bevel(m, Enumerable.Range(0, 12), new BevelOptions { Width = 0.2f, Segments = segments, Intersection = BevelIntersection.GridFill });
        AssertSolid(m);
        // 코너 8개 × 패치 면 수: 짝수 s = 3·(s/2)² 쿼드, 홀수 s = 3·m² + 3·m 쿼드 + 삼각형 1 (m = (s−1)/2)
        int perCorner = segments % 2 == 0 ? 3 * (segments / 2) * (segments / 2) : 3 * ((segments - 1) / 2) * ((segments - 1) / 2) + 3 * ((segments - 1) / 2) + 1;
        int strips = 12 * segments, caps = 8 * perCorner;
        Assert.Equal(6 + strips + caps, m.AliveFaceCount);
        // 홀수 s: 가운데 삼각형(3각형) 존재, 나머지 캡은 쿼드
        int tris = Enumerable.Range(0, m.FaceCount).Count(f => m.Faces[f].Alive && m.FaceDegree(f) == 3);
        Assert.Equal(segments % 2 == 0 ? 0 : 8, tris);
    }

    /// <summary>D: 원형 프로파일(shape 0.5)의 큐브 코너 패치 점들은 모서리 안쪽 구(반지름 = 폭)에 놓인다.</summary>
    [Fact]
    public void CubeCorner_PatchPointsLieOnSphere()
    {
        var m = MeshBuilder.Cube();
        float w = 0.3f;
        MeshOps.Bevel(m, Enumerable.Range(0, 12), new BevelOptions { Width = w, Segments = 6, Shape = 0.5f, Intersection = BevelIntersection.GridFill });
        AssertSolid(m);
        // (+,+,+) 코너: 구 중심 (0.5−w)·(1,1,1). 그 근처(모든 좌표 > 0.5−w)의 정점은 구 위
        var c = new Vector3(0.5f - w);
        int checkedPts = 0;
        for (int v = 0; v < m.VertexCount; v++)
        {
            if (!m.Verts[v].Alive) continue;
            var p = m.Verts[v].Position;
            if (p.X <= c.X + 1e-4f || p.Y <= c.Y + 1e-4f || p.Z <= c.Z + 1e-4f) continue;
            Assert.True(MathF.Abs(Vector3.Distance(p, c) - w) < 0.02f, $"v{v} {p} dist {Vector3.Distance(p, c)}");
            checkedPts++;
        }
        Assert.True(checkedPts >= 19, $"checked {checkedPts}"); // 가운데 1 + 안쪽 선 3×2 + 부채꼴 안쪽 3×4 = 19(둘레 프로파일 점은 x = 0.5−w 평면이라 제외됨)
    }

    /// <summary>D: 4·5·6개 엣지가 모이는 정점(피라미드 꼭짓점 + 바닥)도 패치가 닫힌 유효 메시를 만든다.</summary>
    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void HighValenceApex_Closed(int sides)
    {
        var m = MeshBuilder.Cone(0.5f, 1f, sides, cap: true);
        var all = Enumerable.Range(0, m.EdgeCount).Where(e => m.Edges[e].Alive).ToList();
        MeshOps.Bevel(m, all, new BevelOptions { Width = 0.08f, Segments = 4, Intersection = BevelIntersection.GridFill });
        AssertSolid(m);
    }

    /// <summary>E: 비대칭 폭 — 윗면(A) 쪽 오프셋은 Width, 옆면(B) 쪽은 Width×SideRatio.</summary>
    [Fact]
    public void SideRatio_AsymmetricOffsets()
    {
        var m = MeshBuilder.Cube();
        int top = Enumerable.Range(0, m.FaceCount).First(f => MeshNormals.FaceNormalUnnormalized(m, f).Y > 0.5f);
        var hes = new List<int>(); m.GetFaceHalfEdges(top, hes);
        int e = m.Hes[hes[0]].Edge;
        var (a, b) = m.EdgeVertices(e);
        var strip = MeshOps.Bevel(m, new[] { e }, new BevelOptions { Width = 0.1f, Segments = 1, SideRatio = 3f });
        Assert.Single(strip);
        Assert.Empty(MeshValidator.Check(m));
        var vs = new List<int>(); m.GetFaceVertices(strip[0], vs);
        // 엣지에 수직인 수평축 perp: 윗면 쪽 띠 가장자리는 perp 방향으로 0.1 안쪽(|p·perp| = 0.4), 옆면 쪽은 y가 0.3 내려감
        var dir = Vector3.Normalize(m.Verts[b].Position - m.Verts[a].Position);
        var perp = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, dir));
        float topInset = vs.Select(v => m.Verts[v].Position).Where(p => MathF.Abs(p.Y - 0.5f) < 1e-4f).Select(p => 0.5f - MathF.Abs(Vector3.Dot(p, perp))).First();
        float sideDrop = vs.Select(v => m.Verts[v].Position).Where(p => p.Y < 0.5f - 1e-4f).Select(p => 0.5f - p.Y).First();
        Assert.True(MathF.Abs(topInset - 0.1f) < 1e-3f, $"top inset {topInset}");
        Assert.True(MathF.Abs(sideDrop - 0.3f) < 1e-3f, $"side drop {sideDrop}");
    }

    /// <summary>E: 사용자 프로파일 점 — 가운데 점 (1,1)(모서리 K)을 주면 세그먼트 2 프로파일의 중간 정점이 원래 모서리 위치에 놓인다.</summary>
    [Fact]
    public void UserPoints_ProfilePassesThroughControlPoint()
    {
        var m = MeshBuilder.Cube();
        int top = Enumerable.Range(0, m.FaceCount).First(f => MeshNormals.FaceNormalUnnormalized(m, f).Y > 0.5f);
        var hes = new List<int>(); m.GetFaceHalfEdges(top, hes);
        int e = m.Hes[hes[0]].Edge;
        var (a, b) = m.EdgeVertices(e);
        var pa = m.Verts[a].Position;
        var faces = MeshOps.Bevel(m, new[] { e }, new BevelOptions { Width = 0.2f, Segments = 2, ProfileType = BevelProfileType.Custom, Preset = BevelProfilePreset.UserPoints, CustomPoints = new[] { new Vector2(1, 1) } });
        Assert.Equal(2, faces.Count);
        Assert.Empty(MeshValidator.Check(m));
        // 두 띠가 공유하는 중간 정점 중 하나는 원래 모서리 선 위(y = 0.5, |x| 또는 |z| = 0.5)에 있어야 한다
        var shared = new List<int>(); m.GetFaceVertices(faces[0], shared);
        var other = new List<int>(); m.GetFaceVertices(faces[1], other);
        var mid = shared.Intersect(other).Select(v => m.Verts[v].Position).ToList();
        Assert.True(mid.Count >= 2);
        Assert.All(mid, p => Assert.True(MathF.Abs(p.Y - 0.5f) < 1e-3f && MathF.Abs(MathF.Max(MathF.Abs(p.X), MathF.Abs(p.Z)) - 0.5f) < 1e-3f, $"mid {p}"));
    }
}
