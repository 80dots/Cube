using System.Numerics;
using Cube.Core.IO;
using Cube.Core.IO.Fbx;
using Cube.Core.Scene;

namespace Cube.Core.Tests.IO;

public class FbxAnimationTests
{
    private const long Tick = 46186158000L;

    private static Quaternion Deg(Vector3 axis, float deg) => Quaternion.CreateFromAxisAngle(axis, deg * MathF.PI / 180f);

    /// <summary>조인트 체인 3개 + 피벗이 있는 일반 노드, 클립 하나.</summary>
    private static (Document doc, SceneNode j0, SceneNode j1, SceneNode j2, SceneNode plain) MakeDoc()
    {
        var doc = new Document();
        var j0 = new SceneNode { Name = "hip", Shape = new JointShape(), Local = new Transform3(new Vector3(0, 1, 0), Vector3.Zero, Vector3.One) };
        var j1 = new SceneNode { Name = "knee", Shape = new JointShape(), Local = new Transform3(new Vector3(0, -0.5f, 0), Vector3.Zero, Vector3.One) };
        var j2 = new SceneNode { Name = "ankle", Shape = new JointShape(), Local = new Transform3(new Vector3(0, -0.5f, 0), Vector3.Zero, Vector3.One) };
        var plain = new SceneNode { Name = "prop", Local = new Transform3(Vector3.Zero, Vector3.Zero, Vector3.One, new Vector3(1, 0, 0)) };
        doc.AddNode(j0, doc.Root); doc.AddNode(j1, j0); doc.AddNode(j2, j1); doc.AddNode(plain, doc.Root);

        var clip = new AnimationClip { Name = "walk", FrameRate = 30f };
        var t0 = new NodeTrack { Node = j0.Id, NodeName = "hip" };
        t0.Position.Add(new(0f, new Vector3(0, 1, 0)));
        t0.Position.Add(new(0.5f, new Vector3(0.25f, 1.5f, -1)));
        t0.Scale.Add(new(0f, Vector3.One));
        t0.Scale.Add(new(1f, new Vector3(2, 2, 2)));
        var t1 = new NodeTrack { Node = j1.Id, NodeName = "knee" };
        t1.Rotation.Add(new(0f, Deg(Vector3.UnitZ, 170)));
        t1.Rotation.Add(new(0.5f, Deg(Vector3.UnitZ, 179)));
        t1.Rotation.Add(new(1f, Deg(Vector3.UnitZ, -179)));
        var tp = new NodeTrack { Node = plain.Id, NodeName = "prop" };
        tp.Position.Add(new(0f, new Vector3(0, 2, 0)));
        tp.Rotation.Add(new(0f, Deg(Vector3.UnitY, 90)));
        clip.Tracks.Add(t0); clip.Tracks.Add(t1); clip.Tracks.Add(tp);
        clip.UpdateLength();
        doc.Animations.Add(clip);
        return (doc, j0, j1, j2, plain);
    }

    private sealed class Parsed
    {
        public required List<FbxNode> Top;
        public required FbxNode Objects;
        public required List<(long child, long parent, string? prop)> Conns;
        public FbxNode Obj(long id) => Objects.Children.First(o => o.Props.Count > 0 && o.Props[0] is long l && l == id);
        public long ModelId(string name) => Objects.All("Model").First(m => FbxNode.ReadableId(m.Prop<string>(1)) == "Model::" + name).Prop<long>(0);
        /// <summary>모델에 연결된 커브 노드(속성 이름 → 노드).</summary>
        public Dictionary<string, FbxNode> CurveNodes(string model)
        {
            long mid = ModelId(model);
            return Conns.Where(c => c.parent == mid && c.prop != null && c.prop.StartsWith("Lcl ")).ToDictionary(c => c.prop!, c => Obj(c.child));
        }
        public FbxNode Curve(FbxNode curveNode, string axis)
        {
            long cn = curveNode.Prop<long>(0);
            return Obj(Conns.Single(c => c.parent == cn && c.prop == axis).child);
        }
        public float[] Values(FbxNode curveNode, string axis) => Curve(curveNode, axis).Child("KeyValueFloat")!.Prop<float[]>(0);
    }

