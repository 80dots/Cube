using System.Numerics;

namespace Cube.Core.Mesh;

/// <summary>
/// 표시용 테셀레이션 결과. 정점은 코너(하프에지)마다 언롤되어 UV/노멀 불연속을 그대로 표현한다.
/// 배열은 재사용되므로 외부에서 보관하지 말 것.
/// </summary>
public sealed class RenderMeshData
{
    // 삼각형 표면
    public Vector3[] Positions = Array.Empty<Vector3>();
    public Vector3[] Normals = Array.Empty<Vector3>();
    public Vector2[] Uvs = Array.Empty<Vector2>();
    public int[] Indices = Array.Empty<int>();
    public int CornerCount;
    public int IndexCount;
    public int[] TriToFace = Array.Empty<int>();        // 삼각형 i → faceId
    public int[] CornerToHalfEdge = Array.Empty<int>(); // 코너 i → halfEdgeId
    public int[] CornerToVertex = Array.Empty<int>();   // 코너 i → vertexId (위치 갱신용 캐시)
    public int TriangleCount => IndexCount / 3;

    // 와이어(엣지당 2정점)
    public Vector3[] LinePositions = Array.Empty<Vector3>();
    public int LineVertexCount;
    public int[] LineToEdge = Array.Empty<int>();       // 선분 i → edgeId
    public int[] LineVertices = Array.Empty<int>();     // 선분 i → (vertexId a, b) = [2i], [2i+1] (위치 갱신용 캐시)
    public int LineCount => LineVertexCount / 2;

    // 정점 점
    public Vector3[] PointPositions = Array.Empty<Vector3>();
    public int PointCount;
    public int[] PointToVertex = Array.Empty<int>();    // 점 i → vertexId

    // 면 중심(face 모드 표시용). 코너는 면마다 연속으로 놓이므로 [CornerStart, CornerStart+Degree) 평균이 중심이다
    public Vector3[] FaceCenters = Array.Empty<Vector3>();
    public int[] FaceCenterToFace = Array.Empty<int>();
    public int[] FaceCenterCornerStart = Array.Empty<int>();
    public int[] FaceCenterDegree = Array.Empty<int>();
    public int FaceCenterCount;

    /// <summary>현재 위치의 로컬 AABB(점 기준). 피킹의 레이-박스 조기 기각과 표시 AABB에 쓴다. 점이 없으면 둘 다 0.</summary>
    public Vector3 BoundsMin, BoundsMax;
    /// <summary>Build/UpdatePositions마다 증가. 위치가 바뀌었는지 외부 캐시가 판단하는 데 쓴다.</summary>
    public int PositionVersion;

    internal static void Ensure<T>(ref T[] arr, int size)
    {
        if (arr.Length < size) Array.Resize(ref arr, Math.Max(size, arr.Length * 2));
    }
}
