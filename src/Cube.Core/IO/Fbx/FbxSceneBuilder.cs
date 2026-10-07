using System.Numerics;
using Cube.Core.Mesh;
using Cube.Core.Scene;

namespace Cube.Core.IO.Fbx;

/// <summary>FBX 내보내기 옵션. 단위는 cm(UnitScaleFactor 1)로 쓰고 정점·이동·행렬 이동 성분에 UnitScale(기본 100)을 곱한다(Blender/Maya 방식).</summary>
public sealed record FbxExportOptions(float UnitScale = 100f, bool Compress = true, string Creator = "Cube FBX writer", bool EmbedLights = true, string? BaseDir = null)
{
    public static readonly FbxExportOptions Default = new();
}

/// <summary>
/// Document → FBX 노드 트리. 축 변환은 없다(내부 = Maya와 같은 오른손 Y-up; FBX GlobalSettings도 Y-up/Z-front/X-coord).
/// 메시(Geometry: Vertices/PolygonVertexIndex/LayerElementNormal·UV ByPolygonVertex/LayerElementMaterial), Model(Lcl T/R/S + Rotation/ScalingPivot),
/// Material(Phong + DiffuseColor 텍스처 Texture/Video), 조인트(Model LimbNode + NodeAttribute Skeleton), 라이트(NodeAttribute Light),
/// 스킨(Deformer Skin/Cluster + Pose BindPose; 바인드 포즈 = 내보내는 시점의 현재 포즈, glTF와 동일).
/// </summary>
public sealed class FbxSceneBuilder
{
    private readonly Document _doc;
    private readonly FbxExportOptions _opt;
    private long _nextId = 1_000_000;
    private readonly List<FbxNode> _objects = new();
    private readonly List<(long child, long parent, string? prop)> _connections = new();
    private readonly Dictionary<SceneNode, long> _modelIds = new();
    private readonly Dictionary<int, long> _materialIds = new();
    private readonly Dictionary<string, int> _typeCounts = new();
    public int NodeCount { get; private set; }
    public int TriangleCount { get; private set; }

    public FbxSceneBuilder(Document doc, FbxExportOptions? options = null) { _doc = doc; _opt = options ?? FbxExportOptions.Default; }

    private long NewId(string type) { _typeCounts[type] = _typeCounts.GetValueOrDefault(type) + 1; return _nextId++; }
    private float S => _opt.UnitScale;

    /// <summary>roots와 그 하위 전체를 내보낸다. 스킨이 참조하는 조인트가 roots 밖이면 그 조인트 체인의 루트부터 함께 내보낸다.</summary>
    public List<FbxNode> Build(IReadOnlyList<SceneNode> roots)
    {
        var tops = new List<SceneNode>(roots.Where(r => !r.IsRoot));
        var inSet = new HashSet<SceneNode>();
        foreach (var r in tops) { inSet.Add(r); foreach (var d in r.Descendants()) inSet.Add(d); }
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
        foreach (var t in tops) BuildNode(t, 0, bakeWorld: t.Parent != null && !t.Parent.IsRoot);
        // 스킨(모든 모델 ID가 정해진 뒤)
        foreach (var n in inSet) if (n.Skin != null && n.Mesh != null && _geometryIds.TryGetValue(n, out long geomId)) BuildSkin(n, geomId);

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
        var conns = new FbxNode("Connections");
        foreach (var (child, parent, prop) in _connections)
        {
            if (prop == null) conns.Add("C", "OO", child, parent);
            else conns.Add("C", "OP", child, parent, prop);
        }
        top.Add(conns);
        var takes = new FbxNode("Takes"); takes.Add("Current", ""); top.Add(takes);
        return top;
    }

    // ---------------------------------------------------------------- 헤더/설정

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