    private static Parsed BuildAndRead(Document doc, IReadOnlyList<SceneNode> roots, FbxExportOptions? opt = null)
    {
        var top = new FbxSceneBuilder(doc, opt).Build(roots);
        var (_, nodes) = FbxBinaryReader.Read(FbxBinaryWriter.Write(top));
        var conns = nodes.First(n => n.Name == "Connections").All("C")
            .Select(c => (c.Prop<long>(1), c.Prop<long>(2), c.Props.Count > 3 ? c.Prop<string>(3) : null)).ToList();
        return new Parsed { Top = nodes, Objects = nodes.First(n => n.Name == "Objects"), Conns = conns };
    }

    [Fact]
    public void Export_WritesStackLayerCurveNodesAndCurves()
    {
        var (doc, j0, _, _, plain) = MakeDoc();
        var p = BuildAndRead(doc, new[] { j0, plain });

        var stack = p.Objects.All("AnimationStack").Single();
        Assert.Equal("AnimStack::walk", FbxNode.ReadableId(stack.Prop<string>(1)));
        var sp = stack.Child("Properties70")!.All("P").ToDictionary(x => x.Prop<string>(0), x => x);
        Assert.Equal(0L, sp["LocalStart"].Prop<long>(4));
        Assert.Equal(Tick, sp["LocalStop"].Prop<long>(4));
        Assert.Equal(Tick, sp["ReferenceStop"].Prop<long>(4));
        var layer = p.Objects.All("AnimationLayer").Single();
        Assert.Equal("AnimLayer::BaseLayer", FbxNode.ReadableId(layer.Prop<string>(1)));
        Assert.Contains((layer.Prop<long>(0), stack.Prop<long>(0), (string?)null), p.Conns);

        // hip: T + S (회전 키 없음)
        var hip = p.CurveNodes("hip");
        Assert.Equal(new[] { "Lcl Scaling", "Lcl Translation" }, hip.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal("AnimCurveNode::T", FbxNode.ReadableId(hip["Lcl Translation"].Prop<string>(1)));
        foreach (var cn in hip.Values) Assert.Contains((cn.Prop<long>(0), layer.Prop<long>(0), (string?)null), p.Conns);
        var ty = p.Curve(hip["Lcl Translation"], "d|Y");
        Assert.Equal(new[] { 0L, Tick / 2 }, ty.Child("KeyTime")!.Prop<long[]>(0));
        var tyv = ty.Child("KeyValueFloat")!.Prop<float[]>(0);
        Assert.Equal(100f, tyv[0], 3); Assert.Equal(150f, tyv[1], 3);
        Assert.Equal(-100f, p.Values(hip["Lcl Translation"], "d|Z")[1], 3);
        Assert.Equal(25f, p.Values(hip["Lcl Translation"], "d|X")[1], 3);
        Assert.Equal(4009, ty.Child("KeyVer")!.Prop<int>(0));
        Assert.Equal(new[] { 24836 }, ty.Child("KeyAttrFlags")!.Prop<int[]>(0));
        Assert.Equal(new[] { 2 }, ty.Child("KeyAttrRefCount")!.Prop<int[]>(0));
        Assert.Equal(255790911, BitConverter.SingleToInt32Bits(ty.Child("KeyAttrDataFloat")!.Prop<float[]>(0)[2]));
        Assert.Equal(100.0, ty.Child("Default")!.Prop<double>(0), 3);
        var sx = p.Curve(hip["Lcl Scaling"], "d|X");
        Assert.Equal(new[] { 0L, Tick }, sx.Child("KeyTime")!.Prop<long[]>(0));
        Assert.Equal(new[] { 1f, 2f }, sx.Child("KeyValueFloat")!.Prop<float[]>(0));

        // knee: R만, 170 → 179 → -179가 181로 언랩
        var knee = p.CurveNodes("knee");
        Assert.Equal(new[] { "Lcl Rotation" }, knee.Keys);
        Assert.Equal("AnimCurveNode::R", FbxNode.ReadableId(knee["Lcl Rotation"].Prop<string>(1)));
        var rz = p.Values(knee["Lcl Rotation"], "d|Z");
        Assert.Equal(170f, rz[0], 2); Assert.Equal(179f, rz[1], 2); Assert.Equal(181f, rz[2], 2);
        Assert.Equal(new[] { 0L, Tick / 2, Tick }, p.Curve(knee["Lcl Rotation"], "d|Z").Child("KeyTime")!.Prop<long[]>(0));

        // ankle: 키 없음 → 커브 노드 없음
        Assert.Empty(p.CurveNodes("ankle"));

        // Takes
        var takes = p.Top.First(n => n.Name == "Takes");
        Assert.Equal("walk", takes.Child("Current")!.Prop<string>(0));
        var take = takes.All("Take").Single();
        Assert.Equal("walk", take.Prop<string>(0));
        Assert.Equal("walk.tak", take.Child("FileName")!.Prop<string>(0));
        Assert.Equal(Tick, take.Child("LocalTime")!.Prop<long>(1));
        Assert.Equal(Tick, take.Child("ReferenceTime")!.Prop<long>(1));

        // GlobalSettings: 30fps → TimeMode 6, TimeSpanStop = 1초
        var gs = p.Top.First(n => n.Name == "GlobalSettings").Child("Properties70")!.All("P").ToDictionary(x => x.Prop<string>(0), x => x);
        Assert.Equal(6, gs["TimeMode"].Prop<int>(4));
        Assert.Equal(Tick, gs["TimeSpanStop"].Prop<long>(4));

        // Definitions에 애니메이션 타입 수
        var defs = p.Top.First(n => n.Name == "Definitions").All("ObjectType").ToDictionary(o => o.Prop<string>(0), o => o.Child("Count")!.Prop<int>(0));
        Assert.Equal(1, defs["AnimationStack"]);
        Assert.Equal(1, defs["AnimationLayer"]);
        Assert.Equal(p.Objects.All("AnimationCurveNode").Count(), defs["AnimationCurveNode"]);
        Assert.Equal(p.Objects.All("AnimationCurve").Count(), defs["AnimationCurve"]);
        Assert.Equal(3 * defs["AnimationCurveNode"], defs["AnimationCurve"]);
    }

    [Fact]
    public void Export_PivotNode_BakesTranslationFormula()
    {
        var (doc, j0, _, _, plain) = MakeDoc();
        var p = BuildAndRead(doc, new[] { j0, plain });
        var prop = p.CurveNodes("prop");
        // 피벗 P=(1,0,0), R = Y 90°, p = (0,2,0) → T = P·R + p = (0,0,-1) + (0,2,0)
        var t = prop["Lcl Translation"];
        Assert.Equal(0f, p.Values(t, "d|X")[0], 3);
        Assert.Equal(200f, p.Values(t, "d|Y")[0], 3);
        Assert.Equal(-100f, p.Values(t, "d|Z")[0], 3);
        Assert.Equal(90f, p.Values(prop["Lcl Rotation"], "d|Y")[0], 3);

        // 피벗을 베이크하지 않으면 Rotation/ScalingPivot 모델에 맞춘 T = p + P·R − P
        var p2 = BuildAndRead(doc, new[] { j0, plain }, FbxExportOptions.Default with { BakePivots = false });
        var t2 = p2.CurveNodes("prop")["Lcl Translation"];
        Assert.Equal(-100f, p2.Values(t2, "d|X")[0], 3);
        Assert.Equal(-100f, p2.Values(t2, "d|Z")[0], 3);
    }

    [Fact]
    public void Export_ChildOfPivotedParent_SubtractsParentShift()
    {
        var doc = new Document();
        var parent = new SceneNode { Name = "parent", Local = new Transform3(Vector3.Zero, Vector3.Zero, Vector3.One, new Vector3(0, 1, 0)) };
        var child = new SceneNode { Name = "child" };
        doc.AddNode(parent, doc.Root); doc.AddNode(child, parent);
        var clip = new AnimationClip { Name = "a" };
        var tr = new NodeTrack { Node = child.Id };
        tr.Position.Add(new(0f, new Vector3(3, 0, 0)));
        clip.Tracks.Add(tr); clip.UpdateLength();
        doc.Animations.Add(clip);
        var p = BuildAndRead(doc, new[] { parent });
        var t = p.CurveNodes("child")["Lcl Translation"];
        Assert.Equal(300f, p.Values(t, "d|X")[0], 3);
        Assert.Equal(-100f, p.Values(t, "d|Y")[0], 3);
    }

    [Fact]
    public void Export_ChildExportedAlone_BakesParentWorld()
    {
        var doc = new Document();
        var parent = new SceneNode { Name = "parent", Local = new Transform3(new Vector3(0, 5, 0), Vector3.Zero, Vector3.One) };
        var child = new SceneNode { Name = "child" };
        doc.AddNode(parent, doc.Root); doc.AddNode(child, parent);
        var clip = new AnimationClip { Name = "a" };
        var tr = new NodeTrack { Node = child.Id };
        tr.Position.Add(new(0f, new Vector3(1, 0, 0)));
        clip.Tracks.Add(tr); clip.UpdateLength();
        doc.Animations.Add(clip);
        var p = BuildAndRead(doc, new[] { child });
        var t = p.CurveNodes("child")["Lcl Translation"];
        Assert.Equal(100f, p.Values(t, "d|X")[0], 3);
        Assert.Equal(500f, p.Values(t, "d|Y")[0], 3);
    }

    [Fact]
    public void Export_OnlyExportedModelsGetCurves_AndOptionDisables()
    {
        var (doc, j0, _, _, plain) = MakeDoc();
        var p = BuildAndRead(doc, new[] { plain });
        Assert.Equal(2, p.Objects.All("AnimationCurveNode").Count()); // prop T + R만
        var off = BuildAndRead(doc, new[] { j0, plain }, FbxExportOptions.Default with { ExportAnimations = false });
        Assert.Empty(off.Objects.All("AnimationStack"));
        Assert.Empty(off.Top.First(n => n.Name == "Takes").All("Take"));
    }

    [Fact]
    public void UnwrapEuler_PicksClosestEquivalent()
    {
        var u = FbxSceneBuilder.UnwrapEuler(new Vector3(-179, 10, 350), new Vector3(179, 0, -5));
        Assert.Equal(181f, u.X, 3); Assert.Equal(10f, u.Y, 3); Assert.Equal(-10f, u.Z, 3);
    }

    [Fact]
    public void CubeFile_RoundTripsAnimations()
    {
        var (doc, _, _, _, _) = MakeDoc();
        doc.Animations[0].Loop = true;
        var json = CubeFileFormat.Serialize(doc);
        var doc2 = new Document();
        CubeFileFormat.Deserialize(doc2, json);
        var c = Assert.Single(doc2.Animations);
        var src = doc.Animations[0];
        Assert.Equal("walk", c.Name); Assert.True(c.Loop); Assert.Equal(30f, c.FrameRate); Assert.Equal(src.Length, c.Length);
        Assert.Equal(3, c.Tracks.Count);
        var knee = c.Tracks[1];
        Assert.Equal("knee", doc2.Find(knee.Node)!.Name);
        Assert.Equal("knee", knee.NodeName);
        Assert.Equal(src.Tracks[1].Rotation, knee.Rotation);
        Assert.Equal(src.Tracks[0].Position, c.Tracks[0].Position);
        Assert.Equal(src.Tracks[0].Scale, c.Tracks[0].Scale);
        Assert.Empty(knee.Position);

        // 애니메이션 필드가 없는 예전 파일도 열리고, 다시 불러오면 이전 클립은 지워진다
        var plain = new Document();
        plain.AddNode(new SceneNode { Name = "n" }, plain.Root);
        var plainJson = CubeFileFormat.Serialize(plain);
        Assert.DoesNotContain("animations", plainJson);
        CubeFileFormat.Deserialize(doc2, plainJson);
        Assert.Empty(doc2.Animations);
    }
}
