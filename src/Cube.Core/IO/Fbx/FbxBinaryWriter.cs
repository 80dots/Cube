using System.IO.Compression;
using System.Text;

namespace Cube.Core.IO.Fbx;

/// <summary>
/// 바이너리 FBX 7.4 writer. 레이아웃(Blender encode_bin.py와 같음):
/// 헤더 "Kaydara FBX Binary  \0" 1A 00 + u32 7400 → 노드 레코드들 → 13바이트 NULL 레코드 → 푸터 ID(16) → 4 zero → 16 정렬 패딩 → u32 7400 → 120 zero → 푸터 ID2(16).
/// 노드 레코드(7400 = 32비트 오프셋): EndOffset u32, NumProperties u32, PropertyListLen u32, NameLen u8, Name, 속성들, 자식들, (자식이 있거나 속성이 없으면) NULL 레코드.
/// 배열 속성: 타입 + ArrayLength u32 + Encoding u32(0 raw / 1 zlib) + CompressedLength u32 + 데이터.
/// </summary>
public static class FbxBinaryWriter
{
    /// <summary>기록하는 FBX 버전(7.4 = 32비트 오프셋 레코드).</summary>
    public const int Version = 7400;
    /// <summary>NULL 레코드 길이(EndOffset/NumProperties/PropertyListLen u32 3개 + NameLen u8 = 13바이트, 모두 0).</summary>
    private const int SentinelLength = 13;
    /// <summary>파일 시작 매직("Kaydara FBX Binary  " + 0x00 0x1A 0x00, 23바이트).</summary>
    private static readonly byte[] HeadMagic = Encoding.ASCII.GetBytes("Kaydara FBX Binary  ").Concat(new byte[] { 0x00, 0x1A, 0x00 }).ToArray();
    /// <summary>푸터 첫 고정 ID(Blender/FBX SDK가 쓰는 값과 같다).</summary>
    private static readonly byte[] FootId = { 0xfa, 0xbc, 0xab, 0x09, 0xd0, 0xc8, 0xd4, 0x66, 0xb1, 0x76, 0xfb, 0x83, 0x1c, 0xf7, 0x26, 0x7e };
    /// <summary>파일 마지막 고정 ID.</summary>
    private static readonly byte[] FootId2 = { 0xf8, 0x5a, 0x8c, 0x6a, 0xde, 0xf5, 0xd9, 0x7e, 0xec, 0xe9, 0x0c, 0xe3, 0x75, 0x8f, 0x29, 0x0b };
    /// <summary>이 길이 이상인 배열은 zlib으로 압축한다.</summary>
    public static int CompressThreshold = 64;

