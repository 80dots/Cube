using Cube.App.Bridge;
using Cube.App.Tools;
using Cube.Core.Commands;
using Cube.Core.Mesh;
using Cube.Core.Scene;
using Cube.Core.Selection;
using NMat = System.Numerics.Matrix4x4;
using NVec3 = System.Numerics.Vector3;

namespace Cube.App.UI;

/// <summary>
/// Blender식 Extrude(Edit Mesh → Extrude): Extrude Region(경계 규칙: 열린 판은 닫힌 상자, 닫힌 볼륨은 복제), Extrude Individual Faces,
/// 엣지 Extrude, Extrude Repeat(Steps), 방향(평균 법선/면 법선/X/Y/Z/View), Flip Normals. 정점 선택은 면(모든 정점이 선택된 면) → 엣지 순으로 해석한다.
/// 옵션 창·Action Popup·구성 이력에서 바꿀 수 있고, 기본값(Offset 0)은 기존처럼 조작기로 두께를 당긴다. 단축키는 Maya 기존 Ctrl+E 그대로(Blender 단축키는 넣지 않음).
/// </summary>
public partial class Shell
{
    /// <summary>Direction 옵션 표시 이름(ExtrudeDirection 열거형 정수값 순서: 0 평균 법선 … 5 View = Custom).</summary>
    private static readonly string[] ExtrudeDirections = { "Average Normal", "Face Normals", "X", "Y", "Z", "View" };

    /// <summary>
    /// Extrude 옵션 창 정의. 기본값 = Region, Offset 0, 평균 법선, Steps 1, Flip 끔, 조작기 켬
    /// (Offset 0 + 조작기 = 제자리 돌출 후 파란 화살표로 두께를 당기는 Maya식 흐름).
    /// </summary>
    private static OptionSpec ExtrudeSpec() => new("Extrude Options", v =>
    {
        v.Set("type", 0); v.Set("offset", 0f); v.Set("direction", 0); v.Set("steps", 1); v.Set("flip", 0); v.Set("manip", 1);
    }, new[]
    {
        OptionField.E("type", "Faces", "Region", "Individual Faces"),
        OptionField.F("offset", "Offset", -100000, 100000, 0.01, "Distance per step (0 = extrude in place and pull with the manipulator)"),
        OptionField.E("direction", "Direction", ExtrudeDirections),
        OptionField.I("steps", "Steps (Repeat)", 1, 1000, "Extrude Repeat: number of times to extrude, Offset each"),
        OptionField.B("flip", "Flip Normals", "Reverse the normals of the resulting geometry"),
        OptionField.B("manip", "Manipulator After Extrude", "Switch to the Move manipulator so the new faces/edges can be pulled out"),
    }, "Extrude");

    /// <summary>
    /// 옵션 값 → Core ExtrudeOptions. View 방향은 노드마다 다르므로 호출자가 노드 로컬 공간 뷰 방향을 넘긴다.
    /// </summary>
    /// <param name="v">mesh.extrude 옵션 값.</param>
    /// <param name="viewDirLocal">카메라를 향하는 방향(노드 로컬, 정규화). Direction = View일 때 CustomDirection으로 쓰인다.</param>
    private ExtrudeOptions ExtrudeOptionsFrom(OptionValues v, NVec3 viewDirLocal)
    {
        var dir = (ExtrudeDirection)Math.Clamp(v.Int("direction"), 0, 5);
        return new ExtrudeOptions
        {
            Type = v.Int("type") == 1 ? ExtrudeType.IndividualFaces : ExtrudeType.Region,
            Offset = v.Float("offset"),
            Direction = dir,
            CustomDirection = viewDirLocal,
            Steps = Math.Max(1, v.Int("steps", 1)),
            FlipNormals = v.Bool("flip"),
        };
    }

