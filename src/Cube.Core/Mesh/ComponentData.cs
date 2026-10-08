using System.Numerics;

namespace Cube.Core.Mesh;

/// <summary>
/// Component Editor의 UV 행: 한 정점에서 같은 UV 값을 가진 코너 묶음(= 그 세트의 UV 점 하나).
/// <c>Vertex</c> = 정점 ID, <c>Index</c> = 그 정점 안에서 몇 번째 UV 점인지(0부터),
/// <c>Corners</c> = 이 UV 점을 공유하는 하프에지(코너) ID들. 값을 고치면 모든 코너에 같이 써야 UV 점이 유지된다.
/// </summary>
public sealed record UvRow(int Vertex, int Index, int[] Corners);

/// <summary>
/// Component Editor의 코너 행(모든 UV 세트를 나란히 볼 때): 정점의 면 코너 하나.
/// <c>Vertex</c> = 정점 ID, <c>Face</c> = 코너가 속한 면, <c>HalfEdge</c> = 코너의 하프에지 ID.
/// </summary>
public readonly record struct CornerRow(int Vertex, int Face, int HalfEdge);

/// <summary>
/// Component Editor(Maya)용 정점 데이터 접근. 코너 속성(UV/노멀)은 하프에지에 있고 UV 세트는 현재 세트만 Uv0에 올라와 있으므로
/// 세트 번호로 읽고 쓰는 함수를 둔다(현재 세트 = Uv0, 다른 세트 = UvSets[k].Uvs).
/// 모든 함수는 메시를 직접 읽거나 고치며 Undo 기록·버전 증가는 호출자(명령)가 책임진다.
/// </summary>
public static class ComponentData
{
    /// <summary>세트 수(세트 목록이 없으면 기본 세트 하나).</summary>
    public static int SetCount(PolyMesh m) => Math.Max(1, m.UvSets.Count);

    /// <summary>세트 이름. 세트 목록이 비어 있으면 Maya 기본 이름 "map1"을 돌려준다.</summary>
    public static string SetName(PolyMesh m, int set) => m.UvSets.Count == 0 ? "map1" : m.UvSets[set].Name;

    /// <summary>
    /// 이 세트가 지금 코너(HalfEdge.Uv0)에 올라와 있는 현재 세트인지. 세트 목록이 없으면 유일한 기본 세트이므로 항상 true.
    /// </summary>
    private static bool IsCurrent(PolyMesh m, int set) => m.UvSets.Count == 0 || set == m.CurrentUvSet;

    /// <summary>
    /// 세트 <paramref name="set"/>에서 코너 <paramref name="he"/>의 UV를 읽는다.
    /// 현재 세트면 하프에지의 Uv0, 아니면 세트 배열 값(배열이 짧으면 아직 값이 없는 것으로 보고 0).
    /// </summary>
    public static Vector2 GetUv(PolyMesh m, int set, int he)
    {
        // 현재 세트는 하프에지에 올라와 있는 값이 최신이다(세트 배열은 전환 시점의 스냅샷일 수 있음)
        if (IsCurrent(m, set)) return m.Hes[he].Uv0;
        var a = m.UvSets[set].Uvs;
        return he < a.Length ? a[he] : Vector2.Zero;
    }

    /// <summary>
    /// 세트 <paramref name="set"/>에서 코너 <paramref name="he"/>의 UV를 쓴다.
    /// 현재 세트면 하프에지 Uv0을 바꾸고, 다른 세트면 배열을 하프에지 수까지 늘린 뒤 슬롯에 쓴다.
    /// </summary>
    public static void SetUv(PolyMesh m, int set, int he, Vector2 uv)
    {
        // HalfEdge는 구조체라 꺼내서 고친 뒤 다시 넣어야 한다
        if (IsCurrent(m, set)) { var h = m.Hes[he]; h.Uv0 = uv; m.Hes[he] = h; return; }
        var s = m.UvSets[set];
        // 세트 생성 후 하프에지가 늘었을 수 있으므로 슬롯 수를 맞춘다
        if (s.Uvs.Length < m.HalfEdgeCount) Array.Resize(ref s.Uvs, m.HalfEdgeCount);
        s.Uvs[he] = uv;
    }

    /// <summary>정점 → 그 정점에서 출발하는 살아 있는 코너(하프에지) 목록.</summary>
    /// <returns>정점 ID로 인덱싱되는 배열. 코너가 없는 정점의 항목은 null이다.</returns>
    public static List<int>[] CornersByVertex(PolyMesh m)
    {
        var res = new List<int>[m.VertexCount];
        // 하프에지를 한 번 훑어 출발 정점별로 모은다(면에 속한 살아 있는 코너만)
        for (int h = 0; h < m.HalfEdgeCount; h++)
        {
            var he = m.Hes[h];
            if (!he.Alive || he.Face < 0) continue;
            (res[he.Vertex] ??= new List<int>(4)).Add(h);
        }
        return res;
    }

