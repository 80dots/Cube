using System.Numerics;

namespace Cube.Core.Scene;

/// <summary>
/// Maya 채널 박스와 동일한 TRS 표현. 회전은 도(degree) 단위 오일러, 회전 순서 XYZ(Maya 기본).
/// 행렬은 System.Numerics 행벡터 규약: M = T(-Pivot) · S · Rx · Ry · Rz · T(Pivot + Translation).
/// Pivot은 오브젝트 공간의 회전/스케일 피벗(Maya rotatePivot = scalePivot). 피벗의 월드 위치는 부모 공간에서 Pivot + Translation.
/// </summary>
/// <remarks>값 타입이다. 노드의 Local을 고칠 때는 복사본을 고쳐 통째로 다시 넣는다. 기존 노드의 Local을 행렬에서 다시 만들 때는 반드시 피벗을 넘기는 <see cref="FromMatrix(Matrix4x4, Vector3)"/>를 쓴다.</remarks>
public struct Transform3 : IEquatable<Transform3>
{
    /// <summary>이동(부모 공간, m). 피벗이 있으면 피벗의 부모 공간 위치는 Pivot + Translation.</summary>
    public Vector3 Translation;
    /// <summary>XYZ 오일러 회전(도).</summary>
    public Vector3 RotationDegrees;
    /// <summary>축별 스케일.</summary>
    public Vector3 Scale;
    /// <summary>오브젝트 공간 회전/스케일 피벗.</summary>
    public Vector3 Pivot;

    /// <summary>항등 트랜스폼(이동·회전 0, 스케일 1, 피벗 0).</summary>
    public static readonly Transform3 Identity = new() { Translation = Vector3.Zero, RotationDegrees = Vector3.Zero, Scale = Vector3.One, Pivot = Vector3.Zero };

    /// <summary>피벗 0인 TRS.</summary>
    public Transform3(Vector3 translation, Vector3 rotationDegrees, Vector3 scale)
    {
        Translation = translation; RotationDegrees = rotationDegrees; Scale = scale; Pivot = Vector3.Zero;
    }

    /// <summary>피벗을 포함한 TRS.</summary>
    public Transform3(Vector3 translation, Vector3 rotationDegrees, Vector3 scale, Vector3 pivot)
    {
        Translation = translation; RotationDegrees = rotationDegrees; Scale = scale; Pivot = pivot;
    }

    /// <summary>스케일·회전 부분만(피벗/이동 없음).</summary>
    /// <remarks>S · Rx · Ry · Rz 순서(행벡터이므로 X 회전이 먼저 적용된다).</remarks>
    public readonly Matrix4x4 ScaleRotationMatrix()
    {
        const float d2r = MathF.PI / 180f;
        return Matrix4x4.CreateScale(Scale)
             * Matrix4x4.CreateRotationX(RotationDegrees.X * d2r)
             * Matrix4x4.CreateRotationY(RotationDegrees.Y * d2r)
             * Matrix4x4.CreateRotationZ(RotationDegrees.Z * d2r);
    }

    /// <summary>오일러 회전과 같은 회전을 쿼터니언으로(XYZ 순서).</summary>
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

    /// <summary>로컬 행렬. 피벗이 0이면 S·R·T(Translation), 아니면 T(-Pivot)·S·R·T(Pivot + Translation).</summary>
    public readonly Matrix4x4 ToMatrix()
    {
        if (Pivot == Vector3.Zero) return ScaleRotationMatrix() * Matrix4x4.CreateTranslation(Translation);
        return Matrix4x4.CreateTranslation(-Pivot) * ScaleRotationMatrix() * Matrix4x4.CreateTranslation(Pivot + Translation);
    }

    /// <summary>행렬에서 TRS를 복원한다(스케일이 양수이고 전단이 없다고 가정). 피벗은 0.</summary>
    public static Transform3 FromMatrix(Matrix4x4 m) => FromMatrix(m, Vector3.Zero);

    /// <summary>주어진 피벗을 유지하며 행렬에서 TRS를 복원한다: M = T(-p)·S·R·T(p+t) → t = translation(inv(T(-p)·S·R)·M) - p.</summary>
    /// <remarks>분해에 실패하면(특이 행렬) 회전 0·스케일 1에 행렬 이동 성분만 Translation으로 쓴다.</remarks>
    public static Transform3 FromMatrix(Matrix4x4 m, Vector3 pivot)
    {
        if (!Matrix4x4.Decompose(m, out var s, out var q, out _))
            return new Transform3(m.Translation - pivot, Vector3.Zero, Vector3.One, pivot) { Translation = m.Translation };
        // S/R은 분해값, 이동은 피벗을 고려해 따로 푼다.
        var result = new Transform3(Vector3.Zero, QuaternionToEulerXYZDegrees(q), s, pivot);
        result.Translation = SolveTranslation(m, result);
        return result;
    }

    /// <summary>S/R/Pivot이 정해진 상태에서 목표 행렬을 만드는 Translation.</summary>
    /// <remarks>head = T(-P)·S·R 이면 target = head·T(P+t) → T(P+t) = inv(head)·target, 그 이동 성분에서 P를 뺀다.</remarks>
    public static Vector3 SolveTranslation(Matrix4x4 target, Transform3 sr)
    {
        var head = Matrix4x4.CreateTranslation(-sr.Pivot) * sr.ScaleRotationMatrix();
        Matrix4x4.Invert(head, out var inv);
        return (inv * target).Translation - sr.Pivot;
    }

    /// <summary>피벗만 바꾸고 월드(행렬)는 그대로 유지한다.</summary>
    /// <remarks>Center Pivot/Edit Pivot이 쓴다. 현재 행렬을 기억한 뒤 새 피벗으로 Translation을 다시 푼다.</remarks>
    public readonly Transform3 WithPivotKeepingMatrix(Vector3 newPivot)
    {
        var m = ToMatrix();
        var t = this; t.Pivot = newPivot;
        t.Translation = SolveTranslation(m, t);
        return t;
    }

    /// <summary>쿼터니언을 XYZ 순서 오일러(도)로 변환한다.</summary>
    /// <remarks>ry ∈ [-90°, 90°]. cos(ry) ≈ 0인 짐벌락에서는 rz = 0으로 두고 rx만 구한다.</remarks>
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
            // 일반 경우: cos(b)로 나눈 비율을 atan2로.
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

    /// <summary>네 성분이 정확히 같은지(부동소수 오차 허용 없음).</summary>
    public readonly bool Equals(Transform3 o) => Translation == o.Translation && RotationDegrees == o.RotationDegrees && Scale == o.Scale && Pivot == o.Pivot;
    /// <summary>object 비교.</summary>
    public override readonly bool Equals(object? obj) => obj is Transform3 t && Equals(t);
    /// <summary>네 성분 해시.</summary>
    public override readonly int GetHashCode() => HashCode.Combine(Translation, RotationDegrees, Scale, Pivot);
    /// <summary>값 동등 비교.</summary>
    public static bool operator ==(Transform3 a, Transform3 b) => a.Equals(b);
    /// <summary>값 비동등 비교.</summary>
    public static bool operator !=(Transform3 a, Transform3 b) => !a.Equals(b);
    /// <summary>디버그 표시(피벗이 0이면 생략).</summary>
    public override readonly string ToString() => $"T{Translation} R{RotationDegrees} S{Scale}" + (Pivot == Vector3.Zero ? "" : $" P{Pivot}");
}
