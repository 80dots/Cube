using System.Numerics;

namespace Cube.Core.Mesh;

/// <summary>Component Editor의 UV 행: 한 정점에서 같은 UV 값을 가진 코너 묶음(= 그 세트의 UV 점 하나).</summary>
public sealed record UvRow(int Vertex, int Index, int[] Corners);

/// <summary>Component Editor의 코너 행(모든 UV 세트를 나란히 볼 때): 정점의 면 코너 하나.</summary>
public readonly record struct CornerRow(int Vertex, int Face, int HalfEdge);

/// <summary>
/// Component Editor(Maya)용 정점 데이터 접근. 코너 속성(UV/노멀)은 하프에지에 있고 UV 세트는 현재 세트만 Uv0에 올라와 있으므로
/// 세트 번호로 읽고 쓰는 함수를 둔다(현재 세트 = Uv0, 다른 세트 = UvSets[k].Uvs).
/// </summary>
public static class ComponentData
{
    /// <summary>세트 수(세트 목록이 없으면 기본 세트 하나).</summary>
    public static int SetCount(PolyMesh m) => Math.Max(1, m.UvSets.Count);
    public static string SetName(PolyMesh m, int set) => m.UvSets.Count == 0 ? "map1" : m.UvSets[set].Name;
    private static bool IsCurrent(PolyMesh m, int set) => m.UvSets.Count == 0 || set == m.CurrentUvSet;

    public static Vector2 GetUv(PolyMesh m, int set, int he)
    {
        if (IsCurrent(m, set)) return m.Hes[he].Uv0;
        var a = m.UvSets[set].Uvs;
        return he < a.Length ? a[he] : Vector2.Zero;
    }

    public static void SetUv(PolyMesh m, int set, int he, Vector2 uv)
    {
        if (IsCurrent(m, set)) { var h = m.Hes[he]; h.Uv0 = uv; m.Hes[he] = h; return; }
        var s = m.UvSets[set];
        if (s.Uvs.Length < m.HalfEdgeCount) Array.Resize(ref s.Uvs, m.HalfEdgeCount);
        s.Uvs[he] = uv;
    }

    /// <summary>정점 → 그 정점에서 출발하는 살아 있는 코너(하프에지) 목록.</summary>
    public static List<int>[] CornersByVertex(PolyMesh m)
    {
        var res = new List<int>[m.VertexCount];
        for (int h = 0; h < m.HalfEdgeCount; h++)
        {
            var he = m.Hes[h];
            if (!he.Alive || he.Face < 0) continue;
            (res[he.Vertex] ??= new List<int>(4)).Add(h);
        }
        return res;
    }

    /// <summary>세트 하나의 UV 행: 정점 순서대로, 정점마다 UV 값이 같은 코너끼리 묶는다(Maya의 정점별 UV 점).</summary>
    public static List<UvRow> UvRows(PolyMesh m, int set, IEnumerable<int> vertices, List<int>[]? corners = null)
    {
        corners ??= CornersByVertex(m);
        var rows = new List<UvRow>();
        var groups = new List<(Vector2 uv, List<int> hes)>();
        foreach (int v in vertices)
        {
            if (v < 0 || v >= corners.Length || corners[v] == null) continue;
            groups.Clear();
            foreach (int h in corners[v])
            {
                var uv = GetUv(m, set, h);
                int g = groups.FindIndex(x => Vector2.DistanceSquared(x.uv, uv) < 1e-12f);
                if (g < 0) groups.Add((uv, new List<int> { h })); else groups[g].hes.Add(h);
            }
            for (int i = 0; i < groups.Count; i++) rows.Add(new UvRow(v, i, groups[i].hes.ToArray()));
        }
        return rows;
    }

    public static List<CornerRow> CornerRows(PolyMesh m, IEnumerable<int> vertices, List<int>[]? corners = null)
    {
        corners ??= CornersByVertex(m);
        var rows = new List<CornerRow>();
        foreach (int v in vertices)
        {
            if (v < 0 || v >= corners.Length || corners[v] == null) continue;
            foreach (int h in corners[v].OrderBy(h => m.Hes[h].Face)) rows.Add(new CornerRow(v, m.Hes[h].Face, h));
        }
        return rows;
    }

    /// <summary>정점 노멀 = 코너 노멀 평균(표시용). 잠긴 노멀이 있으면 그 값.</summary>
    public static Vector3 VertexNormal(PolyMesh m, int v, List<int>[] corners)
    {
        if (m.LockedNormals.TryGetValue(v, out var ln)) return ln;
        var sum = Vector3.Zero;
        if (v < corners.Length && corners[v] != null) foreach (int h in corners[v]) sum += m.Hes[h].Normal;
        return sum.LengthSquared() > 1e-20f ? Vector3.Normalize(sum) : Vector3.Zero;
    }

    /// <summary>정점의 코너 노멀 중 하나라도 잠겼거나 정점 노멀이 잠겼는지.</summary>
    public static bool NormalLocked(PolyMesh m, int v, List<int>[] corners)
    {
        if (m.LockedNormals.ContainsKey(v)) return true;
        if (v < corners.Length && corners[v] != null) foreach (int h in corners[v]) if (m.Hes[h].NormalLocked) return true;
        return false;
    }
}
