using Cube.Core.Commands;
using Cube.Core.Scene;
using Cube.Core.Selection;

namespace Cube.Core.Tests.Scene;

/// <summary><see cref="SetVisibilityCommand"/>(Display → Hide Selection / Show)의 가시성·선택 변경과 Undo/Redo를 검증한다.</summary>
public class VisibilityTests
{
    /// <summary>
    /// 두 큐브 중 하나를 숨기면 Visible=false가 되고 선택에서 빠지며, Undo하면 가시성과 선택이 돌아오고 Redo하면 다시 숨겨진다.
    /// 이미 보이는 노드를 Show하는 명령은 비어 있다(Undo 스택에 넣지 않음).
    /// </summary>
    [Fact]
    public void Hide_DeselectsAndUndoes()
    {
        var doc = new Document();
        var a = CreatePrimitiveCommand.Cube(doc); doc.Undo.Push(a);
        var b = CreatePrimitiveCommand.Cube(doc); doc.Undo.Push(b);
        doc.Selection.SelectObjects(new[] { a.Node.Id, b.Node.Id });

        Assert.True(new SetVisibilityCommand(doc, new[] { a.Node.Id }, true).IsEmpty);
        var hide = new SetVisibilityCommand(doc, new[] { a.Node.Id }, false);
        Assert.False(hide.IsEmpty);
        doc.Undo.Push(hide);
        Assert.False(a.Node.Visible);
        Assert.Equal(new[] { b.Node.Id }, doc.Selection.Objects);

        doc.Undo.Undo();
        Assert.True(a.Node.Visible);
        Assert.Equal(new[] { a.Node.Id, b.Node.Id }, doc.Selection.Objects);
        doc.Undo.Redo();
        Assert.False(a.Node.Visible);
        Assert.Equal(new[] { b.Node.Id }, doc.Selection.Objects);

        var show = new SetVisibilityCommand(doc, doc.Nodes.Keys, true);
        doc.Undo.Push(show);
        Assert.True(a.Node.Visible);
        Assert.Equal(SelectMode.Object, doc.Selection.Mode);
    }
}
