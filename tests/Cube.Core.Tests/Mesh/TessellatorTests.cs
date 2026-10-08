using System.Numerics;
using Cube.Core.Mesh;

namespace Cube.Core.Tests.Mesh;

/// <summary>
/// <c>MeshTessellator</c>(PolyMesh → 렌더용 <c>RenderMeshData</c>: 코너 언롤 삼각형·선·점·면 중심 + 역매핑)를 검증한다.
/// 삼각형 수와 감김 방향, n각형/오목 다각형 삼각분할, 위치만 갱신하는 빠른 경로(UpdatePositions)를 다룬다.
/// </summary>
public class TessellatorTests
{
    /// <summary>
    /// 큐브는 코너 24개(면마다 4개, 하드 엣지라 공유 안 함)·삼각형 12·선 12·점 8·면 중심 6을 만들어야 하고,
    /// 각 삼각형의 기하 법선이 <c>TriToFace</c>로 매핑된 원래 면 법선과 같은 방향(CCW 감김 유지)이어야 한다.
    /// </summary>
    [Fact]
    public void Cube_Produces12TrianglesAnd24Corners()
    {
        var m = MeshBuilder.Cube();
        var r = MeshTessellator.Build(m);
        Assert.Equal(24, r.CornerCount);
        Assert.Equal(12, r.TriangleCount);
        Assert.Equal(12, r.LineCount);
        Assert.Equal(8, r.PointCount);
        Assert.Equal(6, r.FaceCenterCount);
        for (int t = 0; t < r.TriangleCount; t++)
        {
            int f = r.TriToFace[t];
            var a = r.Positions[r.Indices[t * 3]]; var b = r.Positions[r.Indices[t * 3 + 1]]; var c = r.Positions[r.Indices[t * 3 + 2]];
            var n = Vector3.Normalize(Vector3.Cross(b - a, c - a));
            Assert.True(Vector3.Dot(n, m.Faces[f].Normal) > 0.99f, $"tri {t} winding mismatch");
        }
    }

    /// <summary>8각 원기둥의 n각형 캡이 (n-2)개 삼각형으로 완전히 분할되어 총 28개가 되는지 확인한다.</summary>
    [Fact]
    public void Cylinder_NgonCaps_TriangulateFully()
    {
        var m = MeshBuilder.Cylinder(segments: 8);
        var r = MeshTessellator.Build(m);
        // 옆면 8쿼드 = 16 tri, 캡 2개 × (8-2) = 12 tri
        Assert.Equal(28, r.TriangleCount);
    }

    /// <summary>
    /// 오목한 L자 6각형 면은 단순 팬 분할로는 바깥으로 삐져나가므로 이어 클리핑(Ear Clipping)을 써야 한다.
    /// 삼각형 4개가 모두 +Y를 향하고(뒤집힘 없음) 넓이 합이 원래 다각형 넓이 3과 같은지로 확인한다.
    /// </summary>
    [Fact]
    public void ConcaveNgon_UsesEarClipping()
    {
        // L자형 6각형 (XZ 평면, 반시계 from +Y)
        var m = new PolyMesh();
        int[] v =
        {
            m.AddVertex(new(0, 0, 0)), m.AddVertex(new(2, 0, 0)), m.AddVertex(new(2, 0, -1)),
            m.AddVertex(new(1, 0, -1)), m.AddVertex(new(1, 0, -2)), m.AddVertex(new(0, 0, -2)),
        };
        int f = m.AddFace(v);
        Assert.True(f >= 0);
        MeshNormals.Recompute(m);
        Assert.True(Vector3.Dot(m.Faces[f].Normal, Vector3.UnitY) > 0.99f);
        var r = MeshTessellator.Build(m);
        Assert.Equal(4, r.TriangleCount);
        // 모든 삼각형이 면 노멀과 같은 방향이고 면적 합이 3
        float area = 0;
        for (int t = 0; t < r.TriangleCount; t++)
        {
            var a = r.Positions[r.Indices[t * 3]]; var b = r.Positions[r.Indices[t * 3 + 1]]; var c = r.Positions[r.Indices[t * 3 + 2]];
            var cr = Vector3.Cross(b - a, c - a);
            Assert.True(cr.Y > 0, $"tri {t} flipped");
            area += cr.Length() / 2;
        }
        Assert.True(MathF.Abs(area - 3f) < 1e-4f, $"area {area}");
    }

