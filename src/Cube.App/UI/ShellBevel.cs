using Cube.App.Tools;
using Cube.Core.Commands;
using Cube.Core.Mesh;
using Cube.Core.Scene;
using Cube.Core.Selection;
using NVec3 = System.Numerics.Vector3;

namespace Cube.App.UI;

/// <summary>
/// Blender식 Bevel(Edit Mesh → Bevel): 옵션 창/Action Popup/구성 이력에 Blender의 모든 Bevel 옵션을 노출하고,
/// Bevel (Interactive)/Bevel Vertices (Interactive)는 마우스로 폭을 정하는 대화형 Bevel 툴(<see cref="BevelTool"/>)을 연다(단축키 없음, 사용자 지시).
/// 데이터 흐름: 옵션 창 값(OptionValues, 키 문자열) ↔ BevelOptions(Core) ↔ HistoryParams(구성 이력).
/// 세 표현 사이의 변환 함수(BevelOptionsFrom/WriteBevelOptions/BevelHistory/BevelFromHistory)가 이 파일에 모여 있으며,
/// 열거형 값은 모두 정수(배열 인덱스 = 열거형 값)로 저장한다.
/// </summary>
public partial class Shell
{
    /// <summary>Width Type 열거형(BevelWidthType) 표시 이름. 인덱스 = 열거형 정수값.</summary>
    private static readonly string[] BevelWidthTypes = { "Offset", "Width", "Depth", "Percent", "Absolute" };
    /// <summary>Miter Outer(반사각 코너) 선택지: Sharp/Patch/Arc(BevelMiter 정수값과 같은 순서).</summary>
    private static readonly string[] BevelMiterOuter = { "Sharp", "Patch", "Arc" };
    /// <summary>Miter Inner 선택지: Sharp/Arc. 옵션 값 0/1을 BevelMiter.Sharp/Arc로 직접 매핑한다(Patch 없음).</summary>
    private static readonly string[] BevelMiterInner = { "Sharp", "Arc" };
    /// <summary>Intersection Type(세 개 이상 베벨 엣지가 만나는 정점 처리) 선택지.</summary>
    private static readonly string[] BevelIntersections = { "Grid Fill", "Cutoff", "N-gon" };
    /// <summary>Face Strength(Weighted Normal 고정 대상 면) 선택지.</summary>
    private static readonly string[] BevelFaceStrengths = { "None", "New", "Affected", "All" };
    /// <summary>Profile Type 선택지: 초타원(Shape 값) 또는 Custom(프리셋 프로파일).</summary>
    private static readonly string[] BevelProfileTypes = { "Superellipse", "Custom" };
    /// <summary>Custom 프로파일 프리셋 이름(BevelProfilePreset 순서).</summary>
    private static readonly string[] BevelPresets = { "Default", "Support Loops", "Cornice Molding", "Crown Molding", "Steps" };

    /// <summary>
    /// Bevel 옵션 창 정의. 기본값은 new BevelOptions()를 WriteBevelOptions로 옮겨 Core 기본값과 일치시킨다.
    /// 키 "distance"는 역사적 이름이며 Width를 뜻한다(예전 Maya식 Distance 옵션과 호환).
    /// </summary>
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
        // d = Core 기본값. 옵션 값에 키가 없으면(예전에 저장된 옵션) 이 값을 쓴다.
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

    /// <summary>
    /// BevelOptions → 옵션 값(OptionValues). 대화형 Bevel 툴이 확정할 때와 옵션 창 기본값을 만들 때 쓴다.
    /// bool은 0/1, 열거형은 정수로 저장한다(BevelOptionsFrom의 역변환).
    /// </summary>
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

    /// <summary>bool 구성 이력 파라미터 생성 헬퍼(Value.X = 0/1, 체크박스로 표시).</summary>
    private static HistoryParam HB(string name, bool v) => new() { Name = name, Kind = HistoryParamKind.Bool, Value = new NVec3(v ? 1 : 0, 0, 0), Min = 0, Max = 1, Step = 1 };

    /// <summary>구성 이력 파라미터(Properties History와 Action Popup에서 다시 계산 가능). 열거형은 정수로.</summary>
    /// <remarks>
    /// 열거형 파라미터 이름에는 값의 의미를 괄호로 적어 둔다(UI 라벨용). BevelFromHistory는 이 이름의 접두어로 찾으므로
    /// 접두어("Width Type", "Miter Outer" 등)를 바꾸면 안 된다. Affect는 이력에 넣지 않고 명령 생성 시 캡처한다.
    /// </remarks>
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

