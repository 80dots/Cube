using Cube.App.Tools;
using Cube.Core.Commands;
using Cube.Core.Mesh;
using Cube.Core.Scene;
using Cube.Core.Selection;
using NVec3 = System.Numerics.Vector3;

namespace Cube.App.UI;

/// <summary>
/// Blender식 Bevel(Edit Mesh → Bevel): 옵션 창/Action Popup/구성 이력에 Blender의 모든 Bevel 옵션을 노출하고,
/// Ctrl+B(엣지) / Shift+Ctrl+B(정점)는 마우스로 폭을 정하는 대화형 Bevel 툴(<see cref="BevelTool"/>)을 연다.
/// </summary>
public partial class Shell
{
    private static readonly string[] BevelWidthTypes = { "Offset", "Width", "Depth", "Percent", "Absolute" };
    private static readonly string[] BevelMiterOuter = { "Sharp", "Patch", "Arc" };
    private static readonly string[] BevelMiterInner = { "Sharp", "Arc" };
    private static readonly string[] BevelIntersections = { "Grid Fill", "Cutoff", "N-gon" };
    private static readonly string[] BevelFaceStrengths = { "None", "New", "Affected", "All" };
    private static readonly string[] BevelProfileTypes = { "Superellipse", "Custom" };
    private static readonly string[] BevelPresets = { "Default", "Support Loops", "Cornice Molding", "Crown Molding", "Steps" };

    private static OptionSpec BevelSpec() => new("Bevel Options", v => WriteBevelOptions(v, new BevelOptions()), new[]
    {
        OptionField.E("affect", "Affect", "Edges", "Vertices"),
        OptionField.E("widthType", "Width Type", BevelWidthTypes),
        OptionField.F("distance", "Width", 0, 100000, 0.001, "Size of the bevel; its meaning depends on Width Type (Percent = % of the adjacent edge length)"),
        OptionField.I("segments", "Segments", 1, 100, "Number of segments in the profile (1 = chamfer)"),
        OptionField.F("shape", "Profile Shape", 0, 1, 0.01, "0.5 = round, 0.25 = flat, 1 = square corner, 0 = concave"),
        OptionField.I("material", "Material Index", -1, 99, "-1 = new faces use the materials of their neighbors"),
        OptionField.B("harden", "Harden Normals", "Lock the new faces' normals so they look smooth without affecting the rest of the mesh"),
        OptionField.B("clamp", "Clamp Overlap", "Prevent the bevel from overshooting past neighboring geometry"),
        OptionField.B("loopSlide", "Loop Slide", "Slide new vertices along the existing edges (off = new edges perpendicular to the beveled edge)"),
        OptionField.B("markSeams", "Mark Seams", "Continue UV seams through the bevel where two seam edges meet"),
        OptionField.B("markSharp", "Mark Sharp", "Continue hard (sharp) edges through the bevel where two hard edges meet"),
        OptionField.E("miterOuter", "Miter Outer", BevelMiterOuter),
        OptionField.E("miterInner", "Miter Inner", BevelMiterInner),
        OptionField.F("spread", "Spread", 0, 100000, 0.001, "Size of the inner Arc miter"),
        OptionField.E("intersection", "Intersection Type", BevelIntersections),
        OptionField.E("faceStrength", "Face Strength", BevelFaceStrengths),
        OptionField.E("profileType", "Profile Type", BevelProfileTypes),
        OptionField.E("preset", "Custom Preset", BevelPresets),
        OptionField.B("sampleStraight", "Sample Straight Edges", "Custom profile: spread extra samples evenly over every segment instead of the longest ones"),
        OptionField.B("sampleEven", "Sample Even Lengths", "Custom profile: distribute samples evenly along the whole profile length"),
    }, "Bevel");

    /// <summary>옵션 값 → BevelOptions(없는 키는 기본값).</summary>
    public BevelOptions BevelOptionsFrom(OptionValues v)
    {
        var d = new BevelOptions();
        int I(string k, int def) => v.Has(k) ? v.Int(k) : def;
        float F(string k, float def) => v.Has(k) ? v.Float(k) : def;
        bool B(string k, bool def) => v.Has(k) ? v.Bool(k) : def;
        return new BevelOptions
        {
            Affect = (BevelAffect)I("affect", (int)d.Affect),
            WidthType = (BevelWidthType)I("widthType", (int)d.WidthType),
            Width = F("distance", d.Width),
            Segments = Math.Max(1, I("segments", d.Segments)),
            Shape = F("shape", d.Shape),
            MaterialIndex = I("material", d.MaterialIndex),
            HardenNormals = B("harden", d.HardenNormals),
            ClampOverlap = B("clamp", d.ClampOverlap),
            LoopSlide = B("loopSlide", d.LoopSlide),
            MarkSeams = B("markSeams", d.MarkSeams),
            MarkSharp = B("markSharp", d.MarkSharp),
            MiterOuter = (BevelMiter)I("miterOuter", (int)d.MiterOuter),
            MiterInner = I("miterInner", 0) == 1 ? BevelMiter.Arc : BevelMiter.Sharp,
            Spread = F("spread", d.Spread),
            Intersection = (BevelIntersection)I("intersection", (int)d.Intersection),
            FaceStrength = (BevelFaceStrength)I("faceStrength", (int)d.FaceStrength),
            ProfileType = (BevelProfileType)I("profileType", (int)d.ProfileType),
            Preset = (BevelProfilePreset)I("preset", (int)d.Preset),
            SampleStraightEdges = B("sampleStraight", d.SampleStraightEdges),
            SampleEvenLengths = B("sampleEven", d.SampleEvenLengths),
        };
    }

