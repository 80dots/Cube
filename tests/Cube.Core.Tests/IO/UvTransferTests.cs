using System.Numerics;
using Cube.Core.IO;
using Cube.Core.Mesh;
using Cube.Core.Scene;

namespace Cube.Core.Tests.IO;

public class UvTransferTests
{
    private static void SetFaceUvsInOrder(PolyMesh m, int f, params Vector2[] uvs)
    {
        var loop = new List<int>();
        int n = m.GetFaceHalfEdges(f, loop);
        Assert.Equal(n, uvs.Length);
        for (int i = 0; i < n; i++) { var h = m.Hes[loop[i]]; h.Uv0 = uvs[i]; m.Hes[loop[i]] = h; }
    }

    [Fact]
    public void Cube_ObjRoundTrip_ModifyUvs_ApplyByFaceOrder_CopiesUvsAndMarksSeams()
    {
        var target = MeshBuilder.Cube();
        var node = new SceneNode { Name = "pCube1", Shape = new MeshShape(target), Local = new Transform3(new Vector3(3, 0, 0), new Vector3(0, 30, 0), new Vector3(2, 2, 2)) };
        // 외부 툴 왕복: 월드 베이크된 OBJ → 읽기
        string text = ObjFormat.WriteToString(new[] { node }, worldSpace: true);
        var source = ObjFormat.ReadFromString(text)[0].Mesh;
        Assert.Empty(MeshValidator.Check(source));
        Assert.Equal(target.AliveFaceCount, source.AliveFaceCount);

        // 외부 툴에서 UV 편집: 면 0(+Z: v0,v1,v2,v3)과 면 2(+X: v1,v5,v6,v2)를 공유 엣지 v1-v2 에서 연속으로 붙인다
        SetFaceUvsInOrder(source, 0, new(0, 0), new(1, 0), new(1, 1), new(0, 1));
        SetFaceUvsInOrder(source, 2, new(1, 0), new(2, 0), new(2, 1), new(1, 1));
        // 면 4(+Y)는 0.25 만큼 평행이동해 다른 면과 모두 불연속
        SetFaceUvsInOrder(source, 4, new(0.25f, 0.25f), new(1.25f, 0.25f), new(1.25f, 1.25f), new(0.25f, 1.25f));

        int transferred = UvTransfer.ApplyByFaceOrder(target, source, out int skipped);
        Assert.Equal(6, transferred);
        Assert.Equal(0, skipped);

        // UV가 면 순서/코너 순서대로 복사됨
        var tl = new List<int>(); var sl = new List<int>();
        for (int f = 0; f < 6; f++)
        {
            target.GetFaceHalfEdges(f, tl); source.GetFaceHalfEdges(f, sl);
            for (int i = 0; i < 4; i++) Assert.Equal(source.Hes[sl[i]].Uv0, target.Hes[tl[i]].Uv0);
        }

        // 심: v1-v2 는 연속 → false, 나머지 모든 엣지는 불연속 → true
        int shared = target.FindEdge(1, 2);
        Assert.True(shared >= 0);
        Assert.False(target.Edges[shared].Seam);
        int seams = 0;
        for (int e = 0; e < target.EdgeCount; e++)
        {
            if (!target.Edges[e].Alive) continue;
            if (e == shared) continue;
            Assert.True(target.Edges[e].Seam, $"edge {e} should be a seam");
            seams++;
        }
        Assert.Equal(11, seams);
        Assert.Empty(MeshValidator.Check(target));
    }

    [Fact]
    public void ApplyByFaceOrder_SkipsFacesWithDifferentCornerCount_AndClearsBoundarySeams()
    {
        // 대상: 쿼드 둘(평면 2x1), 소스: 첫 면이 삼각형이라 건너뜀
        var target = MeshBuilder.Plane(2, 1, 2, 1);
        Assert.Equal(2, target.AliveFaceCount);
        MeshBuilder.SetAllEdgesSeam(target, true); // 전부 심으로 시작 → 전송 후 경계는 해제되어야 함

        var source = new PolyMesh();
        int a = source.AddVertex(new(0, 0, 0)), b = source.AddVertex(new(1, 0, 0)), c = source.AddVertex(new(1, 1, 0)), d = source.AddVertex(new(0, 1, 0));
        int e = source.AddVertex(new(2, 0, 0)), f = source.AddVertex(new(2, 1, 0));
        source.AddFace(new[] { a, b, d });           // 삼각형 → 대상 쿼드와 불일치
        source.AddFace(new[] { b, e, f, c });        // 쿼드
        SetFaceUvsInOrder(source, 1, new(0.5f, 0.5f), new(0.9f, 0.5f), new(0.9f, 0.9f), new(0.5f, 0.9f));

        int transferred = UvTransfer.ApplyByFaceOrder(target, source, out int skipped);
        Assert.Equal(1, transferred);
        Assert.Equal(1, skipped);

        // 두 번째 대상 면의 UV가 소스 두 번째 면과 같음
        var tl = new List<int>(); var sl = new List<int>();
        target.GetFaceHalfEdges(1, tl); source.GetFaceHalfEdges(1, sl);
        for (int i = 0; i < 4; i++) Assert.Equal(source.Hes[sl[i]].Uv0, target.Hes[tl[i]].Uv0);

        // 경계 엣지는 심이 꺼지고, 두 면 사이 내부 엣지는 UV 불연속이라 심
        for (int ed = 0; ed < target.EdgeCount; ed++)
        {
            if (!target.Edges[ed].Alive) continue;
            if (target.IsBoundaryEdge(ed)) Assert.False(target.Edges[ed].Seam);
            else Assert.True(target.Edges[ed].Seam);
        }
    }

    [Fact]
    public void ApplyByFaceOrder_SourceWithFewerFaces_CountsMissingAsSkipped()
    {
        var target = MeshBuilder.Cube();
        var source = MeshBuilder.Plane(1, 1, 1, 1);
        int transferred = UvTransfer.ApplyByFaceOrder(target, source, out int skipped);
        Assert.Equal(1, transferred);
        Assert.Equal(5, skipped);
    }
}
