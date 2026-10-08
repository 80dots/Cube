using System.Runtime.InteropServices;
using Cube.Core.Mesh;
using Godot;
using GArray = Godot.Collections.Array;

namespace Cube.App.Bridge;

/// <summary>
/// RenderMeshData를 Godot ArrayMesh/MultiMesh로 올린다. 마샬링 비용을 줄이기 위해 Godot 배열 버퍼를 재사용한다.
/// 위치만 바뀌는 경우(스킨 변형 재생, 정점 드래그)는 <see cref="UpdateSurfacePositions"/>/<see cref="UpdateLinePositions"/>/
/// <see cref="UpdatePointPositions"/>가 서피스를 다시 만들지 않고 정점 버퍼 영역(SurfaceUpdateVertexRegion)·MultiMesh 버퍼만 덮어쓴다.
/// 정점 버퍼 레이아웃(위치 float3 × N이 오프셋 0부터 연속)은 처음 한 번 RenderingServer에서 읽어 와 검증하고, 다르면 전체 재생성으로 되돌아간다.
/// </summary>
/// <remarks>
/// MeshView 하나가 이 객체를 하나 가지며, 표면/와이어/틴트마다 같은 인스턴스 버퍼를 다시 쓴다(필드 배열은 길이가 정확히 맞을 때만 재사용).
/// 코어와의 규약 변환은 여기서만 한다: ① 삼각형 감기 순서 CCW(코어) → CW(Godot), ② UV v → 1 − v(코어 하단 원점 → Godot 상단 원점),
/// ③ 정점 색 sRGB → 선형. 정적 필드(<c>_vertexRegionOk</c>, <c>_multimeshBufferOk</c>)는 Godot 빌드의 버퍼 레이아웃 검증 결과라
/// 앱 전체에서 한 번만 정해진다.
/// </remarks>
public sealed class GodotMeshBridge
{
    /// <summary>표면 코너 위치 버퍼(렌더 코너 i → Godot 좌표). <see cref="UploadSurface"/>가 채운다.</summary>
    private Vector3[] _pos = Array.Empty<Vector3>();
    /// <summary>표면 코너 노멀 버퍼.</summary>
    private Vector3[] _nrm = Array.Empty<Vector3>();
    /// <summary>표면 코너 UV 버퍼(이미 v = 1 − v로 뒤집힌 Godot 규약).</summary>
    private Vector2[] _uv = Array.Empty<Vector2>();
    /// <summary>표면 삼각형 인덱스 버퍼(감기 순서를 CW로 바꾼 뒤의 값).</summary>
    private int[] _idx = Array.Empty<int>();
    /// <summary>와이어 선분 정점 위치(선분 i = 2i, 2i+1).</summary>
    private Vector3[] _linePos = Array.Empty<Vector3>();
    /// <summary>와이어 선분 정점 색(선형). 같은 선분의 두 정점은 같은 색.</summary>
    private Color[] _lineCol = Array.Empty<Color>();
    /// <summary>선택 면 틴트 삼각형의 정점 위치(삼각형마다 정점 3개를 따로 둔다).</summary>
    private Vector3[] _tintPos = Array.Empty<Vector3>();
    /// <summary>틴트 인덱스(0,1,2,…; 정점을 공유하지 않으므로 항등).</summary>
    private int[] _tintIdx = Array.Empty<int>();
    private int[] _tintCorners = Array.Empty<int>();   // 틴트 정점 k → 렌더 코너 인덱스(위치 갱신용)
    /// <summary>현재 틴트 서피스의 정점 수(0 = 틴트 없음). 위치만 갱신할 때 범위로 쓴다.</summary>
    private int _tintVertexCount;

    /// <summary>표면 코너 정점 색 버퍼(가중치 표시 등에서 <c>cornerColor</c>가 있을 때만 사용).</summary>
    private Color[] _col = Array.Empty<Color>();

    /// <summary>마지막 전체 업로드의 코너 수(-1 = 없음). 위치만 갱신할 때 같은 위상인지 확인한다.</summary>
    private int _surfaceCorners = -1, _lineVerts = -1;
    /// <summary>위치 부분 갱신용 바이트 스크래치 버퍼(float3 × N). 크기가 모자랄 때만 늘린다.</summary>
    private byte[] _bytes = Array.Empty<byte>();