    /// <summary>
    /// Extrude 구성 이력 파라미터(Offset/Steps/Type/Direction/Flip/View Direction). Action Popup·Properties History에서 편집하면
    /// ExtrudeFromHistory로 다시 읽어 Replay한다. View Direction은 실행 시점 카메라 방향을 고정 저장한다.
    /// </summary>
    private static HistoryParams ExtrudeHistory(ExtrudeOptions o) => new(
        HistoryParam.F("Offset", o.Offset, -100000f, 100000f, 0.01f),
        HistoryParam.I("Steps", o.Steps, 1, 1000),
        HistoryParam.I("Type (0 Region 1 Individual)", (int)o.Type, 0, 1),
        HistoryParam.I("Direction (0 Avg 1 Faces 2 X 3 Y 4 Z 5 View)", (int)o.Direction, 0, 5),
        new HistoryParam { Name = "Flip Normals", Kind = HistoryParamKind.Bool, Value = new NVec3(o.FlipNormals ? 1 : 0, 0, 0), Min = 0, Max = 1, Step = 1 },
        HistoryParam.V("View Direction", o.CustomDirection));

    /// <summary>구성 이력 파라미터 → ExtrudeOptions(이름 접두어로 정수 파라미터를 찾고 범위를 Clamp).</summary>
    private static ExtrudeOptions ExtrudeFromHistory(HistoryParams p)
    {
        int I(string prefix) => p.Items.First(x => x.Name.StartsWith(prefix, StringComparison.Ordinal)).Int;
        return new ExtrudeOptions
        {
            Offset = p.Float("Offset"), Steps = Math.Max(1, p.Int("Steps")),
            Type = I("Type") == 1 ? ExtrudeType.IndividualFaces : ExtrudeType.Region,
            Direction = (ExtrudeDirection)Math.Clamp(I("Direction"), 0, 5),
            FlipNormals = p["Flip Normals"].Bool, CustomDirection = p.Vec("View Direction"),
        };
    }

    /// <summary>카메라를 향하는 방향(뷰 Z)을 노드 로컬 공간으로.</summary>
    /// <remarks>
    /// 활성 뷰포트 카메라의 +Z(월드)를 노드 월드 행렬의 역행렬로 방향 변환(TransformNormal; 이동 무시)한다.
    /// 역행렬이 없거나(스케일 0) 결과 길이가 0이면 월드 방향을 그대로 쓴다.
    /// </remarks>
    private NVec3 ViewDirLocal(SceneNode node)
    {
        var z = Viewport.Camera.GlobalTransform.Basis.Z; // Godot 카메라 +Z = 화면에서 나를 향함
        var w = new NVec3(z.X, z.Y, z.Z);
        if (!NMat.Invert(node.WorldMatrix, out var inv)) return w;
        var l = NVec3.TransformNormal(w, inv);
        return l.LengthSquared() > 1e-20f ? NVec3.Normalize(l) : w;
    }

    /// <summary>Extrude 가능 여부: 면/엣지/정점 모드이고 그 모드의 선택 컴포넌트가 있어야 한다.</summary>
    private bool CanExtrude()
    {
        var sel = Document.Selection;
        return sel.Mode is SelectMode.Face or SelectMode.Edge or SelectMode.Vertex && sel.NodesWithComponents(sel.Mode).Any();
    }

