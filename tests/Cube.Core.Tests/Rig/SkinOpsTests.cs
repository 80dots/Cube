using System.Numerics;
using Cube.Core.Commands;
using Cube.Core.IO;
using Cube.Core.Mesh;
using Cube.Core.Rig;
using Cube.Core.Scene;

namespace Cube.Core.Tests.Rig;

/// <summary>
/// 리깅/스키닝 코어(<c>SkinOps</c>, <c>SkinCluster</c>)를 검증한다: 스무스 바인드 가중치, LBS 변형, 가중치 페인트 모드,
/// 바인드/페인트 명령의 Undo/Redo, 조인트·스킨의 .cube 왕복.
/// </summary>
public class SkinOpsTests
{
    /// <summary>
    /// 공용 장면을 만든다: x -2..2의 4x1 평면(가로 8분할), 월드 x=-1의 조인트 A, 그 자식으로 로컬 +2(월드 x=+1)의 조인트 B.
    /// </summary>
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

    /// <summary>
    /// 조인트 노드들을 SmoothBind 입력(<c>JointInfo</c>: ID, 월드 행렬, 자식 조인트 월드 위치 = 본 끝점)으로 바꾼다.
    /// </summary>
    private static List<JointInfo> Infos(params SceneNode[] joints)
        => joints.Select(j => new JointInfo(j.Id, j.WorldMatrix, j.Children.Where(c => c.IsJoint).Select(c => c.WorldMatrix.Translation).ToList())).ToList();

    /// <summary>
    /// 스무스 바인드 결과 모든 정점 가중치 합이 1이고, 왼쪽 끝 정점은 조인트 A가 지배(&gt;0.8)하며,
    /// 오른쪽 끝은 본 A→B의 끝점과 B까지 거리가 같아 대략 절반씩(0.45~0.55) 나뉘는지 확인한다.
    /// </summary>
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

    /// <summary>
    /// LBS 변형: B에 100% 묶인 정점은 B를 위로 1 옮기면 정확히 +Y 1만큼 따라가고,
    /// 움직이지 않은 A에 100% 묶인 정점은 제자리에 있어야 한다.
    /// </summary>
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

    /// <summary>
    /// Replace 모드로 조인트 0을 0.8로 칠하면 나머지 0.2를 다른 조인트가 원래 비율(0.3:0.2 → 0.12:0.08)로 나눠 가져야 한다.
    /// Add 모드(값 0.5 × 강도 0.5)는 0.12 + 0.25 = 0.37이 되고 합은 계속 1이어야 한다.
    /// </summary>
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

    /// <summary>
    /// SetSkinCommand로 스킨을 바인드한 뒤 WeightPaintCommand(정점별 before/after 가중치 스냅샷)를 푸시하고,
    /// Undo/Redo가 가중치를 오가며, 두 번 Undo하면 바인드까지 풀려 Skin이 null이 되는지 확인한다.
    /// </summary>
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

    /// <summary>
    /// 조인트 계층과 스킨(조인트 목록·가중치·바인드 역행렬)이 .cube 직렬화를 거쳐 보존되는지 확인한다.
    /// 조인트 A가 x=-1이므로 바인드 역행렬의 이동 성분은 +1이어야 한다.
    /// </summary>
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