    /// <summary>정점 영역 부분 갱신이 이 Godot 빌드에서 기대한 레이아웃인지(null = 아직 검증 전).</summary>
    private static bool? _vertexRegionOk;
    /// <summary>MultiMesh 인스턴스 버퍼를 한 번에 쓰는 경로(변환 12 + 색 4 float)가 이 빌드에서 맞는지(null = 아직 검증 전).</summary>
    private static bool? _multimeshBufferOk;
    /// <summary>검증 실패 시 이유(디버그).</summary>
    public static string? FastPathDisabledReason { get; private set; }

    /// <summary>셰이딩 표면(삼각형)을 다시 올린다. cornerColor가 있으면 정점 색(선형)을, bones/weights가 있으면 스키닝 배열(코너당 4개)을 함께 올린다.</summary>
    /// <param name="mesh">덮어쓸 ArrayMesh(기존 서피스는 모두 지운다).</param>
    /// <param name="r">코어 테셀레이터 결과(코너 언롤 삼각형).</param>
    /// <param name="cornerColor">렌더 코너 인덱스 → 선형 정점 색. null이면 색 배열을 넣지 않는다.</param>
    /// <param name="bones4">코너당 4개의 본(스킨 바인드) 인덱스. weights4와 함께 있어야 쓰인다.</param>
    /// <param name="weights4">코너당 4개의 가중치.</param>
    public void UploadSurface(ArrayMesh mesh, RenderMeshData r, Func<int, Color>? cornerColor = null, int[]? bones4 = null, float[]? weights4 = null)
    {
        // 기존 서피스를 지우고 위치 갱신 경로를 무효화한다(삼각형이 없으면 빈 메시로 끝)
        mesh.ClearSurfaces();
        _surfaceCorners = -1;
        if (r.IndexCount == 0) return;
        // 버퍼 길이를 코너/인덱스 수에 정확히 맞추고 코어 → Godot 값으로 채운다
        Fit(ref _pos, r.CornerCount); Fit(ref _nrm, r.CornerCount); Fit(ref _uv, r.CornerCount); Fit(ref _idx, r.IndexCount);
        for (int i = 0; i < r.CornerCount; i++)
        {
            _pos[i] = r.Positions[i].ToGodot();
            _nrm[i] = r.Normals[i].ToGodot();
            _uv[i] = new Vector2(r.Uvs[i].X, 1f - r.Uvs[i].Y); // Godot UV는 상단 원점
        }
        // 코어는 반시계(CCW)가 앞면, Godot은 시계(CW)가 앞면 → 삼각형마다 1,2번 인덱스를 바꾼다
        for (int t = 0; t < r.IndexCount; t += 3)
        {
            _idx[t] = r.Indices[t]; _idx[t + 1] = r.Indices[t + 2]; _idx[t + 2] = r.Indices[t + 1];
        }
        // Godot 서피스 배열 구성(Mesh.ArrayType 순서대로 슬롯을 만든 뒤 필요한 것만 채움)
        var arrays = new GArray();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = _pos;
        arrays[(int)Mesh.ArrayType.Normal] = _nrm;
        arrays[(int)Mesh.ArrayType.TexUV] = _uv;
        arrays[(int)Mesh.ArrayType.Index] = _idx;
        if (cornerColor != null)
        {
            Fit(ref _col, r.CornerCount);
            for (int i = 0; i < r.CornerCount; i++) _col[i] = cornerColor(i);
            arrays[(int)Mesh.ArrayType.Color] = _col;
        }
        if (bones4 != null && weights4 != null)
        {
            arrays[(int)Mesh.ArrayType.Bones] = bones4;
            arrays[(int)Mesh.ArrayType.Weights] = weights4;
        }
        // 업로드 후 코너 수를 기억해 두고, 첫 업로드에서 정점 버퍼 레이아웃을 한 번 검증한다
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        _surfaceCorners = r.CornerCount;
        if (_vertexRegionOk == null) VerifyVertexRegion(mesh, r.CornerCount);
    }

