using Cube.Core.Commands;
using Cube.Core.Mesh;
using Cube.Core.Scene;
using Cube.Core.Selection;
using NMat = System.Numerics.Matrix4x4;

namespace Cube.App.UI;

/// <summary>
/// Mesh → Booleans(Maya: Union / Difference / Intersection, v0.0.56). 첫 번째로 선택한 메시 오브젝트(A)에 나머지 선택 메시(B…)를 차례로 적용한다.
/// 결과는 A의 메시를 바꾸는 구성 이력 항목 'Boolean'(Operation 파라미터로 나중에 연산을 바꿀 수 있음; B 메시는 실행 시점 사본으로 저장)이며,
/// Delete Operands(기본 켬)면 B 오브젝트는 지운다(같은 Undo 그룹). 세 연산은 각각 옵션 쌍이라 Action Popup에서 연산·피연산자 처리를 다시 고를 수 있다.
/// 머티리얼은 오브젝트 단위라 결과는 A의 머티리얼을 쓴다.
/// </summary>
public partial class Shell
{
    /// <summary>연산 표시 이름(BooleanOperation 정수 순서).</summary>
    private static readonly string[] BooleanOps = { "Union", "Difference", "Intersection" };

    /// <summary>Boolean 옵션 창 정의(연산 기본값만 다르고 필드는 같다).</summary>
    private static OptionSpec BooleanSpec(int op) => new($"{BooleanOps[op]} Options", v => { v.Set("operation", op); v.Set("deleteOperands", 1); }, new[]
    {
        OptionField.E("operation", "Operation", BooleanOps),
        OptionField.B("deleteOperands", "Delete Operands", "Delete the other selected objects after the operation (otherwise they stay as they are)"),
    }, BooleanOps[op]);

    /// <summary>오브젝트 모드에서 메시 오브젝트가 둘 이상 선택돼 있어야 한다.</summary>
    private bool CanBoolean() => Document.Selection.Mode == SelectMode.Object && Document.Selection.Objects.Count(id => Document.Find(id)?.Mesh != null) >= 2;

    /// <summary>
    /// Boolean 실행: A = 선택 순서상 첫 메시, B = 나머지. B마다 A 공간으로 옮긴 사본으로 MeshOpCommand(이력 'Boolean')를 쌓고,
    /// 옵션에 따라 B를 지운 뒤 A만 선택한다. 입력이 닫혀 있지 않으면 헬프 라인에 경고한다.
    /// </summary>
    private void BooleanSelection(string optionKey)
    {
        var doc = Document;
        var ov = Options(optionKey);
        var op = (BooleanOperation)Math.Clamp(ov.Int("operation"), 0, 2);
        bool delete = ov.Bool("deleteOperands", true);
        var ids = doc.Selection.Objects.Where(id => doc.Find(id)?.Mesh != null).ToList();
        if (ids.Count < 2) { HelpLine.Text = "Boolean: select two or more mesh objects (the first one is kept and receives the result)."; return; }
        var a = doc.Get(ids[0]);
        var warnings = new List<string>();
        int faces = 0;
        using (doc.Undo.BeginGroup(BooleanOps[(int)op]))
        {
            foreach (var bid in ids.Skip(1))
            {
                var b = doc.Get(bid);
                NMat.Invert(a.WorldMatrix, out var invA);
                var bToA = b.WorldMatrix * invA;
                var bMesh = b.Mesh!.Clone();
                string bName = b.Name;
                doc.Undo.Push(new MeshOpCommand("Boolean", a.Id, new HistoryParams(HistoryParam.I("Operation (0 Union 1 Difference 2 Intersection)", (int)op, 0, 2)),
                    (m, p) =>
                    {
                        var o = (BooleanOperation)Math.Clamp(p.Items[0].Int, 0, 2);
                        var r = MeshOps.Boolean(m, bMesh, bToA, o, out var rep);
                        if (!rep.ClosedA || !rep.ClosedB) warnings.Add($"{(rep.ClosedA ? bName : a.Name)} is not closed");
                        if (rep.FilledHoles > 0) warnings.Add($"{rep.FilledHoles} tiny gap(s) closed");
                        faces = rep.Faces;
                        m.CopyFrom(r);
                        return (true, null, null);
                    }));
            }
            if (delete) doc.Undo.Push(new DeleteNodesCommand(doc, ids.Skip(1)));
            RecordSelection(s => { s.Mode = SelectMode.Object; s.SelectObjects(new[] { a.Id }); });
        }
        HelpLine.Text = $"Boolean {BooleanOps[(int)op]}: {a.Name} ← {ids.Count - 1} object(s), {faces} faces." + (warnings.Count > 0 ? " Warning: " + string.Join("; ", warnings.Distinct()) + " (results of open meshes may be wrong)." : "");
    }

    /// <summary>mesh.booleanUnion/Difference/Intersection 옵션 쌍(각 *Apply = 실행)을 등록한다.</summary>
    private void RegisterBooleanActions()
    {
        foreach (var (key, op) in new[] { ("mesh.booleanUnion", 0), ("mesh.booleanDifference", 1), ("mesh.booleanIntersection", 2) })
        {
            string k = key;
            RegisterOptionPair(k, BooleanOps[op], BooleanSpec(op), () => BooleanSelection(k), CanBoolean);
        }
    }
}
