using Cube.Core.Commands;
using Cube.Core.IO;
using Cube.Core.Scene;
using Godot;

namespace Cube.App.IO;

/// <summary>가져오기/내보내기 실행과 네이티브 파일 다이얼로그. 메뉴 액션과 DebugDriver가 공유한다.</summary>
/// <remarks>
/// 확장자로 <see cref="Exporters"/>/<see cref="Importers"/> 중 하나를 고른다. 성공하면 마지막 폴더를
/// Settings.LastExportDir에 저장해 다음 다이얼로그가 그 폴더에서 열린다. 가져오기는 머티리얼·노드·애니메이션을
/// 한 Undo 그룹("Import")으로 문서에 추가하므로 Ctrl+Z 한 번으로 통째로 되돌릴 수 있다.
/// </remarks>
public sealed class FileActions
{
    /// <summary>등록된 내보내기 형식(glTF, 자체 FBX writer, OBJ). 다이얼로그 필터와 확장자 매칭 순서이기도 하다.</summary>
    public readonly List<IExporter> Exporters = new() { new GltfExporter(), new Core.IO.Fbx.FbxExporter(), new Core.IO.ObjExporter() };
    /// <summary>등록된 가져오기 형식(glTF, FBX(Godot FbxDocument/ufbx), OBJ).</summary>
    public readonly List<IImporter> Importers = new() { new GltfImporter(), new FbxImporter(), new Core.IO.ObjImporter() };

    /// <summary>대상 문서.</summary>
    private readonly Document _doc;
    /// <summary>마지막 내보내기/가져오기 폴더를 기억하는 설정.</summary>
    private readonly Settings _settings;
    /// <summary>FileDialog를 자식으로 붙일 노드(셸).</summary>
    private readonly Node _owner;
    /// <summary>결과 메시지를 헬프 라인 등에 표시하는 콜백.</summary>
    private readonly Action<string> _status;

    /// <summary>문서·설정·다이얼로그 소유 노드·상태 메시지 콜백으로 만든다.</summary>
    public FileActions(Document doc, Settings settings, Node owner, Action<string> status)
    {
        _doc = doc; _settings = settings; _owner = owner; _status = status;
    }

    // ---------------------------------------------------------------- 직접 실행

    /// <summary>
    /// <paramref name="path"/>의 확장자에 맞는 exporter로 내보낸다.
    /// <paramref name="selectionOnly"/>면 선택된 오브젝트만, 아니면 루트의 최상위 노드 전부를 넘긴다.
    /// </summary>
    /// <param name="preset">내보내기 대상 엔진 프리셋(null이면 Generic).</param>
    /// <returns>exporter의 결과(메시지는 상태 줄에도 표시).</returns>
    public ExportResult Export(string path, bool selectionOnly, ExportPreset? preset = null)
    {
        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        var exporter = Exporters.FirstOrDefault(e => e.Extensions.Contains(ext));
        if (exporter == null) return ExportResult.Fail($"No exporter for '{ext}'.");
        // 선택 내보내기: 선택 ID를 노드로 바꾸고 이미 지워진(찾을 수 없는) ID는 버린다
        IReadOnlyList<SceneNode> nodes = selectionOnly
            ? _doc.Selection.Objects.Select(id => _doc.Find(id)).Where(n => n != null).Cast<SceneNode>().ToList()
            : _doc.Root.Children.ToList();
        var result = exporter.Export(_doc, nodes, path, preset ?? ExportPreset.Generic);
        _status(result.Message);
        // 성공하면 폴더를 기억해 둔다
        if (result.Ok) { _settings.LastExportDir = System.IO.Path.GetDirectoryName(path); _settings.Save(); }
        return result;
    }

