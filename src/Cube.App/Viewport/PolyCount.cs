using Cube.Core.Scene;
using Cube.Core.Selection;

namespace Cube.App.Viewport;

/// <summary>
/// 뷰포트 좌상단 Poly Count HUD(Maya Heads Up Display → Poly Count)의 수치. Document/Selection 변경 때 다시 센다.
/// Scene = 문서 전체, Object = 선택 오브젝트(컴포넌트 모드면 컴포넌트를 가진 오브젝트), Selected = 선택 컴포넌트 수.
/// </summary>
public sealed class PolyCount
{
    public int SceneVerts, SceneEdges, SceneFaces, SceneTris, SceneObjects;
    public int ObjVerts, ObjEdges, ObjFaces, ObjTris, ObjObjects;
    public int SelVerts, SelEdges, SelFaces, SelUvs;
    public SelectMode Mode;
    private int _version;
    public int Version => _version;

    public void Recompute(Document doc)
    {
        var sel = doc.Selection;
        SceneVerts = SceneEdges = SceneFaces = SceneTris = SceneObjects = 0;
        ObjVerts = ObjEdges = ObjFaces = ObjTris = ObjObjects = 0;
        SelVerts = SelEdges = SelFaces = SelUvs = 0;
        Mode = sel.Mode;
        var objIds = new HashSet<NodeId>(sel.Objects);
        foreach (var (id, c) in sel.Components) if (!c.IsEmpty) objIds.Add(id);
        foreach (var n in doc.Nodes.Values)
        {
            if (n.IsRoot) continue;
            var m = n.Mesh;
            bool selected = objIds.Contains(n.Id);
            if (m == null) { if (n.IsJoint || n.Shape != null) { SceneObjects++; if (selected) ObjObjects++; } continue; }
            int v = 0, e = 0, f = 0, t = 0;
            foreach (var vert in m.Verts) if (vert.Alive) v++;
            foreach (var ed in m.Edges) if (ed.Alive) e++;
            for (int i = 0; i < m.FaceCount; i++) if (m.Faces[i].Alive) { f++; t += Math.Max(0, m.FaceDegree(i) - 2); }
            SceneVerts += v; SceneEdges += e; SceneFaces += f; SceneTris += t; SceneObjects++;
            if (selected)
            {
                ObjVerts += v; ObjEdges += e; ObjFaces += f; ObjTris += t; ObjObjects++;
                if (sel.Components.TryGetValue(n.Id, out var comps)) { SelVerts += comps.Verts.Count; SelEdges += comps.Edges.Count; SelFaces += comps.Faces.Count; SelUvs += comps.Uvs.Count; }
            }
        }
        _version++;
    }
}
