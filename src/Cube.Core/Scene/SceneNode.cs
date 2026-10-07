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
    /// <summary>Bind Skin 된 경우의 skinCluster. 없으면 null.</summary>
    public SkinCluster? Skin { get; set; }
    /// <summary>구성 이력(오래된 것부터). 메시 편집 명령이 넣고 Undo가 뺀다.</summary>
    public List<Commands.HistoryEntry> History { get; } = new();
    /// <summary>Smooth Mesh Preview: 0 = 케이지(1키), 1 = 케이지 + 스무스(2키), 2 = 스무스(3키). 표시 전용.</summary>
    public int SmoothPreview { get; set; }
    public int SmoothPreviewLevels { get; set; } = 2;
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
    public JointShape? Joint => Shape as JointShape;
    public bool IsJoint => Shape is JointShape;
    public LightShape? Light => Shape as LightShape;
    public bool IsLight => Shape is LightShape;
    /// <summary>할당된 머티리얼 ID(0 = 기본 lambert1).</summary>
    public int MaterialId { get; set; }
    public SkinCluster? Skin => MeshShape?.Skin;

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