    /// <summary>
    /// <paramref name="path"/>의 확장자에 맞는 importer로 읽어 문서에 추가한다.
    /// importer는 문서를 바꾸지 않고 결과(노드·머티리얼·애니메이션)만 돌려주며, 여기서 Undo 명령으로 넣는다.
    /// 소요 시간은 [ImportPerf] 로그로 남긴다.
    /// </summary>
    public ImportResult Import(string path, ImportOptions? options = null)
    {
        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        var importer = Importers.FirstOrDefault(i => i.Extensions.Contains(ext));
        if (importer == null) return ImportResult.Fail($"No importer for '{ext}'.");
        var swTotal = System.Diagnostics.Stopwatch.StartNew();
        var result = importer.Import(path, _doc, options ?? ImportOptions.Default);
        GD.Print($"[ImportPerf] importer total: {swTotal.ElapsedMilliseconds} ms");
        _status(result.Message);
        if (result.Ok && result.Nodes.Count > 0)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // 머티리얼 → 노드 → 애니메이션 순으로 같은 Undo 그룹에 넣는다(노드가 머티리얼 ID를, 애니메이션이 노드 ID를 참조)
            using (_doc.Undo.BeginGroup("Import"))
            {
                foreach (var m in result.Materials) _doc.Undo.Push(new AddMaterialCommand(m));
                _doc.Undo.Push(new ImportNodesCommand(result.Nodes));
                if (result.Animations.Count > 0) _doc.Undo.Push(new SetAnimationsCommand("Import Animations", result.Animations));
            }
            GD.Print($"[ImportPerf] add to document (views, outliner, …): {sw.ElapsedMilliseconds} ms");
            _settings.LastExportDir = System.IO.Path.GetDirectoryName(path); _settings.Save();
        }
        return result;
    }

    // ---- 네이티브 파일 다이얼로그(Windows 탐색기 창). 선택이 끝나면 실행하고 다이얼로그 노드를 해제한다.
    // ---------------------------------------------------------------- 다이얼로그

    /// <summary>내보내기 저장 다이얼로그를 띄운다. 기본 파일 이름은 활성 오브젝트 이름(선택 내보내기) 또는 "scene", 확장자 .glb.</summary>
    public void ShowExportDialog(bool selectionOnly)
    {
        var fd = MakeDialog(FileDialog.FileModeEnum.SaveFile, "Export " + (selectionOnly ? "Selection" : "All"));
        foreach (var e in Exporters) fd.AddFilter(string.Join(",", e.Extensions.Select(x => "*" + x)), e.Name);
        fd.CurrentDir = _settings.LastExportDir ?? OS.GetSystemDir(OS.SystemDir.Documents);
        fd.CurrentFile = (selectionOnly && _doc.Find(_doc.Selection.ActiveObject) is { } n ? n.Name : "scene") + ".glb";
        fd.FileSelected += p => { Export(p, selectionOnly); fd.QueueFree(); };
        fd.Canceled += fd.QueueFree;
        fd.PopupCentered();
    }

    /// <summary>가져오기 다이얼로그(여러 파일 선택 가능)를 띄운다. 첫 필터는 지원 확장자 전체, 그 뒤로 형식별 필터.</summary>
    public void ShowImportDialog()
    {
        var fd = MakeDialog(FileDialog.FileModeEnum.OpenFiles, "Import");
        var all = Importers.SelectMany(i => i.Extensions).Select(x => "*" + x);
        fd.AddFilter(string.Join(",", all), "glTF / FBX");
        foreach (var i in Importers) fd.AddFilter(string.Join(",", i.Extensions.Select(x => "*" + x)), i.Name);
        fd.CurrentDir = _settings.LastExportDir ?? OS.GetSystemDir(OS.SystemDir.Documents);
        fd.FilesSelected += paths => { foreach (var p in paths) Import(p); fd.QueueFree(); };
        fd.Canceled += fd.QueueFree;
        fd.PopupCentered();
    }

    /// <summary>파일 시스템 접근·네이티브 다이얼로그 설정으로 FileDialog를 만들어 소유 노드에 붙인다(호출자가 PopupCentered).</summary>
    private FileDialog MakeDialog(FileDialog.FileModeEnum mode, string title)
    {
        var fd = new FileDialog
        {
            FileMode = mode,
            Access = FileDialog.AccessEnum.Filesystem,
            UseNativeDialog = true,
            Title = title,
            Size = new Vector2I(900, 600),
        };
        _owner.AddChild(fd);
        return fd;
    }
}
