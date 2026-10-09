using System.IO.Compression;
using System.Text;

namespace Cube.Core.IO.Fbx;

/// <summary>바이너리 FBX(7.1~7.4 32비트 오프셋, 7.5+ 64비트 오프셋) 리더. writer 검증·테스트용(가져오기는 Godot FbxDocument가 한다).</summary>
public static class FbxBinaryReader
/// <summary>바이너리 FBX인지 매직 문자열("Kaydara FBX Binary")로 판별한다.</summary>
{
    public static bool IsBinaryFbx(byte[] data) => data.Length > 27 && Encoding.ASCII.GetString(data, 0, 18) == "Kaydara FBX Binary";
/// <summary>
/// 바이트 배열을 읽어 버전과 최상위 노드 목록을 돌려준다. 버전은 오프셋 23(매직 다음)의 u32.
/// 최상위 NULL 레코드를 만나면 멈추고 푸터는 읽지 않는다.
/// </summary>

    public static (int version, List<FbxNode> nodes) Read(byte[] data)
    {
        if (!IsBinaryFbx(data)) throw new InvalidDataException("not a binary FBX file");
        using var ms = new MemoryStream(data);
        using var r = new BinaryReader(ms);
        ms.Position = 23;
        int version = (int)r.ReadUInt32();
        bool wide = version >= 7500; // 7.5부터 레코드 헤더의 오프셋/개수/길이가 64비트
        var nodes = new List<FbxNode>();
        while (true)
        {
            var n = ReadNode(r, wide);
            if (n == null) break;
            nodes.Add(n);
        }
        return (version, nodes);
    }
/// <summary>파일을 읽어 <see cref="Read(byte[])"/>한다.</summary>

    public static (int version, List<FbxNode> nodes) Read(string path) => Read(File.ReadAllBytes(path));
/// <summary>
/// 노드 레코드 하나를 재귀적으로 읽는다. 모든 필드가 0이면 NULL 레코드(목록 끝)이므로 null.
/// 속성을 읽은 뒤 EndOffset까지 남은 바이트를 자식 레코드로 읽고, 마지막에 위치를 EndOffset으로 맞춘다.
/// </summary>

    private static FbxNode? ReadNode(BinaryReader r, bool wide)
    {
        long end = wide ? (long)r.ReadUInt64() : r.ReadUInt32();
        long propCount = wide ? (long)r.ReadUInt64() : r.ReadUInt32();
        long propLen = wide ? (long)r.ReadUInt64() : r.ReadUInt32();
        int nameLen = r.ReadByte();
        if (end == 0 && propCount == 0 && propLen == 0 && nameLen == 0) return null; // NULL 레코드
        string name = Encoding.UTF8.GetString(r.ReadBytes(nameLen));
        var node = new FbxNode(name);
        for (int i = 0; i < propCount; i++) node.Props.Add(ReadProp(r));
        while (r.BaseStream.Position < end)
        {
            var child = ReadNode(r, wide);
            if (child == null) break;
            node.Children.Add(child);
        }
        r.BaseStream.Position = end;
        return node;
    }
/// <summary>속성 하나를 타입 코드에 따라 읽어 C# 값(short/bool/int/.../배열)으로 돌려준다.</summary>

    private static object ReadProp(BinaryReader r)
    {
        char t = (char)r.ReadByte();
        switch (t)
        {
            case 'Y': return r.ReadInt16();
            case 'C': return r.ReadByte() != 0;
            case 'I': return r.ReadInt32();
            case 'F': return r.ReadSingle();
            case 'D': return r.ReadDouble();
            case 'L': return r.ReadInt64();
            case 'S': { int len = (int)r.ReadUInt32(); return Encoding.UTF8.GetString(r.ReadBytes(len)); }
            case 'R': { int len = (int)r.ReadUInt32(); return r.ReadBytes(len); }
            case 'f': case 'd': case 'l': case 'i': case 'b':
                // 배열: 요소 수, 인코딩(1 = zlib), 저장 바이트 수 순서. 요소 크기는 타입에서 정한다.
                {
                    int count = (int)r.ReadUInt32();
                    uint encoding = r.ReadUInt32();
                    int packedLen = (int)r.ReadUInt32();
                    var raw = r.ReadBytes(packedLen);
                    int elem = t switch { 'f' => 4, 'd' => 8, 'l' => 8, 'i' => 4, _ => 1 };
                    // zlib 압축이면 요소 수 × 요소 크기만큼 풀고 크기가 정확히 맞는지 확인한다.
                    byte[] bytes;
                    if (encoding == 1)
                    {
                        using var src = new MemoryStream(raw);
                        using var z = new ZLibStream(src, CompressionMode.Decompress);
                        bytes = new byte[count * elem];
                        int got = 0; while (got < bytes.Length) { int n = z.Read(bytes, got, bytes.Length - got); if (n <= 0) break; got += n; }
                        if (got != bytes.Length) throw new InvalidDataException("FBX array decompress size mismatch");
                    }
                    // 압축이 아니면 저장된 바이트를 그대로 쓴다.
                    else bytes = raw;
                    // 바이트를 타입 배열로 복사(bool 배열은 바이트당 하나).
                    switch (t)
                    {
                        case 'f': { var a = new float[count]; Buffer.BlockCopy(bytes, 0, a, 0, bytes.Length); return a; }
                        case 'd': { var a = new double[count]; Buffer.BlockCopy(bytes, 0, a, 0, bytes.Length); return a; }
                        case 'l': { var a = new long[count]; Buffer.BlockCopy(bytes, 0, a, 0, bytes.Length); return a; }
                        case 'i': { var a = new int[count]; Buffer.BlockCopy(bytes, 0, a, 0, bytes.Length); return a; }
                        default: return bytes.Select(b => b != 0).ToArray();
                    }
                }
            default: throw new InvalidDataException($"unknown FBX property type '{t}'");
        }
    }
}
