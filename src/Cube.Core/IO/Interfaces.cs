using Cube.Core.Scene;

namespace Cube.Core.IO;

public enum TargetEngine { Generic, Unity, Godot }

/// <summary>내보내기 프리셋. glTF는 엔진별 차이가 없고, FBX(M4)에서 단위/축 베이크 옵션이 갈린다.</summary>
public sealed record ExportPreset(string Name, TargetEngine Target, bool BakeCentimeters = false)
{
    public static readonly ExportPreset Unity = new("Unity", TargetEngine.Unity, BakeCentimeters: true);
    public static readonly ExportPreset Godot = new("Godot", TargetEngine.Godot);
    public static readonly ExportPreset Generic = new("Generic", TargetEngine.Generic);
}

public sealed record ExportResult(bool Ok, string Message, int NodeCount = 0, int TriangleCount = 0)
{
    public static ExportResult Fail(string msg) => new(false, msg);
}

public sealed record ImportResult(bool Ok, string Message, IReadOnlyList<SceneNode> Nodes)
{
    /// <summary>파일에 들어 있던 애니메이션(트랙의 Node는 Nodes 트리의 노드 ID; 호출자가 문서에 넣는다).</summary>
    public IReadOnlyList<AnimationClip> Animations { get; init; } = Array.Empty<AnimationClip>();
    /// <summary>가져온 머티리얼(ID 배정됨, 아직 문서에 없음; 노드의 MaterialId가 가리킨다. 호출자가 문서에 넣는다).</summary>
    public IReadOnlyList<MaterialDef> Materials { get; init; } = Array.Empty<MaterialDef>();
    public static ImportResult Fail(string msg) => new(false, msg, Array.Empty<SceneNode>());
}

public interface IExporter
{
    string Name { get; }
    /// <summary>소문자 확장자(".glb", ".gltf", ".fbx").</summary>
    IReadOnlyList<string> Extensions { get; }
    ExportResult Export(Document doc, IReadOnlyList<SceneNode> nodes, string path, ExportPreset preset);
}

public interface IImporter
{
    string Name { get; }
    IReadOnlyList<string> Extensions { get; }
    /// <summary>파일을 읽어 노드들을 만든다(문서에 추가하지는 않음). 호출자가 명령으로 추가한다.</summary>
    ImportResult Import(string path, Document doc, ImportOptions options);
}

public sealed record ImportOptions(float WeldThreshold = 1e-5f, float HardAngleDegrees = 60f, bool MergeTriangleQuads = true)
{
    public static readonly ImportOptions Default = new();
}