    public static void WriteBevelOptions(OptionValues v, BevelOptions o)
    {
        v.Set("affect", (int)o.Affect); v.Set("widthType", (int)o.WidthType); v.Set("distance", o.Width); v.Set("segments", o.Segments);
        v.Set("shape", o.Shape); v.Set("material", o.MaterialIndex);
        v.Set("harden", o.HardenNormals ? 1 : 0); v.Set("clamp", o.ClampOverlap ? 1 : 0); v.Set("loopSlide", o.LoopSlide ? 1 : 0);
        v.Set("markSeams", o.MarkSeams ? 1 : 0); v.Set("markSharp", o.MarkSharp ? 1 : 0);
        v.Set("miterOuter", (int)o.MiterOuter); v.Set("miterInner", o.MiterInner == BevelMiter.Arc ? 1 : 0); v.Set("spread", o.Spread);
        v.Set("intersection", (int)o.Intersection); v.Set("faceStrength", (int)o.FaceStrength);
        v.Set("profileType", (int)o.ProfileType); v.Set("preset", (int)o.Preset);
        v.Set("sampleStraight", o.SampleStraightEdges ? 1 : 0); v.Set("sampleEven", o.SampleEvenLengths ? 1 : 0);
    }

    private static HistoryParam HB(string name, bool v) => new() { Name = name, Kind = HistoryParamKind.Bool, Value = new NVec3(v ? 1 : 0, 0, 0), Min = 0, Max = 1, Step = 1 };

    /// <summary>구성 이력 파라미터(Properties History와 Action Popup에서 다시 계산 가능). 열거형은 정수로.</summary>
    public static HistoryParams BevelHistory(BevelOptions o) => new(
        HistoryParam.F("Width", o.Width, 0f, 100000f, 0.001f),
        HistoryParam.I("Segments", o.Segments, 1, 100),
        HistoryParam.F("Profile Shape", o.Shape, 0f, 1f, 0.01f),
        HistoryParam.I("Width Type (0 Offset 1 Width 2 Depth 3 % 4 Abs)", (int)o.WidthType, 0, 4),
        HistoryParam.I("Material Index", o.MaterialIndex, -1, 99),
        HB("Harden Normals", o.HardenNormals), HB("Clamp Overlap", o.ClampOverlap), HB("Loop Slide", o.LoopSlide),
        HB("Mark Seams", o.MarkSeams), HB("Mark Sharp", o.MarkSharp),
        HistoryParam.I("Miter Outer (0 Sharp 1 Patch 2 Arc)", (int)o.MiterOuter, 0, 2),
        HistoryParam.I("Miter Inner (0 Sharp 1 Arc)", o.MiterInner == BevelMiter.Arc ? 1 : 0, 0, 1),
        HistoryParam.F("Spread", o.Spread, 0f, 100000f, 0.001f),
        HistoryParam.I("Intersection (0 Grid 1 Cutoff 2 N-gon)", (int)o.Intersection, 0, 2),
        HistoryParam.I("Face Strength (0 None 1 New 2 Affected 3 All)", (int)o.FaceStrength, 0, 3),
        HistoryParam.I("Profile Type (0 Superellipse 1 Custom)", (int)o.ProfileType, 0, 1),
        HistoryParam.I("Custom Preset (0-4)", (int)o.Preset, 0, 4),
        HB("Sample Straight Edges", o.SampleStraightEdges), HB("Sample Even Lengths", o.SampleEvenLengths));

