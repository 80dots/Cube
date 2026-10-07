using Cube.Core.Commands;
using Cube.Core.IO;
using Cube.Core.Scene;
using Godot;

namespace Cube.App.IO;

/// <summary>가져오기/내보내기 실행과 네이티브 파일 다이얼로그. 메뉴 액션과 DebugDriver가 공유한다.</summary>
public sealed class FileActions
{
    public readonly List<IExporter> Exporters = new() { new GltfExporter(), new Core.IO.Fbx.FbxExporter(), new Core.IO.ObjExporter() };
    public readonly List<IImporter> Importers = new() { new GltfImporter(), new FbxImporter(), new Core.IO.ObjImporter() };

    private readonly Document _doc;
    private readonly Settings _settings;
    private readonly Node _owner;
    private readonly Action<string> _status;

    public FileActions(Document doc, Settings settings, Node owner, Action<string> status)
    {
        _doc = doc; _settings = settings; _owner = owner; _status = status;
    }

    // ---------------------------------------------------------------- 직접 실행

    public ExportResult Export(string path, bool selectionOnly, ExportPreset? preset = null)
    {
        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        var exporter = Exporters.FirstOrDefault(e => e.Extensions.Contains(ext));
        if (exporter == null) return ExportResult.Fail($"No exporter for '{ext}'.");
        IReadOnlyList<SceneNode> nodes = selectionOnly
            ? _doc.Selection.Objects.Select(id => _doc.Find(id)).Where(n => n != null).Cast<SceneNode>().ToList()
            : _doc.Root.Children.ToList();
        var result = exporter.Export(_doc, nodes, path, preset ?? ExportPreset.Generic);
        _status(result.Message);
        if (result.Ok) { _settings.LastExportDir = System.IO.Path.GetDirectoryName(path); _settings.Save(); }
        return result;
    }

    public ImportResult Import(string path, ImportOptions? options = null)
    {
        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        var importer = Importers.FirstOrDefault(i => i.Extensions.Contains(ext));
        if (importer == null) return ImportResult.Fail($"No importer for '{ext}'.");
        var result = importer.Import(path, _doc, options ?? ImportOptions.Default);
        _status(result.Message);
        if (result.Ok && result.Nodes.Count > 0)
        {
            _doc.Undo.Push(new ImportNodesCommand(result.Nodes));
            _settings.LastExportDir = System.IO.Path.GetDirectoryName(path); _settings.Save();
        }
        return result;
    }

    // ---------------------------------------------------------------- 다이얼로그

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
