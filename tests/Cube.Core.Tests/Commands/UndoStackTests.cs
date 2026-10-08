using System.Numerics;
using Cube.Core.Commands;
using Cube.Core.Scene;
using Cube.Core.Selection;

namespace Cube.Core.Tests.Commands;

/// <summary>
/// 자체 <c>UndoStack</c>(Godot UndoRedo 미사용)과 기본 명령들을 검증한다: Push/Undo/Redo, alreadyApplied(드래그 프리뷰 후 커밋),
/// 그룹(한 단계로 묶기), 최대 단계 수, 프리미티브 생성·선택·노드 삭제·트랜스폼·정점 이동 명령, Maya 선택 수식어 규칙, Transform3 행렬 왕복.
/// </summary>
public class UndoStackTests
{
    /// <summary>
    /// 테스트 전용 명령: Do는 정적 카운터에 delta를 더하고 Undo는 뺀다. 문서 상태 없이 스택 동작만 관찰하기 위해 쓴다.
    /// </summary>
    private sealed class CounterCommand : ICommand
    {
        /// <summary>모든 CounterCommand가 공유하는 누적 값(각 테스트 시작 시 초기화한다).</summary>
        public static int Value;
        /// <summary>이 명령이 더하고 빼는 양.</summary>
        private readonly int _delta;
        /// <summary>더할 양을 지정해 명령을 만든다.</summary>
        public CounterCommand(int delta) { _delta = delta; }
        /// <summary>Undo 목록에 표시될 명령 이름.</summary>
        public string Name => "Counter";
        /// <summary>실행: 카운터에 delta를 더한다.</summary>
        public void Do(Document doc) => Value += _delta;
        /// <summary>되돌리기: 카운터에서 delta를 뺀다.</summary>
        public void Undo(Document doc) => Value -= _delta;
    }

    /// <summary>
    /// 두 명령을 푸시한 뒤 Undo/Redo가 순서대로 값을 되돌리고 다시 적용하는지, 더 Undo할 것이 없으면 false인지,
    /// Undo 후 새 명령을 푸시하면 Redo 분기가 사라지는지(CanRedo = false) 확인한다.
    /// </summary>
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

    /// <summary>
    /// alreadyApplied: true로 푸시하면 처음 Do를 건너뛰어야 한다(드래그 중 이미 문서에 반영한 변경을 커밋하는 경로).
    /// 이후 Undo/Redo는 정상적으로 동작해야 한다.
    /// </summary>
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

    /// <summary>
    /// <c>BeginGroup</c> 범위 안에서 푸시한 명령 3개가 그룹 이름("Batch")의 Undo 한 단계로 합쳐지고,
    /// 한 번의 Undo/Redo로 셋 모두 되돌려지고 다시 적용되는지 확인한다.
    /// </summary>
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

    /// <summary>MaxSteps를 넘게 푸시하면 가장 오래된 단계부터 버려져 Undo 개수가 최대값(3)을 넘지 않아야 한다.</summary>
    [Fact]
    public void MaxSteps_DropsOldest()
    {
        var doc = new Document();
        doc.Undo.MaxSteps = 3;
        CounterCommand.Value = 0;
        for (int i = 0; i < 5; i++) doc.Undo.Push(new CounterCommand(1));
        Assert.Equal(3, doc.Undo.UndoCount);
    }

    /// <summary>
    /// 큐브 생성 명령은 노드를 추가하고 그 노드를 선택(활성 오브젝트)해야 한다. Undo하면 노드와 선택이 사라지고,
    /// Redo하면 같은 NodeId로 되살아나 다시 선택되어야 한다(ID 재사용 없음). 다음 큐브 이름은 pCube2가 되어야 한다.
    /// </summary>
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

    /// <summary>
    /// 선택 변경도 Maya처럼 Undo 가능해야 한다: <c>SelectionCommand.Record</c>로 면 모드 전환 + 면 2개 선택을 기록하고,
    /// Undo하면 오브젝트 모드·원래 오브젝트 선택으로, Redo하면 면 모드·면 2개 선택으로 돌아오는지 확인한다.
    /// </summary>
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

    /// <summary>
    /// 오브젝트 선택 수식어: Replace는 교체, Toggle(Shift)은 있으면 빼고 없으면 추가하며 마지막 추가가 활성 오브젝트,
    /// Remove(Ctrl)는 제거, 빈 목록으로 Replace하면 선택 해제가 되어야 한다.
    /// </summary>
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

    /// <summary>가운데 노드를 삭제한 뒤 Undo하면 형제 순서(a, b, c)까지 원래대로 복원되어야 한다(아웃라이너 순서 보존).</summary>
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

    /// <summary>
    /// 오브젝트 이동(TransformNodesCommand, 미리 적용 후 커밋)과 정점 이동(MoveVerticesCommand)의 Undo/Redo가
    /// 각각 트랜스폼과 정점 위치를 정확히 되돌리는지 확인한다.
    /// </summary>
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

        // 이어서 컴포넌트 편집: 정점 0을 +Y로 옮기는 명령(alreadyApplied 아님 → Push 시 Do 실행).
        var mesh = a.Node.Mesh!;
        var p0 = mesh.Verts[0].Position;
        doc.Undo.Push(new MoveVerticesCommand("Move", id, new[] { 0 }, new[] { p0 }, new[] { p0 + Vector3.UnitY }));
        Assert.Equal(p0 + Vector3.UnitY, mesh.Verts[0].Position);
        doc.Undo.Undo();
        Assert.Equal(p0, mesh.Verts[0].Position);
    }

    /// <summary>
    /// 피벗 없는 TRS를 행렬로 만들었다 <c>FromMatrix</c>로 되돌리면 이동·스케일·오일러 회전이 복원되어야 하고,
    /// <c>ToMatrix</c> 결과가 S × R(쿼터니언) × T로 직접 만든 행렬과 일치해야 한다(행벡터 규약 확인).
    /// </summary>
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
