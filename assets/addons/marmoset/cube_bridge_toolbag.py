"""
Cube Bridge for Marmoset Toolbag (4 / 5)

Imports the FBX that Cube writes with Bridge -> Send to Marmoset Toolbag
(%APPDATA%\\Godot\\app_userdata\\Cube\\bridge\\marmoset\\cube_bridge.fbx) as a linked (auto-reloading) model
with its materials, then frames the scene. If the model is already in the scene it is reused: sending again
from Cube just rewrites the FBX and Toolbag reloads it.

Cube runs this automatically when it launches Toolbag (toolbag.exe <script>). To use it manually in an already
open Toolbag: File -> Run Script... (or Plugins menu) and pick this file.
"""
import os
import mset

FBX = os.environ.get("CUBE_BRIDGE_FBX") or os.path.join(os.environ.get("APPDATA", ""), "Godot", "app_userdata", "Cube", "bridge", "marmoset", "cube_bridge.fbx")


def _norm(p):
    return os.path.normcase(os.path.abspath(p))


def run(fbx=FBX):
    if not os.path.isfile(fbx):
        print("[Cube Bridge] not found: " + fbx + " (send from Cube first: Bridge -> Send to Marmoset Toolbag)")
        return None
    target = _norm(fbx)
    found = None
    for o in mset.getAllObjects():
        if isinstance(o, mset.ExternalObject) and o.path and _norm(o.path) == target:
            found = o
            break
    if found is None:
        found = mset.importModel(fbx)
        found.name = "Cube Bridge"
    found.loadMaterials = True
    found.autoReload = True
    mset.frameScene()
    print("[Cube Bridge] loaded " + fbx)
    return found


run()
