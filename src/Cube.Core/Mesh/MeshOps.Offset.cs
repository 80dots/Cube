using System.Numerics;

namespace Cube.Core.Mesh;

public static partial class MeshOps
{
    /// <summary>
    /// Maya Extrude 조작기의 Local Translate Z(두께)용 정점별 오프셋 방향(메시 로컬).
    /// 각 정점은 그 정점을 쓰는 선택 면들의 법선 평균 방향으로 움직이고, 길이는 1/(dir·n_f)의 평균(마이터)이라
    /// 거리 t만큼 움직이면 모든 선택 면이 자기 평면 기준으로 t만큼 평행 이동한다(이웃 면이 서로 다른 방향이어도 형태 유지).
    /// </summary>
    public static Dictionary<int, Vector3> RegionOffsetDirections(PolyMesh m, IEnumerable<int> faceIds)
    {
        var sums = new Dictionary<int, Vector3>();
        var normals = new Dictionary<int, List<Vector3>>();
        var tmp = new List<int>();
        foreach (int f in faceIds)
        {
            if (f < 0 || f >= m.FaceCount || !m.Faces[f].Alive) continue;
            var n = MeshNormals.FaceNormalUnnormalized(m, f);
            if (n.LengthSquared() < 1e-20f) continue;
            n = Vector3.Normalize(n);
            m.GetFaceVertices(f, tmp);
            foreach (int v in tmp)
            {
                if (!normals.TryGetValue(v, out var list)) { list = new List<Vector3>(); normals[v] = list; }
                // 같은 방향 면이 여러 개면 한 번만 센다(평평한 영역 가운데 정점이 치우치지 않게)
                if (list.Any(x => Vector3.Dot(x, n) > 0.9999f)) continue;
                list.Add(n);
                sums[v] = (sums.TryGetValue(v, out var s) ? s : Vector3.Zero) + n;
            }
        }
        var result = new Dictionary<int, Vector3>();
        foreach (var (v, sum) in sums)
        {
            if (sum.LengthSquared() < 1e-12f) { result[v] = Vector3.Zero; continue; }
            var dir = Vector3.Normalize(sum);
            float k = 0f;
            foreach (var n in normals[v]) k += Vector3.Dot(dir, n);
            k /= normals[v].Count;
            float scale = k > 0.2f ? 1f / k : 5f; // 아주 뾰족한 모서리는 과도하게 튀지 않게 제한
            result[v] = dir * scale;
        }
        return result;
    }
}
