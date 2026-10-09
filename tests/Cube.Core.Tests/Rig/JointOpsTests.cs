using System.Numerics;
using Cube.Core.Rig;
using Cube.Core.Scene;

namespace Cube.Core.Tests.Rig;

/// <summary>Mirror Joint / Orient Joint(<see cref="JointOps"/>) 검증: 반사된 월드 위치, 오른손 회전, 이름 치환, 주축 정렬, 자식 월드 유지.</summary>
public class JointOpsTests
{
    /// <summary>조인트 노드를 만든다.</summary>
    private static SceneNode J(string name, Vector3 t, Vector3 rotDeg = default) => new() { Name = name, Shape = new JointShape(), Local = new Transform3(t, rotDeg, Vector3.One) };

    /// <summary>체인(부모 아래 3개)을 문서에 만든다: parent(회전·이동) → L_a → L_b → L_c.</summary>
    private static (Document doc, SceneNode parent, SceneNode a, SceneNode b, SceneNode c) Chain(bool withParent)
    {
        var doc = new Document();
        SceneNode? parent = null;
        if (withParent) { parent = J("spine", new Vector3(0.2f, 1, 0.1f), new Vector3(0, 30, 10)); doc.AddNode(parent); }
        var a = J("L_a", new Vector3(1, 0.5f, 0), new Vector3(0, 0, 20));
        var b = J("L_b", new Vector3(1, 0, 0.3f), new Vector3(10, 0, 0));
        var c = J("L_c", new Vector3(0.8f, -0.2f, 0));
        doc.AddNode(a, parent); doc.AddNode(b, a); doc.AddNode(c, b);
        return (doc, parent!, a, b, c);
    }

    /// <summary>행렬의 3×3 부분 행렬식.</summary>
    private static float Det3(Matrix4x4 m) => Vector3.Dot(Vector3.Cross(new Vector3(m.M11, m.M12, m.M13), new Vector3(m.M21, m.M22, m.M23)), new Vector3(m.M31, m.M32, m.M33));

    /// <summary>두 벡터가 거의 같은지.</summary>
    private static void Near(Vector3 e, Vector3 a, float tol = 1e-4f) => Assert.True(Vector3.Distance(e, a) < tol, $"expected {e}, actual {a}");

    /// <summary>모든 평면 옵션·축에서 미러된 체인의 월드 위치가 원본의 반사이고 회전이 오른손(det&gt;0), 이름이 치환되어야 한다.</summary>
    [Theory]
    [InlineData(false, Axis.X)] [InlineData(true, Axis.X)] [InlineData(true, Axis.Y)] [InlineData(true, Axis.Z)]
    public void Mirror_ReflectsWorldPositions(bool withParent, Axis axis)
    {
        var (doc, parent, a, b, c) = Chain(withParent);
        var n = axis switch { Axis.X => Vector3.UnitX, Axis.Y => Vector3.UnitY, _ => Vector3.UnitZ };
        var plane = withParent ? parent.WorldMatrix.Translation : Vector3.Zero;
        var mir = JointOps.Mirror(a, axis, plane, "L_", "R_", doc.UniqueName);
        doc.AddNode(mir, parent);
        var src = new[] { a, b, c };
        var dst = new[] { mir, mir.Children[0], mir.Children[0].Children[0] };
        for (int i = 0; i < 3; i++)
        {
            var p = src[i].WorldMatrix.Translation;
            var expected = p - 2 * Vector3.Dot(p - plane, n) * n;
            Near(expected, dst[i].WorldMatrix.Translation);
            Assert.True(Det3(dst[i].WorldMatrix) > 0);
            Assert.Equal(src[i].Name.Replace("L_", "R_"), dst[i].Name);
            Assert.True(dst[i].IsJoint);
        }
    }

    /// <summary>
    /// Orient Joint 기본 옵션(X 주축, Y 보조, 월드 +Y): 자식이 있는 조인트의 로컬 X가 자식을 향하고, 자식 월드 위치는 그대로,
    /// 끝 조인트는 부모와 같은 월드 회전을 갖는다.
    /// </summary>
    [Theory]
    [InlineData(Axis.X, Axis.Y)] [InlineData(Axis.Y, Axis.Z)] [InlineData(Axis.Z, Axis.X)] [InlineData(Axis.X, Axis.Z)]
    public void Orient_AimsPrimaryAtChild_KeepsWorldPositions(Axis primary, Axis secondary)
    {
        var (_, _, a, b, c) = Chain(true);
        var before = new[] { a, b, c }.Select(j => j.WorldMatrix.Translation).ToArray();
        var opt = new OrientOptions { Primary = primary, Secondary = secondary, SecondaryWorld = Axis.Y };
        JointOps.Orient(new[] { a }, opt);
        var after = new[] { a, b, c }.Select(j => j.WorldMatrix.Translation).ToArray();
        for (int i = 0; i < 3; i++) Near(before[i], after[i]);
        foreach (var (j, child) in new[] { (a, b), (b, c) })
        {
            var w = j.WorldMatrix;
            var axisVec = primary switch { Axis.X => new Vector3(w.M11, w.M12, w.M13), Axis.Y => new Vector3(w.M21, w.M22, w.M23), _ => new Vector3(w.M31, w.M32, w.M33) };
            var aim = Vector3.Normalize(child.WorldMatrix.Translation - w.Translation);
            Near(aim, Vector3.Normalize(axisVec));
            Assert.True(Det3(w) > 0);
        }
        // 끝 조인트 = 부모 월드 회전(로컬 회전 0)
        Near(Vector3.Zero, c.Local.RotationDegrees, 1e-2f);
    }

    /// <summary>Orient children 끔: 선택 조인트만 회전하고 자식의 월드 트랜스폼은 그대로여야 한다.</summary>
    [Fact]
    public void Orient_WithoutChildren_OnlySelectedChanges()
    {
        var (_, _, a, b, c) = Chain(true);
        var bWorld = b.WorldMatrix; var cLocal = c.Local;
        var changes = JointOps.Orient(new[] { a }, new OrientOptions { OrientChildren = false });
        Assert.Contains(changes, x => x.node == a);
        var bw = b.WorldMatrix;
        for (int i = 0; i < 4; i++) for (int k = 0; k < 4; k++) Assert.True(MathF.Abs(bWorld[i, k] - bw[i, k]) < 1e-4f);
        Assert.Equal(cLocal, c.Local);
    }

    /// <summary>
    /// 반환 목록의 노드별 첫 before로 되돌리면(Undo) 모든 조인트가 원래 로컬로 돌아와야 한다.
    /// (자식까지 정렬할 때 자식의 보정 전 로컬이 기록되지 않아 Undo가 자식을 엉뚱한 곳에 두던 회귀)
    /// </summary>
    [Fact]
    public void Orient_FirstBeforePerNode_RestoresOriginal()
    {
        var (_, _, a, b, c) = Chain(true);
        var orig = new[] { a, b, c }.ToDictionary(j => j, j => j.Local);
        var worldBefore = new[] { a, b, c }.Select(j => j.WorldMatrix.Translation).ToArray();
        var changes = JointOps.Orient(new[] { a }, new OrientOptions());
        var first = new Dictionary<SceneNode, Transform3>();
        foreach (var (n, before, _) in changes) first.TryAdd(n, before);
        foreach (var (n, t) in first) n.Local = t;
        foreach (var j in new[] { a, b, c }) Assert.Equal(orig[j], j.Local);
        var worldAfter = new[] { a, b, c }.Select(j => j.WorldMatrix.Translation).ToArray();
        for (int i = 0; i < 3; i++) Near(worldBefore[i], worldAfter[i]);
    }
}