    /// <summary>
    /// 표면 정점 위치만 갱신한다(위상·노멀·UV·인덱스는 그대로). 마지막 <see cref="UploadSurface"/>와 코너 수가 같고 레이아웃 검증을 통과했을 때만 true.
    /// false면 호출자가 전체 업로드로 되돌아가야 한다.
    /// </summary>
    /// <returns>부분 갱신했으면 true, 전체 업로드가 필요하면 false.</returns>
    public bool UpdateSurfacePositions(ArrayMesh mesh, RenderMeshData r)
    {
        // 검증 통과 + 같은 코너 수 + 서피스 존재일 때만 위치 블록(오프셋 0)을 통째로 덮어쓴다
        if (_vertexRegionOk != true || _surfaceCorners < 0 || _surfaceCorners != r.CornerCount || mesh.GetSurfaceCount() == 0) return false;
        mesh.SurfaceUpdateVertexRegion(0, 0, PositionBytes(r.Positions, r.CornerCount));
        return true;
    }

    /// <summary>와이어(선분)를 정점 색과 함께 올린다. colorOf(edgeId)가 선 색을 정한다.</summary>
    /// <param name="colorOf">엣지 ID → sRGB 색(선택/호버/크리즈 등은 호출자가 결정).</param>
    public void UploadLines(ArrayMesh mesh, RenderMeshData r, Func<int, Color> colorOf)
    {
        mesh.ClearSurfaces();
        _lineVerts = -1;
        if (r.LineVertexCount == 0) return;
        // 선분마다 두 끝점 위치와 같은 색을 넣는다
        Fit(ref _linePos, r.LineVertexCount); Fit(ref _lineCol, r.LineVertexCount);
        for (int i = 0; i < r.LineCount; i++)
        {
            // 정점 색은 셰이더에서 선형으로 취급되므로 sRGB 값을 선형으로 바꿔 넘긴다
            var c = colorOf(r.LineToEdge[i]).SrgbToLinear();
            _linePos[i * 2] = r.LinePositions[i * 2].ToGodot();
            _linePos[i * 2 + 1] = r.LinePositions[i * 2 + 1].ToGodot();
            _lineCol[i * 2] = c; _lineCol[i * 2 + 1] = c;
        }
        // 위치 + 색만 가진 Lines 서피스로 올린다
        var arrays = new GArray();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = _linePos;
        arrays[(int)Mesh.ArrayType.Color] = _lineCol;
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Lines, arrays);
        _lineVerts = r.LineVertexCount;
        if (_vertexRegionOk == null) VerifyVertexRegion(mesh, r.LineVertexCount);
    }

    /// <summary>와이어 정점 위치만 갱신한다(색 그대로). 마지막 <see cref="UploadLines"/>와 선분 수가 같을 때만 true.</summary>
    /// <remarks>와이어의 선분 정점 위치 블록만 <c>SurfaceUpdateVertexRegion</c>으로 덮어쓴다.</remarks>
    public bool UpdateLinePositions(ArrayMesh mesh, RenderMeshData r)
    {
        if (_vertexRegionOk != true || _lineVerts < 0 || _lineVerts != r.LineVertexCount || mesh.GetSurfaceCount() == 0) return false;
        mesh.SurfaceUpdateVertexRegion(0, 0, PositionBytes(r.LinePositions, r.LineVertexCount));
        return true;
    }

    /// <summary>
    /// 점(정점 또는 면 중심)을 MultiMesh 인스턴스로 올린다. buffer는 호출자가 보관하는 인스턴스 버퍼(변환 12 + 색 4 float)로,
    /// 이후 <see cref="UpdatePointPositions"/>가 색을 유지한 채 위치만 바꾼다.
    /// </summary>
    /// <param name="mm">대상 MultiMesh(TransformFormat 3D, UseColors가 켜져 있어야 한다).</param>
    /// <param name="positions">인스턴스(점) 위치들, 로컬 좌표.</param>
    /// <param name="colorOf">인스턴스 인덱스 → sRGB 색.</param>
    /// <param name="buffer">호출자가 보관하는 float 버퍼. 크기가 맞지 않으면 새로 만들어 돌려준다.</param>
    public static void UploadPoints(MultiMesh mm, ReadOnlySpan<System.Numerics.Vector3> positions, Func<int, Color> colorOf, ref float[] buffer)
    {
        // 인스턴스 수를 맞춘다(바뀔 때만 설정해 불필요한 재할당을 피함)
        int n = positions.Length;
        if (mm.InstanceCount != n) mm.InstanceCount = n;
        if (n == 0) return;
        // 버퍼 레이아웃 검증에 실패한 빌드: 느리지만 안전한 인스턴스별 API로 설정한다
        if (_multimeshBufferOk == false)
        {
            for (int i = 0; i < n; i++)
            {
                mm.SetInstanceTransform(i, new Transform3D(Basis.Identity, positions[i].ToGodot()));
                mm.SetInstanceColor(i, colorOf(i).SrgbToLinear());
            }
            return;
        }
        // 빠른 경로: 인스턴스마다 3×4 변환 행(단위 회전 + 위치를 4번째 열)과 선형 색 4개를 버퍼에 써서 한 번에 보낸다
        const int stride = 16;
        if (buffer.Length != n * stride) buffer = new float[n * stride];
        for (int i = 0; i < n; i++)
        {
            int o = i * stride; var p = positions[i]; var c = colorOf(i).SrgbToLinear();
            buffer[o] = 1; buffer[o + 1] = 0; buffer[o + 2] = 0; buffer[o + 3] = p.X;
            buffer[o + 4] = 0; buffer[o + 5] = 1; buffer[o + 6] = 0; buffer[o + 7] = p.Y;
            buffer[o + 8] = 0; buffer[o + 9] = 0; buffer[o + 10] = 1; buffer[o + 11] = p.Z;
            buffer[o + 12] = c.R; buffer[o + 13] = c.G; buffer[o + 14] = c.B; buffer[o + 15] = c.A;
        }
        RenderingServer.MultimeshSetBuffer(mm.GetRid(), buffer);
        if (_multimeshBufferOk == null)
        {
            // 한 번만: 버퍼 레이아웃(변환 12 + 색 4)이 맞는지 첫 인스턴스로 확인. 다르면 인스턴스별 호출로 되돌아간다
            var t = mm.GetInstanceTransform(0); var col = mm.GetInstanceColor(0); var c0 = colorOf(0).SrgbToLinear();
            bool ok = (t.Origin - positions[0].ToGodot()).Length() < 1e-4f && MathF.Abs(col.R - c0.R) < 1e-2f && MathF.Abs(col.G - c0.G) < 1e-2f && MathF.Abs(col.B - c0.B) < 1e-2f;
            _multimeshBufferOk = ok;
            if (!ok)
            {
                FastPathDisabledReason = $"multimesh buffer layout mismatch: origin={t.Origin} expected={positions[0]} color={col} expected={c0}";
                GD.PushWarning("[Cube] " + FastPathDisabledReason);
                var tmp = buffer; UploadPoints(mm, positions, colorOf, ref tmp);
            }
        }
    }

    /// <summary>MultiMesh 인스턴스의 위치만 바꾼다(색은 buffer에 남아 있는 값). 인스턴스 수가 다르면 false(전체 업로드 필요).</summary>
    /// <param name="buffer"><see cref="UploadPoints"/>가 채운 버퍼. 각 인스턴스의 위치 열(3, 7, 11번 float)만 바꾼다.</param>
    public static bool UpdatePointPositions(MultiMesh mm, ReadOnlySpan<System.Numerics.Vector3> positions, float[] buffer)
    {
        int n = positions.Length;
        const int stride = 16;
        if (_multimeshBufferOk != true || n == 0 || mm.InstanceCount != n || buffer.Length != n * stride) return false;
        // 3×4 변환 행의 4번째 열(이동 성분)만 덮어쓰고 색 4개는 그대로 둔다
        for (int i = 0; i < n; i++)
        {
            int o = i * stride; var p = positions[i];
            buffer[o + 3] = p.X; buffer[o + 7] = p.Y; buffer[o + 11] = p.Z;
        }
        RenderingServer.MultimeshSetBuffer(mm.GetRid(), buffer);
        return true;
    }

    /// <summary>선택된 면만 모아 반투명 틴트용 삼각형을 올린다.</summary>
    /// <remarks>
    /// 렌더 삼각형 → 면 매핑(<c>TriToFace</c>)으로 포함할 삼각형을 고르고, 정점을 공유하지 않는 삼각형 목록을 만든다.
    /// 각 틴트 정점이 어느 렌더 코너에서 왔는지(<c>_tintCorners</c>)를 기억해 두어 <see cref="UpdateFaceSubsetPositions"/>가 위치만 다시 채울 수 있다.
    /// </remarks>
    /// <param name="includeFace">면 ID가 틴트 대상인지.</param>
    public void UploadFaceSubset(ArrayMesh mesh, RenderMeshData r, Func<int, bool> includeFace)
    {
        mesh.ClearSurfaces();
        _tintVertexCount = 0;
        // 1단계: 포함할 삼각형 수를 세어 버퍼 크기를 정한다
        int tris = 0;
        for (int t = 0; t < r.TriangleCount; t++) if (includeFace(r.TriToFace[t])) tris++;
        if (tris == 0) return;
        Fit(ref _tintPos, tris * 3); Fit(ref _tintIdx, tris * 3); Fit(ref _tintCorners, tris * 3);
        // 2단계: 포함된 삼각형마다 코너 3개를 복사(감기 순서 뒤집기)하고 원래 코너 번호를 기록
        int k = 0;
        for (int t = 0; t < r.TriangleCount; t++)
        {
            if (!includeFace(r.TriToFace[t])) continue;
            // 코어 CCW → Godot CW: 1,2번을 바꾼다
            int c0 = r.Indices[t * 3], c1 = r.Indices[t * 3 + 2], c2 = r.Indices[t * 3 + 1];
            _tintCorners[k] = c0; _tintPos[k] = r.Positions[c0].ToGodot(); _tintIdx[k] = k; k++;
            _tintCorners[k] = c1; _tintPos[k] = r.Positions[c1].ToGodot(); _tintIdx[k] = k; k++;
            _tintCorners[k] = c2; _tintPos[k] = r.Positions[c2].ToGodot(); _tintIdx[k] = k; k++;
        }
        // 인덱스는 항등이지만 서피스 형식을 표면과 맞추려고 함께 넣는다
        var arrays = new GArray();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = _tintPos;
        arrays[(int)Mesh.ArrayType.Index] = _tintIdx;
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        _tintVertexCount = k;
    }

    /// <summary>틴트 삼각형의 위치만 갱신한다(같은 면 집합). 틴트가 비어 있거나 검증 전이면 false.</summary>
    /// <remarks>
    /// <c>_tintCorners</c>로 각 틴트 정점의 원래 코너 위치를 다시 읽어 float3 연속 바이트로 만든 뒤 정점 영역을 덮어쓴다.
    /// 면 집합이 바뀌었으면 호출자가 <see cref="UploadFaceSubset"/>를 다시 불러야 한다.
    /// </remarks>
    public bool UpdateFaceSubsetPositions(ArrayMesh mesh, RenderMeshData r)
    {
        if (_vertexRegionOk != true || _tintVertexCount <= 0 || mesh.GetSurfaceCount() == 0) return false;
        int n = _tintVertexCount;
        // 스크래치 바이트 버퍼를 float 뷰로 보고 위치를 직접 기록(할당 없음)
        if (_bytes.Length < n * 12) _bytes = new byte[n * 12];
        var floats = MemoryMarshal.Cast<byte, float>(_bytes.AsSpan(0, n * 12));
        for (int k = 0; k < n; k++)
        {
            var p = r.Positions[_tintCorners[k]];
            floats[k * 3] = p.X; floats[k * 3 + 1] = p.Y; floats[k * 3 + 2] = p.Z;
        }
        mesh.SurfaceUpdateVertexRegion(0, 0, _bytes.AsSpan(0, n * 12));
        return true;
    }

    /// <summary>System.Numerics 위치 배열을 Godot 정점 버퍼 바이트(float3 연속)로 바꾼다. 둘 다 float3 연속 레이아웃이라 그대로 복사한다.</summary>
    /// <param name="src">코어 위치 배열(앞쪽 count개만 사용).</param>
    /// <param name="count">복사할 정점 수.</param>
    /// <returns><c>_bytes</c>의 앞부분을 가리키는 span(다음 호출에서 덮어써지므로 바로 써야 한다).</returns>
    private ReadOnlySpan<byte> PositionBytes(System.Numerics.Vector3[] src, int count)
    {
        int bytes = count * 12;
        if (_bytes.Length < bytes) _bytes = new byte[Math.Max(bytes, 1024)];
        MemoryMarshal.AsBytes(src.AsSpan(0, count)).CopyTo(_bytes);
        return _bytes.AsSpan(0, bytes);
    }

    /// <summary>
    /// 정점 영역 부분 갱신 전제(위치 float3 × N이 버퍼 오프셋 0부터 stride 12로 연속)를 실제 서피스 데이터로 한 번 확인한다.
    /// Godot 4.x는 위치 블록 뒤에 노멀/탄젠트 블록을 두므로 통과해야 정상이며, 다르면 부분 갱신을 끄고 전체 재생성만 쓴다.
    /// </summary>
    private static void VerifyVertexRegion(ArrayMesh mesh, int count)
    {
        try
        {
            // 서피스 형식에서 정점 stride와 위치 오프셋을 계산한다(위치 블록이 따로 떨어져 있으면 stride 12, offset 0)
            var rid = mesh.GetRid();
            var format = (RenderingServer.ArrayFormat)(ulong)mesh.SurfaceGetFormat(0);
            int stride = (int)RenderingServer.MeshSurfaceGetFormatVertexStride(format, count);
            int offset = (int)RenderingServer.MeshSurfaceGetFormatOffset(format, count, (int)Mesh.ArrayType.Vertex);
            // 실제 GPU 정점 바이트와 Godot이 보고하는 정점 배열을 함께 읽어 비교 준비
            var data = RenderingServer.MeshGetSurface(rid, 0);
            var bytes = data["vertex_data"].AsByteArray();
            var arrays = mesh.SurfaceGetArrays(0);
            var verts = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            // 레이아웃 조건이 맞으면 모든 정점의 float 값이 정확히 같은지 확인
            bool ok = stride == 12 && offset == 0 && bytes.Length >= count * 12 && verts.Length == count;
            if (ok)
            {
                var f = MemoryMarshal.Cast<byte, float>(bytes.AsSpan(0, count * 12));
                for (int i = 0; i < count && ok; i++)
                    ok = f[i * 3] == verts[i].X && f[i * 3 + 1] == verts[i].Y && f[i * 3 + 2] == verts[i].Z;
            }
            // 결과를 정적 필드에 기록(이후 모든 MeshView에 적용). 실패하면 경고를 남긴다
            _vertexRegionOk = ok;
            if (!ok)
            {
                FastPathDisabledReason = $"vertex buffer layout mismatch: stride={stride} offset={offset} bytes={bytes.Length} count={count}";
                GD.PushWarning("[Cube] " + FastPathDisabledReason);
            }
        }
        // 검증 중 예외(API 차이 등)도 실패로 보고 전체 업로드 경로만 쓴다
        catch (Exception ex)
        {
            _vertexRegionOk = false;
            FastPathDisabledReason = "vertex buffer verify failed: " + ex.Message;
            GD.PushWarning("[Cube] " + FastPathDisabledReason);
        }
    }

    /// <summary>부분 갱신 경로가 켜져 있는지(검증 통과). 디버그 출력용.</summary>
    public static string FastPathState => $"vertexRegion={_vertexRegionOk?.ToString() ?? "unverified"} multimeshBuffer={_multimeshBufferOk?.ToString() ?? "unverified"}";

    /// <summary>배열 길이를 정확히 <paramref name="exact"/>로 맞춘다(다르면 새로 할당, 같으면 재사용).</summary>
    private static void Fit<T>(ref T[] arr, int exact)
    {
        // Godot은 배열 길이를 그대로 쓰므로 정확한 길이가 필요하다
        if (arr.Length != exact) arr = new T[exact];
    }
}
