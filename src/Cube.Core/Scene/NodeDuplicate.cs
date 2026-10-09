using Cube.Core.Mesh;

namespace Cube.Core.Scene;

/// <summary>Edit → Duplicate(Maya 기본: 입력 그래프 없이 복제)의 노드 트리 복제.</summary>
/// <remarks>
/// 트랜스폼(피벗 포함)·가시성·머티리얼·셰이프(메시/조인트/라이트)와 자식 계층을 모두 복제한다.
/// 메시는 구성 이력과 스킨을 버린 현재 메시(= 바인드 포즈) 사본이며 Smooth Mesh Preview 상태는 유지한다.
/// 이름은 문서와 이번 복제에서 이미 쓴 이름을 모두 피해 고유하게 만든다(같은 트리 안 자식끼리 겹치지 않게).
/// </remarks>
public static class NodeDuplicate
{
    /// <summary>
    /// <paramref name="src"/>와 그 자손을 복제한 새 트리를 만든다(문서에는 넣지 않음; AddNodeCommand로 추가).
    /// </summary>
    /// <param name="doc">이름 충돌 검사에 쓸 문서.</param>
    /// <param name="src">복제할 노드.</param>
    /// <param name="usedNames">이번 복제에서 이미 배정한 이름(호출 사이에 공유해 여러 노드를 한 번에 복제할 때 겹치지 않게). 결과 이름이 추가된다.</param>
    public static SceneNode CloneTree(Document doc, SceneNode src, HashSet<string> usedNames)
    {
        var copy = new SceneNode
        {
            Name = UniqueName(doc, src.Name, usedNames),
            Local = src.Local,
            Visible = src.Visible,
            MaterialId = src.MaterialId,
            Shape = src.Shape switch
            {
                MeshShape ms => new MeshShape(ms.Mesh.Clone()) { SmoothPreview = ms.SmoothPreview, SmoothPreviewLevels = ms.SmoothPreviewLevels },
                LightShape ls => ls.Clone(),
                JointShape js => new JointShape { Radius = js.Radius },
                _ => null,
            },
        };
        foreach (var c in src.Children) copy.AttachChild(CloneTree(doc, c, usedNames));
        return copy;
    }

    /// <summary>문서 이름과 <paramref name="usedNames"/>를 모두 피한 이름(끝 숫자를 늘림)을 고르고 usedNames에 넣는다.</summary>
    private static string UniqueName(Document doc, string baseName, HashSet<string> usedNames)
    {
        string name = doc.UniqueName(baseName);
        if (usedNames.Contains(name))
        {
            string stem = baseName.TrimEnd("0123456789".ToCharArray());
            if (stem.Length == 0) stem = baseName;
            var docNames = new HashSet<string>(doc.Nodes.Values.Select(n => n.Name));
            for (int i = 1; ; i++)
            {
                string cand = stem + i;
                if (!usedNames.Contains(cand) && !docNames.Contains(cand)) { name = cand; break; }
            }
        }
        usedNames.Add(name);
        return name;
    }
}
