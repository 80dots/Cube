using Cube.Core.Scene;
using Godot;
using GArr = Godot.Collections.Array;
using GDict = Godot.Collections.Dictionary;
using NVec3 = System.Numerics.Vector3;

namespace Cube.App.IO;

/// <summary>
/// glTF 내보내기에서 문서 머티리얼(<see cref="MaterialDef"/>)의 모든 파라미터·텍스처를 기록하는 GltfDocument 확장.
/// DocumentToGodotScene이 만든 StandardMaterial3D(메타 <see cref="MetaKey"/>)와 짝지어, Godot이 쓴 머티리얼 JSON 항목을 통째로 다시 쓴다.
/// <list type="bullet">
/// <item>_ExportPreserialize: 맵을 glTF 사양이 읽는 채널로 패킹한 이미지/텍스처를 state에 추가(Godot 자신의 이미지보다 앞 번호).</item>
/// <item>_ExportPost: pbrMetallicRoughness·normal·occlusion·emissive·alpha·doubleSided + KHR_materials_* 확장 + KHR_texture_transform, extensionsUsed.</item>
/// </list>
/// 회색조 맵은 원본 이미지의 R 채널을 읽는다(뷰포트 셰이더와 같은 규약). 계수 색은 sRGB → 선형으로 바꿔 쓴다.
/// </summary>
public partial class GltfMaterialExtension : GltfDocumentExtension
{
    public const string MetaKey = "cube_material_id";

    // Godot은 등록한 확장 객체를 그대로 쓰지 않고 같은 클래스의 새 인스턴스를 만들어 호출하므로(4.7에서 확인), 내보내기 상태는 정적 필드에 둔다.
    // 내보내기는 메인 스레드에서 한 번에 하나뿐이다. GltfExporter가 Begin()으로 초기화한다.
    private static readonly Dictionary<int, MaterialDef> _defs = new();
    /// <summary>머티리얼 ID → 슬롯 이름 → glTF 텍스처 번호.</summary>
    private static readonly Dictionary<int, Dictionary<string, int>> _slots = new();
    private static readonly Dictionary<string, int> _packCache = new();
    private static readonly Dictionary<string, Image?> _srcCache = new();
    private static readonly HashSet<string> _used = new();
    public static readonly List<string> Warnings = new();

    /// <summary>이번 내보내기의 머티리얼을 지정하고 이전 상태를 비운다.</summary>
    public static void Begin(IEnumerable<MaterialDef> defs)
    {
        _defs.Clear(); _slots.Clear(); _packCache.Clear(); _srcCache.Clear(); _used.Clear(); Warnings.Clear();
        foreach (var d in defs) _defs[d.Id] = d;
    }

    /// <summary>내보내기 후 이미지 캐시 등을 놓는다.</summary>
    public static void End() { _defs.Clear(); _slots.Clear(); _packCache.Clear(); _srcCache.Clear(); _used.Clear(); }

    public static float ShininessToRoughness(float s) => Math.Clamp(MathF.Sqrt(2f / (s + 2f)), 0.05f, 1f);

    // ------------------------------------------------------------------ 텍스처 패킹

    /// <summary>패킹 레이어: 원본 이미지의 srcCh 채널들을 대상 dstCh 채널로(Multiply면 곱하기). Map은 바이트 변환(선택).</summary>
    private sealed record Layer(string Path, int[] Dst, int[] Src, bool Multiply = false, Func<byte, byte>? Map = null);

    private static readonly int[] RGB = { 0, 1, 2 }, R = { 0 }, G = { 1 }, B = { 2 }, A = { 3 };

    private static Image? LoadSource(string path)
    {
        if (_srcCache.TryGetValue(path, out var img)) return img;
        img = null;
        try
        {
            var i = new Image();
            if (i.Load(path) == Error.Ok)
            {
                if (i.IsCompressed()) i.Decompress();
                i.Convert(Image.Format.Rgba8);
                img = i;
            }
            else Warnings.Add($"image load failed: {path}");
        }
        catch (Exception ex) { Warnings.Add($"image load failed: {path}: {ex.Message}"); }
        _srcCache[path] = img;
        return img;
    }

