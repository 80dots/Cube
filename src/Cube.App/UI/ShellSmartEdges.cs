using Cube.Core.Commands;
using Cube.Core.Mesh;
using Cube.Core.Scene;
using Cube.Core.Selection;

namespace Cube.App.UI;

/// <summary>
/// Mesh Display → Smart Soften/Harden(v0.0.60). 각도 하나가 아니라 형태(링 문맥·메시 전체 분포·루프 연속성·UV 구조)로 하드/소프트를 정한다.
/// 옵션 쌍 `normals.smartSoftenHarden`/`…Apply` + 프리셋(용도별 옵션 묶음). 대상은 ForEachMeshTarget 규칙(오브젝트 모드 = 전체, 엣지/면/정점 선택 = 그 엣지들).
/// 모든 값은 구성 이력 파라미터라 Properties History·Action Popup에서 다시 조정할 수 있다.
/// </summary>
public partial class Shell
{
    private static readonly string[] SmartPresets =
    {
        "Custom", "Low-Poly Game Asset", "Hard Surface / Mechanical", "Organic / Smooth", "Architecture / Planar",
        "Normal Map Bake (UV borders)", "Beveled + Weighted Normals", "Angle Only (classic)", "Soften All", "Harden All",
    };

    /// <summary>프리셋 → 옵션 값. Custom은 아무것도 바꾸지 않는다.</summary>
    private static void ApplySmartPreset(int preset, OptionValues v)
    {
        void Set(SmartThresholdMode mode, float bias, float high, float low, float ctx, float maxSmooth, float minHard, int concave, bool loop, int chain, bool gaps, SmartUvRule uv, bool seams, bool creases, bool keepHard, bool keepSoft, SmartWeightedNormals wn)
        {
            v.Set("mode", (int)mode); v.Set("bias", bias); v.Set("high", high); v.Set("low", low); v.Set("context", ctx); v.Set("maxSmooth", maxSmooth); v.Set("minHard", minHard);
            v.Set("concavity", concave); v.Set("loop", loop ? 1 : 0); v.Set("chain", chain); v.Set("gaps", gaps ? 1 : 0); v.Set("uv", (int)uv); v.Set("seams", seams ? 1 : 0); v.Set("creases", creases ? 1 : 0);
            v.Set("keepHard", keepHard ? 1 : 0); v.Set("keepSoft", keepSoft ? 1 : 0); v.Set("weighted", (int)wn);
        }
        switch (preset)
        {
            case 1: Set(SmartThresholdMode.Auto, 0f, 40, 20, 1f, 80, 15, 0, true, 2, true, SmartUvRule.Ignore, false, true, false, false, SmartWeightedNormals.None); break;                 // 로우폴리 게임: 자동, 곡면 띠 소프트
            case 2: Set(SmartThresholdMode.Auto, 0.3f, 35, 15, 0.7f, 40, 12, 0, true, 2, true, SmartUvRule.Ignore, false, true, false, false, SmartWeightedNormals.FaceArea); break;          // 하드서피스: 더 많이 하드(육각 너트도 하드), 가중 노멀
            case 3: Set(SmartThresholdMode.Auto, -0.4f, 60, 30, 1f, 100, 25, 0, true, 3, true, SmartUvRule.Ignore, false, false, false, false, SmartWeightedNormals.None); break;              // 오가닉: 거의 소프트, 뚜렷한 접힘만
            case 4: Set(SmartThresholdMode.Manual, 0f, 25, 25, 0f, 180, 10, 0, false, 1, false, SmartUvRule.Ignore, false, true, false, false, SmartWeightedNormals.None); break;              // 건축/평면: 각도 25° 이상 모두 하드(문맥 없음)
            case 5: Set(SmartThresholdMode.Auto, 0f, 40, 20, 1f, 80, 15, 0, true, 2, true, SmartUvRule.HardenBordersSoftenInside, true, true, false, false, SmartWeightedNormals.None); break; // 노멀맵 베이크: UV 경계 = 하드, 안쪽 소프트
            case 6: Set(SmartThresholdMode.Auto, -0.2f, 50, 25, 1f, 85, 20, 0, true, 2, true, SmartUvRule.Ignore, false, true, false, false, SmartWeightedNormals.FaceAreaAndAngle); break;    // 베벨 + 가중 노멀: 베벨은 소프트, 큰 면이 노멀 지배
            case 7: Set(SmartThresholdMode.Manual, 0f, 30, 30, 0f, 180, 0, 0, false, 1, false, SmartUvRule.Ignore, false, false, false, false, SmartWeightedNormals.None); break;              // 고전 각도 30°
            case 8: Set(SmartThresholdMode.Manual, 0f, 180, 180, 0f, 180, 0, 0, false, 1, false, SmartUvRule.Ignore, false, false, false, false, SmartWeightedNormals.None); break;            // 전부 소프트
            case 9: Set(SmartThresholdMode.Manual, 0f, 0, 0, 0f, 0, 0, 0, false, 1, false, SmartUvRule.Ignore, false, false, false, false, SmartWeightedNormals.None); break;                  // 전부 하드
        }
    }