    public static BevelOptions BevelFromHistory(HistoryParams p, BevelAffect affect)
    {
        int I(string prefix) => p.Items.First(x => x.Name.StartsWith(prefix, StringComparison.Ordinal)).Int;
        bool B(string name) => p[name].Bool;
        return new BevelOptions
        {
            Affect = affect,
            Width = p.Float("Width"), Segments = Math.Max(1, p.Int("Segments")), Shape = p.Float("Profile Shape"),
            WidthType = (BevelWidthType)Math.Clamp(I("Width Type"), 0, 4), MaterialIndex = p.Int("Material Index"),
            HardenNormals = B("Harden Normals"), ClampOverlap = B("Clamp Overlap"), LoopSlide = B("Loop Slide"),
            MarkSeams = B("Mark Seams"), MarkSharp = B("Mark Sharp"),
            MiterOuter = (BevelMiter)Math.Clamp(I("Miter Outer"), 0, 2), MiterInner = I("Miter Inner") == 1 ? BevelMiter.Arc : BevelMiter.Sharp,
            Spread = p.Float("Spread"),
            Intersection = (BevelIntersection)Math.Clamp(I("Intersection"), 0, 2), FaceStrength = (BevelFaceStrength)Math.Clamp(I("Face Strength"), 0, 3),
            ProfileType = (BevelProfileType)Math.Clamp(I("Profile Type"), 0, 1), Preset = (BevelProfilePreset)Math.Clamp(I("Custom Preset"), 0, 4),
            SampleStraightEdges = B("Sample Straight Edges"), SampleEvenLengths = B("Sample Even Lengths"),
        };
    }

    /// <summary>현재 선택을 Bevel 대상(엣지 또는 정점 ID)으로 바꾼다. 오브젝트 모드 = 메시 전체.</summary>
    public List<(NodeId id, int[] ids)> CollectBevelTargets(BevelAffect affect)
    {
        var res = new List<(NodeId, int[])>();
        var doc = Document; var sel = doc.Selection;
        var want = affect == BevelAffect.Vertices ? SelectMode.Vertex : SelectMode.Edge;
        if (sel.Mode == SelectMode.Object)
        {
            foreach (var id in sel.Objects)
            {
                var mesh = doc.Find(id)?.Mesh; if (mesh == null) continue;
                var all = want == SelectMode.Vertex ? Enumerable.Range(0, mesh.VertexCount).Where(v => mesh.Verts[v].Alive) : Enumerable.Range(0, mesh.EdgeCount).Where(e => mesh.Edges[e].Alive);
                res.Add((id, all.ToArray()));
            }
            return res;
        }
        foreach (var id in sel.NodesWithComponents(sel.Mode))
        {
            var mesh = doc.Find(id)?.Mesh; if (mesh == null) continue;
            var comps = sel.GetComponents(id);
            int[] ids;
            if (want == SelectMode.Edge && sel.Mode == SelectMode.Vertex)
            {
                // 정점 선택에서 엣지 Bevel: 양 끝이 모두 선택된 엣지(Blender와 같음)
                var vs = new HashSet<int>(comps.Verts);
                ids = Enumerable.Range(0, mesh.EdgeCount).Where(e => mesh.Edges[e].Alive && vs.Contains(mesh.EdgeVertices(e).Item1) && vs.Contains(mesh.EdgeVertices(e).Item2)).ToArray();
            }
            else ids = sel.Mode == want ? (want == SelectMode.Vertex ? comps.Verts.ToArray() : comps.Edges.ToArray()) : SelectionOps.Convert(mesh, comps, sel.Mode, want).ToArray();
            if (ids.Length > 0) res.Add((id, ids));
        }
        return res;
    }

    private bool CanBevel() => Document.Selection.Mode == SelectMode.Object
        ? Document.Selection.Objects.Any(id => Document.Find(id)?.Mesh != null)
        : Document.Selection.NodesWithComponents(Document.Selection.Mode).Any();

    private void BevelSelection()
    {
        var o = BevelOptionsFrom(Options("mesh.bevel"));
        var targets = CollectBevelTargets(o.Affect);
        if (targets.Count == 0) { HelpLine.Text = "Bevel: select edges, faces or vertices first."; return; }
        using (Document.Undo.BeginGroup("Bevel"))
            foreach (var (id, ids) in targets)
            {
                var captured = ids; var affect = o.Affect;
                Document.Undo.Push(new MeshOpCommand("Bevel", id, BevelHistory(o),
                    (m, p) => { var faces = MeshOps.Bevel(m, captured, BevelFromHistory(p, affect)); return (faces.Count > 0, SelectMode.Face, faces); }));
            }
        HelpLine.Text = $"Bevel ({(o.Affect == BevelAffect.Vertices ? "vertices" : "edges")}): {BevelWidthTypes[(int)o.WidthType]} {o.Width:0.###}, {o.Segments} segment(s), shape {o.Shape:0.##}. Adjust in the Action Popup.";
    }

    private void RegisterBevelActions()
    {
        RegisterOptionPair("mesh.bevel", "Bevel", BevelSpec(), BevelSelection, CanBevel);
        Actions.Register("mesh.bevelTool", "Bevel (Interactive)", () => StartBevelTool(vertices: false), CanBevel);
        Actions.Register("mesh.bevelVerticesTool", "Bevel Vertices (Interactive)", () => StartBevelTool(vertices: true), CanBevel);
    }

    private void StartBevelTool(bool vertices)
    {
        if (Tools.Get("bevelTool") is not BevelTool t) return;
        t.VertexMode = vertices;
        if (Tools.Current == t) Tools.SetTool("select");
        Tools.SetTool("bevelTool");
    }
}
