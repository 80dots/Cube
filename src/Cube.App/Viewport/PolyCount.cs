using Cube.Core.Scene;
using Cube.Core.Selection;

namespace Cube.App.Viewport;

/// <summary>
/// 뷰포트 좌상단 Poly Count HUD(Maya Heads Up Display → Poly Count)의 수치. Document/Selection 변경 때 다시 센다.
/// Scene = 문서 전체, Object = 선택 오브젝트(컴포넌트 모드면 컴포넌트를 가진 오브젝트), Selected = 선택 컴포넌트 수.
/// </summary>
/// <remarks>
/// Shell이 변경 이벤트에서 dirty 표시만 하고 프레임마다 한 번 <see cref="Recompute"/>를 부른다. 모든 패널의 ViewportOverlay가 같은 인스턴스를 그린다.
/// 삼각형 수는 면마다 (차수 − 2)로 센다(팬 삼각 분할 기준). 메시가 없는 노드(조인트·라이트·그룹 셰이프)는 Objects에만 더한다.
/// </remarks>
public sealed class PolyCount
{
    /// <summary>문서 전체의 정점/엣지/면/삼각형/오브젝트 수(살아 있는 요소만).</summary>
    public int SceneVerts, SceneEdges, SceneFaces, SceneTris, SceneObjects;
    /// <summary>선택된 오브젝트들의 합계(컴포넌트 모드면 컴포넌트가 선택된 오브젝트 포함).</summary>
    public int ObjVerts, ObjEdges, ObjFaces, ObjTris, ObjObjects;
    /// <summary>선택된 컴포넌트 수(정점/엣지/면/UV 점).</summary>
    public int SelVerts, SelEdges, SelFaces, SelUvs;
    /// <summary>계산 시점의 선택 모드(HUD가 Selected 열에 어떤 수를 강조할지 정함).</summary>
    public SelectMode Mode;
    /// <summary>재계산 횟수. 오버레이가 바뀐 경우에만 다시 그리도록 비교한다.</summary>
    private int _version;
    /// <summary>재계산 버전(읽기 전용).</summary>
    public int Version => _version;

    /// <summary>문서와 선택 상태로 모든 수치를 다시 센다(AnimPerf로 시간 측정).</summary>
    public void Recompute(Document doc)
    {
        long t0 = AnimPerf.Begin();
        RecomputeCore(doc);
        AnimPerf.End("shell.polycount", t0);
    }

    /// <summary>실제 집계: 값 초기화 → 선택 오브젝트 집합 구성 → 노드마다 살아 있는 요소 수를 세어 Scene/Object/Selected에 더한다.</summary>
    private void RecomputeCore(Document doc)
    {
        var sel = doc.Selection;
        SceneVerts = SceneEdges = SceneFaces = SceneTris = SceneObjects = 0;
        ObjVerts = ObjEdges = ObjFaces = ObjTris = ObjObjects = 0;
        SelVerts = SelEdges = SelFaces = SelUvs = 0;
        Mode = sel.Mode;
        // 오브젝트 열 대상: 선택 오브젝트 + 컴포넌트가 선택된 노드
        var objIds = new HashSet<NodeId>(sel.Objects);
        foreach (var (id, c) in sel.Components) if (!c.IsEmpty) objIds.Add(id);
        foreach (var n in doc.Nodes.Values)
        {
            if (n.IsRoot) continue;
            var m = n.Mesh;
            bool selected = objIds.Contains(n.Id);
            // 메시 없는 노드: 조인트나 셰이프가 있으면 오브젝트로만 센다
            if (m == null) { if (n.IsJoint || n.Shape != null) { SceneObjects++; if (selected) ObjObjects++; } continue; }
            // 살아 있는(Alive) 요소만 센다(삭제는 슬롯을 남기므로)
            int v = 0, e = 0, f = 0, t = 0;
            foreach (var vert in m.Verts) if (vert.Alive) v++;
            foreach (var ed in m.Edges) if (ed.Alive) e++;
            for (int i = 0; i < m.FaceCount; i++) if (m.Faces[i].Alive) { f++; t += Math.Max(0, m.FaceDegree(i) - 2); }
            SceneVerts += v; SceneEdges += e; SceneFaces += f; SceneTris += t; SceneObjects++;
            // 선택된 오브젝트면 오브젝트 열과, 그 노드의 컴포넌트 선택 수를 더한다
            if (selected)
            {
                ObjVerts += v; ObjEdges += e; ObjFaces += f; ObjTris += t; ObjObjects++;
                if (sel.Components.TryGetValue(n.Id, out var comps)) { SelVerts += comps.Verts.Count; SelEdges += comps.Edges.Count; SelFaces += comps.Faces.Count; SelUvs += comps.Uvs.Count; }
            }
        }
        _version++;
    }
}
