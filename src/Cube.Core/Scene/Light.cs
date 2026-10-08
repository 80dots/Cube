using System.Numerics;

namespace Cube.Core.Scene;

/// <summary>라이트 종류. Directional = 방향광(태양, 위치 무관), Point = 점광원(전방향, Range 감쇠), Spot = 스포트(원뿔, SpotAngle).</summary>
public enum LightType { Directional, Point, Spot }

/// <summary>라이트 shape. 방향은 노드의 -Z(Godot/glTF 규약). 색은 sRGB 0..1.</summary>
/// <remarks>뷰포트는 <c>LightView</c>가 실제 Godot 라이트로 미러링하고, glTF 내보내기에서는 Light3D로, .cube에도 저장된다.</remarks>
public sealed class LightShape : Shape
{
    /// <summary>라이트 종류(기본 Point).</summary>
    public LightType Type = LightType.Point;
    /// <summary>색(sRGB 0..1, Godot 쪽으로 넘길 때 변환).</summary>
    public Vector3 Color = Vector3.One;
    /// <summary>세기(배율, 기본 1).</summary>
    public float Intensity = 1f;
    /// <summary>Point/Spot 감쇠 범위(m).</summary>
    public float Range = 10f;
    /// <summary>Spot 원뿔 각도(도, 반각이 아닌 전체 각).</summary>
    public float SpotAngle = 45f;

    /// <summary>모든 필드를 복사한 새 인스턴스(SetLightCommand 스냅샷용).</summary>
    public LightShape Clone() => new() { Type = Type, Color = Color, Intensity = Intensity, Range = Range, SpotAngle = SpotAngle };
}
