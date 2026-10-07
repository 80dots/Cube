using Cube.Core.Mesh;
using Cube.Core.Uv;

namespace Cube.Core.IO;

/// <summary>
/// 외부 UV 툴(RizomUV 등) 왕복: 대상 메시를 OBJ로 내보내 UV만 편집한 뒤 다시 읽은 메시에서 코너 UV를 면 순서대로 가져온다.
/// </summary>
public static class UvTransfer
{
    /// <summary>
    /// 살아 있는 면을 순서대로 1:1 대응시켜 코너 UV를 복사한다.
    /// <para>
    /// 가정: <paramref name="source"/>는 <paramref name="target"/>을 <see cref="ObjFormat.Write"/>로 쓴 뒤 <see cref="ObjFormat.Read"/>로 읽은 것이므로
    /// i번째 살아 있는 면이 서로 같고, 코너 수와 시작 정점·순서도 같다(OBJ writer는 <c>Faces[f].HalfEdge</c>부터 루프 순서로 쓰고,
    /// reader의 <c>AddFace</c>는 첫 코너를 <c>Faces[f].HalfEdge</c>로 둔다). RizomUV 등 UV 툴은 면 순서와 코너 순서를 보존한다.
    /// 소스 정점은 월드 공간으로 베이크되어 있고 대상은 로컬이므로 위치로 코너 회전을 맞추지는 않는다.
    /// 코너 수가 다른 면(또는 소스에 대응 면이 없는 면)은 건너뛰고 <paramref name="skippedFaces"/>에 센다.
    /// </para>
    /// 복사 후 모든 내부 엣지에 대해 양쪽 코너 UV가 불연속이면 <see cref="Edge.Seam"/>을 켜고 아니면 끈다(경계 엣지는 끈다).
    /// 반환값은 전송한 면 수.
    /// </summary>
    public static int ApplyByFaceOrder(PolyMesh target, PolyMesh source, out int skippedFaces)
    {
        skippedFaces = 0;
        int transferred = 0;
        var tLoop = new List<int>();
        var sLoop = new List<int>();
        int sf = 0;
        var faces = new List<int>();
        for (int tf = 0; tf < target.FaceCount; tf++)
        {
            if (!target.Faces[tf].Alive) continue;
            faces.Add(tf);
            while (sf < source.FaceCount && !source.Faces[sf].Alive) sf++;
            if (sf >= source.FaceCount) { skippedFaces++; continue; }
            int nt = target.GetFaceHalfEdges(tf, tLoop);
            int ns = source.GetFaceHalfEdges(sf, sLoop);
            sf++;
            if (nt != ns) { skippedFaces++; continue; }
            for (int i = 0; i < nt; i++)
            {
                var h = target.Hes[tLoop[i]];
                h.Uv0 = source.Hes[sLoop[i]].Uv0;
                target.Hes[tLoop[i]] = h;
            }
            transferred++;
        }
        // 전체 면 집합에 대해: 경계 엣지 = 심 해제, 내부 엣지 = UV 불연속이면 심
        UvOps.MarkSeamsAroundSelection(target, faces);
        target.BumpGeometry();
        return transferred;
    }

    public static int ApplyByFaceOrder(PolyMesh target, PolyMesh source) => ApplyByFaceOrder(target, source, out _);
}