    /// <summary>
    /// 최상위 노드들을 바이너리 FBX 바이트 배열로 직렬화한다.
    /// 먼저 <see cref="Prepare"/>로 모든 노드의 속성 바이트와 크기를 계산한 뒤(EndOffset을 미리 알아야 함) 순서대로 쓴다.
    /// </summary>
    /// <param name="topLevel">최상위 노드(FBXHeaderExtension, GlobalSettings, Objects, Connections 등).</param>
    /// <param name="compress">true면 <see cref="CompressThreshold"/> 이상 배열을 zlib 압축.</param>
    public static byte[] Write(IEnumerable<FbxNode> topLevel, bool compress = true)
    {
        var prepared = topLevel.Select(n => Prepare(n, compress)).ToList();
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        // 헤더: 매직 + 버전. offset은 다음 노드가 시작하는 파일 내 절대 위치(EndOffset 계산 기준).
        w.Write(HeadMagic);
        w.Write((uint)Version);
        long offset = ms.Position;
        foreach (var p in prepared) { WriteNode(w, p, ref offset); }
        // 최상위 목록의 끝을 알리는 NULL 레코드와 푸터.
        w.Write(new byte[SentinelLength]);
        w.Write(FootId);
        w.Write(new byte[4]);
        // 푸터 패딩: 현재 위치를 16바이트 경계로 맞춘다(이미 경계면 16바이트를 채운다 — Blender와 동일).
        long ofs = ms.Position;
        int pad = (int)(((ofs + 15) & ~15L) - ofs);
        if (pad == 0) pad = 16;
        w.Write(new byte[pad]);
        w.Write((uint)Version);
        w.Write(new byte[120]);
        w.Write(FootId2);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>직렬화한 FBX를 파일로 쓴다.</summary>
    public static void Write(string path, IEnumerable<FbxNode> topLevel, bool compress = true) => File.WriteAllBytes(path, Write(topLevel, compress));

    // ---------------------------------------------------------------- 크기 계산(두 패스: 자식을 먼저 직렬화해 EndOffset을 정확히 적는다)

    /// <summary>노드 하나의 직렬화 준비 결과(이름/속성 바이트, 자식, NULL 레코드 필요 여부).</summary>
    private sealed class Prepared
    {
        /// <summary>UTF-8 노드 이름.</summary>
        public byte[] NameBytes = Array.Empty<byte>();
        /// <summary>모든 속성을 이어 쓴 바이트(PropertyListLen).</summary>
        public byte[] PropBytes = Array.Empty<byte>();
        /// <summary>속성 개수(NumProperties).</summary>
        public int PropCount;
        /// <summary>준비된 자식 노드.</summary>
        public List<Prepared> Children = new();
        /// <summary>노드 끝에 NULL 레코드를 붙일지(자식이 있거나 속성이 없는 노드).</summary>
        public bool Sentinel;
        /// <summary>이 레코드 전체 바이트 수 = 13(헤더) + 이름 + 속성 + 자식들 + NULL 레코드.</summary>
        public long Size => 13 + NameBytes.Length + PropBytes.Length + Children.Sum(c => c.Size) + (Sentinel ? SentinelLength : 0);
    }

    /// <summary>노드를 재귀적으로 준비한다: 이름 길이 검사, 속성을 메모리 스트림에 써서 바이트화, 자식 준비, NULL 레코드 여부 결정.</summary>
    private static Prepared Prepare(FbxNode n, bool compress)
    {
        var p = new Prepared { NameBytes = Encoding.UTF8.GetBytes(n.Name), PropCount = n.Props.Count };
        if (p.NameBytes.Length > 255) throw new InvalidDataException($"FBX node name too long: {n.Name}");
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms))
        {
            foreach (var v in n.Props) WriteProp(w, v, compress);
            w.Flush();
            p.PropBytes = ms.ToArray();
        }
        foreach (var c in n.Children) p.Children.Add(Prepare(c, compress));
        p.Sentinel = n.Children.Count > 0 || n.Props.Count == 0;
        return p;
    }

    /// <summary>
    /// 준비된 노드를 쓴다. EndOffset = 현재 위치 + 전체 크기(절대 위치). 자식을 쓰며 offset을 전진시키고,
    /// 마지막에 계산값과 실제 위치가 같은지 검사한다(어긋나면 파일이 깨지므로 예외).
    /// </summary>
    private static void WriteNode(BinaryWriter w, Prepared p, ref long offset)
    {
        long end = offset + p.Size;
        w.Write((uint)end);
        w.Write((uint)p.PropCount);
        w.Write((uint)p.PropBytes.Length);
        w.Write((byte)p.NameBytes.Length);
        w.Write(p.NameBytes);
        w.Write(p.PropBytes);
        offset += 13 + p.NameBytes.Length + p.PropBytes.Length;
        foreach (var c in p.Children) WriteNode(w, c, ref offset);
        if (p.Sentinel) { w.Write(new byte[SentinelLength]); offset += SentinelLength; }
        if (offset != end) throw new InvalidOperationException("FBX offset bookkeeping mismatch");
    }

    // ---------------------------------------------------------------- 속성

    /// <summary>속성 하나를 타입 코드 + 값으로 쓴다(리틀 엔디언). 문자열/원시 바이트는 길이 u32 + 데이터, 배열은 <see cref="WriteArray"/>.</summary>
    private static void WriteProp(BinaryWriter w, object v, bool compress)
    {
        switch (v)
        {
            case short s: w.Write((byte)'Y'); w.Write(s); break;
            case bool b: w.Write((byte)'C'); w.Write((byte)(b ? 1 : 0)); break;
            case int i: w.Write((byte)'I'); w.Write(i); break;
            case float f: w.Write((byte)'F'); w.Write(f); break;
            case double d: w.Write((byte)'D'); w.Write(d); break;
            case long l: w.Write((byte)'L'); w.Write(l); break;
            case string str:
                {
                    var bytes = Encoding.UTF8.GetBytes(str);
                    w.Write((byte)'S'); w.Write((uint)bytes.Length); w.Write(bytes);
                    break;
                }
            case byte[] raw: w.Write((byte)'R'); w.Write((uint)raw.Length); w.Write(raw); break;
            case float[] fa: WriteArray(w, 'f', fa.Length, Bytes(fa), compress); break;
            case double[] da: WriteArray(w, 'd', da.Length, Bytes(da), compress); break;
            case long[] la: WriteArray(w, 'l', la.Length, Bytes(la), compress); break;
            case int[] ia: WriteArray(w, 'i', ia.Length, Bytes(ia), compress); break;
            case bool[] ba: WriteArray(w, 'b', ba.Length, ba.Select(x => (byte)(x ? 1 : 0)).ToArray(), compress); break;
            default: throw new ArgumentException($"unsupported FBX property {v.GetType().Name}");
        }
    }

    /// <summary>값 타입 배열을 메모리 그대로 바이트 배열로 복사한다(리틀 엔디언 플랫폼 가정).</summary>
    private static byte[] Bytes<T>(T[] arr) where T : unmanaged
    {
        var bytes = new byte[arr.Length * System.Runtime.InteropServices.Marshal.SizeOf<T>()];
        Buffer.BlockCopy(arr, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    /// <summary>배열 속성을 쓴다: 타입 코드, 요소 수, 인코딩(0 = 원본 / 1 = zlib), 데이터 바이트 수, 데이터.</summary>
    private static void WriteArray(BinaryWriter w, char type, int count, byte[] data, bool compress)
    {
        w.Write((byte)type);
        w.Write((uint)count);
        // 충분히 큰 배열만 압축한다(작은 배열은 압축 헤더 오버헤드가 더 크다).
        if (compress && data.Length >= CompressThreshold)
        {
            using var ms = new MemoryStream();
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data, 0, data.Length);
            var packed = ms.ToArray();
            w.Write(1u); w.Write((uint)packed.Length); w.Write(packed);
        }
        else
        {
            w.Write(0u); w.Write((uint)data.Length); w.Write(data);
        }
    }
}
