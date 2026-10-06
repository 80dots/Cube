using System.Numerics;
using Cube.Core.Mesh;

namespace Cube.Core.Scene;

/// <summary>Maya의 shape 노드에 해당. 트랜스폼 노드가 소유한다.</summary>
public abstract class Shape
{
}

public sealed class MeshShape : Shape
{
    public PolyMesh Mesh { get; }
    public MeshShape(PolyMesh mesh) { Mesh = mesh; }
}

/// <summary>Maya의 transform 노드. DAG 계층과 로컬 TRS, 선택적 shape를 가진다.</summary>
public class SceneNode
{
    public NodeId Id { get; internal set; }
    public string Name { get; set; } = "node";
    public Transform3 Local = Transform3.Identity;
    public SceneNode? Parent { get; internal set; }
    public List<SceneNode> Children { get; } = new();
    public Shape? Shape { get; set; }
    public bool Visible { get; set; } = true;

    public MeshShape? MeshShape => Shape as MeshShape;
    public PolyMesh? Mesh => MeshShape?.Mesh;

    public Matrix4x4 WorldMatrix
    {
        get
        {
            var m = Local.ToMatrix();
            var p = Parent;
            while (p != null && !p.IsRoot) { m *= p.Local.ToMatrix(); p = p.Parent; }
            return m;
        }
    }

    /// <summary>문서 루트 여부(루트는 표시·선택되지 않는다).</summary>
    public bool IsRoot { get; internal set; }

    /// <summary>문서에 넣기 전 트리를 구성할 때 쓰는 자식 연결(가져오기 등).</summary>
    public void AttachChild(SceneNode child)
    {
        child.Parent = this;
        Children.Add(child);
    }

    public IEnumerable<SceneNode> Descendants()
    {
        foreach (var c in Children)
        {
            yield return c;
            foreach (var d in c.Descendants()) yield return d;
        }
    }

    public override string ToString() => $"{Name}{Id}";
}
