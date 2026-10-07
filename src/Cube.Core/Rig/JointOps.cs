using System.Numerics;
using Cube.Core.Scene;

namespace Cube.Core.Rig;

public enum Axis { X, Y, Z }

/// <summary>Orient Joint 옵션(Maya Orient Joint Options).</summary>
public sealed class OrientOptions
{
    public Axis Primary = Axis.X;
    public Axis Secondary = Axis.Y;
    /// <summary>보조축을 맞출 월드 방향.</summary>
    public Axis SecondaryWorld = Axis.Y;
    public bool SecondaryWorldNegative;
    public bool OrientChildren = true;
}

/// <summary>조인트 방향 정렬, 미러, 삽입에 쓰는 순수 계산.</summary>
public static class JointOps
{
    private static Vector3 AxisVec(Axis a) => a switch { Axis.X => Vector3.UnitX, Axis.Y => Vector3.UnitY, _ => Vector3.UnitZ };

    /// <summary>
    /// 조인트들의 로컬 회전을 "주축이 첫 자식(자식 평균)을 향하고 보조축이 월드 방향에 가깝게" 정한다.
    /// 자식 조인트가 없는 끝 조인트는 부모의 방향을 따른다(Maya와 동일). 자식들의 월드 트랜스폼은 유지된다.
    /// 반환값은 (노드, 이전 로컬, 새 로컬) 목록.
    /// </summary>
    public static List<(SceneNode node, Transform3 before, Transform3 after)> Orient(IEnumerable<SceneNode> joints, OrientOptions opt)
    {
        var result = new List<(SceneNode, Transform3, Transform3)>();
        var set = new List<SceneNode>();
        foreach (var j in joints)
        {
            if (!j.IsJoint) continue;
            if (opt.OrientChildren) { foreach (var d in new[] { j }.Concat(j.Descendants())) if (d.IsJoint && !set.Contains(d)) set.Add(d); }
            else if (!set.Contains(j)) set.Add(j);
        }
        // 부모부터 처리(깊이 순)
        set.Sort((a, b) => Depth(a).CompareTo(Depth(b)));
        foreach (var j in set)
        {
            var worldBefore = j.WorldMatrix;
            var childWorlds = j.Children.Select(c => (c, c.WorldMatrix)).ToList();
            var p = worldBefore.Translation;
            var childJoints = j.Children.Where(c => c.IsJoint).ToList();
            Quaternion worldRot;
            if (childJoints.Count > 0)
            {
                var target = Vector3.Zero; foreach (var c in childJoints) target += c.WorldMatrix.Translation; target /= childJoints.Count;
                var aim = target - p;
                if (aim.LengthSquared() < 1e-12f) worldRot = ParentWorldRotation(j);
                else worldRot = BuildRotation(Vector3.Normalize(aim), opt);
            }
            else worldRot = ParentWorldRotation(j); // 끝 조인트: 부모 방향
            // 로컬 회전 = 월드 회전 · inv(부모 월드 회전)
            var parentRot = ParentWorldRotation(j);
            var localRot = Quaternion.Concatenate(worldRot, Quaternion.Inverse(parentRot));
            var after = j.Local; after.RotationDegrees = Transform3.QuaternionToEulerXYZDegrees(Quaternion.Normalize(localRot));
            var before = j.Local;
            j.Local = after;
            result.Add((j, before, after));
            // 자식 월드 유지
            Matrix4x4.Invert(j.WorldMatrix, out var inv);
            foreach (var (c, w) in childWorlds)
            {
                var cb = c.Local; var ca = Transform3.FromMatrix(w * inv);
                if (set.Contains(c)) { c.Local = ca; continue; } // 곧 다시 정렬됨(위치만 반영)
                c.Local = ca; result.Add((c, cb, ca));
            }
        }
        return result;
    }

    private static int Depth(SceneNode n) { int d = 0; for (var p = n.Parent; p != null && !p.IsRoot; p = p.Parent) d++; return d; }

    private static Quaternion ParentWorldRotation(SceneNode j)
    {
        if (j.Parent == null || j.Parent.IsRoot) return Quaternion.Identity;
        var m = j.Parent.WorldMatrix;
        return Matrix4x4.Decompose(m, out _, out var q, out _) ? q : Quaternion.Identity;
    }

