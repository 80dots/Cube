# Cube add-ons for external apps

These add-ons connect external applications to Cube's **Bridge** menu. They are shipped with every Cube release:
in the installer / zip under `addons\<app>\`, as the separate `Cube-<version>-addons.zip` release asset, and inside
the app itself (Bridge → Blender Add-on → Install / Save As…).

| App | File | Install |
|---|---|---|
| Blender 3.0+ | `blender/cube_bridge.py` | Edit → Preferences → Add-ons → Install… → pick the file → enable **Cube Bridge**. Or in Cube: Bridge → Add-ons → Install Blender Add-on (copies it into `%APPDATA%\Blender Foundation\Blender\<ver>\scripts\addons`). Panel: View3D sidebar (N) → **Cube** tab: Auto receive imports `cube_bridge.fbx` written by Cube's Bridge → Send All/Selected to Blender (n-gons, shared vertices, normals, UVs, materials, rig); **Send All to Cube** / **Send Selected to Cube** write `cube_bridge.obj` + `cube_bridge.json` (n-gons, normals, UVs, object origins). |
| Marmoset Toolbag 4/5 | `marmoset/cube_bridge_toolbag.py` | No install needed: Cube's **Bridge → Send to Marmoset Toolbag** launches Toolbag with this script (filled with the FBX path), which imports `cube_bridge.fbx` as a linked, auto-reloading model with materials and frames it. Sending again while Toolbag is open just rewrites the FBX (Toolbag reloads it). Manual use in an open Toolbag: File → Run Script… → pick the file (it imports `%APPDATA%\Godotpp_userdata\Cuberidge\marmoset\cube_bridge.fbx`). |

All add-ons exchange files through Cube's bridge folder (`%APPDATA%\Godot\app_userdata\Cube\bridge\<app>\`); Cube watches
that folder and reloads automatically (Bridge → Auto Reload). Source: https://github.com/80dots/Cube
