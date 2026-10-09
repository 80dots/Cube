using System.Numerics;
using Cube.Core.Mesh;
using Cube.Core.Scene;

namespace Cube.Core.IO.Fbx;

/// <summary>FBX 내보내기 옵션. 단위는 cm(UnitScaleFactor 1)로 쓰고 정점·이동·행렬 이동 성분에 UnitScale(기본 100)을 곱한다(Blender/Maya 방식).</summary>
/// <param name="BakePivots">true(기본)면 Rotation/ScalingPivot 속성 대신 피벗을 지오메트리에 베이크한다(정점 −P, Lcl Translation = P+T, 자식 Translation −P). 가져오는 앱(Blender origin, Unity/Godot)의 원점이 Cube 피벗과 일치한다.</param>
/// <param name="ExportAnimations">true(기본)면 Document.Animations의 클립마다 AnimationStack/Layer/CurveNode/Curve와 Take를 쓴다(FbxSceneBuilder.Animation.cs).</param>
/// <param name="UnitScale">m → 파일 단위 배율(기본 100 = cm). 정점·Lcl Translation·피벗·행렬 이동 성분·라이트 범위에 곱한다.</param>
/// <param name="Compress">큰 배열 속성을 zlib으로 압축할지.</param>
/// <param name="Creator">헤더 Creator 문자열.</param>
/// <param name="EmbedLights">라이트 노드를 NodeAttribute Light로 기록할지(false면 Null 모델만).</param>
/// <param name="BaseDir">FBX 파일이 놓일 폴더. 텍스처 RelativeFilename 계산 기준(null이면 파일 이름만).</param>
public sealed record FbxExportOptions(float UnitScale = 100f, bool Compress = true, string Creator = "Cube FBX writer", bool EmbedLights = true, string? BaseDir = null, bool BakePivots = true, bool ExportAnimations = true)
{
    /// <summary>기본 옵션(cm, 압축, 라이트 포함, 피벗 베이크, 애니메이션 포함).</summary>
    public static readonly FbxExportOptions Default = new();
}

/// <summary>
/// Document → FBX 노드 트리. 축 변환은 없다(내부 = Maya와 같은 오른손 Y-up; FBX GlobalSettings도 Y-up/Z-front/X-coord).
/// 메시(Geometry: Vertices/PolygonVertexIndex/LayerElementNormal·UV ByPolygonVertex/LayerElementMaterial), Model(Lcl T/R/S + Rotation/ScalingPivot),
/// Material(Phong + DiffuseColor 텍스처 Texture/Video), 조인트(Model LimbNode + NodeAttribute Skeleton), 라이트(NodeAttribute Light),
/// 스킨(Deformer Skin/Cluster + Pose BindPose; 바인드 포즈 = 내보내는 시점의 현재 포즈, glTF와 동일).
/// </summary>
// 생성 흐름: Build → (BuildNode 재귀 → BuildGeometry/MaterialId/TextureId) → BuildSkin → BuildAnimations
//   → 헤더/설정/Definitions(객체 타입별 개수와 템플릿) → Objects → Connections → Takes.
// FBX 객체는 모두 고유 64비트 ID를 가지며 부모-자식/속성 연결은 Connections 섹션의 "C" 레코드로 표현한다
// (OO = 객체→객체, OP = 객체→속성). 루트 노드 ID는 0이다.
public sealed partial class FbxSceneBuilder
{
    /// <summary>내보낼 문서(머티리얼/조인트 조회용).</summary>
    private readonly Document _doc;
    /// <summary>내보내기 옵션.</summary>
    private readonly FbxExportOptions _opt;
    /// <summary>다음에 배정할 FBX 객체 ID(1,000,000부터 증가; 0은 루트라 피한다).</summary>
    private long _nextId = 1_000_000;
    /// <summary>Objects 섹션에 들어갈 객체 노드(Model, Geometry, Material, Deformer, Pose, Animation* 등) — 만든 순서대로.</summary>
    private readonly List<FbxNode> _objects = new();
    /// <summary>Connections 섹션에 쓸 연결(자식 ID, 부모 ID, 속성 이름; 속성이 null이면 OO, 아니면 OP 연결).</summary>
    private readonly List<(long child, long parent, string? prop)> _connections = new();
    /// <summary>씬 노드 → FBX Model ID.</summary>
    private readonly Dictionary<SceneNode, long> _modelIds = new();
    /// <summary>문서 머티리얼 ID → FBX Material ID(같은 머티리얼은 한 번만 만든다).</summary>
    private readonly Dictionary<int, long> _materialIds = new();
    /// <summary>객체 타입별 생성 개수(Definitions 섹션의 ObjectType Count).</summary>
    private readonly Dictionary<string, int> _typeCounts = new();
    /// <summary>내보낸 모델(노드) 수. 결과 메시지용.</summary>
    public int NodeCount { get; private set; }
    /// <summary>내보낸 삼각형 수(n각형은 n-2개로 셈). 결과 메시지용.</summary>
    public int TriangleCount { get; private set; }

    /// <summary>빌더를 만든다. 한 인스턴스는 한 번의 <see cref="Build"/>에만 쓴다(ID·목록이 누적되므로).</summary>
    public FbxSceneBuilder(Document doc, FbxExportOptions? options = null) { _doc = doc; _opt = options ?? FbxExportOptions.Default; }

    /// <summary>새 객체 ID를 배정하고 그 타입의 개수를 1 늘린다.</summary>
    private long NewId(string type) { _typeCounts[type] = _typeCounts.GetValueOrDefault(type) + 1; return _nextId++; }
    /// <summary>단위 배율(<see cref="FbxExportOptions.UnitScale"/>) 단축 속성.</summary>
    private float S => _opt.UnitScale;