    private static OptionSpec SmartEdgeSpec() => new("Smart Soften/Harden Options", v =>
    {
        v.Set("preset", 1); ApplySmartPreset(1, v); v.Set("preset", 0);
    }, new[]
    {
        OptionField.E("preset", "Preset", SmartPresets),
        OptionField.E("mode", "Threshold", "Auto (shape distribution)", "Manual"),
        OptionField.F("bias", "Auto Bias (−1 soft … +1 hard)", -1, 1, 0.05, "Shift the automatic threshold: negative = fewer hard edges, positive = more"),
        OptionField.F("high", "Manual: Hard Angle", 0, 180, 1, "Crease strength at or above this is hard (Manual mode)"),
        OptionField.F("low", "Manual: Candidate Angle", 0, 180, 1, "Between Candidate and Hard: hard only when connected to a hard edge (hysteresis)"),
        OptionField.F("context", "Shape Context (0 angle only … 1 full)", 0, 1, 0.05, "How much the neighbouring ring edges reduce the strength: curved strips (cylinders, spheres, bevels) become soft"),
        OptionField.F("maxSmooth", "Always Hard Above", 0, 180, 1, "Dihedral angle that is hard regardless of context (a 4-sided cylinder is a box)"),
        OptionField.F("minHard", "Never Hard Below", 0, 90, 1, "Crease strength below this is always soft (filters bevel profile noise)"),
        OptionField.E("concavity", "Creases", "Convex and Concave", "Convex Only", "Concave Only"),
        OptionField.B("loop", "Propagate Along Loops", "Candidates become hard when an edge loop connects them to a hard edge"),
        OptionField.I("chain", "Min Chain Length", 0, 20, "Remove isolated hard chains shorter than this (0 = keep all)"),
        OptionField.B("gaps", "Fill Loop Gaps", "A soft edge between two hard edges on the same loop becomes hard"),
        OptionField.E("uv", "UV Rule", "Ignore UVs", "Harden UV Borders", "Harden Borders + Soften Inside", "Soften Inside Islands"),
        OptionField.B("seams", "UV Seams Hard", "UV seam edges are always hard"),
        OptionField.B("creases", "Crease Edges Hard", "Subdivision crease edges are always hard"),
        OptionField.B("keepHard", "Keep Existing Hard", "Never soften an edge that is hard now (only add)"),
        OptionField.B("keepSoft", "Keep Existing Soft", "Never harden an edge that is soft now (only remove)"),
        OptionField.E("weighted", "Weighted Normals", "None", "Face Area", "Face Area + Corner Angle"),
    }, "Apply");

    private static SmartEdgeOptions SmartOptionsFrom(HistoryParams p) => new()
    {
        ThresholdMode = p.Int("Threshold (0 Auto 1 Manual)") == 1 ? SmartThresholdMode.Manual : SmartThresholdMode.Auto,
        AutoBias = p.Float("Auto Bias"), HighAngle = p.Float("Hard Angle"), LowAngle = p.Float("Candidate Angle"),
        HysteresisRatio = 0.5f, ContextWeight = p.Float("Shape Context"), MaxSmoothAngle = p.Float("Always Hard Above"), MinHardAngle = p.Float("Never Hard Below"),
        Concavity = (SmartConcavity)Math.Clamp(p.Int("Creases (0 Both 1 Convex 2 Concave)"), 0, 2),
        LoopPropagation = p["Propagate Along Loops"].Bool, MinChainLength = p.Int("Min Chain Length"), FillGaps = p["Fill Loop Gaps"].Bool,
        UvRule = (SmartUvRule)Math.Clamp(p.Int("UV Rule (0 Ignore 1 Borders 2 Borders+Inside 3 Inside)"), 0, 3),
        SeamsHard = p["UV Seams Hard"].Bool, CreasesHard = p["Crease Edges Hard"].Bool,
        KeepExistingHard = p["Keep Existing Hard"].Bool, KeepExistingSoft = p["Keep Existing Soft"].Bool,
        WeightedNormals = (SmartWeightedNormals)Math.Clamp(p.Int("Weighted Normals (0 None 1 Area 2 Area+Angle)"), 0, 2),
    };

