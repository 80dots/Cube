using Cube.Core.IO;
using Cube.Core.Scene;
using Godot;

namespace Cube.App.IO;

/// <summary>Godot 내장 GltfDocument로 .glb/.gltf를 쓴다. 좌표계 변환은 Godot이 처리(둘 다 오른손 Y-up, m).</summary>
/// <remarks>
/// 순서: 포즈를 rest로 돌림 → <see cref="DocumentToGodotScene.Build"/>로 임시 Godot 씬 생성 →
/// <see cref="GltfMaterialExtension"/>을 이 내보내기 동안만 등록 → AppendFromScene/WriteToFilesystem →
/// finally에서 확장 등록 해제와 임시 씬 해제. 확장자 .glb면 바이너리, .gltf면 JSON+외부 버퍼로 쓰인다.
/// </remarks>
public sealed class GltfExporter : IExporter
{
    /// <summary>메뉴·다이얼로그 필터에 표시되는 형식 이름.</summary>
    public string Name => "glTF 2.0";
    /// <summary>이 exporter가 처리하는 확장자.</summary>
    public IReadOnlyList<string> Extensions { get; } = new[] { ".glb", ".gltf" };

    /// <summary>
    /// <paramref name="nodes"/>(와 하위 트리)를 glTF로 쓴다.
    /// 반환 메시지에는 노드 수·삼각형 수와 머티리얼 확장 경고(예: 이미지 로드 실패)가 포함된다.
    /// </summary>
    public ExportResult Export(Document doc, IReadOnlyList<SceneNode> nodes, string path, ExportPreset preset)
    {
        using var restPose = Cube.Core.Scene.AnimationPose.RestScope(doc); // 재생 포즈가 아니라 rest(바인드) 포즈로 기록
        if (nodes.Count == 0) return ExportResult.Fail("Nothing to export.");
        // 파일 이름을 glTF 루트 노드 이름으로 쓴다. Build는 사용한 머티리얼을 usedMaterials에 채운다
        string rootName = System.IO.Path.GetFileNameWithoutExtension(path);
        var usedMaterials = new List<MaterialDef>();
        var (root, count, tris) = DocumentToGodotScene.Build(nodes, SanitizeName(rootName), doc.Animations, usedMaterials, doc);
        // 머티리얼 텍스처·PBR 확장은 문서 확장이 기록한다(GltfDocument 확장 등록은 전역이라 이 내보내기 동안만 등록)
        GltfMaterialExtension.Begin(usedMaterials);
        var matExt = new GltfMaterialExtension();
        GltfDocument.RegisterGltfDocumentExtension(matExt);
        try
        {
            var gltf = new GltfDocument();
            var state = new GltfState();
            var err = gltf.AppendFromScene(root, state);
            if (err != Error.Ok) return ExportResult.Fail($"AppendFromScene failed: {err}");
            err = gltf.WriteToFilesystem(state, path);
            if (err != Error.Ok) return ExportResult.Fail($"WriteToFilesystem failed: {err}");
            // 확장 경고는 중복을 제거해 결과 메시지 뒤에 붙인다
            string warn = GltfMaterialExtension.Warnings.Count > 0 ? " (" + string.Join("; ", GltfMaterialExtension.Warnings.Distinct()) + ")" : "";
            return new ExportResult(true, $"Exported {count} node(s), {tris} triangles to {path}{warn}", count, tris);
        }
        catch (Exception ex) { return ExportResult.Fail(ex.Message); }
        finally { GltfDocument.UnregisterGltfDocumentExtension(matExt); GltfMaterialExtension.End(); root.Free(); }
    }

    /// <summary>노드 이름에 쓸 수 없는 문자(글자·숫자·_ 이외)를 _로 바꾼다. 비면 "Scene".</summary>
    private static string SanitizeName(string s)
    {
        var chars = s.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray();
        var r = new string(chars);
        return r.Length == 0 ? "Scene" : r;
    }
}
