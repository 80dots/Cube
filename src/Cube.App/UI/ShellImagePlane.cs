using Cube.App.Bridge;
using Cube.Core.Commands;
using Cube.Core.Scene;
using Godot;
using NVec3 = System.Numerics.Vector3;

namespace Cube.App.UI;

/// <summary>
/// 이미지 플레인(v0.0.71, Maya View → Image Plane → Import Image...): Create → Image Plane → Import Image...로 파일을 고르면
/// 활성 뷰포트 카메라를 마주 보는 사각형 노드(<see cref="ImagePlaneShape"/>)를 만든다. 직교 뷰(front/side/top…)에서 만들면
/// 그 뷰에서만 보이도록(OnlyView) 붙이고, 원근 뷰에서는 모든 뷰에 보인다. 크기는 이미지 비율로 긴 변 2, 위치는 뷰 피벗에서 카메라 반대쪽으로 10.
/// 속성(이미지·크기·불투명도·표시 뷰·잠금)은 Properties의 Image Plane 그룹에서 편집한다.
/// </summary>
public partial class Shell
{
    private void RegisterImagePlaneActions()
    {
        Actions.Register("create.imagePlane", "Import Image...", () => PickImageFile("Import Image Plane", path => CreateImagePlane(path)));
        Actions.Register("create.imagePlaneEmpty", "Image Plane (empty)", () => CreateImagePlane(""), repeatable: true);
    }

    /// <summary>네이티브 파일 다이얼로그로 이미지를 고른다.</summary>
    public void PickImageFile(string title, Action<string> onPicked)
    {
        var dlg = new FileDialog { FileMode = FileDialog.FileModeEnum.OpenFile, Access = FileDialog.AccessEnum.Filesystem, UseNativeDialog = true, Title = title };
        dlg.Filters = new[] { "*.png, *.jpg, *.jpeg, *.bmp, *.tga, *.webp, *.exr, *.hdr ; Images" };
        dlg.FileSelected += path => { onPicked(path); dlg.QueueFree(); };
        dlg.Canceled += () => dlg.QueueFree();
        AddChild(dlg);
        dlg.PopupCentered();
    }

    /// <summary>이미지 플레인 노드를 활성 뷰포트 기준으로 만든다(Undo 가능). 돌려주는 값은 만든 노드.</summary>
    public SceneNode CreateImagePlane(string path)
    {
        var panel = Viewport;
        var cam = panel.CameraController;
        // 크기: 플레인 깊이에서 뷰에 꽉 차게(Maya처럼 이미지 전체가 보이도록) — 세로 = 그 깊이의 뷰 높이(직교 = OrthoSize, 원근 = 2·D·tan(fov/2)),
        // 가로는 이미지 비율(읽기 실패 시 정사각)로, 뷰 폭을 넘으면 폭에 맞춘다.
        const float depthBehind = 10f;
        float viewH = cam.State.IsOrtho ? cam.State.OrthoSize : 2f * (cam.State.Distance + depthBehind) * MathF.Tan(cam.State.FovDegrees * 0.5f * MathF.PI / 180f);
        float viewW = viewH * MathF.Max(panel.Aspect, 0.01f);
        float aspect = 1f;
        if (!string.IsNullOrEmpty(path))
        {
            var img = Image.LoadFromFile(path);
            if (img != null && img.GetWidth() > 0 && img.GetHeight() > 0) aspect = img.GetWidth() / (float)img.GetHeight();
        }
        float h = viewH * 0.9f, w = h * aspect;
        if (w > viewW * 0.9f) { w = viewW * 0.9f; h = w / aspect; }
        // 방향: 카메라 기저 그대로(쿼드 +Z = 카메라 쪽), 위치: 피벗에서 카메라 반대쪽으로 10(지오메트리 뒤에 놓여 트레이싱 배경이 됨)
        var basis = panel.Camera.GlobalTransform.Basis;
        var pivot = cam.State.Pivot;
        var pos = pivot - basis.Z.ToNumerics() * depthBehind;
        var m = new System.Numerics.Matrix4x4(
            basis.X.X, basis.X.Y, basis.X.Z, 0,
            basis.Y.X, basis.Y.Y, basis.Y.Z, 0,
            basis.Z.X, basis.Z.Y, basis.Z.Z, 0,
            pos.X, pos.Y, pos.Z, 1);
        var local = Transform3.FromMatrix(m);
        // 직교 프리셋 뷰에서 만들면 그 뷰에만 표시(Maya "Looking Through Camera"), 원근은 모든 뷰
        string? onlyView = cam.Kind != Core.Camera.ViewKind.Persp ? cam.Kind.ToString().ToLowerInvariant() : null;
        var shape = new ImagePlaneShape { ImagePath = path, Width = w, Height = h, OnlyView = onlyView };
        var node = new SceneNode { Name = Document.UniqueName("imagePlane1"), Shape = shape, Local = local };
        Document.Undo.Push(new AddNodeCommand("Create Image Plane", node));
        HelpLine.Text = string.IsNullOrEmpty(path) ? "Image Plane created (set the image in Properties → Image Plane)." : $"Image Plane: {System.IO.Path.GetFileName(path)} ({w:0.##} × {h:0.##}){(onlyView != null ? $", shown in {onlyView} view only" : "")}.";
        return node;
    }
}
