using Cube.Core.Commands;
using Cube.Core.Mesh;
using Cube.Core.Scene;
using Cube.Core.Selection;
using NMat = System.Numerics.Matrix4x4;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;

namespace Cube.App.UI;

/// <summary>
/// Mesh → Array(Blender Array 모디파이어 기준, v0.0.55). 선택한 메시 오브젝트를 규칙에 따라 여러 개 복사·배치한다.
/// 규칙: 개수(Fixed Count) 또는 길이 채우기(Fit Length), Relative Offset(경계 상자 크기 배율), Constant Offset(거리),
/// Offset Transform(복사본마다 누적되는 회전·스케일 + 중심 = Blender Object Offset 대용), Merge(이웃 복사본 정점 병합 + First Last), UV 오프셋.
/// 출력: Single Mesh = 그 오브젝트의 메시를 Array 결과로 바꾸는 구성 이력 항목(Properties History·Action Popup에서 다시 조정),
/// Separate Objects = 복사본마다 새 오브젝트(Maya Duplicate Special처럼; 오브젝트 트랜스폼에 단계 변환을 곱함).
/// </summary>
public partial class Shell
{
    /// <summary>Array 옵션 창 정의. 기본값 = Single Mesh, Fixed Count 3, Relative (1,0,0) — 경계 상자 폭만큼 X로 붙여 나열.</summary>
    private static OptionSpec ArraySpec() => new("Array Options", v =>
    {
        v.Set("output", 0); v.Set("fit", 0); v.Set("count", 3); v.Set("length", 4f);
        v.Set("useRelative", 1); v.Set("relative", new Godot.Vector3(1, 0, 0));
        v.Set("useConstant", 0); v.Set("constant", new Godot.Vector3(0.1f, 0, 0));
        v.Set("useTransform", 0); v.Set("rotate", Godot.Vector3.Zero); v.Set("scale", Godot.Vector3.One); v.Set("center", Godot.Vector3.Zero);
        v.Set("merge", 0); v.Set("mergeDistance", 0.01f); v.Set("firstLast", 0);
        v.Set("uvU", 0f); v.Set("uvV", 0f);
    }, new[]
    {
        OptionField.E("output", "Output", "Single Mesh", "Separate Objects"),
        OptionField.E("fit", "Fit Type", "Fixed Count", "Fit Length"),
        OptionField.I("count", "Count", 1, ArrayOptions.MaxCount, "Total number of instances including the original (Fixed Count)"),
        OptionField.F("length", "Length", 0, 100000, 0.01, "Length to fill with copies, measured along the per-copy offset (Fit Length)"),
        OptionField.B("useRelative", "Relative Offset", "Offset each copy by a multiple of the object's bounding box size"),
        OptionField.V("relative", "Relative Factor", 0.01),
        OptionField.B("useConstant", "Constant Offset", "Offset each copy by a fixed distance"),
        OptionField.V("constant", "Constant Distance", 0.01),
        OptionField.B("useTransform", "Offset Transform", "Rotate/scale each copy cumulatively around the Center (like Blender's Object Offset)"),
        OptionField.V("rotate", "Rotate per Copy (°)", 0.5),
        OptionField.V("scale", "Scale per Copy", 0.01),
        OptionField.V("center", "Center (local)", 0.01),
        OptionField.B("merge", "Merge", "Weld vertices of neighbouring copies that lie within the distance (Single Mesh)"),
        OptionField.F("mergeDistance", "Merge Distance", 0, 1000, 0.001),
        OptionField.B("firstLast", "Merge First and Last", "Also weld the last copy to the first (closes circular arrays)"),
        OptionField.F("uvU", "Offset U", -1000, 1000, 0.01, "UV offset added per copy (Single Mesh, current UV set)"),
        OptionField.F("uvV", "Offset V", -1000, 1000, 0.01),
    }, "Array");

    /// <summary>Godot 벡터 → System.Numerics.</summary>
    private static NVec3 N(Godot.Vector3 v) => new(v.X, v.Y, v.Z);

    /// <summary>옵션 값 → Core ArrayOptions.</summary>
    private static ArrayOptions ArrayOptionsFrom(OptionValues v) => new()
    {
        FitType = v.Int("fit") == 1 ? ArrayFitType.FitLength : ArrayFitType.FixedCount,
        Count = Math.Clamp(v.Int("count", 3), 1, ArrayOptions.MaxCount), Length = v.Float("length", 4f),
        UseRelative = v.Bool("useRelative", true), Relative = N(v.Vec("relative", new Godot.Vector3(1, 0, 0))),
        UseConstant = v.Bool("useConstant"), Constant = N(v.Vec("constant")),
        UseTransform = v.Bool("useTransform"), RotateDegrees = N(v.Vec("rotate")), Scale = N(v.Vec("scale", Godot.Vector3.One)), Center = N(v.Vec("center")),
        Merge = v.Bool("merge"), MergeDistance = v.Float("mergeDistance", 0.01f), MergeFirstLast = v.Bool("firstLast"),
        UvOffset = new NVec2(v.Float("uvU"), v.Float("uvV")),
    };

