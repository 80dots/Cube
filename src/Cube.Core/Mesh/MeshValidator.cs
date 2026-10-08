namespace Cube.Core.Mesh;

/// <summary>
/// 하프에지 불변식을 검사한다. 테스트와 디버그 빌드에서 사용.
/// 새 메시 연산 테스트는 결과에 대해 <see cref="Check"/>가 빈 목록인지 확인한다(CLAUDE.md 테스트 규약).
/// 죽은(Alive=false) 슬롯은 건너뛰고 살아 있는 요소끼리의 참조 일관성만 본다.
/// </summary>
public static class MeshValidator
{
    /// <summary>
    /// 메시의 하프에지 구조를 전수 검사해 위반 사항을 사람이 읽을 수 있는 문자열 목록으로 돌려준다.
    /// 검사 항목: ① 하프에지의 정점/Next/Prev/Face/Edge가 살아 있고 Next.Prev·Prev.Next가 자신인지, 엣지가 자신을 참조하는지,
    /// 트윈이 서로를 가리키고 같은 엣지·반대 방향인지 ② 면 루프가 닫혀 있고 모두 그 면 소속이며 차수 ≥ 3인지
    /// ③ 엣지의 He0이 살아 있고 He1이 있으면 He0.Twin == He1인지 ④ 정점의 대표 하프에지가 살아 있고 그 정점에서 출발하는지.
    /// </summary>
    /// <returns>오류 메시지 목록. 비어 있으면 유효.</returns>
    public static List<string> Check(PolyMesh m)
    {
        var errors = new List<string>();
        // ① 하프에지 단위 검사
        for (int h = 0; h < m.Hes.Count; h++)
        {
            var he = m.Hes[h];
            if (!he.Alive) continue;
            if ((uint)he.Vertex >= (uint)m.Verts.Count || !m.Verts[he.Vertex].Alive) errors.Add($"he{h}: dead vertex {he.Vertex}");
            if ((uint)he.Next >= (uint)m.Hes.Count || !m.Hes[he.Next].Alive) errors.Add($"he{h}: bad next {he.Next}");
            else if (m.Hes[he.Next].Prev != h) errors.Add($"he{h}: next.prev != self");
            if ((uint)he.Prev >= (uint)m.Hes.Count || !m.Hes[he.Prev].Alive) errors.Add($"he{h}: bad prev {he.Prev}");
            else if (m.Hes[he.Prev].Next != h) errors.Add($"he{h}: prev.next != self");
            if ((uint)he.Face >= (uint)m.Faces.Count || !m.Faces[he.Face].Alive) errors.Add($"he{h}: dead face {he.Face}");
            if ((uint)he.Edge >= (uint)m.Edges.Count || !m.Edges[he.Edge].Alive) errors.Add($"he{h}: dead edge {he.Edge}");
            else
            {
                // 엣지는 He0 또는 He1으로 이 하프에지를 가리켜야 한다
                var e = m.Edges[he.Edge];
                if (e.He0 != h && e.He1 != h) errors.Add($"he{h}: edge {he.Edge} does not reference it");
            }
            if (he.Twin >= 0)
            {
                if (he.Twin >= m.Hes.Count || !m.Hes[he.Twin].Alive) errors.Add($"he{h}: dead twin {he.Twin}");
                else
                {
                    // 트윈은 서로를 가리키고, 같은 엣지이며, 방향이 반대(내 끝 정점 = 트윈 시작 정점)여야 한다
                    var t = m.Hes[he.Twin];
                    if (t.Twin != h) errors.Add($"he{h}: twin.twin != self");
                    if (t.Edge != he.Edge) errors.Add($"he{h}: twin edge mismatch");
                    if (t.Vertex != m.Hes[he.Next].Vertex || he.Vertex != m.Hes[t.Next].Vertex) errors.Add($"he{h}: twin direction mismatch");
                }
            }
        }
        // ② 면 루프 검사: Next를 따라 시작점으로 돌아와야 하고 차수 3 이상
        for (int f = 0; f < m.Faces.Count; f++)
        {
            if (!m.Faces[f].Alive) continue;
            int start = m.Faces[f].HalfEdge, he = start, n = 0;
            do
            {
                if (!m.Hes[he].Alive || m.Hes[he].Face != f) { errors.Add($"face{f}: loop he{he} invalid"); break; }
                he = m.Hes[he].Next; n++;
                // 루프가 닫히지 않는 손상 구조에서 무한 루프 방지
                if (n > 100000) { errors.Add($"face{f}: loop not closed"); break; }
            } while (he != start);
            if (n < 3) errors.Add($"face{f}: degree {n}");
        }
        // ③ 엣지 검사
        for (int e = 0; e < m.Edges.Count; e++)
        {
            var ed = m.Edges[e];
            if (!ed.Alive) continue;
            if (ed.He0 < 0 || !m.Hes[ed.He0].Alive) errors.Add($"edge{e}: dead he0");
            if (ed.He1 >= 0 && !m.Hes[ed.He1].Alive) errors.Add($"edge{e}: dead he1");
            if (ed.He1 >= 0 && m.Hes[ed.He0].Twin != ed.He1) errors.Add($"edge{e}: he0.twin != he1");
        }
        // ④ 정점 검사: 대표 하프에지(-1 허용 = 고립 정점)
        for (int v = 0; v < m.Verts.Count; v++)
        {
            var vt = m.Verts[v];
            if (!vt.Alive) continue;
            if (vt.HalfEdge >= 0 && (!m.Hes[vt.HalfEdge].Alive || m.Hes[vt.HalfEdge].Vertex != v)) errors.Add($"vertex{v}: bad halfedge ref");
        }
        return errors;
    }

    /// <summary>위반 사항이 하나도 없으면 true(<see cref="Check"/>의 편의 래퍼).</summary>
    public static bool IsValid(PolyMesh m) => Check(m).Count == 0;
}
