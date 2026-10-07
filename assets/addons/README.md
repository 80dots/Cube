# Cube add-ons for external apps

These add-ons connect external applications to Cube's **Bridge** menu. They are shipped with every Cube release:
in the installer / zip under `addons\<app>\`, as the separate `Cube-<version>-addons.zip` release asset, and inside
the app itself (Bridge → Blender Add-on → Install / Save As…).

| App | File | Install |
|---|---|---|
| Blender 3.0+ | `blender/cube_bridge.py` | Edit → Preferences → Add-ons → Install… → pick the file → enable **Cube Bridge**. Or in Cube: Bridge → Add-ons → Install Blender Add-on (copies it into `%APPDATA%\Blender Foundation\Blender\<ver>\scripts\addons`). Panel: View3D sidebar (N) → **Cube** tab. Cube → Blender is `cube_bridge.fbx` (n-gons, shared vertices, normals, UVs, materials, rig); Blender → Cube is `cube_bridge.obj` (n-gons, normals, UVs). |

All add-ons exchange files through Cube's bridge folder (`%APPDATA%\Godot\app_userdata\Cube\bridge\<app>\`); Cube watches
that folder and reloads automatically (Bridge → Auto Reload). Source: https://github.com/80dots/Cube
