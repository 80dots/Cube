using System.Numerics;

namespace Cube.Core.Mesh;

/// <summary>면 노멀과 코너(하프에지) 노멀을 계산한다. 하드 엣지는 노멀 평활을 끊는다.</summary>
public static class MeshNormals
{
    /// <summary>Newell 방법으로 n-gon 면 노멀(면적 가중, 비정규화)을 구한다.</summary>
    public static Vector3 FaceNormalUnnormalized(PolyMesh m, int f)
    {
        var n = Vector3.Zero;
        int start = m.Faces[f].HalfEdge, he = start;
        do
        {
            var a = m.Verts[m.Hes[he].Vertex].Position;
            var b = m.Verts[m.Hes[m.Hes[he].Next].Vertex].Position;
            n.X += (a.Y - b.Y) * (a.Z + b.Z);
            n.Y += (a.Z - b.Z) * (a.X + b.X);
            n.Z += (a.X - b.X) * (a.Y + b.Y);
            he = m.Hes[he].Next;
        } while (he != start);
        return n;
    }

    public static void Recompute(PolyMesh m)
    {
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

        for (int h = 0; h < m.Hes.Count; h++)
        {
            var he = m.Hes[h];
            if (!he.Alive || he.NormalLocked) continue; // 고정된 코너 노멀은 그대로
            var sum = areaNormals[he.Face];

            // 방향 1: 이 코너에서 나가는 하프에지의 엣지를 건너 이웃 면으로 (Next(Twin(cur)))
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
            float len = sum.Length();
            he.Normal = len > 1e-12f ? sum / len : m.Faces[he.Face].Normal;
            if (m.LockedNormals.TryGetValue(he.Vertex, out var locked) && locked.LengthSquared() > 1e-12f) he.Normal = Vector3.Normalize(locked);
            m.Hes[h] = he;
        }
    }
}
