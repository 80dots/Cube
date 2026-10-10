using System.Numerics;
using Cube.Core.Mesh;

namespace Cube.Core.Scene;

/// <summary>Maya의 shape 노드에 해당. 트랜스폼 노드가 소유한다.</summary>
/// <remarks>파생: <see cref="MeshShape"/>(폴리곤 메시), <see cref="JointShape"/>(조인트), <see cref="LightShape"/>(라이트). 노드당 셰이프는 최대 하나.</remarks>
public abstract class Shape
{
}

/// <summary>폴리곤 메시 셰이프. 메시 본체와 스킨, 구성 이력, 스무스 프리뷰 표시 상태를 가진다.</summary>
public sealed class MeshShape : Shape
{
    /// <summary>하프에지 메시(오브젝트 로컬 좌표). 객체 자체는 교체되지 않고 CopyFrom으로 내용만 바뀐다.</summary>
    public PolyMesh Mesh { get; }
    /// <summary>Bind Skin 된 경우의 skinCluster. 없으면 null.</summary>
    public SkinCluster? Skin { get; set; }
    /// <summary>구성 이력(오래된 것부터). 메시 편집 명령이 넣고 Undo가 뺀다.</summary>
    public List<Commands.HistoryEntry> History { get; } = new();
    /// <summary>Smooth Mesh Preview: 0 = 케이지(1키), 1 = 케이지 + 스무스(2키), 2 = 스무스(3키). 표시 전용.</summary>
    public int SmoothPreview { get; set; }
    /// <summary>스무스 프리뷰의 Catmull-Clark 분할 단계(기본 2).</summary>
    public int SmoothPreviewLevels { get; set; } = 2;
    /// <summary>메시를 감싼다.</summary>
    public MeshShape(PolyMesh mesh) { Mesh = mesh; }
}

/// <summary>Maya의 transform 노드. DAG 계층과 로컬 TRS, 선택적 shape를 가진다.</summary>
public class SceneNode
{
    /// <summary>문서 내 식별자(문서가 배정; 넣기 전에는 None).</summary>
    public NodeId Id { get; internal set; }
    /// <summary>표시 이름(Outliner, 내보내기 노드 이름).</summary>
    public string Name { get; set; } = "node";
    /// <summary>부모 공간 기준 로컬 트랜스폼(편집 데이터, 저장·Undo 대상). 구조체 필드라 통째로 교체한다.</summary>
    public Transform3 Local = Transform3.Identity;
    /// <summary>
    /// 애니메이션 재생/스크럽 중의 포즈(표시·월드 계산용). null이면 Local. 편집 데이터(Local)와 분리되어 저장·Undo 대상이 아니다.
    /// 내보내기·저장 전에는 비운다(AnimationPose.RestScope).
    /// </summary>
    public Transform3? Pose;
    /// <summary>현재 보이는 로컬 트랜스폼(포즈가 있으면 포즈).</summary>
    public Transform3 Evaluated => Pose ?? Local;
    /// <summary>부모 노드(최상위 노드는 Root, 문서에서 뗀 노드는 null).</summary>
    public SceneNode? Parent { get; internal set; }
    /// <summary>자식 노드(순서 = Outliner 순서).</summary>
    public List<SceneNode> Children { get; } = new();
    /// <summary>셰이프(없으면 빈 그룹/트랜스폼 노드).</summary>
    public Shape? Shape { get; set; }
    /// <summary>표시 여부.</summary>
    public bool Visible { get; set; } = true;

    /// <summary>메시 셰이프(아니면 null).</summary>
    public MeshShape? MeshShape => Shape as MeshShape;
    /// <summary>메시(메시 셰이프가 아니면 null).</summary>
    public PolyMesh? Mesh => MeshShape?.Mesh;
    /// <summary>조인트 셰이프(아니면 null).</summary>
    public JointShape? Joint => Shape as JointShape;
    /// <summary>조인트 노드인지.</summary>
    public bool IsJoint => Shape is JointShape;
    /// <summary>라이트 셰이프(아니면 null).</summary>
    public LightShape? Light => Shape as LightShape;
    /// <summary>라이트 노드인지.</summary>
    public bool IsLight => Shape is LightShape;
    /// <summary>이미지 플레인 셰이프(아니면 null).</summary>
    public ImagePlaneShape? ImagePlane => Shape as ImagePlaneShape;
    /// <summary>이미지 플레인 노드인지.</summary>
    public bool IsImagePlane => Shape is ImagePlaneShape;
    /// <summary>할당된 머티리얼 ID(0 = 기본 lambert1).</summary>
    public int MaterialId { get; set; }
    /// <summary>메시의 skinCluster(없으면 null).</summary>
    public SkinCluster? Skin => MeshShape?.Skin;

    /// <summary>월드 행렬 = 자신의 Evaluated 행렬 · 부모 · ... · 최상위(루트 제외). 행벡터 규약이라 자식이 왼쪽.</summary>
    /// <remarks>재생 포즈를 반영하기 위해 Local이 아니라 Evaluated를 쓴다. 호출마다 계산하므로 반복 사용 시 캐시는 호출자 몫.</remarks>
    public Matrix4x4 WorldMatrix
    {
        get
        {
            var m = Evaluated.ToMatrix();
            var p = Parent;
            while (p != null && !p.IsRoot) { m *= p.Evaluated.ToMatrix(); p = p.Parent; }
            return m;
        }
    }

    /// <summary>회전/스케일 피벗의 월드 위치.</summary>
    /// <remarks>피벗(오브젝트 공간)에 월드 행렬을 적용한 점. 오브젝트 모드 조작기가 여기에 놓인다.</remarks>
    public Vector3 PivotWorld => Vector3.Transform(Evaluated.Pivot, WorldMatrix);

    /// <summary>문서 루트 여부(루트는 표시·선택되지 않는다).</summary>
    public bool IsRoot { get; internal set; }

    /// <summary>문서에 넣기 전 트리를 구성할 때 쓰는 자식 연결(가져오기 등).</summary>
    public void AttachChild(SceneNode child)
    {
        child.Parent = this;
        Children.Add(child);
    }

    /// <summary>모든 하위 노드를 깊이 우선(전위) 순서로 열거한다(자신 제외).</summary>
    public IEnumerable<SceneNode> Descendants()
    {
        foreach (var c in Children)
        {
            yield return c;
            foreach (var d in c.Descendants()) yield return d;
        }
    }

    /// <summary>디버그 표시 "이름#ID".</summary>
    public override string ToString() => $"{Name}{Id}";
}