    /// <summary>roots와 그 하위 전체를 내보낸다. 스킨이 참조하는 조인트가 roots 밖이면 그 조인트 체인의 루트부터 함께 내보낸다.</summary>
    /// <returns>파일 최상위 노드 목록(<see cref="FbxBinaryWriter.Write(IEnumerable{FbxNode}, bool)"/>에 넘긴다).</returns>
    public List<FbxNode> Build(IReadOnlyList<SceneNode> roots)
    {
        // 1단계: 내보낼 최상위 노드와 그 하위 전체 집합(inSet)을 만든다. 문서 루트 자체는 제외.
        // 조상이 함께 넘어온 노드는 빼고 최상위만 남긴다(부모와 자식을 함께 선택해 내보내면 자식 모델이 두 번 나가던 문제).
        var given = new HashSet<SceneNode>(roots);
        bool AncestorGiven(SceneNode n) { for (var p = n.Parent; p != null && !p.IsRoot; p = p.Parent) if (given.Contains(p)) return true; return false; }
        var tops = new List<SceneNode>(roots.Where(r => !r.IsRoot && !AncestorGiven(r)).Distinct());
        var inSet = new HashSet<SceneNode>();
        foreach (var r in tops) { inSet.Add(r); foreach (var d in r.Descendants()) inSet.Add(d); }
        // 스킨이 참조하는 조인트가 집합 밖이면, 집합에 없는 조인트 부모를 따라 올라가 체인 루트를 찾아 그 하위 트리를 추가한다
        // (선택 내보내기에서 메시만 골라도 스켈레톤이 함께 나가도록).
        foreach (var n in inSet.ToList())
        {
            if (n.Skin == null) continue;
            foreach (var jid in n.Skin.Joints)
            {
                var j = _doc.Find(jid);
                if (j == null || inSet.Contains(j)) continue;
                var chainRoot = j; while (chainRoot.Parent != null && !chainRoot.Parent.IsRoot && chainRoot.Parent.IsJoint && !inSet.Contains(chainRoot.Parent)) chainRoot = chainRoot.Parent;
                if (inSet.Contains(chainRoot)) continue;
                tops.Add(chainRoot); inSet.Add(chainRoot); foreach (var d in chainRoot.Descendants()) inSet.Add(d);
            }
        }

        // 객체: 모델 트리(부모가 내보내기 대상이 아니면 월드 베이크)
        foreach (var t in tops) BuildNode(t, 0, bakeWorld: t.Parent != null && !t.Parent.IsRoot, parentShift: Vector3.Zero);
        // 스킨(모든 모델 ID가 정해진 뒤)
        foreach (var n in inSet) if (n.Skin != null && n.Mesh != null && _geometryIds.TryGetValue(n, out long geomId)) BuildSkin(n, geomId);
        // 애니메이션(모델 ID와 베이크 피벗이 정해진 뒤)
        if (_opt.ExportAnimations) BuildAnimations();

        // 2단계: 파일 최상위 섹션을 FBX SDK와 같은 순서로 구성한다. Definitions는 객체 생성 후에 만들어야 개수가 정확하다.
        var top = new List<FbxNode>
        {
            HeaderExtension(),
            new FbxNode("FileId", new byte[] { 0x28, 0xb3, 0x2a, 0xeb, 0xb6, 0x24, 0xcc, 0xc2, 0xbf, 0xc8, 0xb0, 0x2a, 0xa9, 0x2b, 0xfc, 0xf1 }),
            new FbxNode("CreationTime", "1970-01-01 10:00:00:000"),
            new FbxNode("Creator", _opt.Creator),
            GlobalSettings(),
            Documents(),
            new FbxNode("References"),
            Definitions(),
        };
        var objects = new FbxNode("Objects");
        foreach (var o in _objects) objects.Add(o);
        top.Add(objects);
        // Connections: 모든 연결을 "C" 레코드로 기록.
        var conns = new FbxNode("Connections");
        foreach (var (child, parent, prop) in _connections)
        {
            if (prop == null) conns.Add("C", "OO", child, parent);
            else conns.Add("C", "OP", child, parent, prop);
        }
        top.Add(conns);
        top.Add(Takes());
        return top;
    }

    // ---------------------------------------------------------------- 헤더/설정

    /// <summary>
    /// FBXHeaderExtension 섹션: 헤더/FBX 버전, 생성 시각, Creator, SceneInfo(GlobalInfo 메타데이터와 Original/LastSaved 애플리케이션 정보).
    /// </summary>
    private FbxNode HeaderExtension()
    {
        var now = DateTime.Now;
        var h = new FbxNode("FBXHeaderExtension");
        h.Add("FBXHeaderVersion", 1003);
        h.Add("FBXVersion", FbxBinaryWriter.Version);
        h.Add("EncryptionType", 0);
        var ts = h.Add("CreationTimeStamp");
        ts.Add("Version", 1000); ts.Add("Year", now.Year); ts.Add("Month", now.Month); ts.Add("Day", now.Day);
        ts.Add("Hour", now.Hour); ts.Add("Minute", now.Minute); ts.Add("Second", now.Second); ts.Add("Millisecond", now.Millisecond);
        h.Add("Creator", _opt.Creator);
        var info = h.Add("SceneInfo", FbxNode.Id("SceneInfo", "GlobalInfo"), "UserData");
        info.Add("Type", "UserData"); info.Add("Version", 100);
        var meta = info.Add("MetaData");
        meta.Add("Version", 100); meta.Add("Title", ""); meta.Add("Subject", ""); meta.Add("Author", ""); meta.Add("Keywords", ""); meta.Add("Revision", ""); meta.Add("Comment", "");
        var p = info.Add("Properties70");
        p.Add("P", "DocumentUrl", "KString", "Url", "", "");
        p.Add("P", "SrcDocumentUrl", "KString", "Url", "", "");
        p.Add("P", "Original", "Compound", "", "");
        p.Add("P", "Original|ApplicationVendor", "KString", "", "", "80dots");
        p.Add("P", "Original|ApplicationName", "KString", "", "", "Cube");
        p.Add("P", "Original|ApplicationVersion", "KString", "", "", "1.0");
        p.Add("P", "Original|DateTime_GMT", "DateTime", "", "", now.ToUniversalTime().ToString("dd/MM/yyyy HH:mm:ss.fff"));
        p.Add("P", "Original|FileName", "KString", "", "", "");
        p.Add("P", "LastSaved", "Compound", "", "");
        p.Add("P", "LastSaved|ApplicationVendor", "KString", "", "", "80dots");
        p.Add("P", "LastSaved|ApplicationName", "KString", "", "", "Cube");
        p.Add("P", "LastSaved|ApplicationVersion", "KString", "", "", "1.0");
        p.Add("P", "LastSaved|DateTime_GMT", "DateTime", "", "", now.ToUniversalTime().ToString("dd/MM/yyyy HH:mm:ss.fff"));
        return h;
    }