    /// <summary>
    /// Array 구성 이력 파라미터(Properties History에서 개별 조정). 불 옵션은 Bool 종류, 벡터는 Vector3.
    /// </summary>
    private static HistoryParams ArrayHistory(ArrayOptions o)
    {
        static HistoryParam B(string n, bool b) => new() { Name = n, Kind = HistoryParamKind.Bool, Value = new NVec3(b ? 1 : 0, 0, 0), Min = 0, Max = 1, Step = 1 };
        return new HistoryParams(
            HistoryParam.I("Fit (0 Count 1 Length)", (int)o.FitType, 0, 1),
            HistoryParam.I("Count", o.Count, 1, ArrayOptions.MaxCount),
            HistoryParam.F("Length", o.Length, 0f, 100000f, 0.01f),
            B("Relative Offset", o.UseRelative), HistoryParam.V("Relative Factor", o.Relative, 0.01f),
            B("Constant Offset", o.UseConstant), HistoryParam.V("Constant Distance", o.Constant, 0.01f),
            B("Offset Transform", o.UseTransform), HistoryParam.V("Rotate per Copy", o.RotateDegrees, 0.5f), HistoryParam.V("Scale per Copy", o.Scale, 0.01f), HistoryParam.V("Center", o.Center, 0.01f),
            B("Merge", o.Merge), HistoryParam.F("Merge Distance", o.MergeDistance, 0f, 1000f, 0.001f), B("Merge First Last", o.MergeFirstLast),
            HistoryParam.V("UV Offset", new NVec3(o.UvOffset.X, o.UvOffset.Y, 0), 0.01f));
    }

    /// <summary>구성 이력 파라미터 → ArrayOptions.</summary>
    private static ArrayOptions ArrayFromHistory(HistoryParams p)
    {
        var uv = p.Vec("UV Offset");
        return new ArrayOptions
        {
            FitType = p.Items.First(x => x.Name.StartsWith("Fit", StringComparison.Ordinal)).Int == 1 ? ArrayFitType.FitLength : ArrayFitType.FixedCount,
            Count = Math.Clamp(p.Int("Count"), 1, ArrayOptions.MaxCount), Length = p.Float("Length"),
            UseRelative = p["Relative Offset"].Bool, Relative = p.Vec("Relative Factor"),
            UseConstant = p["Constant Offset"].Bool, Constant = p.Vec("Constant Distance"),
            UseTransform = p["Offset Transform"].Bool, RotateDegrees = p.Vec("Rotate per Copy"), Scale = p.Vec("Scale per Copy"), Center = p.Vec("Center"),
            Merge = p["Merge"].Bool, MergeDistance = p.Float("Merge Distance"), MergeFirstLast = p["Merge First Last"].Bool,
            UvOffset = new NVec2(uv.X, uv.Y),
        };
    }

    /// <summary>오브젝트 모드에서 메시 오브젝트가 하나 이상 선택돼 있어야 한다.</summary>
    private bool CanArray() => Document.Selection.Mode == SelectMode.Object && HasMeshSelection();

    /// <summary>
    /// mesh.arrayApply 본체. 선택한 메시 오브젝트마다(한 Undo 그룹):
    /// Single Mesh = MeshOpCommand(이력 'Array'; 원본 컴포넌트 ID 유지), Separate Objects = 복사본 i의 로컬 행렬 = Step^i · 원래 로컬 행렬인 새 노드.
    /// </summary>
    private void ArraySelection()
    {
        var doc = Document;
        var ov = Options("mesh.array");
        var o = ArrayOptionsFrom(ov);
        bool separate = ov.Int("output") == 1;
        var ids = doc.Selection.Objects.Where(id => doc.Find(id)?.Mesh != null).ToArray();
        if (ids.Length == 0) { HelpLine.Text = "Array: select one or more mesh objects."; return; }
        int total = 0;
        using (doc.Undo.BeginGroup("Array"))
        {
            if (!separate)
            {
                foreach (var id in ids)
                {
                    total = Math.Max(total, MeshOps.ArrayCount(doc.Get(id).Mesh!, o));
                    doc.Undo.Push(new MeshOpCommand("Array", id, ArrayHistory(o),
                        (m, p) => { int n = MeshOps.MakeArray(m, ArrayFromHistory(p)); return (n > 1, null, null); }));
                }
            }
            else
            {
                var created = new List<NodeId>(ids);
                foreach (var id in ids)
                {
                    var src = doc.Get(id); var mesh = src.Mesh!;
                    int count = MeshOps.ArrayCount(mesh, o); total = Math.Max(total, count);
                    var step = MeshOps.ArrayStep(mesh, o);
                    var local = src.Local.ToMatrix();
                    var acc = NMat.Identity;
                    var parent = src.Parent != null && !src.Parent.IsRoot ? src.Parent.Id : NodeId.None;
                    for (int i = 1; i < count; i++)
                    {
                        acc *= step;
                        // 메시 정점 v의 복사본 = v·Step^i·Local → 복사본 노드의 로컬 행렬 = Step^i·Local(피벗 유지)
                        var copy = new SceneNode
                        {
                            Name = doc.UniqueName(src.Name), Visible = src.Visible, MaterialId = src.MaterialId,
                            Local = Transform3.FromMatrix(acc * local, src.Local.Pivot),
                        };
                        var cm = mesh.Clone();
                        if (o.UvOffset != NVec2.Zero)
                            for (int h = 0; h < cm.Hes.Count; h++) { var he = cm.Hes[h]; he.Uv0 += o.UvOffset * i; cm.Hes[h] = he; }
                        copy.Shape = new MeshShape(cm);
                        doc.Undo.Push(new AddNodeCommand("Array", copy, parent));
                        created.Add(copy.Id);
                    }
                }
                RecordSelection(s => { s.Mode = SelectMode.Object; s.SelectObjects(created); });
            }
        }
        HelpLine.Text = $"Array: {total} instance(s) as {(separate ? "separate objects" : "one mesh")}. Adjust in the Action Popup" + (separate ? "." : " or the History in Properties.");
    }

    /// <summary>mesh.array(옵션 창)/mesh.arrayApply(실행; Mesh 메뉴·셸프·Edit 파이) 옵션 쌍을 등록한다.</summary>
    private void RegisterArrayActions() => RegisterOptionPair("mesh.array", "Array", ArraySpec(), ArraySelection, CanArray);
}
