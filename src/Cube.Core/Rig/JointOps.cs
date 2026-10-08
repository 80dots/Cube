using System.Numerics;
using Cube.Core.Scene;

namespace Cube.Core.Rig;

/// <summary>
/// 로컬 좌표축 선택(X/Y/Z). 정수 값 0/1/2가 축 인덱스로도 쓰인다(<see cref="JointOps.BuildRotation"/>에서 행 번호).
/// </summary>
public enum Axis { X, Y, Z }

/// <summary>Orient Joint 옵션(Maya Orient Joint Options).</summary>
/// <remarks>Skeleton → Orient Joint 옵션 창 값이 이 객체로 넘어온다.</remarks>
public sealed class OrientOptions
{
    /// <summary>자식 조인트를 향할 주축(Maya 기본 X).</summary>
    public Axis Primary = Axis.X;
    /// <summary>보조축(주축과 같으면 다음 축으로 자동 변경). 이 축이 <see cref="SecondaryWorld"/> 방향에 가깝게 놓인다.</summary>
    public Axis Secondary = Axis.Y;
    /// <summary>보조축을 맞출 월드 방향.</summary>
    public Axis SecondaryWorld = Axis.Y;
    /// <summary>true면 보조축 월드 방향을 반대(−축)로 쓴다.</summary>
    public bool SecondaryWorldNegative;
    /// <summary>true면 선택 조인트의 하위 조인트 전체도 함께 정렬한다(Maya "Orient children of selected joints").</summary>
    public bool OrientChildren = true;
}

/// <summary>조인트 방향 정렬, 미러, 삽입에 쓰는 순수 계산.</summary>
public static class JointOps
{
    /// <summary>축 열거값 → 월드 단위 벡터.</summary>
    private static Vector3 AxisVec(Axis a) => a switch { Axis.X => Vector3.UnitX, Axis.Y => Vector3.UnitY, _ => Vector3.UnitZ };

