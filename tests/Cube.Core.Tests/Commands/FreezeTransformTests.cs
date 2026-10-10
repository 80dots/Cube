using System.Numerics;
using Cube.Core.Commands;
using Cube.Core.Scene;
using Cube.Core.Mesh;

namespace Cube.Core.Tests.Commands;

/// <summary>Freeze Transformations(v0.0.66): 전체/부분 얼리기, 월드 모양 유지, 피벗 유지, 자식 보정, 음수 스케일 면 뒤집기, Undo.</summary>
public class FreezeTransformTests
{
    private static Vector3[] WorldVerts(SceneNode n) { var w = n.WorldMatrix; return n.Mesh!.Verts.Where(v => v.Alive).Select(v => Vector3.Transform(v.Position, w)).ToArray(); }
    private static void AssertSame(Vector3[] a, Vector3[] b, float eps = 1e-4f) { Assert.Equal(a.Length, b.Length); for (int i = 0; i < a.Length; i++) Assert.True(Vector3.Distance(a[i], b[i]) < eps, $"#{i} {a[i]} vs {b[i]}"); }

    [Fact]
    public void FreezeAll_BakesGeometry_KeepsWorldAndPivot_Undoes()
    {
        var doc = new Document();
        var add = CreatePrimitiveCommand.Cube(doc); doc.Undo.Push(add);
        var node = add.Node;
        node.Local = new Transform3(new Vector3(1, 2, 3), new Vector3(0, 90, 30), new Vector3(2, 1, 1), new Vector3(0.5f, 0, 0));
        var child = CreatePrimitiveCommand.Cube(doc, 0.2f, 0.2f, 0.2f); doc.Undo.Push(child);
        doc.Reparent(child.Node, node);
        child.Node.Local = new Transform3(new Vector3(0, 1, 0), new Vector3(45, 0, 0), Vector3.One);
        var worldBefore = WorldVerts(node); var childWorldBefore = WorldVerts(child.Node);
        var pivotWorld = node.PivotWorld; var localBefore = node.Local; var meshBefore = node.Mesh!.Clone();

        var cmd = new FreezeTransformCommand(new[] { node.Id }, new FreezeOptions());
        doc.Undo.Push(cmd);
        Assert.Equal(Vector3.Zero, node.Local.Translation); Assert.Equal(Vector3.Zero, node.Local.RotationDegrees); Assert.Equal(Vector3.One, node.Local.Scale);
        Assert.Equal(Vector3.Zero, child.Node.Local.Translation); Assert.Equal(Vector3.Zero, child.Node.Local.RotationDegrees);
        AssertSame(worldBefore, WorldVerts(node));
        AssertSame(childWorldBefore, WorldVerts(child.Node));
        Assert.True(Vector3.Distance(pivotWorld, node.PivotWorld) < 1e-4f);
        Assert.Empty(MeshValidator.Check(node.Mesh!));

        doc.Undo.Undo();
        Assert.Equal(localBefore, node.Local);
        for (int v = 0; v < meshBefore.VertexCount; v++) Assert.True(Vector3.Distance(meshBefore.Verts[v].Position, node.Mesh!.Verts[v].Position) < 1e-6f);
        AssertSame(worldBefore, WorldVerts(node));
    }

    [Fact]
    public void FreezeRotateOnly_KeepsTranslateAndScale()
    {
        var doc = new Document();
        var add = CreatePrimitiveCommand.Cube(doc); doc.Undo.Push(add);
        var node = add.Node;
        node.Local = new Transform3(new Vector3(1, 2, 3), new Vector3(0, 90, 0), new Vector3(2, 2, 2));
        var worldBefore = WorldVerts(node);
        doc.Undo.Push(new FreezeTransformCommand(new[] { node.Id }, new FreezeOptions { Translate = false, Rotate = true, Scale = false }));
        Assert.Equal(new Vector3(1, 2, 3), node.Local.Translation);
        Assert.Equal(Vector3.Zero, node.Local.RotationDegrees);
        Assert.True(Vector3.Distance(new Vector3(2, 2, 2), node.Local.Scale) < 1e-4f);
        AssertSame(worldBefore, WorldVerts(node));
    }

    [Fact]
    public void NegativeScale_FlipsFaces_NormalsStayOutward()
    {
        var doc = new Document();
        var add = CreatePrimitiveCommand.Cube(doc); doc.Undo.Push(add);
        var node = add.Node; var m = node.Mesh!;
        node.Local = new Transform3(Vector3.Zero, Vector3.Zero, new Vector3(-1, 1, 1));
        doc.Undo.Push(new FreezeTransformCommand(new[] { node.Id }, new FreezeOptions()));
        Assert.Equal(Vector3.One, node.Local.Scale);
        Assert.Empty(MeshValidator.Check(m));
        for (int f = 0; f < m.FaceCount; f++)
        {
            if (!m.Faces[f].Alive) continue;
            var n = MeshNormals.FaceNormalUnnormalized(m, f);
            var c = Vector3.Zero; int k = 0;
            int start = m.Faces[f].HalfEdge, he = start; do { c += m.Verts[m.Hes[he].Vertex].Position; k++; he = m.Hes[he].Next; } while (he != start);
            Assert.True(Vector3.Dot(n, c / k) > 0, $"face {f} inward");
        }
    }
}