    private static HistoryParams SmartHistory(OptionValues v)
    {
        static HistoryParam B(string n, bool b) => new() { Name = n, Kind = HistoryParamKind.Bool, Value = new System.Numerics.Vector3(b ? 1 : 0, 0, 0), Min = 0, Max = 1, Step = 1 };
        return new HistoryParams(
            HistoryParam.I("Threshold (0 Auto 1 Manual)", v.Int("mode"), 0, 1),
            HistoryParam.F("Auto Bias", v.Float("bias"), -1f, 1f, 0.05f),
            HistoryParam.F("Hard Angle", v.Float("high", 40f), 0f, 180f, 1f),
            HistoryParam.F("Candidate Angle", v.Float("low", 20f), 0f, 180f, 1f),
            HistoryParam.F("Shape Context", v.Float("context", 1f), 0f, 1f, 0.05f),
            HistoryParam.F("Always Hard Above", v.Float("maxSmooth", 80f), 0f, 180f, 1f),
            HistoryParam.F("Never Hard Below", v.Float("minHard", 15f), 0f, 90f, 1f),
            HistoryParam.I("Creases (0 Both 1 Convex 2 Concave)", v.Int("concavity"), 0, 2),
            B("Propagate Along Loops", v.Bool("loop", true)),
            HistoryParam.I("Min Chain Length", v.Int("chain", 2), 0, 20),
            B("Fill Loop Gaps", v.Bool("gaps", true)),
            HistoryParam.I("UV Rule (0 Ignore 1 Borders 2 Borders+Inside 3 Inside)", v.Int("uv"), 0, 3),
            B("UV Seams Hard", v.Bool("seams")), B("Crease Edges Hard", v.Bool("creases", true)),
            B("Keep Existing Hard", v.Bool("keepHard")), B("Keep Existing Soft", v.Bool("keepSoft")),
            HistoryParam.I("Weighted Normals (0 None 1 Area 2 Area+Angle)", v.Int("weighted"), 0, 2));
    }

    /// <summary>
    /// normals.smartSoftenHardenApply 본체. 프리셋이 Custom이 아니면 프리셋 값을 옵션에 써 넣고(Action Popup·옵션 창이 그 값을 보이게) Custom으로 되돌린 뒤 실행한다.
    /// 대상(ForEachMeshTarget 엣지 규칙)마다 MeshOpCommand(이력 'Smart Soften/Harden') → 헬프 라인에 하드/소프트 수와 쓴 임계값.
    /// </summary>
    private void SmartSoftenHardenSelection()
    {
        var v = Options("normals.smartSoftenHarden");
        int preset = v.Int("preset");
        if (preset > 0) { ApplySmartPreset(preset, v); v.Set("preset", 0); }
        var hist = SmartHistory(v);
        var reports = new List<(string name, SmartEdgeReport rep)>();
        ForEachMeshTarget("Smart Soften/Harden", SelectMode.Edge, (id, ids) =>
        {
            var node = Document.Get(id);
            bool whole = Document.Selection.Mode == SelectMode.Object;
            var edgeIds = whole ? null : ids;
            string name = node.Name;
            return new MeshOpCommand("Smart Soften/Harden", id, hist.Clone(), (m, p) =>
            {
                var rep = MeshOps.SmartSoftenHarden(m, edgeIds, SmartOptionsFrom(p));
                reports.Add((name, rep));
                return (true, null, null);
            });
        });
        if (reports.Count == 0) return;
        var r = reports[^1].rep;
        string presetName = preset > 0 ? $"[{SmartPresets[preset]}] " : "";
        HelpLine.Text = $"Smart Soften/Harden {presetName}{reports.Count} mesh(es): {reports.Sum(x => x.rep.Hard)} hard, {reports.Sum(x => x.rep.Soft)} soft, {reports.Sum(x => x.rep.Changed)} changed"
            + (r.HighUsed > 0 ? $" (threshold {r.HighUsed:0.#}°{(r.AutoFallback ? " default" : " auto")}, candidates ≥ {r.LowUsed:0.#}°)" : "") + ". Adjust in the Action Popup.";
    }

    private void RegisterSmartEdgeActions()
    {
        RegisterOptionPair("normals.smartSoftenHarden", "Smart Soften/Harden", SmartEdgeSpec(), SmartSoftenHardenSelection, HasMeshSelection);
        // 프리셋 바로 실행(메뉴 하위 항목): 옵션의 preset만 바꾸고 Apply
        for (int i = 1; i < SmartPresets.Length; i++)
        {
            int p = i;
            Actions.Register($"normals.smartPreset{p}", SmartPresets[p], () => { Options("normals.smartSoftenHarden").Set("preset", p); Actions.Invoke("normals.smartSoftenHardenApply"); }, canExecute: HasMeshSelection, repeatable: true);
        }
    }
}
