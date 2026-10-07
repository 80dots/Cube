using System.Numerics;
using Cube.Core.Commands;
using Cube.Core.IO;
using Cube.Core.Mesh;
using Cube.Core.Rig;
using Cube.Core.Scene;

namespace Cube.Core.Tests.Rig;

public class SkinOpsTests
{
    private static (Document doc, SceneNode mesh, SceneNode jA, SceneNode jB) MakeScene()
    {
        var doc = new Document();
        // 4x1 평면(x: -2..2), 조인트 A at x=-1, B at x=+1 (B는 A의 자식)
        var mesh = new SceneNode { Name = "plane", Shape = new MeshShape(MeshBuilder.Plane(4, 1, 8, 1)) };
        var jA = new SceneNode { Name = "jointA", Shape = new JointShape(), Local = new Transform3(new Vector3(-1, 0, 0), Vector3.Zero, Vector3.One) };
        var jB = new SceneNode { Name = "jointB", Shape = new JointShape(), Local = new Transform3(new Vector3(2, 0, 0), Vector3.Zero, Vector3.One) };
        doc.AddNode(mesh); doc.AddNode(jA); doc.AddNode(jB, jA);
        return (doc, mesh, jA, jB);
    }

    private static List<JointInfo> Infos(params SceneNode[] joints)
        => joints.Select(j => new JointInfo(j.Id, j.WorldMatrix, j.Children.Where(c => c.IsJoint).Select(c => c.WorldMatrix.Translation).ToList())).ToList();

    [Fact]
    public void SmoothBind_WeightsNormalized_And_FollowDistance()
    {
        var (doc, mesh, jA, jB) = MakeScene();
        var skin = SkinOps.SmoothBind(mesh.Mesh!, mesh.WorldMatrix, Infos(jA, jB));
        Assert.Equal(2, skin.Joints.Count);
        var m = mesh.Mesh!;
        for (int v = 0; v < m.VertexCount; v++)
        {
            float sum = skin.Weights[v]!.Sum(w => w.weight);
            Assert.True(MathF.Abs(sum - 1f) < 1e-4f, $"v{v} sum {sum}");
            float x = m.Verts[v].Position.X;
            float wA = skin.GetWeight(v, 0), wB = skin.GetWeight(v, 1);
            // B는 A의 자식(로컬 +2 → 월드 x=+1). 왼쪽 끝은 A 지배, 오른쪽 끝은 본 A→B 끝점과 B가 같은 거리라 절반씩
            if (x < -1.5f) Assert.True(wA > 0.8f, $"v{v} x={x} wA={wA}");
            if (x > 1.5f) Assert.True(wB >= 0.45f && wB <= 0.55f, $"v{v} x={x} wB={wB}");
        }
    }

    [Fact]
    public void Deform_TranslatingJoint_MovesFullyWeightedVertices()
    {
        var (doc, mesh, jA, jB) = MakeScene();
        var skin = SkinOps.SmoothBind(mesh.Mesh!, mesh.WorldMatrix, Infos(jA, jB));
        var m = mesh.Mesh!;
        // 정점 하나를 B에 100% 할당하고 B를 위로 1 이동
        int v = Enumerable.Range(0, m.VertexCount).First(i => m.Verts[i].Position.X > 1.9f);
        SkinOps.SetWeightNormalized(skin, v, 1, 1f);
        jB.Local = new Transform3(new Vector3(2, 1, 0), Vector3.Zero, Vector3.One);
        var outPos = new Vector3[m.VertexCount];
        Matrix4x4.Invert(mesh.WorldMatrix, out var inv);
        SkinOps.Deform(m, skin, id => doc.Find(id)?.WorldMatrix, inv, outPos);
        Assert.True(Vector3.Distance(outPos[v], m.Verts[v].Position + Vector3.UnitY) < 1e-4f, $"moved to {outPos[v]}");
        // A에 100%인 정점은 그대로(A는 움직이지 않음)
        int va = Enumerable.Range(0, m.VertexCount).First(i => m.Verts[i].Position.X < -1.9f);
        SkinOps.SetWeightNormalized(skin, va, 0, 1f);
        SkinOps.Deform(m, skin, id => doc.Find(id)?.WorldMatrix, inv, outPos);
        Assert.True(Vector3.Distance(outPos[va], m.Verts[va].Position) < 1e-3f);
    }

