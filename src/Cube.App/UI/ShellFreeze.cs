using Cube.Core.Commands;
using Cube.Core.Scene;
using Cube.Core.Selection;

namespace Cube.App.UI;

/// <summary>
/// Maya Modify → Freeze Transformations / Reset Transformations(v0.0.66). 옵션 쌍 <c>edit.freeze</c>(옵션 창: Translate/Rotate/Scale) /
/// <c>edit.freezeApply</c>(실행; Edit 메뉴·오브젝트 Edit 파이·Action Popup)와 <c>edit.resetTransforms</c>(TRS를 0/0/1로, 지오메트리는 그대로, 피벗 유지).
/// 실제 굽기는 Core <see cref="FreezeTransformCommand"/>.
/// </summary>
public partial class Shell
{
    private static readonly OptionField[] FreezeFields =
    {
        OptionField.B("translate", "Translate"), OptionField.B("rotate", "Rotate"), OptionField.B("scale", "Scale"),
    };
    private static void FreezeDefaults(OptionValues v) { v.Set("translate", 1f); v.Set("rotate", 1f); v.Set("scale", 1f); }

    private void RegisterFreezeActions()
    {
        var doc = Document; var sel = doc.Selection;
        bool HasObjects() => sel.Mode == SelectMode.Object && sel.Objects.Any(id => doc.Find(id) is { } n && !n.IsLight);
        RegisterOptionPair("edit.freeze", "Freeze Transformations", new OptionSpec("Freeze Transformations Options", FreezeDefaults, FreezeFields), FreezeSelection, HasObjects);
        Actions.Register("edit.resetTransforms", "Reset Transformations", ResetSelection, canExecute: HasObjects, repeatable: true);
    }

    /// <summary>선택 오브젝트(와 자손)의 TRS를 지오메트리에 굽는다. 결과는 헬프 라인에 요약.</summary>
    private void FreezeSelection()
    {
        var o = Options("edit.freeze");
        var opts = new FreezeOptions { Translate = o.Bool("translate"), Rotate = o.Bool("rotate"), Scale = o.Bool("scale") };
        if (!opts.Translate && !opts.Rotate && !opts.Scale) { HelpLine.Text = "Freeze Transformations: nothing selected to freeze (enable Translate, Rotate or Scale in the options)."; return; }
        var ids = Document.Selection.Objects.ToArray();
        if (ids.Length == 0) return;
        var cmd = new FreezeTransformCommand(ids, opts);
        Document.Undo.Push(cmd);
        var parts = new List<string>();
        if (opts.Translate) parts.Add("translate"); if (opts.Rotate) parts.Add("rotate"); if (opts.Scale) parts.Add("scale");
        string msg = $"Freeze Transformations ({string.Join(", ", parts)}): {cmd.Frozen} node(s) frozen.";
        if (cmd.SkippedSkinned > 0) msg += $" {cmd.SkippedSkinned} skinned mesh(es) skipped (detach skin first).";
        if (cmd.SkippedLights > 0) msg += $" {cmd.SkippedLights} light(s) skipped.";
        HelpLine.Text = msg;
    }

    /// <summary>Maya Reset Transformations: 선택 오브젝트의 TRS를 0/0/1로(지오메트리는 그대로라 모양이 원래 오브젝트 공간으로 돌아간다). 피벗은 유지.</summary>
    private void ResetSelection()
    {
        var ids = Document.Selection.Objects.Where(id => Document.Find(id) is { } n && !n.IsLight).ToArray();
        if (ids.Length == 0) return;
        var before = ids.Select(id => Document.Find(id)!.Local).ToArray();
        var after = before.Select(t => new Transform3(System.Numerics.Vector3.Zero, System.Numerics.Vector3.Zero, System.Numerics.Vector3.One, t.Pivot)).ToArray();
        var cmd = new TransformNodesCommand("Reset Transformations", ids, before, after);
        if (!cmd.IsNoop) Document.Undo.Push(cmd);
        HelpLine.Text = $"Reset Transformations: {ids.Length} node(s).";
    }
}
