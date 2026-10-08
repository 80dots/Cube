using Cube.Core.Scene;

namespace Cube.Core.IO;

/// <summary>
/// 내보내기 대상 엔진. 프리셋이 어떤 엔진용인지 표시하며 FBX에서 단위/축 베이크 같은 엔진별 차이를 고를 때 쓴다.
/// </summary>
public enum TargetEngine { Generic, Unity, Godot }

/// <summary>내보내기 프리셋. glTF는 엔진별 차이가 없고, FBX(M4)에서 단위/축 베이크 옵션이 갈린다.</summary>
/// <remarks>
/// <see cref="Name"/>은 UI 표시용 이름, <see cref="Target"/>은 대상 엔진,
/// <see cref="BakeCentimeters"/>가 true면 위치 값을 cm(×100)로 베이크해 내보낸다(Unity의 FBX 관례).
/// </remarks>
public sealed record ExportPreset(string Name, TargetEngine Target, bool BakeCentimeters = false)
{
    /// <summary>Unity용 프리셋. cm 베이크를 켠다.</summary>
    public static readonly ExportPreset Unity = new("Unity", TargetEngine.Unity, BakeCentimeters: true);
    /// <summary>Godot용 프리셋. 내부 좌표계(m, Y-up)를 그대로 쓴다.</summary>
    public static readonly ExportPreset Godot = new("Godot", TargetEngine.Godot);
    /// <summary>특정 엔진을 가정하지 않는 기본 프리셋.</summary>
    public static readonly ExportPreset Generic = new("Generic", TargetEngine.Generic);
}

/// <summary>
/// 내보내기 결과. <c>Ok</c> = 성공 여부, <c>Message</c> = 사용자에게 보여 줄 메시지(실패 사유 또는 요약),
/// <c>NodeCount</c>/<c>TriangleCount</c> = 기록한 노드 수와 삼각형 수(상태 표시용 통계).
/// </summary>
public sealed record ExportResult(bool Ok, string Message, int NodeCount = 0, int TriangleCount = 0)
{
    /// <summary>실패 결과를 만드는 단축 함수(통계는 0).</summary>
    public static ExportResult Fail(string msg) => new(false, msg);
}

/// <summary>
/// 가져오기 결과. <c>Nodes</c>는 새로 만든 최상위 노드들(아직 문서에 들어가지 않음)이며,
/// 호출자(FileActions 등)가 명령(Undo 가능)으로 문서에 추가한다.
/// </summary>
public sealed record ImportResult(bool Ok, string Message, IReadOnlyList<SceneNode> Nodes)
{
    /// <summary>파일에 들어 있던 애니메이션(트랙의 Node는 Nodes 트리의 노드 ID; 호출자가 문서에 넣는다).</summary>
    public IReadOnlyList<AnimationClip> Animations { get; init; } = Array.Empty<AnimationClip>();
    /// <summary>가져온 머티리얼(ID 배정됨, 아직 문서에 없음; 노드의 MaterialId가 가리킨다. 호출자가 문서에 넣는다).</summary>
    public IReadOnlyList<MaterialDef> Materials { get; init; } = Array.Empty<MaterialDef>();
    /// <summary>실패 결과를 만드는 단축 함수(빈 노드 목록).</summary>
    public static ImportResult Fail(string msg) => new(false, msg, Array.Empty<SceneNode>());
}

/// <summary>
/// 파일 내보내기 플러그인 인터페이스(glTF/FBX/OBJ 등). 구현체는 반드시 <c>AnimationPose.RestScope</c>로 감싸
/// 재생 포즈가 아닌 rest 포즈로 기록해야 한다.
/// </summary>
public interface IExporter
{
    /// <summary>메뉴/다이얼로그에 보일 형식 이름.</summary>
    string Name { get; }
    /// <summary>소문자 확장자(".glb", ".gltf", ".fbx").</summary>
    IReadOnlyList<string> Extensions { get; }
    /// <summary>
    /// <paramref name="nodes"/>(선택 내보내기면 선택 노드, 아니면 전체)를 <paramref name="path"/>에 기록한다.
    /// </summary>
    /// <param name="doc">머티리얼·애니메이션 등 문서 전역 데이터를 읽기 위한 문서.</param>
    /// <param name="nodes">내보낼 노드(하위 트리 포함).</param>
    /// <param name="path">출력 파일 경로.</param>
    /// <param name="preset">엔진별 옵션.</param>
    /// <returns>성공 여부와 통계.</returns>
    ExportResult Export(Document doc, IReadOnlyList<SceneNode> nodes, string path, ExportPreset preset);
}

/// <summary>파일 가져오기 플러그인 인터페이스. 문서를 직접 바꾸지 않고 노드만 만들어 돌려준다.</summary>
public interface IImporter
{
    /// <summary>메뉴/다이얼로그에 보일 형식 이름.</summary>
    string Name { get; }
    /// <summary>지원하는 소문자 확장자 목록.</summary>
    IReadOnlyList<string> Extensions { get; }
    /// <summary>파일을 읽어 노드들을 만든다(문서에 추가하지는 않음). 호출자가 명령으로 추가한다.</summary>
    ImportResult Import(string path, Document doc, ImportOptions options);
}

/// <summary>
/// 가져오기 옵션. <c>WeldThreshold</c> = 같은 정점으로 합칠 위치 거리(m),
/// <c>HardAngleDegrees</c> = 이 각도 이상 꺾인 엣지를 하드 엣지로 추론하는 기준,
/// <c>MergeTriangleQuads</c> = 같은 평면의 삼각형 쌍을 쿼드로 합칠지 여부(<see cref="TriangleSoupToPolyMesh"/>가 사용).
/// </summary>
public sealed record ImportOptions(float WeldThreshold = 1e-5f, float HardAngleDegrees = 60f, bool MergeTriangleQuads = true)
{
    /// <summary>기본 옵션(1e-5 m 용접, 60° 하드 엣지, 쿼드 병합 켜짐).</summary>
    public static readonly ImportOptions Default = new();
}