    [Fact]
    public void Paint_Replace_KeepsOthersProportional()
    {
        var skin = new SkinCluster();
        skin.Joints.AddRange(new[] { new NodeId(1), new NodeId(2), new NodeId(3) });
        skin.SetWeights(0, new List<(int, float)> { (0, 0.5f), (1, 0.3f), (2, 0.2f) });
        SkinOps.PaintVertex(skin, 0, 0, PaintMode.Replace, 0.8f, 1f);
        Assert.True(MathF.Abs(skin.GetWeight(0, 0) - 0.8f) < 1e-5f);
        Assert.True(MathF.Abs(skin.GetWeight(0, 1) - 0.12f) < 1e-5f);
        Assert.True(MathF.Abs(skin.GetWeight(0, 2) - 0.08f) < 1e-5f);
        SkinOps.PaintVertex(skin, 0, 1, PaintMode.Add, 0.5f, 0.5f); // 0.12 + 0.25
        Assert.True(MathF.Abs(skin.GetWeight(0, 1) - 0.37f) < 1e-5f);
        Assert.True(MathF.Abs(skin.Weights[0]!.Sum(w => w.weight) - 1f) < 1e-5f);
    }

    [Fact]
    public void WeightPaintCommand_UndoRedo_And_SetSkinCommand()
    {
        var (doc, mesh, jA, jB) = MakeScene();
        var skin = SkinOps.SmoothBind(mesh.Mesh!, mesh.WorldMatrix, Infos(jA, jB));
        doc.Undo.Push(new SetSkinCommand("Bind Skin", mesh.Id, skin));
        Assert.Same(skin, mesh.Skin);
        var before = new[] { skin.CopyWeights(0) };
        SkinOps.SetWeightNormalized(skin, 0, 1, 1f);
        var after = new[] { skin.CopyWeights(0) };
        doc.Undo.Push(new WeightPaintCommand(mesh.Id, new[] { 0 }, before, after), alreadyApplied: true);
        Assert.Equal(1f, skin.GetWeight(0, 1), 4);
        doc.Undo.Undo();
        Assert.True(skin.GetWeight(0, 1) < 0.5f);
        doc.Undo.Redo();
        Assert.Equal(1f, skin.GetWeight(0, 1), 4);
        doc.Undo.Undo(); doc.Undo.Undo();
        Assert.Null(mesh.Skin);
    }

    [Fact]
    public void CubeFile_RoundTrips_JointsAndSkin()
    {
        var (doc, mesh, jA, jB) = MakeScene();
        var skin = SkinOps.SmoothBind(mesh.Mesh!, mesh.WorldMatrix, Infos(jA, jB));
        mesh.MeshShape!.Skin = skin;
        var json = CubeFileFormat.Serialize(doc);
        var doc2 = new Document();
        CubeFileFormat.Deserialize(doc2, json);
        var joints = doc2.JointNodes().ToList();
        Assert.Equal(2, joints.Count);
        var child = joints.First(j => j.Name == "jointB");
        Assert.Equal("jointA", child.Parent!.Name);
        var m2 = doc2.SkinnedNodes().Single();
        Assert.Equal(2, m2.Skin!.Joints.Count);
        Assert.Equal("jointA", doc2.Get(m2.Skin.Joints[0]).Name);
        for (int v = 0; v < m2.Mesh!.VertexCount; v++)
            Assert.True(MathF.Abs(m2.Skin.Weights[v]!.Sum(w => w.weight) - 1f) < 1e-4f);
        Assert.True(MathF.Abs(m2.Skin.BindInverse[0].M41 - 1f) < 1e-5f); // A at x=-1 → inverse translation +1
    }
}