    /// <summary>
    /// 구성 이력 파라미터 → BevelOptions. 히스토리 Replay(이력 편집/Action Popup)마다 호출된다.
    /// 열거형은 범위를 벗어나지 않도록 Clamp한다.
    /// </summary>
    /// <param name="p">BevelHistory로 만든(사용자가 편집했을 수 있는) 파라미터.</param>
    /// <param name="affect">명령 생성 시 캡처한 Affect(Edges/Vertices).</param>
    public static BevelOptions BevelFromHistory(HistoryParams p, BevelAffect affect)
    {
        // I = 이름이 접두어로 시작하는 정수 파라미터(이름 끝의 설명 괄호를 무시하기 위해 접두어로 찾음)
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
    /// <remarks>
    /// 규칙: 오브젝트 모드 = 선택 메시의 살아 있는 모든 엣지/정점.
    /// 컴포넌트 모드에서 원하는 종류(want)와 현재 모드가 같으면 그대로, 정점 선택 + 엣지 Bevel이면 양끝이 선택된 엣지,
    /// 그 밖에는 SelectionOps.Convert로 변환한다(면 선택 → 면의 엣지 등). 대상이 빈 노드는 결과에서 뺀다.
    /// </remarks>
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

    /// <summary>Bevel 실행 가능 여부: 오브젝트 모드면 메시 선택, 컴포넌트 모드면 현재 모드 선택이 있어야 한다.</summary>
    private bool CanBevel() => Document.Selection.Mode == SelectMode.Object
        ? Document.Selection.Objects.Any(id => Document.Find(id)?.Mesh != null)
        : Document.Selection.NodesWithComponents(Document.Selection.Mode).Any();

    /// <summary>
    /// mesh.bevelApply 본체: 마지막 옵션으로 대상 노드마다 MeshOpCommand("Bevel")를 한 Undo 그룹에 넣는다.
    /// 명령은 이력 파라미터(BevelHistory)를 갖고 Replay 때 BevelFromHistory로 다시 계산하므로
    /// Properties History와 Action Popup에서 값을 바꿀 수 있다. 결과 면(새 띠·캡)을 면 모드로 선택한다.
    /// </summary>
    private void BevelSelection()
    {
        var o = BevelOptionsFrom(Options("mesh.bevel"));
        var targets = CollectBevelTargets(o.Affect);
        if (targets.Count == 0) { HelpLine.Text = "Bevel: select edges, faces or vertices first."; return; }
        using (Document.Undo.BeginGroup("Bevel"))
            foreach (var (id, ids) in targets)
            {
                // 람다가 루프 변수를 공유하지 않도록 지역 변수로 캡처
                var captured = ids; var affect = o.Affect;
                Document.Undo.Push(new MeshOpCommand("Bevel", id, BevelHistory(o),
                    (m, p) => { var faces = MeshOps.Bevel(m, captured, BevelFromHistory(p, affect)); return (faces.Count > 0, SelectMode.Face, faces); }));
            }
        HelpLine.Text = $"Bevel ({(o.Affect == BevelAffect.Vertices ? "vertices" : "edges")}): {BevelWidthTypes[(int)o.WidthType]} {o.Width:0.###}, {o.Segments} segment(s), shape {o.Shape:0.##}. Adjust in the Action Popup.";
    }

    /// <summary>
    /// Bevel 액션 등록: mesh.bevel(옵션 창)/mesh.bevelApply(실행) 옵션 쌍과 대화형 Bevel 툴 두 가지(엣지/정점).
    /// </summary>
    private void RegisterBevelActions()
    {
        RegisterOptionPair("mesh.bevel", "Bevel", BevelSpec(), BevelSelection, CanBevel);
        Actions.Register("mesh.bevelTool", "Bevel (Interactive)", () => StartBevelTool(vertices: false), CanBevel);
        Actions.Register("mesh.bevelVerticesTool", "Bevel Vertices (Interactive)", () => StartBevelTool(vertices: true), CanBevel);
    }

    /// <summary>
    /// 대화형 Bevel 툴을 시작한다. 이미 그 툴이면 Select로 한 번 바꿨다가 다시 켜서 툴 상태를 새로 시작하게 한다.
    /// </summary>
    /// <param name="vertices">true면 정점 Bevel, false면 엣지 Bevel.</param>
    private void StartBevelTool(bool vertices)
    {
        if (Tools.Get("bevelTool") is not BevelTool t) return;
        t.VertexMode = vertices;
        if (Tools.Current == t) Tools.SetTool("select");
        Tools.SetTool("bevelTool");
    }
}
