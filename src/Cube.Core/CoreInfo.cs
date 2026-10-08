namespace Cube.Core;

/// <summary>
/// Godot 의존이 없는 순수 C# 코어 어셈블리의 식별 정보.
/// Cube.Core는 메시·씬·UV·리그·입출력 같은 순수 로직을 담고 System.Numerics만 사용하므로
/// Godot 없이 <c>dotnet test</c>로 반복 검증할 수 있다. 이 클래스는 어셈블리가 제대로 참조되는지
/// 확인하거나 로그에 이름을 찍을 때 쓰는 상수만 가진다.
/// </summary>
public static class CoreInfo
{
    /// <summary>코어 어셈블리 이름("Cube.Core"). 어셈블리 식별·로그 표시용 상수.</summary>
    public const string Name = "Cube.Core";
}
