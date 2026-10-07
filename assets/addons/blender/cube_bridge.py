# Cube Bridge — Blender add-on for exchanging meshes with Cube (https://github.com/80dots/Cube).
#
# Install: Blender > Edit > Preferences > Add-ons > Install... > choose this file > enable "Cube Bridge"
#          (or Cube > Bridge > Add-ons > Install Blender Add-on, which copies it into your Blender add-on folders).
# Use:     View3D sidebar (N) > Cube tab.
#          Receive from Cube  — imports cube_bridge.fbx written by Cube > Bridge > Send to Blender
#                               (FBX keeps n-gons, shared vertices, custom normals, UVs, materials/textures, joints/skin).
#          Send to Cube       — exports the scene (or the selection) to cube_bridge.obj (n-gons, normals, UVs) plus cube_bridge.json
#                               (object origins = world matrices, so Cube restores pivot/rotation/scale); Cube reloads it.
#          Auto receive       — watches cube_bridge.fbx and imports it whenever Cube writes it.
#          Export on save     — also sends to Cube every time the .blend is saved.
# The bridge folder defaults to %APPDATA%/Godot/app_userdata/Cube/bridge/blender (Cube's user:// folder on Windows).

bl_info = {
    "name": "Cube Bridge",
    "author": "Cube",
    "version": (1, 2, 0),
    "blender": (3, 0, 0),
    "location": "View3D > Sidebar > Cube",
    "description": "Send meshes to and receive meshes from Cube (FBX in, OBJ out, polygons intact)",
    "category": "Import-Export",
}

import os
import time
import bpy
from bpy.props import StringProperty, BoolProperty
from bpy.app.handlers import persistent

RECEIVE_NAME = "cube_bridge.fbx"   # Cube -> Blender
SEND_NAME = "cube_bridge.obj"      # Blender -> Cube
COLLECTION_NAME = "Cube Bridge"
_last_mtime = 0.0
_status = "Idle"


def default_bridge_dir():
    appdata = os.environ.get("APPDATA")
    if appdata:
        return os.path.join(appdata, "Godot", "app_userdata", "Cube", "bridge", "blender")
    home = os.path.expanduser("~")
    for cand in (os.path.join(home, "Library", "Application Support", "Godot", "app_userdata", "Cube", "bridge", "blender"),
                 os.path.join(home, ".local", "share", "godot", "app_userdata", "Cube", "bridge", "blender")):
        return cand
    return home


def prefs():
    return bpy.context.preferences.addons[__name__].preferences


def bridge_dir():
    d = prefs().bridge_dir or default_bridge_dir()
    return bpy.path.abspath(d)


def receive_file():
    return os.path.join(bridge_dir(), RECEIVE_NAME)


def send_file():
    return os.path.join(bridge_dir(), SEND_NAME)


def file_mtime(path):
    try:
        return os.path.getmtime(path)
    except OSError:
        return 0.0


def set_status(text):
    global _status
    _status = text
    try:
        for win in bpy.context.window_manager.windows:
            for area in win.screen.areas:
                if area.type == 'VIEW_3D':
                    area.tag_redraw()
    except Exception:
        pass


class CubeBridgePreferences(bpy.types.AddonPreferences):
    bl_idname = __name__
    bridge_dir: StringProperty(name="Bridge folder", subtype='DIR_PATH', default=default_bridge_dir(),
                               description="Folder where Cube writes cube_bridge.fbx and reads cube_bridge.obj (Cube user://bridge/blender)")
    auto_receive: BoolProperty(name="Auto receive", default=True, description="Import cube_bridge.fbx whenever Cube writes it")
    export_on_save: BoolProperty(name="Send to Cube when saving the .blend", default=False)
    replace_previous: BoolProperty(name="Replace previously received objects", default=True,
                                   description="Delete the objects imported last time (kept in the 'Cube Bridge' collection) before importing again")
    use_selection: BoolProperty(name="Send only the selection when something is selected", default=True)

    def draw(self, context):
        col = self.layout.column()
        col.prop(self, "bridge_dir")
        col.prop(self, "auto_receive")
        col.prop(self, "export_on_save")
        col.prop(self, "replace_previous")
        col.prop(self, "use_selection")
        col.label(text="Receive: " + receive_file())
        col.label(text="Send:    " + send_file())


