using System.Numerics;
using Cube.Core.Commands;
using Cube.Core.Scene;
using Cube.Core.Selection;

namespace Cube.Core.Tests.Commands;

public class UndoStackTests
{
    private sealed class CounterCommand : ICommand
    {
        public static int Value;
        private readonly int _delta;
        public CounterCommand(int delta) { _delta = delta; }
        public string Name => "Counter";
        public void Do(Document doc) => Value += _delta;
        public void Undo(Document doc) => Value -= _delta;
    }

    [Fact]
    public void Push_Undo_Redo_RoundTrip()
    {
        var doc = new Document();
        CounterCommand.Value = 0;
        doc.Undo.Push(new CounterCommand(1));
        doc.Undo.Push(new CounterCommand(10));
        Assert.Equal(11, CounterCommand.Value);
        Assert.True(doc.Undo.Undo()); Assert.Equal(1, CounterCommand.Value);
        Assert.True(doc.Undo.Undo()); Assert.Equal(0, CounterCommand.Value);
        Assert.False(doc.Undo.Undo());
        Assert.True(doc.Undo.Redo()); Assert.Equal(1, CounterCommand.Value);
        doc.Undo.Push(new CounterCommand(100)); // redo 분기 소멸
        Assert.False(doc.Undo.CanRedo);
        Assert.Equal(101, CounterCommand.Value);
    }

    [Fact]
    public void AlreadyApplied_SkipsFirstDo()
    {
        var doc = new Document();
        CounterCommand.Value = 5;
        doc.Undo.Push(new CounterCommand(5), alreadyApplied: true);
        Assert.Equal(5, CounterCommand.Value);
        doc.Undo.Undo(); Assert.Equal(0, CounterCommand.Value);
        doc.Undo.Redo(); Assert.Equal(5, CounterCommand.Value);
    }

    [Fact]
    public void Group_CollapsesToOneStep()
    {
        var doc = new Document();
        CounterCommand.Value = 0;
        using (doc.Undo.BeginGroup("Batch"))
        {
            doc.Undo.Push(new CounterCommand(1));
            doc.Undo.Push(new CounterCommand(2));
            doc.Undo.Push(new CounterCommand(3));
        }
        Assert.Equal(1, doc.Undo.UndoCount);
        Assert.Equal("Batch", doc.Undo.UndoName);
        doc.Undo.Undo(); Assert.Equal(0, CounterCommand.Value);
        doc.Undo.Redo(); Assert.Equal(6, CounterCommand.Value);
    }

    [Fact]
    public void MaxSteps_DropsOldest()
    {
        var doc = new Document();
        doc.Undo.MaxSteps = 3;
        CounterCommand.Value = 0;
        for (int i = 0; i < 5; i++) doc.Undo.Push(new CounterCommand(1));
        Assert.Equal(3, doc.Undo.UndoCount);
    }

    [Fact]
    public void CreatePrimitive_UndoRedo_KeepsNodeIdAndSelection()
    {
        var doc = new Document();
        var cmd = CreatePrimitiveCommand.Cube(doc);
        doc.Undo.Push(cmd);
        var id = cmd.Node.Id;
        Assert.False(id.IsNone);
        Assert.Equal("pCube1", cmd.Node.Name);
        Assert.Equal(id, doc.Selection.ActiveObject);
        Assert.Single(doc.Nodes);

        doc.Undo.Undo();
        Assert.Empty(doc.Nodes);
        Assert.True(doc.Selection.IsEmpty);

        doc.Undo.Redo();
        Assert.Equal(id, cmd.Node.Id);
        Assert.NotNull(doc.Find(id));
        Assert.Equal(id, doc.Selection.ActiveObject);

        var cmd2 = CreatePrimitiveCommand.Cube(doc);
        doc.Undo.Push(cmd2);
        Assert.Equal("pCube2", cmd2.Node.Name);
    }

    [Fact]
    public void SelectionCommand_RestoresComponents()
    {
        var doc = new Document();
        var cube = CreatePrimitiveCommand.Cube(doc); doc.Undo.Push(cube);
        var id = cube.Node.Id;
        var sel = SelectionCommand.Record(doc, s =>
        {
            s.Mode = SelectMode.Face;
            s.Apply(new[] { new SelItem(id, 0), new SelItem(id, 2) }, SelectModifier.Replace);
        });
        Assert.False(sel.IsNoop);
        doc.Undo.Push(sel, alreadyApplied: true);
        Assert.True(doc.Selection.IsComponentSelected(id, SelectMode.Face, 2));

        doc.Undo.Undo();
        Assert.Equal(SelectMode.Object, doc.Selection.Mode);
        Assert.False(doc.Selection.IsComponentSelected(id, SelectMode.Face, 2));
        Assert.Equal(id, doc.Selection.ActiveObject);

        doc.Undo.Redo();
        Assert.Equal(SelectMode.Face, doc.Selection.Mode);
        Assert.Equal(2, doc.Selection.GetComponents(id).Faces.Count);
    }

