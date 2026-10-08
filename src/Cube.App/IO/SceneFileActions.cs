using Cube.Core.IO;
using Cube.Core.Scene;
using Godot;

namespace Cube.App.IO;

/// <summary>.cube 문서의 새로 만들기/열기/저장과 관련 다이얼로그.</summary>
/// <remarks>
/// 형식 처리는 Core의 <see cref="CubeFileFormat"/>(JSON)이 하고, 여기서는 최근 파일·마지막 폴더 기록,
/// 상태 메시지, 미저장 변경 확인, 네이티브 다이얼로그를 맡는다. 성공하면 <see cref="FileChanged"/>로 창 제목 등을 갱신하게 한다.
/// </remarks>
public sealed class SceneFileActions
{
    /// <summary>대상 문서.</summary>
    private readonly Document _doc;
    /// <summary>최근 파일 목록과 마지막 장면 폴더를 저장하는 설정.</summary>
    private readonly Settings _settings;
    /// <summary>다이얼로그를 붙일 소유 노드.</summary>
    private readonly Node _owner;
    /// <summary>상태 메시지 콜백.</summary>
    private readonly Action<string> _status;
    /// <summary>저장·열기·새로 만들기 뒤 발생(창 제목 갱신 등).</summary>
    public event Action? FileChanged;

    /// <summary>문서·설정·소유 노드·상태 콜백으로 만든다.</summary>
    public SceneFileActions(Document doc, Settings settings, Node owner, Action<string> status)
    {
        _doc = doc; _settings = settings; _owner = owner; _status = status;
    }

    /// <summary>창 제목 "Cube - 파일이름[*]". 경로가 없으면 untitled, 저장 안 된 변경이 있으면 * 표시.</summary>
    public string Title
    {
        get
        {
            string name = _doc.FilePath != null ? System.IO.Path.GetFileName(_doc.FilePath) : "untitled";
            return $"Cube - {name}{(_doc.IsDirty ? "*" : "")}";
        }
    }

    // ---------------------------------------------------------------- 직접 실행

    /// <summary>.cube로 저장한다(확장자가 없으면 붙임). 성공하면 최근 파일에 추가하고 true, 실패하면 상태 메시지에 오류를 보이고 false.</summary>
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

    /// <summary>.cube 파일을 현재 문서에 읽어 들인다(기존 내용 대체). 성공 여부를 돌려준다.</summary>
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

    /// <summary>확인 없이 문서를 비운다(노드·머티리얼·애니메이션·선택 초기화는 Document.Clear가 한다).</summary>
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

    /// <summary>다른 이름으로 저장 다이얼로그. 기본 파일 이름은 현재 파일 이름 또는 untitled.cube.</summary>
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

    /// <summary>미저장 변경 확인 후 열기 다이얼로그를 띄운다.</summary>
    public void ShowOpenDialog() => ConfirmDiscard(() =>
    {
        var fd = MakeDialog(FileDialog.FileModeEnum.OpenFile, "Open Scene");
        fd.AddFilter("*" + CubeFileFormat.Extension, "Cube Scene");
        fd.CurrentDir = _settings.LastSceneDir ?? OS.GetSystemDir(OS.SystemDir.Documents);
        fd.FileSelected += p => { Open(p); fd.QueueFree(); };
        fd.Canceled += fd.QueueFree;
        fd.PopupCentered();
    });

    /// <summary>미저장 변경 확인 후 새 장면.</summary>
    public void NewWithConfirm() => ConfirmDiscard(New);

    /// <summary>미저장 변경 확인 후 최근 파일 열기.</summary>
    public void OpenRecentWithConfirm(string path) => ConfirmDiscard(() => Open(path));

    /// <summary>수정 사항이 있으면 버릴지 묻고, 확인 시 action 실행.</summary>
    /// <remarks>변경이 없으면 즉시 실행한다. 확인 창은 소유 노드에 붙이고 닫히면 해제한다.</remarks>
    public void ConfirmDiscard(Action action)
    {
        if (!_doc.IsDirty) { action(); return; }
        var dlg = new ConfirmationDialog { Title = "Unsaved Changes", DialogText = "The scene has unsaved changes. Discard them?", OkButtonText = "Discard" };
        dlg.Confirmed += () => { action(); dlg.QueueFree(); };
        dlg.Canceled += dlg.QueueFree;
        _owner.AddChild(dlg);
        dlg.PopupCentered();
    }

    /// <summary>파일 시스템 접근 네이티브 FileDialog를 만들어 소유 노드에 붙인다.</summary>
    private FileDialog MakeDialog(FileDialog.FileModeEnum mode, string title)
    {
        var fd = new FileDialog { FileMode = mode, Access = FileDialog.AccessEnum.Filesystem, UseNativeDialog = true, Title = title, Size = new Vector2I(900, 600) };
        _owner.AddChild(fd);
        return fd;
    }
}
