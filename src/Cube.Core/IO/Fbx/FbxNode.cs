using System.Text;

namespace Cube.Core.IO.Fbx;

/// <summary>
/// FBX 노드 레코드(이름 + 속성 목록 + 자식). 속성 값 타입은 바이너리 FBX의 타입 코드와 1:1로 대응한다:
/// short(Y) bool(C) int(I) float(F) double(D) long(L) string(S) byte[](R) float[](f) double[](d) long[](l) int[](i) bool[](b).
/// </summary>
/// <remarks>
/// <see cref="FbxSceneBuilder"/>가 이 트리를 만들고 <see cref="FbxBinaryWriter"/>가 바이트로 쓰며,
/// <see cref="FbxBinaryReader"/>는 읽은 결과를 같은 구조로 돌려준다(테스트에서 왕복 검증).
/// </remarks>
public sealed class FbxNode
{
    /// <summary>노드 이름(예: "Objects", "Model", "Vertices"). UTF-8로 255바이트 이하여야 한다.</summary>
    public string Name { get; }
    /// <summary>속성 값 목록(순서가 의미를 가진다). 허용 타입은 <see cref="Check"/>가 검사한다.</summary>
    public readonly List<object> Props = new();
    /// <summary>자식 노드 목록.</summary>
    public readonly List<FbxNode> Children = new();

    /// <summary>이름과 속성으로 노드를 만든다. 지원하지 않는 타입의 속성이면 즉시 예외.</summary>
    public FbxNode(string name, params object[] props)
    {
        Name = name;
        foreach (var p in props) Props.Add(Check(p));
    }

    /// <summary>자식 노드를 추가하고 그 자식을 돌려준다(체이닝용).</summary>
    public FbxNode Add(string name, params object[] props)
    {
        var n = new FbxNode(name, props);
        Children.Add(n);
        return n;
    }

    /// <summary>이미 만든 자식 노드를 붙이고 그대로 돌려준다.</summary>
    public FbxNode Add(FbxNode child) { Children.Add(child); return child; }

    /// <summary>이름이 같은 첫 자식(없으면 null). 리더 결과 탐색/테스트용.</summary>
    public FbxNode? Child(string name) => Children.FirstOrDefault(c => c.Name == name);
    /// <summary>이름이 같은 모든 자식.</summary>
    public IEnumerable<FbxNode> All(string name) => Children.Where(c => c.Name == name);

    /// <summary>i번째 속성을 T로 캐스팅해 꺼낸다(타입이 다르면 InvalidCastException).</summary>
    public T Prop<T>(int i) => (T)Props[i];

    /// <summary>속성 값이 바이너리 FBX가 표현할 수 있는 타입인지 검사하고 그대로 돌려준다.</summary>
    private static object Check(object p) => p switch
    {
        short or bool or int or float or double or long or string or byte[] or float[] or double[] or long[] or int[] or bool[] => p,
        _ => throw new ArgumentException($"unsupported FBX property type {p.GetType().Name}"),
    };

    /// <summary>"Class::Name" 식별 문자열의 바이너리 표현: 이름 + \0\x01 + 클래스(파일에는 이 순서로 저장된다).</summary>
    public static string Id(string cls, string name) => name + "\0\u0001" + cls;

    /// <summary>바이너리 표현을 사람이 읽는 "Class::Name"으로.</summary>
    public static string ReadableId(string s)
    {
        int i = s.IndexOf("\0\u0001", StringComparison.Ordinal);
        return i < 0 ? s : s[(i + 2)..] + "::" + s[..i];
    }

    /// <summary>디버그 표시: "이름: 속성..." (문자열은 읽기 쉬운 식별자, 배열은 [길이]).</summary>
    public override string ToString()
    {
        var sb = new StringBuilder(Name).Append(':');
        foreach (var p in Props) sb.Append(' ').Append(p is string s ? '"' + ReadableId(s) + '"' : p is Array a ? $"[{a.Length}]" : p);
        return sb.ToString();
    }
}