    /// <summary>세트 하나의 UV 행: 정점 순서대로, 정점마다 UV 값이 같은 코너끼리 묶는다(Maya의 정점별 UV 점).</summary>
    /// <param name="m">대상 메시.</param>
    /// <param name="set">UV 세트 번호.</param>
    /// <param name="vertices">행을 만들 정점들(표시 순서).</param>
    /// <param name="corners">미리 계산한 <see cref="CornersByVertex"/> 결과(없으면 새로 계산).</param>
    /// <returns>정점마다 UV 점 개수만큼의 행. 같은 정점 안의 Index는 처음 나온 순서.</returns>
    public static List<UvRow> UvRows(PolyMesh m, int set, IEnumerable<int> vertices, List<int>[]? corners = null)
    {
        corners ??= CornersByVertex(m);
        var rows = new List<UvRow>();
        // 정점마다 재사용하는 그룹 버퍼: (대표 UV, 그 UV를 가진 코너들)
        var groups = new List<(Vector2 uv, List<int> hes)>();
        foreach (int v in vertices)
        {
            if (v < 0 || v >= corners.Length || corners[v] == null) continue;
            groups.Clear();
            // 코너 UV를 거의 같은 값(제곱 거리 1e-12 미만)끼리 묶는다. 정점당 코너는 몇 개뿐이라 선형 탐색으로 충분하다
            foreach (int h in corners[v])
            {
                var uv = GetUv(m, set, h);
                int g = groups.FindIndex(x => Vector2.DistanceSquared(x.uv, uv) < 1e-12f);
                if (g < 0) groups.Add((uv, new List<int> { h })); else groups[g].hes.Add(h);
            }
            // 묶음 하나가 UV 점 하나 = 행 하나
            for (int i = 0; i < groups.Count; i++) rows.Add(new UvRow(v, i, groups[i].hes.ToArray()));
        }
        return rows;
    }

    /// <summary>
    /// 코너 행: 정점 순서대로, 정점마다 그 정점의 모든 면 코너를 면 ID 순으로 나열한다(여러 UV 세트를 나란히 볼 때 행 기준).
    /// </summary>
    /// <param name="corners">미리 계산한 <see cref="CornersByVertex"/> 결과(없으면 새로 계산).</param>
    public static List<CornerRow> CornerRows(PolyMesh m, IEnumerable<int> vertices, List<int>[]? corners = null)
    {
        corners ??= CornersByVertex(m);
        var rows = new List<CornerRow>();
        foreach (int v in vertices)
        {
            if (v < 0 || v >= corners.Length || corners[v] == null) continue;
            // 면 ID 순으로 정렬해 표시 순서를 안정적으로 만든다
            foreach (int h in corners[v].OrderBy(h => m.Hes[h].Face)) rows.Add(new CornerRow(v, m.Hes[h].Face, h));
        }
        return rows;
    }

    /// <summary>정점 노멀 = 코너 노멀 평균(표시용). 잠긴 노멀이 있으면 그 값.</summary>
    /// <returns>정규화된 노멀. 코너가 없거나 합이 0이면 Vector3.Zero.</returns>
    public static Vector3 VertexNormal(PolyMesh m, int v, List<int>[] corners)
    {
        // 정점 단위로 잠긴 노멀(Lock Normals/Set Vertex Normal)이 우선
        if (m.LockedNormals.TryGetValue(v, out var ln)) return ln;
        var sum = Vector3.Zero;
        if (v < corners.Length && corners[v] != null) foreach (int h in corners[v]) sum += m.Hes[h].Normal;
        return sum.LengthSquared() > 1e-20f ? Vector3.Normalize(sum) : Vector3.Zero;
    }

    /// <summary>정점의 코너 노멀 중 하나라도 잠겼거나 정점 노멀이 잠겼는지.</summary>
    public static bool NormalLocked(PolyMesh m, int v, List<int>[] corners)
    {
        // 정점 단위 잠금(LockedNormals) 또는 코너 단위 잠금(HalfEdge.NormalLocked: Bevel Harden Normals 등)
        if (m.LockedNormals.ContainsKey(v)) return true;
        if (v < corners.Length && corners[v] != null) foreach (int h in corners[v]) if (m.Hes[h].NormalLocked) return true;
        return false;
    }
}
