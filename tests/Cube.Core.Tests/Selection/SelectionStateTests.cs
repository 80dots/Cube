using Cube.Core.Commands;
using Cube.Core.Scene;
using Cube.Core.Selection;

namespace Cube.Core.Tests.Selection;

/// <summary>
/// <see cref="SelectionState"/>의 컴포넌트 모드 단일 대상(Maya hilite) 규칙, 수식어 적용, 선택 Undo(SelectionCommand)를 검증한다.
/// </summary>
public class SelectionStateTests
{
    /// <summary>큐브 두 개가 있는 문서와 두 노드 ID.</summary>
    private static (Document doc, NodeId a, NodeId b) TwoCubes()
    {
        var doc = new Document();
        var ca = CreatePrimitiveCommand.Cube(doc); doc.Undo.Push(ca);
        var cb = CreatePrimitiveCommand.Cube(doc); doc.Undo.Push(cb);
        return (doc, ca.Node.Id, cb.Node.Id);
    }

    /// <summary>
    /// 두 오브젝트를 선택한 채 컴포넌트 모드로 가면 활성(마지막) 오브젝트만 대상이 되고, 다른 노드의 항목은 클릭/마키로 집혀도 버려진다.
    /// 컴포넌트 모드에서 SelectObjects(Outliner)는 대상만 바꾸고 이전 대상의 컴포넌트 선택은 해제한다.
    /// </summary>
    [Fact]
    public void ComponentMode_SingleTarget()
    {
        var (doc, a, b) = TwoCubes();
        var sel = doc.Selection;
        sel.SelectObjects(new[] { a, b });
        sel.Mode = SelectMode.Vertex;
        Assert.Equal(b, sel.ComponentTarget);
        Assert.Equal(new[] { b }, sel.Objects);
        sel.Apply(new[] { new SelItem(a, 0), new SelItem(b, 1) }, SelectModifier.Replace);
        Assert.False(sel.Components.ContainsKey(a));
        Assert.Equal(new[] { 1 }, sel.GetComponents(b).Verts);
        Assert.True(sel.IsComponentEditable(b));
        Assert.False(sel.IsComponentEditable(a));

        sel.SelectObjects(new[] { a });
        Assert.Equal(a, sel.ComponentTarget);
        Assert.Empty(sel.NodesWithComponents(SelectMode.Vertex));
        sel.Mode = SelectMode.Object;
        Assert.Equal(NodeId.None, sel.ComponentTarget);
    }

    /// <summary>오브젝트 선택 없이 컴포넌트 모드에 들어가면 처음 집은 항목의 노드가 대상이 된다.</summary>
    [Fact]
    public void ComponentMode_NoTarget_FirstPickedNodeBecomesTarget()
    {
        var (doc, a, b) = TwoCubes();
        var sel = doc.Selection;
        sel.ClearAll();
        sel.Mode = SelectMode.Face;
        Assert.Equal(NodeId.None, sel.ComponentTarget);
        sel.Apply(new[] { new SelItem(a, 2), new SelItem(b, 3) }, SelectModifier.Replace);
        Assert.Equal(a, sel.ComponentTarget);
        Assert.Equal(new[] { 2 }, sel.GetComponents(a).Faces);
        Assert.False(sel.Components.ContainsKey(b));
    }

    /// <summary>Maya 수식어: Shift = 토글, Ctrl = 제거, Ctrl+Shift = 추가, 빈 Replace = 현재 모드 해제(다른 모드 선택은 유지).</summary>
    [Fact]
    public void Modifiers_FollowMayaRules()
    {
        var (doc, a, _) = TwoCubes();
        var sel = doc.Selection;
        sel.SelectObjects(new[] { a });
        sel.Mode = SelectMode.Edge;
        sel.Apply(new[] { new SelItem(a, 1), new SelItem(a, 2) }, SelectModifier.Replace);
        sel.Apply(new[] { new SelItem(a, 2), new SelItem(a, 3) }, SelectModifier.Toggle);
        Assert.Equal(new HashSet<int> { 1, 3 }, sel.GetComponents(a).Edges);
        sel.Apply(new[] { new SelItem(a, 1) }, SelectModifier.Remove);
        Assert.Equal(new HashSet<int> { 3 }, sel.GetComponents(a).Edges);
        sel.Apply(new[] { new SelItem(a, 3), new SelItem(a, 4) }, SelectModifier.Add);
        Assert.Equal(new HashSet<int> { 3, 4 }, sel.GetComponents(a).Edges);
        sel.Mode = SelectMode.Vertex;
        sel.Apply(new[] { new SelItem(a, 0) }, SelectModifier.Replace);
        sel.Apply(Array.Empty<SelItem>(), SelectModifier.Replace);
        Assert.Empty(sel.GetComponents(a).Verts);
        Assert.Equal(new HashSet<int> { 3, 4 }, sel.GetComponents(a).Edges);
    }

    /// <summary>SelectionCommand(Record)는 모드·대상·컴포넌트를 함께 Undo/Redo하고, 아무것도 안 바뀌면 Noop이다.</summary>
    [Fact]
    public void SelectionCommand_UndoRedo_RestoresModeTargetComponents()
    {
        var (doc, a, b) = TwoCubes();
        var sel = doc.Selection;
        sel.SelectObjects(new[] { a });
        var before = sel.Capture();
        var cmd = SelectionCommand.Record(doc, s => { s.Mode = SelectMode.Face; s.Apply(new[] { new SelItem(a, 5) }, SelectModifier.Replace); });
        Assert.False(cmd.IsNoop);
        doc.Undo.Push(cmd, alreadyApplied: true);
        doc.Undo.Undo();
        Assert.True(SelectionState.AreEqual(before, sel.Capture()));
        doc.Undo.Redo();
        Assert.Equal(SelectMode.Face, sel.Mode);
        Assert.Equal(a, sel.ComponentTarget);
        Assert.Equal(new[] { 5 }, sel.GetComponents(a).Faces);
        Assert.True(SelectionCommand.Record(doc, s => { }).IsNoop);
        _ = b;
    }
}
