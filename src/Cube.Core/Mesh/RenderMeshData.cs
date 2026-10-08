using System.Numerics;

namespace Cube.Core.Mesh;

/// <summary>
/// 표시용 테셀레이션 결과. 정점은 코너(하프에지)마다 언롤되어 UV/노멀 불연속을 그대로 표현한다.
/// 배열은 재사용되므로 외부에서 보관하지 말 것.
/// <see cref="MeshTessellator.Build"/>가 채우고 <see cref="MeshTessellator.UpdatePositions(PolyMesh, RenderMeshData)"/>가
/// 위치만 갱신한다. 배열의 실제 길이는 용량이며(재할당을 줄이려고 2배씩 키움) 유효 개수는 각 Count 필드가 나타낸다.
/// 좌표는 메시 로컬 공간, 삼각형 방향은 코어 규약(반시계 = 앞면)이며 Godot 전달 시 브리지에서 뒤집는다.
/// </summary>
public sealed class RenderMeshData
{
    // 삼각형 표면
    /// <summary>코너별 정점 위치(로컬). 유효 길이는 <see cref="CornerCount"/>. 면마다 코너가 루프 순서로 연속 배치된다.</summary>
    public Vector3[] Positions = Array.Empty<Vector3>();
    /// <summary>코너별 노멀(하프에지 Normal 복사). 하드 엣지에서 같은 정점이라도 코너마다 다를 수 있다.</summary>
    public Vector3[] Normals = Array.Empty<Vector3>();
    /// <summary>코너별 UV(현재 UV 세트의 Uv0, 하단 원점). 심에서 같은 정점의 코너 UV가 갈라진다.</summary>
    public Vector2[] Uvs = Array.Empty<Vector2>();
    /// <summary>삼각형 인덱스(코너 번호, 3개씩 한 삼각형). 유효 길이는 <see cref="IndexCount"/>.</summary>
    public int[] Indices = Array.Empty<int>();
    /// <summary>유효 코너 수 = 살아 있는 면들의 차수 합.</summary>
    public int CornerCount;
    /// <summary>유효 인덱스 수(삼각형 수 × 3).</summary>
    public int IndexCount;
    public int[] TriToFace = Array.Empty<int>();        // 삼각형 i → faceId
    public int[] CornerToHalfEdge = Array.Empty<int>(); // 코너 i → halfEdgeId
    public int[] CornerToVertex = Array.Empty<int>();   // 코너 i → vertexId (위치 갱신용 캐시)
    /// <summary>삼각형 수(<see cref="IndexCount"/> / 3). 피킹에서 <see cref="TriToFace"/>를 순회할 때 쓴다.</summary>
    public int TriangleCount => IndexCount / 3;

    // 와이어(엣지당 2정점)
    /// <summary>와이어 선분 끝점 위치. 엣지 하나당 2개씩, 유효 길이는 <see cref="LineVertexCount"/>.</summary>
    public Vector3[] LinePositions = Array.Empty<Vector3>();
    /// <summary>유효 선분 끝점 수(살아 있는 엣지 수 × 2).</summary>
    public int LineVertexCount;
    public int[] LineToEdge = Array.Empty<int>();       // 선분 i → edgeId
    public int[] LineVertices = Array.Empty<int>();     // 선분 i → (vertexId a, b) = [2i], [2i+1] (위치 갱신용 캐시)
    /// <summary>선분 수(엣지 수). 엣지 피킹·와이어 그리기에 쓴다.</summary>
    public int LineCount => LineVertexCount / 2;

    // 정점 점
    /// <summary>살아 있는 정점마다 하나씩의 점 위치(정점 모드 표시·피킹용). 유효 길이는 <see cref="PointCount"/>.</summary>
    public Vector3[] PointPositions = Array.Empty<Vector3>();
    /// <summary>유효 점 수(살아 있는 정점 수).</summary>
    public int PointCount;
    public int[] PointToVertex = Array.Empty<int>();    // 점 i → vertexId

    // 면 중심(face 모드 표시용). 코너는 면마다 연속으로 놓이므로 [CornerStart, CornerStart+Degree) 평균이 중심이다
    /// <summary>면 중심 위치(코너 위치 평균). Face 모드의 중심 점 표시와 피킹에 쓴다.</summary>
    public Vector3[] FaceCenters = Array.Empty<Vector3>();
    /// <summary>면 중심 i → faceId.</summary>
    public int[] FaceCenterToFace = Array.Empty<int>();
    /// <summary>면 중심 i의 첫 코너 번호. 위치 갱신 시 메시를 다시 걷지 않고 코너 범위로 평균을 낸다.</summary>
    public int[] FaceCenterCornerStart = Array.Empty<int>();
    /// <summary>면 중심 i의 면 차수(코너 수).</summary>
    public int[] FaceCenterDegree = Array.Empty<int>();
    /// <summary>유효 면 중심 수(살아 있는 면 수).</summary>
    public int FaceCenterCount;

    /// <summary>현재 위치의 로컬 AABB(점 기준). 피킹의 레이-박스 조기 기각과 표시 AABB에 쓴다. 점이 없으면 둘 다 0.</summary>
    public Vector3 BoundsMin, BoundsMax;
    /// <summary>Build/UpdatePositions마다 증가. 위치가 바뀌었는지 외부 캐시가 판단하는 데 쓴다.</summary>
    public int PositionVersion;

    /// <summary>
    /// 배열 용량을 최소 <paramref name="size"/>로 보장한다. 부족하면 요청 크기와 현재 길이의 2배 중 큰 쪽으로 늘려
    /// 반복 Build에서 재할당 횟수를 줄인다. 이미 충분하면 아무것도 하지 않는다(기존 내용은 Resize가 보존).
    /// </summary>
    /// <param name="arr">용량을 확보할 배열(참조로 교체될 수 있음).</param>
    /// <param name="size">필요한 최소 원소 수.</param>
    internal static void Ensure<T>(ref T[] arr, int size)
    {
        if (arr.Length < size) Array.Resize(ref arr, Math.Max(size, arr.Length * 2));
    }
}