def _bridge_collection(create=True):
    coll = bpy.data.collections.get(COLLECTION_NAME)
    if coll is None and create:
        coll = bpy.data.collections.new(COLLECTION_NAME)
        bpy.context.scene.collection.children.link(coll)
    return coll


def _remove_previous(coll):
    for ob in list(coll.objects):
        data = ob.data
        bpy.data.objects.remove(ob, do_unlink=True)
        if data is not None and getattr(data, "users", 1) == 0:
            try:
                if isinstance(data, bpy.types.Mesh): bpy.data.meshes.remove(data)
                elif isinstance(data, bpy.types.Armature): bpy.data.armatures.remove(data)
                elif isinstance(data, bpy.types.Light): bpy.data.lights.remove(data)
            except Exception:
                pass


def receive(context, report=None):
    global _last_mtime
    path = receive_file()
    if not os.path.isfile(path):
        msg = "Bridge file not found: " + path
        if report: report({'WARNING'}, msg)
        set_status(msg)
        return False
    p = prefs()
    coll = _bridge_collection(create=True)
    if p.replace_previous:
        _remove_previous(coll)
    before = set(bpy.data.objects)
    try:
        try:
            bpy.ops.import_scene.fbx(filepath=path, use_custom_normals=True, use_image_search=True)
        except TypeError:
            bpy.ops.import_scene.fbx(filepath=path)
    except Exception as e:
        msg = "FBX import failed: %s" % e
        if report: report({'ERROR'}, msg)
        set_status(msg)
        return False
    new = [ob for ob in bpy.data.objects if ob not in before]
    for ob in new:
        for c in list(ob.users_collection):
            if c is not coll:
                c.objects.unlink(ob)
        if ob.name not in coll.objects:
            coll.objects.link(ob)
    _last_mtime = file_mtime(path)
    msg = "Received %d object(s) from Cube (%s)" % (len(new), time.strftime("%H:%M:%S"))
    if report: report({'INFO'}, msg)
    set_status(msg)
    return True



def _write_origins(path_json, objects):
    """Export-space (Y-up, -Z forward) world matrices of the exported mesh objects: Cube restores origin/rotation/scale from them."""
    import json
    from bpy_extras.io_utils import axis_conversion
    G = axis_conversion(to_forward='-Z', to_up='Y').to_4x4()
    Ginv = G.inverted()
    data = {}
    for ob in objects:
        if ob.type != 'MESH':
            continue
        M = G @ ob.matrix_world @ Ginv
        data[ob.name] = [[float(M[r][c]) for c in range(4)] for r in range(4)]
    with open(path_json, "w", encoding="utf-8") as f:
        json.dump({"version": 1, "axes": "Y-up,-Z forward", "objects": data}, f)


def _export_obj(path, use_sel):
    # Blender 3.3+/4.x: new OBJ exporter (keeps n-gons, writes normals/UVs). Older: legacy python exporter.
    if hasattr(bpy.ops.wm, "obj_export"):
        bpy.ops.wm.obj_export(filepath=path, export_selected_objects=use_sel, apply_modifiers=True,
                              export_normals=True, export_uv=True, export_materials=False,
                              export_triangulated_mesh=False, forward_axis='NEGATIVE_Z', up_axis='Y', global_scale=1.0)
    else:
        bpy.ops.export_scene.obj(filepath=path, use_selection=use_sel, use_mesh_modifiers=True, use_normals=True,
                                 use_uvs=True, use_materials=False, use_triangles=False,
                                 axis_forward='-Z', axis_up='Y', global_scale=1.0)