    /// <summary>레이어들을 한 RGBA8 이미지로 합쳐 state에 이미지+텍스처를 추가하고 텍스처 번호를 돌려준다(같은 조합은 재사용). 원본이 하나도 없으면 -1.</summary>
    private static int Pack(GltfState state, string name, byte[] fill, params Layer[] layers)
    {
        string key = string.Join("|", fill) + "#" + string.Join(";", layers.Select(l => $"{l.Path}>{string.Join(",", l.Dst)}<{string.Join(",", l.Src)}{(l.Multiply ? "*" : "")}{(l.Map != null ? "f" + name : "")}"));
        if (_packCache.TryGetValue(key, out int cached)) return cached;
        var srcs = new List<(Layer l, Image img)>();
        foreach (var l in layers) { var img = LoadSource(l.Path); if (img != null) srcs.Add((l, img)); }
        if (srcs.Count == 0) { _packCache[key] = -1; return -1; }
        int w = srcs.Max(s => s.img.GetWidth()), h = srcs.Max(s => s.img.GetHeight());
        var data = new byte[w * h * 4];
        for (int i = 0; i < data.Length; i += 4) { data[i] = fill[0]; data[i + 1] = fill[1]; data[i + 2] = fill[2]; data[i + 3] = fill[3]; }
        foreach (var (l, img0) in srcs)
        {
            var img = img0;
            if (img.GetWidth() != w || img.GetHeight() != h) { img = (Image)img0.Duplicate(); img.Resize(w, h, Image.Interpolation.Bilinear); }
            var src = img.GetData();
            for (int p = 0; p < w * h; p++)
                for (int k = 0; k < l.Dst.Length; k++)
                {
                    byte v = src[p * 4 + l.Src[k]];
                    if (l.Map != null) v = l.Map(v);
                    int di = p * 4 + l.Dst[k];
                    data[di] = l.Multiply ? (byte)((data[di] * v + 127) / 255) : v;
                }
        }
        var outImg = Image.CreateFromData(w, h, false, Image.Format.Rgba8, data);
        outImg.ResourceName = name;
        var tex = ImageTexture.CreateFromImage(outImg);
        tex.ResourceName = name;
        var images = state.GetImages();
        images.Add(tex);
        state.SetImages(images);
        var textures = state.GetTextures();
        textures.Add(new GltfTexture { SrcImage = images.Count - 1 });
        state.SetTextures(textures);
        int idx = textures.Count - 1;
        _packCache[key] = idx;
        return idx;
    }

    private static string Safe(string s) => new(s.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());

    private static bool Applies(MaterialDef d, string key) => MaterialParams.Get(key)?.Types.Contains(d.Type) == true;
    private static string? TexOf(MaterialDef d, string key) => Applies(d, key) ? d.Tex(key) : null;

    public override Error _ExportPreflight(GltfState state, Node root) => Error.Ok;

    public override Error _ExportPreserialize(GltfState state)
    {
        foreach (var d in _defs.Values) _slots[d.Id] = BuildTextures(state, d);
        return Error.Ok;
    }

