using System.Numerics;

namespace Cube.Core.Scene;

/// <summary>
/// Maya 채널 박스와 동일한 TRS 표현. 회전은 도(degree) 단위 오일러, 회전 순서 XYZ(Maya 기본).
/// 행렬은 System.Numerics 행벡터 규약: M = S · Rx · Ry · Rz · T.
/// </summary>
public struct Transform3 : IEquatable<Transform3>
{
    public Vector3 Translation;
    public Vector3 RotationDegrees;
    public Vector3 Scale;

    public static readonly Transform3 Identity = new() { Translation = Vector3.Zero, RotationDegrees = Vector3.Zero, Scale = Vector3.One };

    public Transform3(Vector3 translation, Vector3 rotationDegrees, Vector3 scale)
    {
        Translation = translation; RotationDegrees = rotationDegrees; Scale = scale;
    }

    public readonly Quaternion Rotation
    {
        get
        {
            const float d2r = MathF.PI / 180f;
            var qx = Quaternion.CreateFromAxisAngle(Vector3.UnitX, RotationDegrees.X * d2r);
            var qy = Quaternion.CreateFromAxisAngle(Vector3.UnitY, RotationDegrees.Y * d2r);
            var qz = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, RotationDegrees.Z * d2r);
            // XYZ 순서: X 먼저 적용 → q = qz * qy * qx (System.Numerics의 곱은 q1 then q2 → Concatenate(qx, qy) 등)
            return Quaternion.Concatenate(Quaternion.Concatenate(qx, qy), qz);
        }
    }

    public readonly Matrix4x4 ToMatrix()
    {
        const float d2r = MathF.PI / 180f;
        return Matrix4x4.CreateScale(Scale)
             * Matrix4x4.CreateRotationX(RotationDegrees.X * d2r)
             * Matrix4x4.CreateRotationY(RotationDegrees.Y * d2r)
             * Matrix4x4.CreateRotationZ(RotationDegrees.Z * d2r)
             * Matrix4x4.CreateTranslation(Translation);
    }

    /// <summary>행렬에서 TRS를 복원한다(스케일이 양수이고 전단이 없다고 가정).</summary>
    public static Transform3 FromMatrix(Matrix4x4 m)
    {
        if (!Matrix4x4.Decompose(m, out var s, out var q, out var t))
            return new Transform3(m.Translation, Vector3.Zero, Vector3.One);
        return new Transform3(t, QuaternionToEulerXYZDegrees(q), s);
    }

    /// <summary>쿼터니언을 XYZ 순서 오일러(도)로 변환한다.</summary>
    public static Vector3 QuaternionToEulerXYZDegrees(Quaternion q)
    {
        // 회전 행렬 R = Rx·Ry·Rz (행벡터) 의 성분으로부터 복원
        var m = Matrix4x4.CreateFromQuaternion(q);
        const float r2d = 180f / MathF.PI;
        float sy = m.M13; // = -sin(y)... 행벡터 규약에서 R(X)R(Y)R(Z)의 M13 = -sin(ry)
        // 행벡터 규약에서 CreateRotationX(a)*CreateRotationY(b)*CreateRotationZ(c):
        // M13 = -sin(b), M23 = sin(a)cos(b), M33 = cos(a)cos(b), M11 = cos(b)cos(c), M12 = cos(b)sin(c)
        float ry = MathF.Asin(Math.Clamp(-sy, -1f, 1f));
        float rx, rz;
        if (MathF.Abs(MathF.Cos(ry)) > 1e-6f)
        {
            rx = MathF.Atan2(m.M23, m.M33);
            rz = MathF.Atan2(m.M12, m.M11);
        }
        else
        {
            // 짐벌락: rz=0으로 두고 rx 계산
            rx = MathF.Atan2(-m.M32, m.M22);
            rz = 0;
        }
        return new Vector3(rx * r2d, ry * r2d, rz * r2d);
    }

    public readonly bool Equals(Transform3 o) => Translation == o.Translation && RotationDegrees == o.RotationDegrees && Scale == o.Scale;
    public override readonly bool Equals(object? obj) => obj is Transform3 t && Equals(t);
    public override readonly int GetHashCode() => HashCode.Combine(Translation, RotationDegrees, Scale);
    public static bool operator ==(Transform3 a, Transform3 b) => a.Equals(b);
    public static bool operator !=(Transform3 a, Transform3 b) => !a.Equals(b);
    public override readonly string ToString() => $"T{Translation} R{RotationDegrees} S{Scale}";
}