    /// <summary>
    /// GlobalSettings 섹션: 축(Up = Y, Front = +Z, Coord = +X; 오른손 Y-up), UnitScaleFactor 1(= cm 파일),
    /// 그리고 애니메이션 시간 설정(TimeMode/TimeSpan/CustomFrameRate; <c>TimeSettings()</c>가 내보내는 클립에서 계산).
    /// </summary>
    private FbxNode GlobalSettings()
    {
        var g = new FbxNode("GlobalSettings");
        g.Add("Version", 1000);
        var p = g.Add("Properties70");
        p.Add("P", "UpAxis", "int", "Integer", "", 1);
        p.Add("P", "UpAxisSign", "int", "Integer", "", 1);
        p.Add("P", "FrontAxis", "int", "Integer", "", 2);
        p.Add("P", "FrontAxisSign", "int", "Integer", "", 1);
        p.Add("P", "CoordAxis", "int", "Integer", "", 0);
        p.Add("P", "CoordAxisSign", "int", "Integer", "", 1);
        p.Add("P", "OriginalUpAxis", "int", "Integer", "", 1);
        p.Add("P", "OriginalUpAxisSign", "int", "Integer", "", 1);
        p.Add("P", "UnitScaleFactor", "double", "Number", "", 1.0);
        p.Add("P", "OriginalUnitScaleFactor", "double", "Number", "", 1.0);
        p.Add("P", "AmbientColor", "ColorRGB", "Color", "", 0.0, 0.0, 0.0);
        p.Add("P", "DefaultCamera", "KString", "", "", "Producer Perspective");
        var (timeMode, customRate, spanStart, spanStop) = TimeSettings();
        p.Add("P", "TimeMode", "enum", "", "", timeMode);
        p.Add("P", "TimeSpanStart", "KTime", "Time", "", spanStart);
        p.Add("P", "TimeSpanStop", "KTime", "Time", "", spanStop);
        p.Add("P", "CustomFrameRate", "double", "Number", "", customRate);
        return g;
    }

    /// <summary>Documents 섹션: 씬 문서 하나(RootNode 0)와 활성 애니메이션 스택 이름(첫 클립).</summary>
    private FbxNode Documents()
    {
        var d = new FbxNode("Documents");
        d.Add("Count", 1);
        var doc = d.Add("Document", _nextId++, "Scene", "Scene");
        var p = doc.Add("Properties70");
        p.Add("P", "SourceObject", "object", "", "");
        p.Add("P", "ActiveAnimStackName", "KString", "", "", ExportedClips.Count > 0 ? ExportedClips[0].Name : "");
        doc.Add("RootNode", 0L);
        return d;
    }

    /// <summary>
    /// Definitions 섹션: GlobalSettings 1개 + 실제로 만든 객체 타입별 개수, 그리고 타입별 PropertyTemplate(기본 속성값).
    /// 템플릿은 가져오는 쪽이 생략된 속성의 기본값으로 쓴다(FBX SDK/Blender 출력과 같은 값).
    /// </summary>
    private FbxNode Definitions()
    {
        var d = new FbxNode("Definitions");
        d.Add("Version", 100);
        d.Add("Count", 1 + _typeCounts.Values.Sum());
        d.Add("ObjectType", "GlobalSettings").Add("Count", 1);
        // 타입 이름 순으로 정렬해 출력을 결정적으로 만든다.
        foreach (var (type, count) in _typeCounts.OrderBy(kv => kv.Key))
        {
            var ot = d.Add("ObjectType", type);
            ot.Add("Count", count);
            switch (type)
            {
                case "Model":
                    {
                        var p = ot.Add("PropertyTemplate", "FbxNode").Add("Properties70");
                        p.Add("P", "QuaternionInterpolate", "enum", "", "", 0);
                        p.Add("P", "RotationOffset", "Vector3D", "Vector", "", 0.0, 0.0, 0.0);
                        p.Add("P", "RotationPivot", "Vector3D", "Vector", "", 0.0, 0.0, 0.0);
                        p.Add("P", "ScalingOffset", "Vector3D", "Vector", "", 0.0, 0.0, 0.0);
                        p.Add("P", "ScalingPivot", "Vector3D", "Vector", "", 0.0, 0.0, 0.0);
                        p.Add("P", "TranslationActive", "bool", "", "", 0);
                        p.Add("P", "RotationOrder", "enum", "", "", 0);
                        p.Add("P", "RotationActive", "bool", "", "", 0);
                        p.Add("P", "InheritType", "enum", "", "", 0);
                        p.Add("P", "ScalingActive", "bool", "", "", 0);
                        p.Add("P", "Visibility", "Visibility", "", "A", 1.0);
                        p.Add("P", "Lcl Translation", "Lcl Translation", "", "A", 0.0, 0.0, 0.0);
                        p.Add("P", "Lcl Rotation", "Lcl Rotation", "", "A", 0.0, 0.0, 0.0);
                        p.Add("P", "Lcl Scaling", "Lcl Scaling", "", "A", 1.0, 1.0, 1.0);
                        p.Add("P", "DefaultAttributeIndex", "int", "Integer", "", -1);
                        break;
                    }
                case "Geometry":
                    {
                        var p = ot.Add("PropertyTemplate", "FbxMesh").Add("Properties70");
                        p.Add("P", "Color", "ColorRGB", "Color", "", 0.8, 0.8, 0.8);
                        p.Add("P", "BBoxMin", "Vector3D", "Vector", "", 0.0, 0.0, 0.0);
                        p.Add("P", "BBoxMax", "Vector3D", "Vector", "", 0.0, 0.0, 0.0);
                        p.Add("P", "Primary Visibility", "bool", "", "", 1);
                        p.Add("P", "Casts Shadows", "bool", "", "", 1);
                        p.Add("P", "Receive Shadows", "bool", "", "", 1);
                        break;
                    }
                case "Material":
                    {
                        var p = ot.Add("PropertyTemplate", "FbxSurfacePhong").Add("Properties70");
                        p.Add("P", "ShadingModel", "KString", "", "", "Phong");
                        p.Add("P", "MultiLayer", "bool", "", "", 0);
                        p.Add("P", "EmissiveColor", "Color", "", "A", 0.0, 0.0, 0.0);
                        p.Add("P", "EmissiveFactor", "Number", "", "A", 1.0);
                        p.Add("P", "AmbientColor", "Color", "", "A", 0.2, 0.2, 0.2);
                        p.Add("P", "AmbientFactor", "Number", "", "A", 1.0);
                        p.Add("P", "DiffuseColor", "Color", "", "A", 0.8, 0.8, 0.8);
                        p.Add("P", "DiffuseFactor", "Number", "", "A", 1.0);
                        p.Add("P", "TransparentColor", "Color", "", "A", 0.0, 0.0, 0.0);
                        p.Add("P", "TransparencyFactor", "Number", "", "A", 0.0);
                        p.Add("P", "Opacity", "Number", "", "A", 1.0);
                        p.Add("P", "SpecularColor", "Color", "", "A", 0.2, 0.2, 0.2);
                        p.Add("P", "SpecularFactor", "Number", "", "A", 1.0);
                        p.Add("P", "ShininessExponent", "Number", "", "A", 20.0);
                        p.Add("P", "ReflectionColor", "Color", "", "A", 0.0, 0.0, 0.0);
                        p.Add("P", "ReflectionFactor", "Number", "", "A", 1.0);
                        break;
                    }
                case "Texture":
                    {
                        var p = ot.Add("PropertyTemplate", "FbxFileTexture").Add("Properties70");
                        p.Add("P", "TextureTypeUse", "enum", "", "", 0);
                        p.Add("P", "Texture alpha", "Number", "", "A", 1.0);
                        p.Add("P", "CurrentMappingType", "enum", "", "", 0);
                        p.Add("P", "WrapModeU", "enum", "", "", 0);
                        p.Add("P", "WrapModeV", "enum", "", "", 0);
                        p.Add("P", "UVSwap", "bool", "", "", 0);
                        p.Add("P", "PremultiplyAlpha", "bool", "", "", 1);
                        p.Add("P", "Translation", "Vector", "", "A", 0.0, 0.0, 0.0);
                        p.Add("P", "Rotation", "Vector", "", "A", 0.0, 0.0, 0.0);
                        p.Add("P", "Scaling", "Vector", "", "A", 1.0, 1.0, 1.0);
                        p.Add("P", "TextureRotationPivot", "Vector3D", "Vector", "", 0.0, 0.0, 0.0);
                        p.Add("P", "TextureScalingPivot", "Vector3D", "Vector", "", 0.0, 0.0, 0.0);
                        p.Add("P", "CurrentTextureBlendMode", "enum", "", "", 1);
                        p.Add("P", "UVSet", "KString", "", "", "default");
                        p.Add("P", "UseMaterial", "bool", "", "", 0);
                        p.Add("P", "UseMipMap", "bool", "", "", 0);
                        break;
                    }
                case "AnimationStack":
                case "AnimationLayer":
                case "AnimationCurveNode":
                    AnimationTemplate(type, ot);
                    break;
                case "Video":
                    {
                        var p = ot.Add("PropertyTemplate", "FbxVideo").Add("Properties70");
                        p.Add("P", "Path", "KString", "XRefUrl", "", "");
                        p.Add("P", "RelPath", "KString", "XRefUrl", "", "");
                        break;
                    }
            }
        }
        return d;
    }