    private static FbxNode GlobalSettings()
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
        p.Add("P", "TimeMode", "enum", "", "", 11);
        p.Add("P", "TimeSpanStart", "KTime", "Time", "", 0L);
        p.Add("P", "TimeSpanStop", "KTime", "Time", "", 46186158000L);
        p.Add("P", "CustomFrameRate", "double", "Number", "", 24.0);
        return g;
    }

    private FbxNode Documents()
    {
        var d = new FbxNode("Documents");
        d.Add("Count", 1);
        var doc = d.Add("Document", _nextId++, "Scene", "Scene");
        var p = doc.Add("Properties70");
        p.Add("P", "SourceObject", "object", "", "");
        p.Add("P", "ActiveAnimStackName", "KString", "", "", "");
        doc.Add("RootNode", 0L);
        return d;
    }

    private FbxNode Definitions()
    {
        var d = new FbxNode("Definitions");
        d.Add("Version", 100);
        d.Add("Count", 1 + _typeCounts.Values.Sum());
        d.Add("ObjectType", "GlobalSettings").Add("Count", 1);
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

    private readonly Dictionary<SceneNode, long> _geometryIds = new();

    private void BuildNode(SceneNode n, long parentId, bool bakeWorld)
    {
        NodeCount++;
        string type = n.IsJoint ? "LimbNode" : n.Mesh != null ? "Mesh" : n.IsLight ? "Light" : "Null";
        long id = NewId("Model");
        _modelIds[n] = id;
        var model = new FbxNode("Model", id, FbxNode.Id("Model", n.Name), type);
        model.Add("Version", 232);
        var t = bakeWorld ? Transform3.FromMatrix(n.WorldMatrix, n.Local.Pivot) : n.Local;
        var p = model.Add("Properties70");
        if (t.Pivot != Vector3.Zero)
        {
            p.Add("P", "RotationPivot", "Vector3D", "Vector", "", (double)(t.Pivot.X * S), (double)(t.Pivot.Y * S), (double)(t.Pivot.Z * S));
            p.Add("P", "ScalingPivot", "Vector3D", "Vector", "", (double)(t.Pivot.X * S), (double)(t.Pivot.Y * S), (double)(t.Pivot.Z * S));
        }
        p.Add("P", "RotationActive", "bool", "", "", 1);
        p.Add("P", "InheritType", "enum", "", "", 1);
        p.Add("P", "ScalingMax", "Vector3D", "Vector", "", 0.0, 0.0, 0.0);
        p.Add("P", "DefaultAttributeIndex", "int", "Integer", "", 0);
        p.Add("P", "Lcl Translation", "Lcl Translation", "", "A", (double)(t.Translation.X * S), (double)(t.Translation.Y * S), (double)(t.Translation.Z * S));
        p.Add("P", "Lcl Rotation", "Lcl Rotation", "", "A", (double)t.RotationDegrees.X, (double)t.RotationDegrees.Y, (double)t.RotationDegrees.Z);
        p.Add("P", "Lcl Scaling", "Lcl Scaling", "", "A", (double)t.Scale.X, (double)t.Scale.Y, (double)t.Scale.Z);
        if (!n.Visible) p.Add("P", "Visibility", "Visibility", "", "A", 0.0);
        model.Add("MultiLayer", 0);
        model.Add("MultiTake", 0);
        model.Add("Shading", true);
        model.Add("Culling", "CullingOff");
        _objects.Add(model);
        _connections.Add((id, parentId, null));

        if (n.IsJoint)
        {
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
            long gid = BuildGeometry(n);
            _geometryIds[n] = gid;
            _connections.Add((gid, id, null));
            long mid = MaterialId(n.MaterialId);
            _connections.Add((mid, id, null));
        }
        else if (n.IsLight && _opt.EmbedLights)
        {
            long aid = NewId("NodeAttribute");
            var l = n.Light!;
            var attr = new FbxNode("NodeAttribute", aid, FbxNode.Id("NodeAttribute", n.Name), "Light");
            attr.Add("TypeFlags", "Light");
            attr.Add("GeometryVersion", 124);
            var ap = attr.Add("Properties70");
            ap.Add("P", "LightType", "enum", "", "", l.Type switch { LightType.Directional => 1, LightType.Spot => 2, _ => 0 });
            ap.Add("P", "Color", "Color", "", "A", (double)l.Color.X, (double)l.Color.Y, (double)l.Color.Z);
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

        foreach (var c in n.Children) BuildNode(c, id, bakeWorld: false);
    }

    // ---------------------------------------------------------------- 지오메트리

    private readonly Dictionary<SceneNode, int[]> _vertexRemap = new();

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
            var pos = mesh.Verts[v].Position;
            verts.Add(pos.X * S); verts.Add(pos.Y * S); verts.Add(pos.Z * S);
        }
        _vertexRemap[n] = remap;
        g.Add("Vertices", verts.ToArray());

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
                var nrm = he.Normal;
                if (nrm.LengthSquared() < 1e-12f) nrm = mesh.Faces[f].Normal;
                normals.Add(nrm.X); normals.Add(nrm.Y); normals.Add(nrm.Z);
                var key = (he.Uv0.X, he.Uv0.Y);
                if (!uvDict.TryGetValue(key, out int ui)) { ui = uvDict.Count; uvDict[key] = ui; uvs.Add(he.Uv0.X); uvs.Add(he.Uv0.Y); }
                uvIndex.Add(ui);
            }
        }
        g.Add("PolygonVertexIndex", poly.ToArray());

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

    private long MaterialId(int docMaterialId)
    {
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
        var color = def?.Color ?? new Vector3(0.5f, 0.5f, 0.5f);
        p.Add("P", "ShadingModel", "KString", "", "", phong ? "Phong" : "Lambert");
        p.Add("P", "EmissiveColor", "Color", "", "A", 0.0, 0.0, 0.0);
        p.Add("P", "EmissiveFactor", "Number", "", "A", 0.0);
        p.Add("P", "AmbientColor", "Color", "", "A", 0.0, 0.0, 0.0);
        p.Add("P", "AmbientFactor", "Number", "", "A", 0.0);
        p.Add("P", "DiffuseColor", "Color", "", "A", (double)color.X, (double)color.Y, (double)color.Z);
        p.Add("P", "DiffuseFactor", "Number", "", "A", 1.0);
        p.Add("P", "TransparentColor", "Color", "", "A", 1.0, 1.0, 1.0);
        p.Add("P", "TransparencyFactor", "Number", "", "A", 0.0);
        p.Add("P", "Opacity", "Number", "", "A", 1.0);
        if (phong)
        {
            var spec = def!.Type == MaterialType.BlinnPhong ? def.Specular : new Vector3(def.Metallic * 0.9f + 0.04f);
            float shininess = def.Type == MaterialType.BlinnPhong ? def.Shininess : MathF.Max(2f, (1f - def.Roughness) * (1f - def.Roughness) * 128f);
            p.Add("P", "SpecularColor", "Color", "", "A", (double)spec.X, (double)spec.Y, (double)spec.Z);
            p.Add("P", "SpecularFactor", "Number", "", "A", 1.0);
            p.Add("P", "Shininess", "Number", "", "A", (double)shininess);
            p.Add("P", "ShininessExponent", "Number", "", "A", (double)shininess);
            p.Add("P", "ReflectionColor", "Color", "", "A", 0.0, 0.0, 0.0);
            p.Add("P", "ReflectionFactor", "Number", "", "A", 0.0);
        }
        // Maya 호환 단일 값 속성
        p.Add("P", "Emissive", "Vector3D", "Vector", "", 0.0, 0.0, 0.0);
        p.Add("P", "Ambient", "Vector3D", "Vector", "", 0.0, 0.0, 0.0);
        p.Add("P", "Diffuse", "Vector3D", "Vector", "", (double)color.X, (double)color.Y, (double)color.Z);
        p.Add("P", "Opacity", "double", "Number", "", 1.0);
        _objects.Add(m);

        if (def != null && !string.IsNullOrEmpty(def.TexturePath))
        {
            string path = def.TexturePath.Replace('\\', '/');
            string file = Path.GetFileName(path);
            // RelativeFilename은 FBX 파일 위치 기준(임포터가 먼저 찾는 경로)
            string rel = file;
            if (!string.IsNullOrEmpty(_opt.BaseDir))
            {
                try { rel = Path.GetRelativePath(_opt.BaseDir, def.TexturePath).Replace('\\', '/'); } catch { rel = file; }
            }
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

            long tid = NewId("Texture");
            var tex = new FbxNode("Texture", tid, FbxNode.Id("Texture", file), "");
            tex.Add("Type", "TextureVideoClip");
            tex.Add("Version", 202);
            tex.Add("TextureName", FbxNode.Id("Texture", file));
            var tp = tex.Add("Properties70");
            tp.Add("P", "UVSet", "KString", "", "", "map1");
            tp.Add("P", "UseMaterial", "bool", "", "", 1);
            tex.Add("Media", FbxNode.Id("Video", file));
            tex.Add("FileName", path);
            tex.Add("RelativeFilename", rel);
            tex.Add("ModelUVTranslation", 0, 0);
            tex.Add("ModelUVScaling", 1, 1);
            tex.Add("Texture_Alpha_Source", "None");
            tex.Add("Cropping", 0, 0, 0, 0);
            _objects.Add(tex);
            _connections.Add((vid, tid, null));
            _connections.Add((tid, id, "DiffuseColor"));
        }
        return id;
    }

    // ---------------------------------------------------------------- 스킨

    private void BuildSkin(SceneNode n, long geomId)
    {
        var skin = n.Skin!;
        var mesh = n.Mesh!;
        var remap = _vertexRemap[n];
        var jointNodes = new List<(int index, SceneNode node)>();
        for (int j = 0; j < skin.Joints.Count; j++)
        {
            var jn = _doc.Find(skin.Joints[j]);
            if (jn != null && _modelIds.ContainsKey(jn)) jointNodes.Add((j, jn));
        }
        if (jointNodes.Count == 0) return;

        long sid = NewId("Deformer");
        var sk = new FbxNode("Deformer", sid, FbxNode.Id("Deformer", n.Name + "_skin"), "Skin");
        sk.Add("Version", 101);
        sk.Add("Link_DeformAcuracy", 50.0);
        sk.Add("SkinningType", "Linear");
        _objects.Add(sk);
        _connections.Add((sid, geomId, null));

        var meshWorld = Scaled(n.WorldMatrix);
        long poseId = NewId("Pose");
        var pose = new FbxNode("Pose", poseId, FbxNode.Id("Pose", n.Name + "_bind"), "BindPose");
        pose.Add("Type", "BindPose");
        pose.Add("Version", 100);
        var poseNodes = new List<FbxNode>();
        var pn = new FbxNode("PoseNode"); pn.Add("Node", _modelIds[n]); pn.Add("Matrix", ToArray(meshWorld)); poseNodes.Add(pn);

        foreach (var (j, jn) in jointNodes)
        {
            var idx = new List<int>(); var wts = new List<double>();
            for (int v = 0; v < skin.Weights.Length && v < mesh.VertexCount; v++)
            {
                var list = skin.Weights[v];
                if (list == null || remap[v] < 0) continue;
                foreach (var (joint, weight) in list)
                    if (joint == j && weight > 0f) { idx.Add(remap[v]); wts.Add(weight); }
            }
            long cid = NewId("Deformer");
            var cl = new FbxNode("Deformer", cid, FbxNode.Id("SubDeformer", n.Name + "_" + jn.Name), "Cluster");
            cl.Add("Version", 100);
            cl.Add("UserData", "", "");
            cl.Add("Indexes", idx.ToArray());
            cl.Add("Weights", wts.ToArray());
            var jointWorld = Scaled(jn.WorldMatrix);
            cl.Add("Transform", ToArray(meshWorld));
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

    private Matrix4x4 Scaled(Matrix4x4 m) { m.M41 *= S; m.M42 *= S; m.M43 *= S; return m; }

    private static double[] ToArray(Matrix4x4 m) => new double[]
    {
        m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44,
    };
}