    /// <summary>
    /// 정점 0을 옮기고 <c>UpdatePositions</c>만 호출해도 그 정점을 쓰는 코너 3개, 점 위치, 선분 끝점 3개,
    /// 바운드(최대/최소)까지 모두 따라가는지 확인한다(위상 변경 없는 드래그 프리뷰용 빠른 경로).
    /// </summary>
    [Fact]
    public void UpdatePositions_TracksVertexMoves()
    {
        var m = MeshBuilder.Cube();
        var r = MeshTessellator.Build(m);
        var v = m.Verts[0]; v.Position = new Vector3(5, 5, 5); m.Verts[0] = v; m.BumpGeometry();
        MeshTessellator.UpdatePositions(m, r);
        int hits = 0;
        for (int i = 0; i < r.CornerCount; i++) if (r.Positions[i] == new Vector3(5, 5, 5)) hits++;
        Assert.Equal(3, hits); // 정점 0은 3개 면의 코너
        Assert.Contains(new Vector3(5, 5, 5), r.PointPositions.Take(r.PointCount));
        // 바운드·선분·면 중심도 같이 따라간다
        Assert.Equal(new Vector3(5, 5, 5), r.BoundsMax);
        Assert.Equal(new Vector3(-0.5f, -0.5f, -0.5f), r.BoundsMin);
        int lineHits = 0; for (int i = 0; i < r.LineVertexCount; i++) if (r.LinePositions[i] == new Vector3(5, 5, 5)) lineHits++;
        Assert.Equal(3, lineHits); // 정점 0에 엣지 3개
    }

    /// <summary>
    /// 스킨 변형 표시 경로: 변형된 정점 배열을 넘긴 <c>UpdatePositions</c> 결과가, 그 위치로 메시를 고쳐 전체 재구성한 결과와
    /// 코너·선·점·면 중심·바운드 모두 같아야 하고 PositionVersion이 증가해야 한다. null을 넘기면 원래 메시 위치로 돌아와야 한다.
    /// </summary>
    [Fact]
    public void UpdatePositions_WithDeformedArray_MatchesFullRebuild()
    {
        // 스킨 변형 경로: 정점 배열을 넘기면 코너/선분/점/면 중심/바운드가 그 배열로 다시 만든 결과와 같아야 한다
        var m = MeshBuilder.Cylinder(segments: 8);
        var r = MeshTessellator.Build(m);
        int v0 = r.PositionVersion;
        var deformed = new Vector3[m.VertexCount];
        for (int v = 0; v < m.VertexCount; v++) deformed[v] = m.Verts[v].Position * new Vector3(2f, 0.5f, 1f) + new Vector3(0.1f * v, 0, 0);
        MeshTessellator.UpdatePositions(m, r, deformed);
        Assert.True(r.PositionVersion > v0);

        // 기대값: 복제 메시에 변형 위치를 직접 써서 처음부터 다시 테셀레이션한 결과.
        var expected = m.Clone();
        for (int v = 0; v < expected.VertexCount; v++) { var vert = expected.Verts[v]; vert.Position = deformed[v]; expected.Verts[v] = vert; }
        var r2 = MeshTessellator.Build(expected);
        Assert.Equal(r2.CornerCount, r.CornerCount);
        for (int i = 0; i < r.CornerCount; i++) Assert.Equal(r2.Positions[i], r.Positions[i]);
        for (int i = 0; i < r.LineVertexCount; i++) Assert.Equal(r2.LinePositions[i], r.LinePositions[i]);
        for (int i = 0; i < r.PointCount; i++) Assert.Equal(r2.PointPositions[i], r.PointPositions[i]);
        for (int i = 0; i < r.FaceCenterCount; i++) Assert.True(Vector3.Distance(r2.FaceCenters[i], r.FaceCenters[i]) < 1e-5f);
        Assert.Equal(r2.BoundsMin, r.BoundsMin); Assert.Equal(r2.BoundsMax, r.BoundsMax);
        // null을 넘기면 메시 위치로 돌아온다
        MeshTessellator.UpdatePositions(m, r, null);
        var r3 = MeshTessellator.Build(m);
        for (int i = 0; i < r.CornerCount; i++) Assert.Equal(r3.Positions[i], r.Positions[i]);
    }
}