def send(context, report=None):
    path = send_file()
    os.makedirs(os.path.dirname(path), exist_ok=True)
    p = prefs()
    use_sel = bool(p.use_selection and context.selected_objects)
    try:
        _export_obj(path, use_sel)
        objects = context.selected_objects if use_sel else list(context.view_layer.objects)
        _write_origins(os.path.splitext(path)[0] + ".json", objects)
    except Exception as e:
        msg = "OBJ export failed: %s" % e
        if report: report({'ERROR'}, msg)
        set_status(msg)
        return False
    msg = "Sent %s to Cube (%s)" % ("selection" if use_sel else "scene", time.strftime("%H:%M:%S"))
    if report: report({'INFO'}, msg)
    set_status(msg)
    return True


class CUBE_OT_receive(bpy.types.Operator):
    bl_idname = "cube.receive_from_cube"
    bl_label = "Receive from Cube"
    bl_description = "Import cube_bridge.fbx written by Cube (Bridge > Send to Blender)"

    def execute(self, context):
        return {'FINISHED'} if receive(context, self.report) else {'CANCELLED'}


class CUBE_OT_send(bpy.types.Operator):
    bl_idname = "cube.send_to_cube"
    bl_label = "Send to Cube"
    bl_description = "Export the scene (or the selection) to cube_bridge.obj; Cube reloads it automatically"

    def execute(self, context):
        return {'FINISHED'} if send(context, self.report) else {'CANCELLED'}


class CUBE_OT_open_folder(bpy.types.Operator):
    bl_idname = "cube.open_bridge_folder"
    bl_label = "Open Bridge Folder"

    def execute(self, context):
        d = bridge_dir()
        os.makedirs(d, exist_ok=True)
        bpy.ops.wm.path_open(filepath=d)
        return {'FINISHED'}


class CUBE_PT_bridge(bpy.types.Panel):
    bl_label = "Cube Bridge"
    bl_space_type = 'VIEW_3D'
    bl_region_type = 'UI'
    bl_category = "Cube"

    def draw(self, context):
        p = prefs()
        layout = self.layout
        col = layout.column(align=True)
        col.operator("cube.receive_from_cube", icon='IMPORT')
        col.operator("cube.send_to_cube", icon='EXPORT')
        layout.prop(p, "auto_receive")
        layout.prop(p, "export_on_save")
        layout.prop(p, "replace_previous")
        layout.prop(p, "use_selection")
        box = layout.box()
        rp = receive_file()
        box.label(text="in: " + RECEIVE_NAME, icon='FILE_3D' if os.path.isfile(rp) else 'ERROR')
        box.label(text="out: " + SEND_NAME)
        box.label(text=_status)
        layout.operator("cube.open_bridge_folder", icon='FILE_FOLDER')


def _watch():
    global _last_mtime
    try:
        p = prefs()
        if p.auto_receive:
            path = receive_file()
            m = file_mtime(path)
            if m > _last_mtime and (time.time() - m) > 1.0:  # wait until Cube has finished writing
                if _last_mtime == 0.0:
                    _last_mtime = m  # file that existed before the add-on started: don't import it unasked
                else:
                    receive(bpy.context)
    except Exception as e:
        print("Cube Bridge watcher:", e)
    return 1.0


@persistent
def _on_save(_dummy):
    try:
        if prefs().export_on_save:
            send(bpy.context)
    except Exception as e:
        print("Cube Bridge export on save failed:", e)


classes = (CubeBridgePreferences, CUBE_OT_receive, CUBE_OT_send, CUBE_OT_open_folder, CUBE_PT_bridge)


def register():
    global _last_mtime
    for c in classes:
        bpy.utils.register_class(c)
    _last_mtime = 0.0
    if not bpy.app.timers.is_registered(_watch):
        bpy.app.timers.register(_watch, first_interval=1.0, persistent=True)
    if _on_save not in bpy.app.handlers.save_post:
        bpy.app.handlers.save_post.append(_on_save)


def unregister():
    if bpy.app.timers.is_registered(_watch):
        bpy.app.timers.unregister(_watch)
    if _on_save in bpy.app.handlers.save_post:
        bpy.app.handlers.save_post.remove(_on_save)
    for c in reversed(classes):
        bpy.utils.unregister_class(c)


if __name__ == "__main__":
    register()
