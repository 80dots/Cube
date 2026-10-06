using Cube.Core.IO;
using Cube.Core.Scene;
using Godot;

namespace Cube.App.IO;

/// <summary>.cube 문서의 새로 만들기/열기/저장과 관련 다이얼로그.</summary>
public sealed class SceneFileActions
{
    private readonly Document _doc;
    private readonly Settings _settings;
    private readonly Node _owner;
    private readonly Action<string> _status;
    public event Action? FileChanged;

    public SceneFileActions(Document doc, Settings settings, Node owner, Action<string> status)
    {
        _doc = doc; _settings = settings; _owner = owner; _status = status;
    }

    public string Title
    {
        get
        {
            string name = _doc.FilePath != null ? System.IO.Path.GetFileName(_doc.FilePath) : "untitled";
            return $"Cube - {name}{(_doc.IsDirty ? "*" : "")}";
        }
    }

    // ---------------------------------------------------------------- 직접 실행

    public bool Save(string path)
    {
        try
        {
            if (!path.EndsWith(CubeFileFormat.Extension, StringComparison.OrdinalIgnoreCase)) path += CubeFileFormat.Extension;
            CubeFileFormat.Save(_doc, path);
            _settings.AddRecent(path); _settings.LastSceneDir = System.IO.Path.GetDirectoryName(path); _settings.Save();
            _status($"Saved {System.IO.Path.GetFileName(path)}");
            FileChanged?.Invoke();
            return true;
        }
        catch (Exception ex) { _status("Save failed: " + ex.Message); return false; }
    }

    public bool Open(string path)
    {
        try
        {
            CubeFileFormat.Load(_doc, path);
            _settings.AddRecent(path); _settings.LastSceneDir = System.IO.Path.GetDirectoryName(path); _settings.Save();
            _status($"Opened {System.IO.Path.GetFileName(path)}");
            FileChanged?.Invoke();
            return true;
        }
        catch (Exception ex) { _status("Open failed: " + ex.Message); return false; }
    }

    public void New()
    {
        _doc.Clear();
        _status("New scene");
        FileChanged?.Invoke();
    }

    // ---------------------------------------------------------------- 다이얼로그

    /// <summary>Ctrl+S: 경로가 있으면 바로 저장, 없으면 다른 이름으로 저장.</summary>
    public void SaveOrSaveAs()
    {
        if (_doc.FilePath != null) Save(_doc.FilePath); else ShowSaveAsDialog();
    }

    public void ShowSaveAsDialog()
    {
        var fd = MakeDialog(FileDialog.FileModeEnum.SaveFile, "Save Scene As");
        fd.AddFilter("*" + CubeFileFormat.Extension, "Cube Scene");
        fd.CurrentDir = _settings.LastSceneDir ?? OS.GetSystemDir(OS.SystemDir.Documents);
        fd.CurrentFile = _doc.FilePath != null ? System.IO.Path.GetFileName(_doc.FilePath) : "untitled.cube";
        fd.FileSelected += p => { Save(p); fd.QueueFree(); };
        fd.Canceled += fd.QueueFree;
        fd.PopupCentered();
    }

    public void ShowOpenDialog() => ConfirmDiscard(() =>
    {
        var fd = MakeDialog(FileDialog.FileModeEnum.OpenFile, "Open Scene");
        fd.AddFilter("*" + CubeFileFormat.Extension, "Cube Scene");
        fd.CurrentDir = _settings.LastSceneDir ?? OS.GetSystemDir(OS.SystemDir.Documents);
        fd.FileSelected += p => { Open(p); fd.QueueFree(); };
        fd.Canceled += fd.QueueFree;
        fd.PopupCentered();
    });

    public void NewWithConfirm() => ConfirmDiscard(New);

    public void OpenRecentWithConfirm(string path) => ConfirmDiscard(() => Open(path));

    /// <summary>수정 사항이 있으면 버릴지 묻고, 확인 시 action 실행.</summary>
    public void ConfirmDiscard(Action action)
    {
        if (!_doc.IsDirty) { action(); return; }
        var dlg = new ConfirmationDialog { Title = "Unsaved Changes", DialogText = "The scene has unsaved changes. Discard them?", OkButtonText = "Discard" };
        dlg.Confirmed += () => { action(); dlg.QueueFree(); };
        dlg.Canceled += dlg.QueueFree;
        _owner.AddChild(dlg);
        dlg.PopupCentered();
    }

    private FileDialog MakeDialog(FileDialog.FileModeEnum mode, string title)
    {
        var fd = new FileDialog { FileMode = mode, Access = FileDialog.AccessEnum.Filesystem, UseNativeDialog = true, Title = title, Size = new Vector2I(900, 600) };
        _owner.AddChild(fd);
        return fd;
    }
}