    /// <summary>주축 = aim, 보조축 = 월드 방향을 aim에 직교화, 세 번째 = 외적. 행벡터 회전 행렬 → 쿼터니언.</summary>
    public static Quaternion BuildRotation(Vector3 aim, OrientOptions opt)
    {
        var up = AxisVec(opt.SecondaryWorld) * (opt.SecondaryWorldNegative ? -1f : 1f);
        var sec = up - aim * Vector3.Dot(up, aim);
        if (sec.LengthSquared() < 1e-8f) { var alt = MathF.Abs(aim.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX; sec = alt - aim * Vector3.Dot(alt, aim); }
        sec = Vector3.Normalize(sec);
        var axes = new Vector3[3];
        int pi = (int)opt.Primary, si = (int)opt.Secondary;
        if (si == pi) si = (pi + 1) % 3;
        int ti = 3 - pi - si;
        axes[pi] = aim; axes[si] = sec;
        // 오른손 좌표계 유지: X×Y=Z, Y×Z=X, Z×X=Y
        axes[ti] = (pi, si) switch
        {
            (0, 1) or (1, 2) or (2, 0) => Vector3.Cross(axes[pi], axes[si]),
            _ => Vector3.Cross(axes[si], axes[pi]),
        };
        var m = new Matrix4x4(axes[0].X, axes[0].Y, axes[0].Z, 0, axes[1].X, axes[1].Y, axes[1].Z, 0, axes[2].X, axes[2].Y, axes[2].Z, 0, 0, 0, 0, 1);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m));
    }

    /// <summary>
    /// 조인트 서브트리를 월드 평면(axis에 수직, planePoint를 지남)으로 미러한 새 서브트리를 만든다(아직 문서에 넣지 않음).
    /// 이름은 search → replace 치환(없으면 접미 "_mirror").
    /// </summary>
    public static SceneNode Mirror(SceneNode root, Axis axis, Vector3 planePoint, string search, string replace, Func<string, string> uniqueName)
    {
        var n = AxisVec(axis);
        var reflect = Matrix4x4.Identity - Matrix4x4.Multiply(Outer(n, n), 2f);
        reflect.M41 = 2f * Vector3.Dot(planePoint, n) * n.X; reflect.M42 = 2f * Vector3.Dot(planePoint, n) * n.Y; reflect.M43 = 2f * Vector3.Dot(planePoint, n) * n.Z;
        SceneNode Build(SceneNode src, Matrix4x4 parentWorldNew)
        {
            var w = src.WorldMatrix * reflect; // 반사된 월드(행벡터: 왼쪽부터 적용)
            // 반사는 좌우를 뒤집으므로 회전 부분이 반사 행렬이 됨 → 주축 방향만 유지하도록 한 축을 뒤집어 정상 회전으로 만든다
            var t = w.Translation;
            var rx = new Vector3(w.M11, w.M12, w.M13); var ry = new Vector3(w.M21, w.M22, w.M23); var rz = new Vector3(w.M31, w.M32, w.M33);
            if (Vector3.Dot(Vector3.Cross(rx, ry), rz) < 0) rz = -rz; // 오른손계 복원(Maya "behavior" 미러와 유사)
            var rot = new Matrix4x4(rx.X, rx.Y, rx.Z, 0, ry.X, ry.Y, ry.Z, 0, rz.X, rz.Y, rz.Z, 0, t.X, t.Y, t.Z, 1);
            Matrix4x4.Invert(parentWorldNew, out var inv);
            var local = Transform3.FromMatrix(rot * inv);
            local.Scale = src.Local.Scale;
            string name = search.Length > 0 && src.Name.Contains(search) ? src.Name.Replace(search, replace) : src.Name + "_mirror";
            var node = new SceneNode { Name = uniqueName(name), Shape = src.Joint != null ? new JointShape { Radius = src.Joint.Radius } : null, Local = local };
            foreach (var c in src.Children) if (c.IsJoint) node.AttachChild(Build(c, rot));
            return node;
        }
        var parentWorld = root.Parent != null && !root.Parent.IsRoot ? root.Parent.WorldMatrix : Matrix4x4.Identity;
        return Build(root, parentWorld);
    }

    private static Matrix4x4 Outer(Vector3 a, Vector3 b) => new(a.X * b.X, a.X * b.Y, a.X * b.Z, 0, a.Y * b.X, a.Y * b.Y, a.Y * b.Z, 0, a.Z * b.X, a.Z * b.Y, a.Z * b.Z, 0, 0, 0, 0, 1);
}