    /// <summary>
    /// mesh.extrudeApply 본체. 순서:
    /// ① 노드별 대상 결정(면 모드 = 선택 면, 엣지 모드 = 선택 엣지, 정점 모드 = 모든 정점이 선택된 면, 없으면 양끝이 선택된 엣지)
    /// ② 대상이 없으면 안내 후 종료(Cube 메시는 면 없는 와이어 엣지가 없어 단독 정점은 돌출 불가)
    /// ③ 한 Undo 그룹에서 노드마다 MeshOpCommand(면 = MeshOps.Extrude → 새 캡 면 선택, 엣지 = ExtrudeEdges → 새 바깥 엣지 선택)
    /// ④ "Manipulator After Extrude"면 축을 Normal로 바꾸고 Move 툴로 전환, 면 Extrude는 BeginExtrudeManip으로 두께 드래그 모드 시작
    /// ⑤ 사용한 옵션 요약을 헬프 라인에 표시.
    /// </summary>
    private void ExtrudeSelection()
    {
        var doc = Document; var sel = doc.Selection;
        var ov = Options("mesh.extrude");
        bool manip = ov.Bool("manip", true);
        // 노드별 대상: 면 또는 엣지(정점 선택은 면 → 엣지 순으로 해석)
        var faceTargets = new List<(NodeId id, int[] faces)>();
        var edgeTargets = new List<(NodeId id, int[] edges)>();
        foreach (var id in sel.NodesWithComponents(sel.Mode))
        {
            var mesh = doc.Find(id)?.Mesh; if (mesh == null) continue;
            var comps = sel.GetComponents(id);
            switch (sel.Mode)
            {
                case SelectMode.Face: faceTargets.Add((id, comps.Faces.ToArray())); break;
                case SelectMode.Edge: edgeTargets.Add((id, comps.Edges.ToArray())); break;
                case SelectMode.Vertex:
                    {
                        var vs = new HashSet<int>(comps.Verts); var tmp = new List<int>();
                        var faces = Enumerable.Range(0, mesh.FaceCount).Where(f => { if (!mesh.Faces[f].Alive) return false; mesh.GetFaceVertices(f, tmp); return tmp.All(vs.Contains); }).ToArray();
                        if (faces.Length > 0) { faceTargets.Add((id, faces)); break; }
                        var edges = Enumerable.Range(0, mesh.EdgeCount).Where(e => mesh.Edges[e].Alive && vs.Contains(mesh.EdgeVertices(e).Item1) && vs.Contains(mesh.EdgeVertices(e).Item2)).ToArray();
                        if (edges.Length > 0) edgeTargets.Add((id, edges));
                        break;
                    }
            }
        }
        if (faceTargets.Count == 0 && edgeTargets.Count == 0)
        {
            HelpLine.Text = "Extrude: select faces, border edges, or vertices that form faces/edges (loose vertices cannot be extruded: Cube meshes have no wire edges).";
            return;
        }
        // used = 마지막으로 사용한 옵션(헬프 라인 요약용; 대상이 하나 이상 있으므로 아래에서 null이 아님)
        ExtrudeOptions? used = null;
        using (doc.Undo.BeginGroup("Extrude"))
        {
            foreach (var (id, faces) in faceTargets)
            {
                var o = ExtrudeOptionsFrom(ov, ViewDirLocal(doc.Get(id))); used = o; var f = faces;
                doc.Undo.Push(new MeshOpCommand("Extrude", id, ExtrudeHistory(o),
                    (m, p) => { var caps = MeshOps.Extrude(m, f, ExtrudeFromHistory(p)); return (caps.Count > 0, SelectMode.Face, caps); }));
            }
            foreach (var (id, edges) in edgeTargets)
            {
                var o = ExtrudeOptionsFrom(ov, ViewDirLocal(doc.Get(id))); used = o; var e = edges;
                doc.Undo.Push(new MeshOpCommand("Extrude Edges", id, ExtrudeHistory(o),
                    (m, p) => { var nf = MeshOps.ExtrudeEdges(m, e, ExtrudeFromHistory(p), out var ne); return (nf.Count > 0, SelectMode.Edge, ne); }));
            }
        }
        // 조작기 모드: 법선 축 Move 툴로 전환. 면 선택이 유지될 때만 파란 화살표가 '두께' 드래그가 된다.
        if (manip)
        {
            ToolContext.AxisOrientation = AxisOrientation.Normal;
            Tools.SetTool("move");
            if (faceTargets.Count > 0 && Document.Selection.Mode == SelectMode.Face) (Tools.Current as MoveTool)?.BeginExtrudeManip();
        }
        var u = used!;
        HelpLine.Text = faceTargets.Count > 0
            ? $"Extrude {(u.Type == ExtrudeType.IndividualFaces ? "Individual Faces" : "Region")}: offset {u.Offset:0.###} × {u.Steps} along {ExtrudeDirections[(int)u.Direction]}." + (manip ? " Drag the blue (normal) arrow to pull the faces out." : "") + " Adjust in the Action Popup."
            : $"Extrude Edges: offset {u.Offset:0.###} × {u.Steps}." + (manip ? " Drag the manipulator to pull the new edges." : "") + " Border edges only.";
    }

    /// <summary>mesh.extrude(옵션 창)/mesh.extrudeApply(실행; 셸프·파이·Ctrl+E) 옵션 쌍을 등록한다.</summary>
    private void RegisterExtrudeActions() => RegisterOptionPair("mesh.extrude", "Extrude", ExtrudeSpec(), ExtrudeSelection, CanExtrude);
}
