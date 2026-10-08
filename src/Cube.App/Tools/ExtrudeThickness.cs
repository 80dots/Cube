using Cube.Core.Commands;
using Cube.Core.Mesh;
using Cube.Core.Scene;
using NVec3 = System.Numerics.Vector3;

namespace Cube.App.Tools;

/// <summary>
/// 면 Extrude 두께: 캡 면의 정점을 정점별 영역 법선(MeshOps.RegionOffsetDirections, 마이터)으로 밀어 각 면이 자기 법선 방향으로 평행 이동한다.
/// Extrude 조작기의 파란 화살표 드래그(MoveTool)와 Action Popup의 Thickness 필드가 같은 결과를 낸다. 히스토리 항목 "Extrude Thickness".
/// </summary>
public static class ExtrudeThickness
/// <remarks>Action Popup이 Thickness 값을 바꿀 때 이전 두께 명령을 Undo하고 이 함수로 새 명령을 만들어 다시 넣는다.</remarks>
{
    /// <summary>두께 명령(아직 적용 안 됨; Undo.Push로 적용). thickness는 월드 거리.</summary>
    public static MoveVerticesCommand? Make(Document doc, NodeId node, IEnumerable<int> faces, float thickness)
    /// <param name="doc">대상 문서.</param>
    /// <param name="node">Extrude된 메시 노드.</param>
    /// <param name="faces">밀어낼 캡 면 ID들.</param>
    /// <param name="thickness">월드 거리(m).</param>
    /// <returns>MoveVerticesCommand(히스토리 'Extrude Thickness'), 노드/메시/방향이 없으면 null.</returns>
    {
        // 노드와 메시 확인 후 정점별 마이터 오프셋 방향(로컬) 계산
        var n = doc.Find(node); var mesh = n?.Mesh;
        if (n == null || mesh == null) return null;
        var dirs = MeshOps.RegionOffsetDirections(mesh, faces);
        if (dirs.Count == 0) return null;
        // 방향이 정해진 정점 ID와 방향을 같은 순서의 배열로
        var ids = dirs.Keys.ToArray();
        var dirArr = ids.Select(v => dirs[v]).ToArray();
        // 월드 거리 -> 로컬 거리(균일 스케일 가정; 평균 법선 방향의 스케일)
        var avg = NVec3.Zero; foreach (var d in dirArr) avg += d;
        float worldPerLocal = 1f;
        if (avg.LengthSquared() > 1e-12f)
        {
            var w = NVec3.TransformNormal(NVec3.Normalize(avg), n.WorldMatrix);
            if (w.LengthSquared() > 1e-12f) worldPerLocal = w.Length();
        }
        // 시작 위치와 결과 위치(로컬 = 월드 두께 / 비율)
        var before = ids.Select(v => mesh.Verts[v].Position).ToArray();
        float local = thickness / worldPerLocal;
        var after = before.Select((p, i) => p + dirArr[i] * local).ToArray();
        // 재실행(replay)은 파라미터 Thickness로 같은 정점에 방향 × 두께를 더한다(삭제된 정점은 건너뜀)
        var prm = new HistoryParams(HistoryParam.F("Thickness", thickness, -1000f, 1000f, 0.01f));
        return new MoveVerticesCommand("Extrude Thickness", node, ids, before, after, null, prm, (m, p) =>
        {
            float l = p.Float("Thickness") / worldPerLocal;
            for (int i = 0; i < ids.Length; i++)
            {
                if (ids[i] >= m.VertexCount || !m.Verts[ids[i]].Alive) continue;
                var vtx = m.Verts[ids[i]]; vtx.Position += dirArr[i] * l; m.Verts[ids[i]] = vtx;
            }
            return true;
        });
    }
}
