using System.Numerics;

namespace Cube.Core.Scene;

public enum LightType { Directional, Point, Spot }

/// <summary>라이트 shape. 방향은 노드의 -Z(Godot/glTF 규약). 색은 sRGB 0..1.</summary>
public sealed class LightShape : Shape
{
    public LightType Type = LightType.Point;
    public Vector3 Color = Vector3.One;
    public float Intensity = 1f;
    /// <summary>Point/Spot 감쇠 범위(m).</summary>
    public float Range = 10f;
    /// <summary>Spot 원뿔 각도(도, 반각이 아닌 전체 각).</summary>
    public float SpotAngle = 45f;

    public LightShape Clone() => new() { Type = Type, Color = Color, Intensity = Intensity, Range = Range, SpotAngle = SpotAngle };
}