    [Fact]
    public void Selection_Modifiers_FollowMayaRules()
    {
        var doc = new Document();
        var a = CreatePrimitiveCommand.Cube(doc); doc.Undo.Push(a);
        var b = CreatePrimitiveCommand.Sphere(doc); doc.Undo.Push(b);
        var s = doc.Selection;
        s.Apply(new[] { new SelItem(a.Node.Id, -1) }, SelectModifier.Replace);
        Assert.Equal(new[] { a.Node.Id }, s.Objects);
        s.Apply(new[] { new SelItem(b.Node.Id, -1) }, SelectModifier.Toggle);
        Assert.Equal(new[] { a.Node.Id, b.Node.Id }, s.Objects);
        Assert.Equal(b.Node.Id, s.ActiveObject);
        s.Apply(new[] { new SelItem(a.Node.Id, -1) }, SelectModifier.Toggle);
        Assert.Equal(new[] { b.Node.Id }, s.Objects);
        s.Apply(new[] { new SelItem(b.Node.Id, -1) }, SelectModifier.Remove);
        Assert.True(s.IsEmpty);
        s.Apply(Array.Empty<SelItem>(), SelectModifier.Replace);
        Assert.True(s.IsEmpty);
    }

    [Fact]
    public void DeleteNodes_UndoRestoresOrder()
    {
        var doc = new Document();
        var a = CreatePrimitiveCommand.Cube(doc); doc.Undo.Push(a);
        var b = CreatePrimitiveCommand.Cube(doc); doc.Undo.Push(b);
        var c = CreatePrimitiveCommand.Cube(doc); doc.Undo.Push(c);
        doc.Undo.Push(new DeleteNodesCommand(doc, new[] { b.Node.Id }));
        Assert.Equal(new[] { a.Node, c.Node }, doc.Root.Children);
        doc.Undo.Undo();
        Assert.Equal(new[] { a.Node, b.Node, c.Node }, doc.Root.Children);
    }

    [Fact]
    public void TransformNodes_AndMoveVertices_RoundTrip()
    {
        var doc = new Document();
        var a = CreatePrimitiveCommand.Cube(doc); doc.Undo.Push(a);
        var id = a.Node.Id;
        var before = a.Node.Local;
        var after = before; after.Translation = new Vector3(1, 2, 3);
        a.Node.Local = after; // 드래그 프리뷰처럼 직접 적용
        doc.Undo.Push(new TransformNodesCommand("Move", new[] { id }, new[] { before }, new[] { after }), alreadyApplied: true);
        doc.Undo.Undo();
        Assert.Equal(Vector3.Zero, a.Node.Local.Translation);
        doc.Undo.Redo();
        Assert.Equal(new Vector3(1, 2, 3), a.Node.Local.Translation);

        var mesh = a.Node.Mesh!;
        var p0 = mesh.Verts[0].Position;
        doc.Undo.Push(new MoveVerticesCommand("Move", id, new[] { 0 }, new[] { p0 }, new[] { p0 + Vector3.UnitY }));
        Assert.Equal(p0 + Vector3.UnitY, mesh.Verts[0].Position);
        doc.Undo.Undo();
        Assert.Equal(p0, mesh.Verts[0].Position);
    }

    [Fact]
    public void Transform3_Matrix_RoundTrip()
    {
        var t = new Transform3(new Vector3(1, 2, 3), new Vector3(30, -45, 60), new Vector3(2, 2, 2));
        var m = t.ToMatrix();
        var back = Transform3.FromMatrix(m);
        Assert.True(Vector3.Distance(t.Translation, back.Translation) < 1e-4f);
        Assert.True(Vector3.Distance(t.Scale, back.Scale) < 1e-4f);
        Assert.True(Vector3.Distance(t.RotationDegrees, back.RotationDegrees) < 1e-2f, $"{back.RotationDegrees}");
        // 쿼터니언 경로와 행렬 경로가 일치
        var mq = Matrix4x4.CreateScale(t.Scale) * Matrix4x4.CreateFromQuaternion(t.Rotation) * Matrix4x4.CreateTranslation(t.Translation);
        for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) Assert.True(MathF.Abs(m[r, c] - mq[r, c]) < 1e-4f, $"[{r},{c}] {m[r, c]} vs {mq[r, c]}");
    }
}