    // ---------------------------------------------------------------- 노드

    /// <summary>메시 노드 → FBX Geometry ID(스킨 Deformer를 연결할 대상).</summary>
    private readonly Dictionary<SceneNode, long> _geometryIds = new();
    /// <summary>BakePivots일 때 노드마다 지오메트리/자식에 적용한 피벗 오프셋(오브젝트 공간, m).</summary>
    private readonly Dictionary<SceneNode, Vector3> _bakedPivot = new();

    /// <summary>
    /// 씬 노드 하나를 FBX Model로 만들고 재귀적으로 자식을 처리한다.
    /// 모델 타입: 조인트 = LimbNode, 메시 = Mesh, 라이트 = Light, 그 외 = Null.
    /// 셰이프에 따라 NodeAttribute(조인트/라이트) 또는 Geometry + Material을 만들어 Model에 연결한다.
    /// </summary>
    /// <param name="n">내보낼 노드.</param>
    /// <param name="parentId">부모 Model ID(최상위는 0 = 루트).</param>
    /// <param name="bakeWorld">부모가 내보내기 대상이 아니면 true — 로컬 대신 월드 행렬을 TRS로 분해해 쓴다.</param>
    /// <param name="parentShift">부모가 지오메트리/자식에 베이크한 피벗 오프셋(자식 Translation에서 뺀다).</param>
    private void BuildNode(SceneNode n, long parentId, bool bakeWorld, Vector3 parentShift)
    {
        NodeCount++;
        string type = n.IsJoint ? "LimbNode" : n.Mesh != null ? "Mesh" : n.IsLight ? "Light" : "Null";
        long id = NewId("Model");
        _modelIds[n] = id;
        if (bakeWorld) _bakedWorldNodes.Add(n);
        var model = new FbxNode("Model", id, FbxNode.Id("Model", n.Name), type);
        model.Add("Version", 232);
        // 월드 베이크 노드는 월드 행렬을 피벗 유지 분해, 아니면 로컬 TRS 그대로.
        var t = bakeWorld ? Transform3.FromMatrix(n.WorldMatrix, n.Local.Pivot) : n.Local;
        var p = model.Add("Properties70");
        var shift = Vector3.Zero; // 이 노드의 지오메트리와 자식에 적용할 −피벗 오프셋
        if (_opt.BakePivots)
        {
            // M = T(−P)·S·R·T(P+T) 를 지오메트리에 T(−P)를 흡수시켜 S·R·T(P+T)로 쓴다. 부모가 베이크한 P_parent만큼 이 노드의 Translation을 당긴다.
            shift = t.Pivot;
            t.Translation = t.Translation + t.Pivot - parentShift;
            t.Pivot = Vector3.Zero;
            _bakedPivot[n] = shift;
            _parentShiftOf[n] = parentShift;
        }
        // 베이크하지 않을 때는 FBX의 RotationPivot/ScalingPivot 속성으로 피벗을 표현한다(cm 단위).
        else if (t.Pivot != Vector3.Zero)
        {
            p.Add("P", "RotationPivot", "Vector3D", "Vector", "", (double)(t.Pivot.X * S), (double)(t.Pivot.Y * S), (double)(t.Pivot.Z * S));
            p.Add("P", "ScalingPivot", "Vector3D", "Vector", "", (double)(t.Pivot.X * S), (double)(t.Pivot.Y * S), (double)(t.Pivot.Z * S));
        }
        // 라이트: FBX(SDK·ufbx·Blender) 라이트는 노드 −Y를 비추고 Cube/Godot 라이트는 −Z를 비추므로 회전 앞에 X+90°를 끼워 넣는다.
        // (전에는 그대로 써서 Godot/Blender로 가져오면 라이트가 90° 틀어졌다.) 자식은 아래에서 월드를 베이크해 이 보정을 물려받지 않게 한다.
        bool lightCorr = IsCorrectedLight(n);
        if (lightCorr) t.RotationDegrees = Transform3.QuaternionToEulerXYZDegrees(LightCorrected(t.Rotation));
        // 표준 모델 속성: 회전 활성, InheritType 1(RSrs; Maya 기본 상속), Lcl T(cm)/R(XYZ 오일러, 도)/S.
        p.Add("P", "RotationActive", "bool", "", "", 1);
        p.Add("P", "InheritType", "enum", "", "", 1);
        p.Add("P", "ScalingMax", "Vector3D", "Vector", "", 0.0, 0.0, 0.0);
        p.Add("P", "DefaultAttributeIndex", "int", "Integer", "", 0);
        p.Add("P", "Lcl Translation", "Lcl Translation", "", "A", (double)(t.Translation.X * S), (double)(t.Translation.Y * S), (double)(t.Translation.Z * S));
        p.Add("P", "Lcl Rotation", "Lcl Rotation", "", "A", (double)t.RotationDegrees.X, (double)t.RotationDegrees.Y, (double)t.RotationDegrees.Z);
        p.Add("P", "Lcl Scaling", "Lcl Scaling", "", "A", (double)t.Scale.X, (double)t.Scale.Y, (double)t.Scale.Z);
        if (!n.Visible) p.Add("P", "Visibility", "Visibility", "", "A", 0.0);
        // 모델 끝 레코드 + 부모 모델과의 OO 연결.
        model.Add("MultiLayer", 0);
        model.Add("MultiTake", 0);
        model.Add("Shading", true);
        model.Add("Culling", "CullingOff");
        _objects.Add(model);
        _connections.Add((id, parentId, null));

        if (n.IsJoint)
        {
            // 조인트: Skeleton 타입의 LimbNode NodeAttribute(Size = 조인트 반지름 × 단위).
            long aid = NewId("NodeAttribute");
            var attr = new FbxNode("NodeAttribute", aid, FbxNode.Id("NodeAttribute", n.Name), "LimbNode");
            var ap = attr.Add("Properties70");
            ap.Add("P", "Size", "double", "Number", "", (double)(n.Joint!.Radius * S));
            attr.Add("TypeFlags", "Skeleton");
            _objects.Add(attr);
            _connections.Add((aid, id, null));
        }
        else if (n.Mesh != null)
        {
            // 메시: Geometry를 만들어 연결하고 머티리얼(없으면 기본 lambert1)도 모델에 연결한다.
            long gid = BuildGeometry(n);
            _geometryIds[n] = gid;
            _connections.Add((gid, id, null));
            long mid = MaterialId(n.MaterialId);
            _connections.Add((mid, id, null));
        }
        else if (n.IsLight && _opt.EmbedLights)
        {
            // 라이트: LightType(0 Point/1 Directional/2 Spot), 색, 세기(×100 = FBX 관례 %), 감쇠 범위(cm), 스폿 내/외 각.
            long aid = NewId("NodeAttribute");
            var l = n.Light!;
            var attr = new FbxNode("NodeAttribute", aid, FbxNode.Id("NodeAttribute", n.Name), "Light");
            attr.Add("TypeFlags", "Light");
            attr.Add("GeometryVersion", 124);
            var ap = attr.Add("Properties70");
            ap.Add("P", "LightType", "enum", "", "", l.Type switch { LightType.Directional => 1, LightType.Spot => 2, _ => 0 });
            ap.Add("P", "Color", "Color", "", "A", Lin(l.Color.X), Lin(l.Color.Y), Lin(l.Color.Z));
            ap.Add("P", "Intensity", "Number", "", "A", (double)(l.Intensity * 100f));
            ap.Add("P", "CastShadows", "bool", "", "", 0);
            if (l.Type != LightType.Directional)
            {
                ap.Add("P", "DecayType", "enum", "", "", 0);
                ap.Add("P", "EnableFarAttenuation", "bool", "", "", 1);
                ap.Add("P", "FarAttenuationStart", "Number", "", "A", 0.0);
                ap.Add("P", "FarAttenuationEnd", "Number", "", "A", (double)(l.Range * S));
            }
            if (l.Type == LightType.Spot)
            {
                ap.Add("P", "InnerAngle", "Number", "", "A", (double)(l.SpotAngle * 0.8f));
                ap.Add("P", "OuterAngle", "Number", "", "A", (double)l.SpotAngle);
            }
            _objects.Add(attr);
            _connections.Add((aid, id, null));
        }

        // 자식 재귀. 자식은 항상 로컬 기준이며 이 노드가 베이크한 피벗(shift)을 넘긴다.
        // 보정 회전이 들어간 라이트의 자식은 그 회전을 물려받지 않도록 루트에 월드를 베이크해 붙인다(계층은 잃지만 위치는 정확).
        foreach (var c in n.Children)
            if (lightCorr) BuildNode(c, 0, bakeWorld: true, parentShift: Vector3.Zero);
            else BuildNode(c, id, bakeWorld: false, parentShift: shift);
    }