    private static Dictionary<string, int> BuildTextures(GltfState state, MaterialDef d)
    {
        var s = new Dictionary<string, int>();
        string n = Safe(d.Name);
        void Put(string slot, int idx) { if (idx >= 0) s[slot] = idx; }
        byte[] white = { 255, 255, 255, 255 };

        // 베이스 컬러(+ 불투명도 맵 → 알파 채널에 곱함)
        string? col = TexOf(d, "color"), alpha = TexOf(d, "alpha");
        if (col != null || alpha != null)
        {
            var ls = new List<Layer>();
            if (col != null) { ls.Add(new Layer(col, RGB, RGB)); ls.Add(new Layer(col, A, A)); }
            if (alpha != null) ls.Add(new Layer(alpha, A, R, Multiply: true));
            Put("baseColor", Pack(state, n + "_baseColor", white, ls.ToArray()));
        }
        if (d.Type == MaterialType.Pbr)
        {
            string? met = TexOf(d, "metallic"), rough = TexOf(d, "roughness");
            if (met != null || rough != null)
            {
                var ls = new List<Layer>();
                if (rough != null) ls.Add(new Layer(rough, G, R));
                if (met != null) ls.Add(new Layer(met, B, R));
                Put("metallicRoughness", Pack(state, n + "_metallicRoughness", white, ls.ToArray()));
            }
        }
        else if (d.Type == MaterialType.BlinnPhong && TexOf(d, "shininess") is { } shin)
        {
            // 광택 맵(0..1 → 1..128) → 러프니스 맵(G), roughnessFactor = 1
            Put("metallicRoughness", Pack(state, n + "_roughnessFromShininess", white,
                new Layer(shin, G, R, Map: v => (byte)Math.Clamp((int)MathF.Round(ShininessToRoughness(1f + v / 255f * 127f) * 255f), 0, 255))));
        }
        if (TexOf(d, "normal") is { } nrm) Put("normal", Pack(state, n + "_normal", white, new Layer(nrm, RGB, RGB)));
        if (TexOf(d, "occlusion") is { } occ) Put("occlusion", Pack(state, n + "_occlusion", white, new Layer(occ, RGB, new[] { 0, 0, 0 })));
        if (TexOf(d, "emissive") is { } em) Put("emissive", Pack(state, n + "_emissive", white, new Layer(em, RGB, RGB)));

        if (d.Type == MaterialType.BlinnPhong && TexOf(d, "specular") is { } bspec)
            Put("specularColor", Pack(state, n + "_specularColor", white, new Layer(bspec, RGB, RGB)));

        if (d.Type != MaterialType.Pbr) return s;
        // 클리어코트: R = 세기, G = 러프니스(한 이미지)
        {
            string? cc = d.Tex("clearcoat"), ccr = d.Tex("clearcoatRoughness");
            if (cc != null || ccr != null)
            {
                var ls = new List<Layer>();
                if (cc != null) ls.Add(new Layer(cc, R, R));
                if (ccr != null) ls.Add(new Layer(ccr, G, R));
                Put("clearcoat", Pack(state, n + "_clearcoat", white, ls.ToArray()));
            }
            if (d.Tex("clearcoatNormal") is { } ccn) Put("clearcoatNormal", Pack(state, n + "_clearcoatNormal", white, new Layer(ccn, RGB, RGB)));
        }
        if (d.Tex("transmission") is { } tr) Put("transmission", Pack(state, n + "_transmission", white, new Layer(tr, R, R)));
        if (d.Tex("thickness") is { } th) Put("thickness", Pack(state, n + "_thickness", white, new Layer(th, G, R)));
        // 스펙큘러: RGB = 색, A = 세기(한 이미지)
        {
            string? sf = d.Tex("specularFactor"), sc = d.Tex("specularColorFactor");
            if (sf != null || sc != null)
            {
                var ls = new List<Layer>();
                if (sc != null) ls.Add(new Layer(sc, RGB, RGB));
                if (sf != null) ls.Add(new Layer(sf, A, R));
                Put("specular", Pack(state, n + "_specular", white, ls.ToArray()));
            }
        }
        // 시인: RGB = 색, A = 러프니스
        {
            string? sc = d.Tex("sheenColor"), sr = d.Tex("sheenRoughness");
            if (sc != null || sr != null)
            {
                var ls = new List<Layer>();
                if (sc != null) ls.Add(new Layer(sc, RGB, RGB));
                if (sr != null) ls.Add(new Layer(sr, A, R));
                Put("sheen", Pack(state, n + "_sheen", white, ls.ToArray()));
            }
        }
        // 이리데선스: R = 세기, G = 두께(min..max 보간)
        {
            string? ir = d.Tex("iridescence"), it = d.Tex("iridescenceThicknessMax");
            if (ir != null || it != null)
            {
                var ls = new List<Layer>();
                if (ir != null) ls.Add(new Layer(ir, R, R));
                if (it != null) ls.Add(new Layer(it, G, R));
                Put("iridescence", Pack(state, n + "_iridescence", white, ls.ToArray()));
            }
        }
        // 이방성: RG = 방향 (1,0) → (1.0, 0.5), B = 세기
        if (d.Tex("anisotropy") is { } an) Put("anisotropy", Pack(state, n + "_anisotropy", new byte[] { 255, 128, 255, 255 }, new Layer(an, B, R)));
        return s;
    }

    // ------------------------------------------------------------------ JSON

    public override Error _ExportPost(GltfState state)
    {
        var json = state.Json;
        if (!json.TryGetValue("materials", out var mv) || mv.VariantType != Variant.Type.Array) return Error.Ok;
        var mats = mv.AsGodotArray();
        var stateMats = state.GetMaterials();
        for (int i = 0; i < mats.Count && i < stateMats.Count; i++)
        {
            var gm = stateMats[i];
            if (gm == null || !gm.HasMeta(MetaKey)) continue;
            int id = gm.GetMeta(MetaKey).AsInt32();
            if (!_defs.TryGetValue(id, out var def)) continue;
            var old = mats[i].AsGodotDictionary();
            mats[i] = BuildMaterial(def, old.TryGetValue("name", out var nm) ? nm.AsString() : def.Name);
        }
        json["materials"] = mats;
        if (_used.Count > 0)
        {
            var used = json.TryGetValue("extensionsUsed", out var uv) ? uv.AsGodotArray() : new GArr();
            foreach (var e in _used.OrderBy(x => x)) if (!used.Contains(e)) used.Add(e);
            json["extensionsUsed"] = used;
        }
        state.Json = json;
        return Error.Ok;
    }

