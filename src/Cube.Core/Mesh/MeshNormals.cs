using System.Numerics;

namespace Cube.Core.Mesh;

/// <summary>
/// 면 노멀과 코너(하프에지) 노멀을 계산한다. 하드 엣지는 노멀 평활을 끊는다.
/// 메시 연산 자체는 노멀을 고치지 않으므로 명령(호출자)이 위치·위상을 바꾼 뒤 <see cref="Recompute"/>를 부른다.
/// </summary>
public static class MeshNormals
{
    /// <summary>Newell 방법으로 n-gon 면 노멀(면적 가중, 비정규화)을 구한다.</summary>
    /// <remarks>
    /// 루프의 연속한 정점 쌍마다 (yz, zx, xy) 평면 투영 넓이를 누적한다. 결과 길이 = 면 넓이 × 2, 방향 = 반시계 감김의 앞면.
    /// 비평면·오목 n각형에서도 안정적이며, 길이가 넓이에 비례해 정점 노멀 평균에서 면적 가중치로 그대로 쓰인다.
    /// </remarks>
    public static Vector3 FaceNormalUnnormalized(PolyMesh m, int f)
    {
        var n = Vector3.Zero;
        int start = m.Faces[f].HalfEdge, he = start;
        do
        {
            // 변 a→b의 기여
            var a = m.Verts[m.Hes[he].Vertex].Position;
            var b = m.Verts[m.Hes[m.Hes[he].Next].Vertex].Position;
            n.X += (a.Y - b.Y) * (a.Z + b.Z);
            n.Y += (a.Z - b.Z) * (a.X + b.X);
            n.Z += (a.X - b.X) * (a.Y + b.Y);
            he = m.Hes[he].Next;
        } while (he != start);
        return n;
    }

    /// <summary>
    /// 모든 면 노멀(<see cref="Face.Normal"/>, 정규화)과 코너 노멀(<see cref="HalfEdge.Normal"/>)을 다시 계산한다.
    /// 코너 노멀 = 그 정점 주위에서 하드 엣지나 경계를 넘지 않고 이어진 면들(스무딩 팬)의 면적 가중 노멀 합을 정규화한 값.
    /// 규칙: <see cref="HalfEdge.NormalLocked"/> 코너는 건드리지 않고, <see cref="PolyMesh.LockedNormals"/>에 있는 정점은
    /// 계산 결과 대신 잠긴 값으로 덮어쓴다. 넓이 0 면은 노멀을 +Y로 둔다.
    /// </summary>
    public static void Recompute(PolyMesh m)
    {
        // 1단계: 면 노멀. 면적 가중(비정규화) 값은 코너 평균용으로 따로 보관한다
        int fc = m.Faces.Count;
        var areaNormals = new Vector3[fc];
        for (int f = 0; f < fc; f++)
        {
            if (!m.Faces[f].Alive) continue;
            var n = FaceNormalUnnormalized(m, f);
            areaNormals[f] = n;
            var face = m.Faces[f];
            float len = n.Length();
            face.Normal = len > 1e-12f ? n / len : Vector3.UnitY;
            m.Faces[f] = face;
        }

        // 2단계: 코너마다 정점 주위의 면을 양방향으로 돌며(하드/경계에서 멈춤) 면적 노멀을 합산
        for (int h = 0; h < m.Hes.Count; h++)
        {
            var he = m.Hes[h];
            if (!he.Alive || he.NormalLocked) continue; // 고정된 코너 노멀은 그대로
            var sum = areaNormals[he.Face];

            // 방향 1: 이 코너에서 나가는 하프에지의 엣지를 건너 이웃 면으로 (Next(Twin(cur)))
            // wrapped = 정점을 한 바퀴 다 돌아 자기 코너로 돌아옴(내부 정점이고 하드 엣지 없음) → 반대 방향 탐색 불필요
            bool wrapped = false;
            int cur = h;
            for (int guard = 0; guard < 4096; guard++)
            {
                var ch = m.Hes[cur];
                if (ch.Twin < 0 || m.Edges[ch.Edge].Hard) break;
                int nxt = m.Hes[ch.Twin].Next;
                if (nxt == h) { wrapped = true; break; }
                sum += areaNormals[m.Hes[nxt].Face];
                cur = nxt;
            }
            // 방향 2: 이 코너로 들어오는 하프에지(Prev)의 엣지를 건너 (Twin(Prev(cur)))
            if (!wrapped)
            {
                cur = h;
                for (int guard = 0; guard < 4096; guard++)
                {
                    var ph = m.Hes[m.Hes[cur].Prev];
                    if (ph.Twin < 0 || m.Edges[ph.Edge].Hard) break;
                    int nxt = ph.Twin;
                    if (nxt == h) break;
                    sum += areaNormals[m.Hes[nxt].Face];
                    cur = nxt;
                }
            }
            // 정규화(합이 0이면 면 노멀로 대체). 정점 잠금 노멀이 있으면 그 값으로 고정
            float len = sum.Length();
            he.Normal = len > 1e-12f ? sum / len : m.Faces[he.Face].Normal;
            if (m.LockedNormals.TryGetValue(he.Vertex, out var locked) && locked.LengthSquared() > 1e-12f) he.Normal = Vector3.Normalize(locked);
            m.Hes[h] = he;
        }
    }
}