    /// <summary>
    /// 조인트들의 로컬 회전을 "주축이 첫 자식(자식 평균)을 향하고 보조축이 월드 방향에 가깝게" 정한다.
    /// 자식 조인트가 없는 끝 조인트는 부모의 방향을 따른다(Maya와 동일). 자식들의 월드 트랜스폼은 유지된다.
    /// 반환값은 (노드, 이전 로컬, 새 로컬) 목록.
    /// </summary>
    /// <param name="joints">선택된 노드들(조인트가 아닌 노드는 무시).</param>
    /// <param name="opt">주축/보조축 옵션.</param>
    /// <remarks>노드의 Local을 직접 바꾸므로 호출자는 반환 목록으로 Undo 명령을 만든다. 로컬 재구성은 피벗을 유지(FromMatrix(m, pivot))한다.</remarks>
    public static List<(SceneNode node, Transform3 before, Transform3 after)> Orient(IEnumerable<SceneNode> joints, OrientOptions opt)
    {
        // 1단계: 처리할 조인트 집합 수집(옵션에 따라 하위 조인트 포함, 중복 제거).
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
            // 바꾸기 전에 이 조인트와 자식들의 월드 행렬을 기억해 둔다(자식 월드 유지용).
            var worldBefore = j.WorldMatrix;
            var childWorlds = j.Children.Select(c => (c, c.WorldMatrix)).ToList();
            var p = worldBefore.Translation;
            var childJoints = j.Children.Where(c => c.IsJoint).ToList();
            Quaternion worldRot;
            // 자식 조인트가 있으면 그 월드 위치 평균을 향하도록 주축을 겨눈다(겹쳐 있으면 부모 방향).
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
                var cb = c.Local; var ca = Transform3.FromMatrix(w * inv, c.Local.Pivot);
                if (set.Contains(c)) { c.Local = ca; continue; } // 곧 다시 정렬됨(위치만 반영)
                c.Local = ca; result.Add((c, cb, ca));
            }
        }
        return result;
    }

    /// <summary>루트(문서 Root 제외)로부터의 깊이. 부모 먼저 처리하도록 정렬하는 데 쓴다.</summary>
    private static int Depth(SceneNode n) { int d = 0; for (var p = n.Parent; p != null && !p.IsRoot; p = p.Parent) d++; return d; }

    /// <summary>부모 월드 행렬의 회전 부분(부모가 없거나 분해 실패면 항등). 끝 조인트의 방향 및 로컬 회전 계산 기준.</summary>
    private static Quaternion ParentWorldRotation(SceneNode j)
    {
        if (j.Parent == null || j.Parent.IsRoot) return Quaternion.Identity;
        var m = j.Parent.WorldMatrix;
        return Matrix4x4.Decompose(m, out _, out var q, out _) ? q : Quaternion.Identity;
    }

    /// <summary>주축 = aim, 보조축 = 월드 방향을 aim에 직교화, 세 번째 = 외적. 행벡터 회전 행렬 → 쿼터니언.</summary>
    /// <param name="aim">주축이 향할 월드 단위 벡터.</param>
    /// <param name="opt">축 배정 옵션.</param>
    /// <returns>월드 회전 쿼터니언(행 i = 로컬 축 i의 월드 방향).</returns>
    public static Quaternion BuildRotation(Vector3 aim, OrientOptions opt)
    {
        // 보조축 = 월드 up 방향에서 aim 성분을 뺀 직교 성분(그람-슈미트). aim과 평행하면 다른 기준축으로 대체한다.
        var up = AxisVec(opt.SecondaryWorld) * (opt.SecondaryWorldNegative ? -1f : 1f);
        var sec = up - aim * Vector3.Dot(up, aim);
        if (sec.LengthSquared() < 1e-8f) { var alt = MathF.Abs(aim.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX; sec = alt - aim * Vector3.Dot(alt, aim); }
        sec = Vector3.Normalize(sec);
        // 주/보조/세 번째 축 인덱스를 정한다(보조가 주축과 같으면 다음 축).
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
        // 행 0/1/2 = 로컬 X/Y/Z 축의 월드 방향인 회전 행렬(행벡터 규약)을 쿼터니언으로.
        var m = new Matrix4x4(axes[0].X, axes[0].Y, axes[0].Z, 0, axes[1].X, axes[1].Y, axes[1].Z, 0, axes[2].X, axes[2].Y, axes[2].Z, 0, 0, 0, 0, 1);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m));
    }

    /// <summary>
    /// 조인트 서브트리를 월드 평면(axis에 수직, planePoint를 지남)으로 미러한 새 서브트리를 만든다(아직 문서에 넣지 않음).
    /// 이름은 search → replace 치환(없으면 접미 "_mirror").
    /// </summary>
    /// <param name="root">미러할 서브트리 루트 조인트.</param>
    /// <param name="axis">반사 평면의 법선 축(X면 YZ 평면 기준 좌우 반전).</param>
    /// <param name="planePoint">반사 평면이 지나는 월드 점(월드 원점/부모/선택 조인트 위치).</param>
    /// <param name="search">이름에서 찾을 문자열(예: "_L").</param>
    /// <param name="replace">바꿀 문자열(예: "_R").</param>
    /// <param name="uniqueName">문서에서 유일한 이름을 만드는 함수.</param>
    /// <returns>새 서브트리 루트(호출자가 원래 부모 아래에 명령으로 추가한다).</returns>
    public static SceneNode Mirror(SceneNode root, Axis axis, Vector3 planePoint, string search, string replace, Func<string, string> uniqueName)
    {
        // 반사 행렬 = I − 2·n·nᵀ(평면이 원점을 지날 때) + 이동 2·(planePoint·n)·n(평면 오프셋 보정).
        var n = AxisVec(axis);
        var reflect = Matrix4x4.Identity - Matrix4x4.Multiply(Outer(n, n), 2f);
        reflect.M41 = 2f * Vector3.Dot(planePoint, n) * n.X; reflect.M42 = 2f * Vector3.Dot(planePoint, n) * n.Y; reflect.M43 = 2f * Vector3.Dot(planePoint, n) * n.Z;
        // 원본 노드를 재귀적으로 복제한다. parentWorldNew = 새 부모의 월드 행렬(새 로컬을 구하는 기준).
        SceneNode Build(SceneNode src, Matrix4x4 parentWorldNew)
        {
            var w = src.WorldMatrix * reflect; // 반사된 월드(행벡터: 왼쪽부터 적용)
            // 반사는 좌우를 뒤집으므로 회전 부분이 반사 행렬이 됨 → 주축 방향만 유지하도록 한 축을 뒤집어 정상 회전으로 만든다
            var t = w.Translation;
            var rx = new Vector3(w.M11, w.M12, w.M13); var ry = new Vector3(w.M21, w.M22, w.M23); var rz = new Vector3(w.M31, w.M32, w.M33);
            if (Vector3.Dot(Vector3.Cross(rx, ry), rz) < 0) rz = -rz; // 오른손계 복원(Maya "behavior" 미러와 유사)
            var rot = new Matrix4x4(rx.X, rx.Y, rx.Z, 0, ry.X, ry.Y, ry.Z, 0, rz.X, rz.Y, rz.Z, 0, t.X, t.Y, t.Z, 1);
            // 새 부모 기준 로컬로 바꾸고, 스케일은 원본 값을 유지한다(반사 행렬 분해로 스케일 부호가 바뀌는 것 방지).
            Matrix4x4.Invert(parentWorldNew, out var inv);
            var local = Transform3.FromMatrix(rot * inv);
            local.Scale = src.Local.Scale;
            // 이름 치환(검색어가 없거나 이름에 없으면 "_mirror" 접미).
            string name = search.Length > 0 && src.Name.Contains(search) ? src.Name.Replace(search, replace) : src.Name + "_mirror";
            var node = new SceneNode { Name = uniqueName(name), Shape = src.Joint != null ? new JointShape { Radius = src.Joint.Radius } : null, Local = local };
            // 자식 조인트만 따라가며 이 노드의 새 월드(rot)를 부모 기준으로 넘긴다.
            foreach (var c in src.Children) if (c.IsJoint) node.AttachChild(Build(c, rot));
            return node;
        }
        var parentWorld = root.Parent != null && !root.Parent.IsRoot ? root.Parent.WorldMatrix : Matrix4x4.Identity;
        return Build(root, parentWorld);
    }

    /// <summary>두 벡터의 외적 행렬 a·bᵀ(3×3 부분, 동차 성분 1). 반사 행렬 I − 2·n·nᵀ 계산용.</summary>
    private static Matrix4x4 Outer(Vector3 a, Vector3 b) => new(a.X * b.X, a.X * b.Y, a.X * b.Z, 0, a.Y * b.X, a.Y * b.Y, a.Y * b.Z, 0, a.Z * b.X, a.Z * b.Y, a.Z * b.Z, 0, 0, 0, 0, 1);
}
