using GVec2 = Godot.Vector2;
using GVec3 = Godot.Vector3;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;
using NMat = System.Numerics.Matrix4x4;

namespace Cube.App.Bridge;

/// <summary>System.Numerics(코어) ↔ Godot 타입 변환. 코어는 Godot을 모르므로 경계는 이 파일 하나다.</summary>
/// <remarks>
/// 확장 메서드 모음이라 <c>v.ToGodot()</c>/<c>m.ToNumerics()</c>처럼 쓴다. 성분 순서·단위(m)·좌표계(오른손, Y-up)는 양쪽이 같아
/// 벡터는 성분을 그대로 복사하고, 행렬만 규약 차이(행벡터 ↔ 열 = 축)를 맞춘다.
/// </remarks>
public static class MathConvert
{
    /// <summary>코어 3D 벡터를 Godot Vector3로 바꾼다(성분 그대로 복사).</summary>
    public static GVec3 ToGodot(this NVec3 v) => new(v.X, v.Y, v.Z);
    /// <summary>Godot Vector3를 코어(System.Numerics) 3D 벡터로 바꾼다.</summary>
    public static NVec3 ToNumerics(this GVec3 v) => new(v.X, v.Y, v.Z);
    /// <summary>코어 2D 벡터를 Godot Vector2로 바꾼다. UV의 v 뒤집기는 하지 않는다(그건 <see cref="GodotMeshBridge"/>의 몫).</summary>
    public static GVec2 ToGodot(this NVec2 v) => new(v.X, v.Y);
    /// <summary>Godot Vector2를 코어 2D 벡터로 바꾼다.</summary>
    public static NVec2 ToNumerics(this GVec2 v) => new(v.X, v.Y);

    /// <summary>행벡터 규약 행렬(행 0..2 = 축, 행 3 = 이동)을 Godot Transform3D(열 = 축)로.</summary>
    /// <remarks>
    /// System.Numerics 행렬은 행벡터 규약(v' = v·M)이라 M11..M33의 각 행이 변환된 X/Y/Z 축이고 M41..M43이 이동이다.
    /// Godot Basis 생성자는 열(축) 벡터 세 개를 받으므로 각 행을 그대로 축으로 넘기면 같은 변환이 된다(투영 성분 M14/M24/M34는 무시).
    /// </remarks>
    public static Godot.Transform3D ToGodot(this NMat m)
    {
        // 행 0..2 → Basis의 X/Y/Z 축(열)
        var basis = new Godot.Basis(
            new GVec3(m.M11, m.M12, m.M13),
            new GVec3(m.M21, m.M22, m.M23),
            new GVec3(m.M31, m.M32, m.M33));
        // 행 3 = 이동(Origin)
        return new Godot.Transform3D(basis, new GVec3(m.M41, m.M42, m.M43));
    }

    /// <summary><c>ToGodot(Matrix4x4)</c>의 역변환: Godot Transform3D(축 = 열)를 행벡터 규약 Matrix4x4로 바꾼다.</summary>
    /// <remarks>Basis의 각 축을 한 행에, Origin을 4행에 넣고 마지막 열은 (0,0,0,1)로 채운 아핀 행렬이다.</remarks>
    public static NMat ToNumerics(this Godot.Transform3D t)
    {
        var b = t.Basis;
        return new NMat(
            b.X.X, b.X.Y, b.X.Z, 0,
            b.Y.X, b.Y.Y, b.Y.Z, 0,
            b.Z.X, b.Z.Y, b.Z.Z, 0,
            t.Origin.X, t.Origin.Y, t.Origin.Z, 1);
    }

    /// <summary>코어 <c>Transform3</c>(Maya식 TRS + 피벗)를 행렬로 만든 뒤 Godot Transform3D로 바꾼다.</summary>
    public static Godot.Transform3D ToGodot(this Core.Scene.Transform3 t) => t.ToMatrix().ToGodot();

    /// <summary>0xRRGGBB 16진 정수를 Godot Color(sRGB 0..1)로 바꾼다. 테마·뷰포트 색 상수를 짧게 쓰기 위한 도우미.</summary>
    /// <param name="hex">하위 24비트에 R(16..23), G(8..15), B(0..7)가 들어 있는 색.</param>
    /// <param name="alpha">알파(0..1).</param>
    /// <returns>sRGB 공간 색. 셰이더 정점 색으로 넘길 때는 호출자가 <c>SrgbToLinear()</c>로 바꿔야 한다.</returns>
    public static Godot.Color Rgb(uint hex, float alpha = 1f)
        => new(((hex >> 16) & 0xFF) / 255f, ((hex >> 8) & 0xFF) / 255f, (hex & 0xFF) / 255f, alpha);
}
