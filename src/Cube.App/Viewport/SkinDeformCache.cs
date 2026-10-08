using System.Numerics;
using Cube.Core.Rig;
using Cube.Core.Scene;

namespace Cube.App.Viewport;

/// <summary>
/// 스킨 변형(CPU LBS) 결과를 문서 통지 일련번호(<see cref="Document.ChangeSerial"/>)로 캐시해 뷰포트 패널 4개가 같은 포즈를 각자 계산하지 않게 한다.
/// 같은 일련번호 안에서는 조인트 월드 행렬도 메모해 메시마다 조인트 체인을 다시 곱하지 않는다.
/// <see cref="Update"/>는 여러 메시의 변형을 병렬로 계산한다(순수 C#; 행렬 메모는 먼저 순차로 채운다).
/// 반환 배열은 공유되므로 읽기만 할 것(MeshView.Deformed가 그대로 참조한다).
/// </summary>
public static class SkinDeformCache
{
    /// <summary>노드별 캐시 항목: 계산한 통지 일련번호(-1 = 없음)와 변형 위치 버퍼(정점 ID 순).</summary>
    private sealed class Entry { public int Serial = -1; public Vector3[] Buf = Array.Empty<Vector3>(); }
    /// <summary>스킨 노드 ID → 캐시 항목.</summary>
    private static readonly Dictionary<NodeId, Entry> _entries = new();
    /// <summary>현재 일련번호에서 메모한 노드 월드 행렬(메시 노드와 조인트). 병렬 단계에서는 읽기만 한다.</summary>
    private static readonly Dictionary<NodeId, Matrix4x4> _worlds = new();
    /// <summary><c>_worlds</c>를 채운 통지 일련번호. 다르면 메모를 비운다.</summary>
    private static int _worldSerial = -1;
    /// <summary>캐시가 속한 문서. 문서가 바뀌면 전부 비운다.</summary>
    private static Document? _doc;
    /// <summary>이번 Update에서 다시 계산해야 하는 노드 목록(재사용 리스트).</summary>
    private static readonly List<SceneNode> _stale = new();

    /// <summary>주어진 스킨 노드들의 변형을 이 통지 기준으로 최신화한다(이미 계산된 노드는 건너뜀, 나머지는 병렬).</summary>
    public static void Update(Document doc, IReadOnlyList<SceneNode> skinned)
    {
        // 문서가 바뀌었으면 전체 초기화, 일련번호가 바뀌었으면 월드 행렬 메모만 초기화
        if (!ReferenceEquals(_doc, doc)) { _entries.Clear(); _worlds.Clear(); _doc = doc; _worldSerial = -1; }
        int serial = doc.ChangeSerial;
        if (_worldSerial != serial) { _worlds.Clear(); _worldSerial = serial; }
        _stale.Clear();
        // 이미 이 통지에서 계산했고 정점 수가 같은 노드는 건너뛰고, 나머지는 버퍼 준비 + 월드 행렬 메모 후 목록에 넣는다
        foreach (var n in skinned)
        {
            if (n.Mesh == null || n.Skin == null) continue;
            if (!_entries.TryGetValue(n.Id, out var e)) _entries[n.Id] = e = new Entry();
            int vc = n.Mesh.VertexCount;
            if (e.Serial == serial && e.Buf.Length == vc) continue;
            if (e.Buf.Length != vc) e.Buf = new Vector3[vc];
            // 월드 행렬 메모는 순차로(사전 쓰기) — 병렬 단계에서는 읽기만 한다
            World(n);
            foreach (var jid in n.Skin.Joints) if (doc.Find(jid) is { } j) World(j);
            _stale.Add(n);
        }
        // 하나면 바로, 여럿이면 메시별 병렬 계산
        if (_stale.Count == 0) return;
        if (_stale.Count == 1) DeformOne(_stale[0], serial);
        else Parallel.For(0, _stale.Count, i => DeformOne(_stale[i], serial));
    }

    /// <summary>
    /// 노드 하나의 LBS 변형을 계산해 버퍼에 쓴다. 조인트 월드 행렬은 메모에서 읽고, 결과를 메시 로컬 공간으로 되돌리기 위해 메시 월드 역행렬을 넘긴다.
    /// </summary>
    private static void DeformOne(SceneNode n, int serial)
    {
        var e = _entries[n.Id];
        Matrix4x4.Invert(_worlds[n.Id], out var inv);
        SkinOps.Deform(n.Mesh!, n.Skin!, jid => _worlds.TryGetValue(jid, out var w) ? w : null, inv, e.Buf);
        e.Serial = serial;
    }

    /// <summary>노드의 현재 변형 위치(정점 ID 순, 길이 = VertexCount). 이 통지에서 아직 계산하지 않았으면 지금 계산한다.</summary>
    public static Vector3[] Get(Document doc, SceneNode n)
    {
        // 캐시가 없거나 낡았으면 이 노드 하나만 Update로 계산(정적 배열 하나로 할당 없이 목록 전달)
        if (!ReferenceEquals(_doc, doc) || !_entries.TryGetValue(n.Id, out var e) || e.Serial != doc.ChangeSerial || e.Buf.Length != n.Mesh!.VertexCount)
        {
            _one[0] = n;
            Update(doc, _one);
            e = _entries[n.Id];
        }
        return e.Buf;
    }
    /// <summary><see cref="Get"/>이 단일 노드 Update에 쓰는 1칸 배열.</summary>
    private static readonly SceneNode[] _one = new SceneNode[1];

    /// <summary>노드 월드 행렬을 메모에서 찾고, 없으면 계산해 메모한다(순차 단계에서만 호출).</summary>
    private static Matrix4x4 World(SceneNode n)
    {
        if (_worlds.TryGetValue(n.Id, out var m)) return m;
        m = n.WorldMatrix;
        _worlds[n.Id] = m;
        return m;
    }

    /// <summary>노드가 지워졌거나 스킨이 떨어졌을 때 버퍼를 버린다.</summary>
    public static void Forget(NodeId id) => _entries.Remove(id);

    /// <summary>모든 캐시를 비운다(문서 리셋).</summary>
    public static void Clear() { _entries.Clear(); _worlds.Clear(); _worldSerial = -1; }
}