    private static float ToLinear(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    private static GArr Lin(NVec3 c) => new() { R4(ToLinear(c.X)), R4(ToLinear(c.Y)), R4(ToLinear(c.Z)) };
    private static double R4(float v) => Math.Round(v, 6);

    private static GDict BuildMaterial(MaterialDef d, string name)
    {
        var slots = _slots.TryGetValue(d.Id, out var s0) ? s0 : new Dictionary<string, int>();
        var xform = TextureTransform(d);
        GDict? Tex(string slot, string? scaleKey = null, double scale = 1)
        {
            if (!slots.TryGetValue(slot, out int idx)) return null;
            var t = new GDict { ["index"] = idx };
            if (scaleKey != null) t[scaleKey] = scale;
            if (xform != null) { t["extensions"] = new GDict { ["KHR_texture_transform"] = xform.Duplicate(true) }; _used.Add("KHR_texture_transform"); }
            return t;
        }
        void SetTex(GDict target, string key, GDict? t) { if (t != null) target[key] = t; }

        var m = new GDict { ["name"] = name };
        var pbr = new GDict();
        var c = d.Color;
        pbr["baseColorFactor"] = new GArr { R4(ToLinear(c.X)), R4(ToLinear(c.Y)), R4(ToLinear(c.Z)), R4(Math.Clamp(d.GetF("alpha"), 0, 1)) };
        SetTex(pbr, "baseColorTexture", Tex("baseColor"));
        var ext = new GDict();
        switch (d.Type)
        {
            case MaterialType.Pbr:
                pbr["metallicFactor"] = R4(d.Metallic); pbr["roughnessFactor"] = R4(d.Roughness);
                SetTex(pbr, "metallicRoughnessTexture", Tex("metallicRoughness"));
                break;
            case MaterialType.BlinnPhong:
                pbr["metallicFactor"] = 0.0;
                bool shinMap = slots.ContainsKey("metallicRoughness");
                pbr["roughnessFactor"] = shinMap ? 1.0 : R4(ShininessToRoughness(d.Shininess));
                SetTex(pbr, "metallicRoughnessTexture", Tex("metallicRoughness"));
                if (slots.ContainsKey("specularColor"))
                {
                    var sp = new GDict { ["specularColorFactor"] = Lin(d.Specular) };
                    SetTex(sp, "specularColorTexture", Tex("specularColor"));
                    ext["KHR_materials_specular"] = sp;
                }
                break;
            case MaterialType.Unlit:
            case MaterialType.Matcap:
                pbr["metallicFactor"] = 0.0; pbr["roughnessFactor"] = 1.0;
                ext["KHR_materials_unlit"] = new GDict();
                break;
            default:
                pbr["metallicFactor"] = 0.0; pbr["roughnessFactor"] = 1.0;
                break;
        }
        m["pbrMetallicRoughness"] = pbr;

        if (Applies(d, "normal")) SetTex(m, "normalTexture", Tex("normal", "scale", R4(d.GetF("normal"))));
        if (Applies(d, "occlusion")) SetTex(m, "occlusionTexture", Tex("occlusion", "strength", R4(d.GetF("occlusion"))));
        if (Applies(d, "emissive"))
        {
            var em = d.Get("emissive");
            if (em != NVec3.Zero || slots.ContainsKey("emissive"))
            {
                m["emissiveFactor"] = Lin(em);
                SetTex(m, "emissiveTexture", Tex("emissive"));
                float es = d.GetF("emissiveStrength");
                if (es != 1f) ext["KHR_materials_emissive_strength"] = new GDict { ["emissiveStrength"] = R4(es) };
            }
        }
        switch ((int)d.GetF("alphaMode"))
        {
            case 1: m["alphaMode"] = "MASK"; m["alphaCutoff"] = R4(d.GetF("alphaCutoff")); break;
            case 2: m["alphaMode"] = "BLEND"; break;
        }
        if (d.GetF("doubleSided") > 0.5f) m["doubleSided"] = true;

        if (d.Type == MaterialType.Pbr)
        {
            var used = d.UsedExtensions().ToHashSet();
            if (used.Contains("KHR_materials_clearcoat"))
            {
                var e = new GDict { ["clearcoatFactor"] = R4(d.GetF("clearcoat")), ["clearcoatRoughnessFactor"] = R4(d.GetF("clearcoatRoughness")) };
                if (d.Tex("clearcoat") != null) SetTex(e, "clearcoatTexture", Tex("clearcoat"));
                if (d.Tex("clearcoatRoughness") != null) SetTex(e, "clearcoatRoughnessTexture", Tex("clearcoat"));
                SetTex(e, "clearcoatNormalTexture", Tex("clearcoatNormal", "scale", R4(d.GetF("clearcoatNormal"))));
                ext["KHR_materials_clearcoat"] = e;
            }
            if (used.Contains("KHR_materials_transmission"))
            {
                var e = new GDict { ["transmissionFactor"] = R4(d.GetF("transmission")) };
                SetTex(e, "transmissionTexture", Tex("transmission"));
                ext["KHR_materials_transmission"] = e;
            }
            if (used.Contains("KHR_materials_volume"))
            {
                var e = new GDict { ["thicknessFactor"] = R4(d.GetF("thickness")) };
                SetTex(e, "thicknessTexture", Tex("thickness"));
                float ad = d.GetF("attenuationDistance");
                if (ad > 0) e["attenuationDistance"] = R4(ad);
                e["attenuationColor"] = Lin(d.Get("attenuationColor"));
                ext["KHR_materials_volume"] = e;
            }
            if (used.Contains("KHR_materials_ior")) ext["KHR_materials_ior"] = new GDict { ["ior"] = R4(d.GetF("ior")) };
            if (used.Contains("KHR_materials_dispersion")) ext["KHR_materials_dispersion"] = new GDict { ["dispersion"] = R4(d.GetF("dispersion")) };
            if (used.Contains("KHR_materials_specular"))
            {
                var e = new GDict { ["specularFactor"] = R4(d.GetF("specularFactor")), ["specularColorFactor"] = Lin(d.Get("specularColorFactor")) };
                if (d.Tex("specularFactor") != null) SetTex(e, "specularTexture", Tex("specular"));
                if (d.Tex("specularColorFactor") != null) SetTex(e, "specularColorTexture", Tex("specular"));
                ext["KHR_materials_specular"] = e;
            }
            if (used.Contains("KHR_materials_sheen"))
            {
                var e = new GDict { ["sheenColorFactor"] = Lin(d.Get("sheenColor")), ["sheenRoughnessFactor"] = R4(d.GetF("sheenRoughness")) };
                if (d.Tex("sheenColor") != null) SetTex(e, "sheenColorTexture", Tex("sheen"));
                if (d.Tex("sheenRoughness") != null) SetTex(e, "sheenRoughnessTexture", Tex("sheen"));
                ext["KHR_materials_sheen"] = e;
            }
            if (used.Contains("KHR_materials_iridescence"))
            {
                var e = new GDict
                {
                    ["iridescenceFactor"] = R4(d.GetF("iridescence")), ["iridescenceIor"] = R4(d.GetF("iridescenceIor")),
                    ["iridescenceThicknessMinimum"] = R4(d.GetF("iridescenceThicknessMin")), ["iridescenceThicknessMaximum"] = R4(d.GetF("iridescenceThicknessMax")),
                };
                if (d.Tex("iridescence") != null) SetTex(e, "iridescenceTexture", Tex("iridescence"));
                if (d.Tex("iridescenceThicknessMax") != null) SetTex(e, "iridescenceThicknessTexture", Tex("iridescence"));
                ext["KHR_materials_iridescence"] = e;
            }
            if (used.Contains("KHR_materials_anisotropy"))
            {
                var e = new GDict { ["anisotropyStrength"] = R4(d.GetF("anisotropy")), ["anisotropyRotation"] = R4(d.GetF("anisotropyRotation") * MathF.PI / 180f) };
                SetTex(e, "anisotropyTexture", Tex("anisotropy"));
                ext["KHR_materials_anisotropy"] = e;
            }
        }
        if (ext.Count > 0)
        {
            m["extensions"] = ext;
            foreach (var k in ext.Keys) _used.Add(k.AsString());
        }
        return m;
    }

    /// <summary>KHR_texture_transform 객체(기본값이면 null). 뷰포트와 같이 glTF(상단 원점) UV 공간 값 그대로.</summary>
    private static GDict? TextureTransform(MaterialDef d)
    {
        float ou = d.GetF("uvOffsetU"), ov = d.GetF("uvOffsetV"), su = d.GetF("uvScaleU"), sv = d.GetF("uvScaleV"), rot = d.GetF("uvRotation");
        if (ou == 0 && ov == 0 && su == 1 && sv == 1 && rot == 0) return null;
        var t = new GDict();
        if (ou != 0 || ov != 0) t["offset"] = new GArr { R4(ou), R4(ov) };
        if (su != 1 || sv != 1) t["scale"] = new GArr { R4(su), R4(sv) };
        if (rot != 0) t["rotation"] = R4(rot * MathF.PI / 180f);
        return t;
    }
}
