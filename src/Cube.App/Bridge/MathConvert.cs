using GVec2 = Godot.Vector2;
using GVec3 = Godot.Vector3;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;
using NMat = System.Numerics.Matrix4x4;

namespace Cube.App.Bridge;

/// <summary>System.Numerics(코어) ↔ Godot 타입 변환. 코어는 Godot을 모르므로 경계는 이 파일 하나다.</summary>
public static class MathConvert
{
    public static GVec3 ToGodot(this NVec3 v) => new(v.X, v.Y, v.Z);
    public static NVec3 ToNumerics(this GVec3 v) => new(v.X, v.Y, v.Z);
    public static GVec2 ToGodot(this NVec2 v) => new(v.X, v.Y);
    public static NVec2 ToNumerics(this GVec2 v) => new(v.X, v.Y);

    /// <summary>행벡터 규약 행렬(행 0..2 = 축, 행 3 = 이동)을 Godot Transform3D(열 = 축)로.</summary>
    public static Godot.Transform3D ToGodot(this NMat m)
    {
        var basis = new Godot.Basis(
            new GVec3(m.M11, m.M12, m.M13),
            new GVec3(m.M21, m.M22, m.M23),
            new GVec3(m.M31, m.M32, m.M33));
        return new Godot.Transform3D(basis, new GVec3(m.M41, m.M42, m.M43));
    }

    public static NMat ToNumerics(this Godot.Transform3D t)
    {
        var b = t.Basis;
        return new NMat(
            b.X.X, b.X.Y, b.X.Z, 0,
            b.Y.X, b.Y.Y, b.Y.Z, 0,
            b.Z.X, b.Z.Y, b.Z.Z, 0,
            t.Origin.X, t.Origin.Y, t.Origin.Z, 1);
    }

    public static Godot.Transform3D ToGodot(this Core.Scene.Transform3 t) => t.ToMatrix().ToGodot();

    public static Godot.Color Rgb(uint hex, float alpha = 1f)
        => new(((hex >> 16) & 0xFF) / 255f, ((hex >> 8) & 0xFF) / 255f, (hex & 0xFF) / 255f, alpha);
}