    /// <summary>FBX 라이트 방향 보정(−Z → −Y)을 적용해 쓰는 라이트 노드인지.</summary>
    private bool IsCorrectedLight(SceneNode n) => n.IsLight && !n.IsJoint && n.Mesh == null && _opt.EmbedLights;

    /// <summary>회전 q 앞에 X+90°를 적용한 회전(행벡터 규약: 로컬 −Y가 원래 −Z 방향을 향함).</summary>
    private static Quaternion LightCorrected(Quaternion q)
        => Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(Matrix4x4.CreateRotationX(MathF.PI / 2f) * Matrix4x4.CreateFromQuaternion(q)));

    // ---------------------------------------------------------------- 지오메트리

    /// <summary>메시 노드 → (메시 정점 슬롯 → FBX 정점 인덱스, 죽은 정점 = -1). 스킨 Cluster Indexes 변환에 쓴다.</summary>
    private readonly Dictionary<SceneNode, int[]> _vertexRemap = new();

    /// <summary>
    /// 메시 노드의 Geometry 객체를 만든다.
    /// Vertices = 살아 있는 정점(−베이크 피벗, ×단위), PolygonVertexIndex = 면 코너 정점 인덱스(면의 마지막 코너는 비트 반전 ~i로 면 끝 표시, CCW 그대로),
    /// LayerElementNormal = 코너 노멀(ByPolygonVertex/Direct), LayerElementUV "map1" = 코너 UV(ByPolygonVertex/IndexToDirect, 같은 UV 값 공유, 하단 원점 그대로),
    /// LayerElementMaterial = AllSame(노드당 머티리얼 하나), Layer 0에 세 요소를 등록.
    /// </summary>
    /// <returns>Geometry 객체 ID.</returns>
    private long BuildGeometry(SceneNode n)
    {
        var mesh = n.Mesh!;
        long gid = NewId("Geometry");
        var g = new FbxNode("Geometry", gid, FbxNode.Id("Geometry", n.Name), "Mesh");
        g.Add("Properties70");
        g.Add("GeometryVersion", 124);

        // 살아 있는 정점만 압축 인덱스로
        var remap = new int[mesh.VertexCount];
        var verts = new List<double>();
        int vi = 0;
        for (int v = 0; v < mesh.VertexCount; v++)
        {
            if (!mesh.Verts[v].Alive) { remap[v] = -1; continue; }
            remap[v] = vi++;
            var pos = mesh.Verts[v].Position - (_bakedPivot.TryGetValue(n, out var bp) ? bp : Vector3.Zero);
            verts.Add(pos.X * S); verts.Add(pos.Y * S); verts.Add(pos.Z * S);
        }
        _vertexRemap[n] = remap;
        g.Add("Vertices", verts.ToArray());

        // 면 코너 순회용 버퍼: poly = 정점 인덱스(끝 코너 ~idx), normals = 코너 노멀, uvs/uvIndex = 고유 UV 값과 코너별 인덱스.
        var poly = new List<int>();
        var normals = new List<double>();
        var uvs = new List<double>();
        var uvIndex = new List<int>();
        var uvDict = new Dictionary<(float, float), int>();
        var hes = new List<int>();
        int faces = 0;
        for (int f = 0; f < mesh.FaceCount; f++)
        {
            if (!mesh.Faces[f].Alive) continue;
            int deg = mesh.GetFaceHalfEdges(f, hes);
            if (deg < 3) continue;
            faces++;
            TriangleCount += deg - 2;
            for (int k = 0; k < deg; k++)
            {
                var he = mesh.Hes[hes[k]];
                int idx = remap[he.Vertex];
                poly.Add(k == deg - 1 ? ~idx : idx);
                // 코너 노멀이 비어 있으면(계산 전 등) 면 노멀로 대체한다.
                var nrm = he.Normal;
                if (nrm.LengthSquared() < 1e-12f) nrm = mesh.Faces[f].Normal;
                normals.Add(nrm.X); normals.Add(nrm.Y); normals.Add(nrm.Z);
                // UV는 값이 같은 것끼리 하나의 항목을 공유한다(IndexToDirect).
                var key = (he.Uv0.X, he.Uv0.Y);
                if (!uvDict.TryGetValue(key, out int ui)) { ui = uvDict.Count; uvDict[key] = ui; uvs.Add(he.Uv0.X); uvs.Add(he.Uv0.Y); }
                uvIndex.Add(ui);
            }
        }
        g.Add("PolygonVertexIndex", poly.ToArray());

        // 레이어 요소들(노멀/UV/머티리얼) 기록.
        var ln = g.Add("LayerElementNormal", 0);
        ln.Add("Version", 101); ln.Add("Name", "");
        ln.Add("MappingInformationType", "ByPolygonVertex");
        ln.Add("ReferenceInformationType", "Direct");
        ln.Add("Normals", normals.ToArray());

        var lu = g.Add("LayerElementUV", 0);
        lu.Add("Version", 101); lu.Add("Name", "map1");
        lu.Add("MappingInformationType", "ByPolygonVertex");
        lu.Add("ReferenceInformationType", "IndexToDirect");
        lu.Add("UV", uvs.ToArray());
        lu.Add("UVIndex", uvIndex.ToArray());

        var lm = g.Add("LayerElementMaterial", 0);
        lm.Add("Version", 101); lm.Add("Name", "");
        lm.Add("MappingInformationType", "AllSame");
        lm.Add("ReferenceInformationType", "IndexToDirect");
        lm.Add("Materials", new[] { 0 });

        // Layer 0에 위 요소들을 등록해야 대부분의 임포터가 읽는다.
        var layer = g.Add("Layer", 0);
        layer.Add("Version", 100);
        foreach (var le in new[] { "LayerElementNormal", "LayerElementMaterial", "LayerElementUV" })
        {
            var e = layer.Add("LayerElement"); e.Add("Type", le); e.Add("TypedIndex", 0);
        }
        _objects.Add(g);
        return gid;
    }

    // ---------------------------------------------------------------- 머티리얼 / 텍스처

    /// <summary>머티리얼 파라미터 → FBX 텍스처 연결 속성. PBR 확장 맵은 FBX에 표준 슬롯이 없어 glTF로만 나간다.</summary>
    private static readonly (string key, string prop)[] FbxTextureSlots =
    {
        ("color", "DiffuseColor"), ("alpha", "TransparentColor"), ("specular", "SpecularColor"), ("shininess", "ShininessExponent"),
        ("roughness", "ShininessExponent"), ("metallic", "ReflectionFactor"), ("normal", "NormalMap"), ("occlusion", "AmbientColor"),
        ("emissive", "EmissiveColor"), ("specularColorFactor", "SpecularColor"),
    };

    /// <summary>이미지 전체 경로 → Texture 객체 ID(같은 이미지 재사용).</summary>
    private readonly Dictionary<string, long> _textureIds = new();

    /// <summary>이미지 경로 하나에 Video + Texture 노드(RelativeFilename = FBX 위치 기준). 같은 경로는 재사용.</summary>
    private long TextureId(string fullPath)
    {
        // 경로는 '/'로 통일하고, BaseDir이 있으면 FBX 위치 기준 상대 경로를 RelativeFilename으로 쓴다(실패 시 파일 이름).
        if (_textureIds.TryGetValue(fullPath, out long tid0)) return tid0;
        string path = fullPath.Replace('\\', '/');
        string file = Path.GetFileName(path);
        string rel = file;
        if (!string.IsNullOrEmpty(_opt.BaseDir))
        {
            try { rel = Path.GetRelativePath(_opt.BaseDir, fullPath).Replace('\\', '/'); } catch { rel = file; }
        }
        // Video(Clip) 객체: 실제 이미지 파일 참조.
        long vid = NewId("Video");
        var video = new FbxNode("Video", vid, FbxNode.Id("Video", file), "Clip");
        video.Add("Type", "Clip");
        var vp = video.Add("Properties70");
        vp.Add("P", "Path", "KString", "XRefUrl", "", path);
        vp.Add("P", "RelPath", "KString", "XRefUrl", "", rel);
        video.Add("UseMipMap", 0);
        video.Add("Filename", path);
        video.Add("RelativeFilename", rel);
        _objects.Add(video);

        // Texture 객체: Video를 Media로 참조하고 UV 세트 map1을 사용. Video → Texture로 연결한다.
        long tid = NewId("Texture");
        var tex = new FbxNode("Texture", tid, FbxNode.Id("Texture", file), "");
        tex.Add("Type", "TextureVideoClip");
        tex.Add("Version", 202);
        tex.Add("TextureName", FbxNode.Id("Texture", file));
        var tpp = tex.Add("Properties70");
        tpp.Add("P", "UVSet", "KString", "", "", "map1");
        tpp.Add("P", "UseMaterial", "bool", "", "", 1);
        tex.Add("Media", FbxNode.Id("Video", file));
        tex.Add("FileName", path);
        tex.Add("RelativeFilename", rel);
        tex.Add("ModelUVTranslation", 0, 0);
        tex.Add("ModelUVScaling", 1, 1);
        tex.Add("Texture_Alpha_Source", "None");
        tex.Add("Cropping", 0, 0, 0, 0);
        _objects.Add(tex);
        _connections.Add((vid, tid, null));
        _textureIds[fullPath] = tid;
        return tid;
    }

    /// <summary>
    /// 문서 머티리얼 ID에 대한 FBX Material을 만들거나 재사용한다.
    /// Lambert/Unlit/Matcap = Lambert, BlinnPhong/Pbr = Phong. PBR은 Metallic/Roughness를 근사 스펙큘러 색·광택 지수로 바꾼다.
    /// 발광/투명도/노멀 강도(BumpFactor)도 기록하고, 파라미터 텍스처는 <see cref="FbxTextureSlots"/>의 FBX 표준 속성에 OP 연결한다.
    /// </summary>
    /// <param name="docMaterialId">문서 머티리얼 ID(없는 ID면 이름 lambert1, 회색 기본 머티리얼).</param>
    /// <returns>FBX Material 객체 ID.</returns>
    private long MaterialId(int docMaterialId)
    {
        // 색은 선형으로 쓴다: 문서 색은 sRGB(UI 값)인데 FBX 색 속성은 Maya(장면 선형)·Blender·Unity(Linear 프로젝트에서 .gamma로 되돌림)·ufbx 모두 선형으로 읽는다.
        // 전에는 sRGB 값을 그대로 써서 가져온 앱에서 색이 밝게(0.9 → 0.955) 보였다.
        if (_materialIds.TryGetValue(docMaterialId, out long id)) return id;
        var def = _doc.FindMaterial(docMaterialId);
        string name = def?.Name ?? "lambert1";
        id = NewId("Material");
        _materialIds[docMaterialId] = id;
        var m = new FbxNode("Material", id, FbxNode.Id("Material", name), "");
        m.Add("Version", 102);
        bool phong = def != null && def.Type is MaterialType.BlinnPhong or MaterialType.Pbr;
        m.Add("ShadingModel", phong ? "Phong" : "Lambert");
        m.Add("MultiLayer", 0);
        var p = m.Add("Properties70");
        // 공통 속성: 셰이딩 모델, 발광(색 + 세기; 발광이 없으면 Factor 0), 확산색, 투명도(Opacity = alpha, TransparencyFactor = 1 − alpha).
        var color = def?.Color ?? new Vector3(0.5f, 0.5f, 0.5f);
        p.Add("P", "ShadingModel", "KString", "", "", phong ? "Phong" : "Lambert");
        var emis = def?.Get("emissive") ?? Vector3.Zero;
        float emisStrength = def?.GetF("emissiveStrength") ?? 1f;
        bool hasEmis = emis != Vector3.Zero || def?.Tex("emissive") != null;
        p.Add("P", "EmissiveColor", "Color", "", "A", Lin(emis.X), Lin(emis.Y), Lin(emis.Z));
        p.Add("P", "EmissiveFactor", "Number", "", "A", hasEmis ? (double)emisStrength : 0.0);
        p.Add("P", "AmbientColor", "Color", "", "A", 0.0, 0.0, 0.0);
        p.Add("P", "AmbientFactor", "Number", "", "A", 0.0);
        p.Add("P", "DiffuseColor", "Color", "", "A", Lin(color.X), Lin(color.Y), Lin(color.Z));
        p.Add("P", "DiffuseFactor", "Number", "", "A", 1.0);
        p.Add("P", "TransparentColor", "Color", "", "A", 1.0, 1.0, 1.0);
        float opacity = def?.GetF("alpha") ?? 1f;
        p.Add("P", "TransparencyFactor", "Number", "", "A", (double)(1f - opacity));
        p.Add("P", "Opacity", "Number", "", "A", (double)opacity);
        if (def != null && def.GetF("normal") != 1f) p.Add("P", "BumpFactor", "double", "Number", "", (double)def.GetF("normal"));
        if (phong)
        {
            // BlinnPhong은 값 그대로, PBR은 스펙큘러 = 0.04 + 0.9·metallic 회색, 광택 지수 = (1 − roughness)²·128(최소 2)로 근사한다.
            var spec = def!.Type == MaterialType.BlinnPhong ? def.Specular : new Vector3(def.Metallic * 0.9f + 0.04f);
            float shininess = def.Type == MaterialType.BlinnPhong ? def.Shininess : MathF.Max(2f, (1f - def.Roughness) * (1f - def.Roughness) * 128f);
            p.Add("P", "SpecularColor", "Color", "", "A", Lin(spec.X), Lin(spec.Y), Lin(spec.Z));
            p.Add("P", "SpecularFactor", "Number", "", "A", 1.0);
            p.Add("P", "Shininess", "Number", "", "A", (double)shininess);
            p.Add("P", "ShininessExponent", "Number", "", "A", (double)shininess);
            p.Add("P", "ReflectionColor", "Color", "", "A", 0.0, 0.0, 0.0);
            p.Add("P", "ReflectionFactor", "Number", "", "A", 0.0);
        }
        // Maya 호환 단일 값 속성
        p.Add("P", "Emissive", "Vector3D", "Vector", "", 0.0, 0.0, 0.0);
        p.Add("P", "Ambient", "Vector3D", "Vector", "", 0.0, 0.0, 0.0);
        p.Add("P", "Diffuse", "Vector3D", "Vector", "", Lin(color.X), Lin(color.Y), Lin(color.Z));
        p.Add("P", "Opacity", "double", "Number", "", 1.0);
        _objects.Add(m);

        // 파라미터 텍스처 → FBX 표준 슬롯(Maya/Unity/Blender가 읽는 이름). 같은 이미지는 Video/Texture를 한 번만 만든다.
        if (def != null)
        {
            foreach (var (key, prop) in FbxTextureSlots)
            {
                var tp = def.Tex(key);
                if (tp == null) continue;
                if (key == "roughness" && def.Tex("shininess") != null) continue; // 같은 슬롯
                _connections.Add((TextureId(tp), id, prop));
            }
        }
        return id;
    }

    /// <summary>sRGB 0..1 성분을 선형 값으로 바꾼다(FBX 색 속성용).</summary>
    internal static double Lin(float c) => c <= 0.04045f ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    // ---------------------------------------------------------------- 스킨

    /// <summary>
    /// 스킨이 있는 메시에 Deformer Skin과 조인트별 Cluster, 그리고 BindPose를 만든다.
    /// Cluster: Indexes/Weights = 그 조인트의 가중치가 0보다 큰 정점(FBX 정점 인덱스), TransformLink = 조인트 월드,
    /// Transform = 메시 공간 → 본 공간(행벡터 규약 메시 월드 · inv(조인트 월드); FBX SDK/Maya/Blender와 같은 의미, ufbx의 mesh_node_to_bone).
    /// v0.0.56까지는 Transform에 메시 월드를 그대로 써서 Godot(ufbx)이 바인드 포즈를 단위 행렬로 읽어 원점에 없는 본의 스킨이 어긋났다.
    /// 바인드 포즈는 저장된 바인드 행렬이 아니라 내보내는 시점의 현재(rest) 월드 행렬이다(glTF 내보내기와 동일).
    /// 행렬 이동 성분은 cm로 스케일한다.
    /// </summary>
    /// <param name="n">스킨 메시 노드.</param>
    /// <param name="geomId">그 메시의 Geometry ID(Skin Deformer가 여기에 연결된다).</param>
    private void BuildSkin(SceneNode n, long geomId)
    {
        var skin = n.Skin!;
        var mesh = n.Mesh!;
        var remap = _vertexRemap[n];
        // 내보내기에 포함된 조인트만 (스킨 조인트 슬롯 번호, 노드)로 모은다. 하나도 없으면 스킨을 쓰지 않는다.
        var jointNodes = new List<(int index, SceneNode node)>();
        for (int j = 0; j < skin.Joints.Count; j++)
        {
            var jn = _doc.Find(skin.Joints[j]);
            if (jn != null && _modelIds.ContainsKey(jn)) jointNodes.Add((j, jn));
        }
        if (jointNodes.Count == 0) return;

        // Skin Deformer → Geometry 연결.
        long sid = NewId("Deformer");
        var sk = new FbxNode("Deformer", sid, FbxNode.Id("Deformer", n.Name + "_skin"), "Skin");
        sk.Add("Version", 101);
        sk.Add("Link_DeformAcuracy", 50.0);
        sk.Add("SkinningType", "Linear");
        _objects.Add(sk);
        _connections.Add((sid, geomId, null));

        // 피벗을 베이크했으면 메시 모델의 월드는 T(P)·M (지오메트리가 −P만큼 옮겨졌으므로)
        var meshWorld = Scaled(_bakedPivot.TryGetValue(n, out var mp) ? Matrix4x4.CreateTranslation(mp) * n.WorldMatrix : n.WorldMatrix);
        // BindPose: 메시와 각 조인트의 바인드 월드 행렬 목록.
        long poseId = NewId("Pose");
        var pose = new FbxNode("Pose", poseId, FbxNode.Id("Pose", n.Name + "_bind"), "BindPose");
        pose.Add("Type", "BindPose");
        pose.Add("Version", 100);
        var poseNodes = new List<FbxNode>();
        var pn = new FbxNode("PoseNode"); pn.Add("Node", _modelIds[n]); pn.Add("Matrix", ToArray(meshWorld)); poseNodes.Add(pn);

        foreach (var (j, jn) in jointNodes)
        {
            // 이 조인트 슬롯 j에 가중치를 가진 정점들을 FBX 정점 인덱스(remap)로 모은다.
            var idx = new List<int>(); var wts = new List<double>();
            for (int v = 0; v < skin.Weights.Length && v < mesh.VertexCount; v++)
            {
                var list = skin.Weights[v];
                if (list == null || remap[v] < 0) continue;
                foreach (var (joint, weight) in list)
                    if (joint == j && weight > 0f) { idx.Add(remap[v]); wts.Add(weight); }
            }
            // Cluster(SubDeformer) 객체: Skin에 연결하고, 조인트 Model을 Cluster에 연결(링크)한다.
            long cid = NewId("Deformer");
            var cl = new FbxNode("Deformer", cid, FbxNode.Id("SubDeformer", n.Name + "_" + jn.Name), "Cluster");
            cl.Add("Version", 100);
            cl.Add("UserData", "", "");
            cl.Add("Indexes", idx.ToArray());
            cl.Add("Weights", wts.ToArray());
            var jointWorld = Scaled(jn.WorldMatrix);
            Matrix4x4.Invert(jointWorld, out var invJoint);
            cl.Add("Transform", ToArray(meshWorld * invJoint));
            cl.Add("TransformLink", ToArray(jointWorld));
            _objects.Add(cl);
            _connections.Add((cid, sid, null));
            _connections.Add((_modelIds[jn], cid, null));
            var jp = new FbxNode("PoseNode"); jp.Add("Node", _modelIds[jn]); jp.Add("Matrix", ToArray(jointWorld)); poseNodes.Add(jp);
        }
        pose.Add("NbPoseNodes", poseNodes.Count);
        foreach (var p in poseNodes) pose.Add(p);
        _objects.Add(pose);
    }

    /// <summary>행렬의 이동 성분(M41..M43)에만 단위 배율을 곱한다(회전/스케일 부분은 그대로).</summary>
    private Matrix4x4 Scaled(Matrix4x4 m) { m.M41 *= S; m.M42 *= S; m.M43 *= S; return m; }

    /// <summary>행렬을 행 우선 16개 double로 펼친다. 행벡터 규약(이동 = 4행)이 FBX의 열 우선 저장과 같은 바이트 순서가 된다.</summary>
    private static double[] ToArray(Matrix4x4 m) => new double[]
    {
        m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44,
    };
}
