using Cube.Core.Scene;

namespace Cube.Core.IO.Fbx;

/// <summary>자체 바이너리 FBX 7.4 writer(M4). Godot 의존 없음. 단위 cm 베이크(Unity "Convert Units"에서 scale 1).</summary>
/// <remarks>
/// FbxSceneBuilder로 노드 트리를 만들고 FbxBinaryWriter로 기록한다. 텍스처 상대 경로 기준이 되도록 BaseDir에 출력 폴더를 넣는다.
/// </remarks>
public sealed class FbxExporter : IExporter
{
    /// <summary>형식 이름.</summary>
    public string Name => "FBX (binary 7.4)";
    /// <summary>지원 확장자(.fbx).</summary>
    public IReadOnlyList<string> Extensions { get; } = new[] { ".fbx" };

    /// <summary>
    /// 선택 노드(또는 전체)를 FBX 7.4 바이너리로 쓴다. 노드 수·삼각형 수는 빌더가 센 값을 결과에 담는다. 예외는 실패 결과로 바꾼다.
    /// </summary>
    public ExportResult Export(Document doc, IReadOnlyList<SceneNode> nodes, string path, ExportPreset preset)
    {
        using var restPose = Cube.Core.Scene.AnimationPose.RestScope(doc); // 재생 포즈가 아니라 rest(바인드) 포즈로 기록
        if (nodes.Count == 0) return ExportResult.Fail("Nothing to export.");
        try
        {
            var builder = new FbxSceneBuilder(doc, FbxExportOptions.Default with { BaseDir = Path.GetDirectoryName(Path.GetFullPath(path)) });
            var top = builder.Build(nodes);
            FbxBinaryWriter.Write(path, top, FbxExportOptions.Default.Compress);
            return new ExportResult(true, $"Exported {builder.NodeCount} node(s), {builder.TriangleCount} triangles to {path} (FBX 7.4, cm)", builder.NodeCount, builder.TriangleCount);
        }
        catch (Exception ex) { return ExportResult.Fail(ex.Message); }
    }
}
