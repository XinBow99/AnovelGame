# MiG-25 → Unity. Run inside the Blender that has SM_DST_Vehicle_MiG25.blend open
# (MCP: runpy.run_path(this, run_name="__main__")), or headless:
#   blender -b SM_DST_Vehicle_MiG25.blend --factory-startup -P mig25_export_unity.py -- [out_dir]
# 1. Rebuilds the model from mig25_build.py (so the export always matches the script).
# 2. Writes HDRP skin textures for the 4 m panel tile (UVs are world-metre box projection):
#    T_DST_MiG25_Skin_BaseColor (sRGB), _Normal (OpenGL, from the panel height), _Mask
#    (R metallic, G AO from seam grime, B detail 0, A smoothness), plus the insignia PNG.
# 3. Exports SM_DST_Vehicle_MiG25.fbx at REAL size; Unity's importer applies ×0.5
#    (DSTMiG25Setup sets globalScale = 0.5) so part transforms stay at scale 1.
#    Forward -Z / up Y; the axis conversion is baked by Unity (ModelImporter.bakeAxisConversion),
#    which maps Blender +Y → Unity +Z and ignores the FBX forward setting. The model's nose is on
#    Blender -Y, so the export turns the aircraft 180° about Z first (airframe vertices + part
#    transforms), exports, then rebuilds so the .blend keeps its orientation. Result: nose on
#    Unity +Z; Blender +X (pilot's left, the _L parts) on Unity -X.
#    — Blender's bake_space_transform left the rotated part transforms unconverted.
import bpy, os, sys, runpy
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__)) if "__file__" in globals() else \
    r"C:\Users\asdew\My project\Tools\Blender"
OUT = r"C:\Users\asdew\My project\Assets\DUST\Vehicles\MiG25"
if "--" in sys.argv and len(sys.argv) > sys.argv.index("--") + 1:
    OUT = sys.argv[sys.argv.index("--") + 1]


def save_png(name, rgba):
    res = rgba.shape[0]
    img = bpy.data.images.get(name + "_export")
    if img is None or img.size[0] != res:
        if img:
            bpy.data.images.remove(img)
        img = bpy.data.images.new(name + "_export", res, res, alpha=True)
    img.colorspace_settings.name = "Non-Color"   # write values as given; sRGB flag is set in Unity
    img.pixels.foreach_set(np.clip(rgba, 0, 1).astype(np.float32).ravel())
    path = os.path.join(OUT, "Textures", name + ".png")
    img.filepath_raw = path
    img.file_format = "PNG"
    img.save()
    print("wrote", path)


def tile_noise(res, cells, rng):
    """Tileable value noise (wraps at the texture edge like the panel tile)."""
    g = rng.random((cells, cells))
    ys, xs = np.mgrid[0:res, 0:res] / res * cells
    i, j = xs.astype(int), ys.astype(int)
    fx, fy = xs - i, ys - j
    fx, fy = fx * fx * (3 - 2 * fx), fy * fy * (3 - 2 * fy)
    i1, j1 = (i + 1) % cells, (j + 1) % cells
    a = g[j, i] * (1 - fx) + g[j, i1] * fx
    b = g[j1, i] * (1 - fx) + g[j1, i1] * fx
    return a * (1 - fy) + b * fy


def skin_textures(arr, res):
    H, tint, grime = arr["height"], arr["tint"], arr["grime"]
    rng = np.random.default_rng(7)
    n = tile_noise(res, 4, rng) * 0.5 + tile_noise(res, 16, rng) * 0.3 + tile_noise(res, 64, rng) * 0.2
    # weathered stainless grey — same ramp as the Blender skin shader (dark 72/70/66 % → base)
    base = np.array([0.42, 0.44, 0.45])
    dark = base * np.array([0.72, 0.70, 0.66])
    f = np.clip((n - 0.35) / 0.35, 0, 1)[..., None]
    col = (dark * (1 - f) + base * f) * tint[..., None]
    # Blender stores pixels linear; PNG wants sRGB-encoded colour
    srgb = np.where(col <= 0.0031308, col * 12.92, 1.055 * np.power(np.clip(col, 0, 1), 1 / 2.4) - 0.055)
    save_png("T_DST_MiG25_Skin_BaseColor", np.dstack([srgb, np.ones_like(H)]))
    # normal (OpenGL +Y) from height; 1 height unit ≈ 7 mm — 3× the Blender bump so seams and
    # rivets still read at the ×0.5 game scale (measured: 2.4 mm gave max 14° tilt, too faint)
    px_m = 4.0 / res
    hm = H * 0.0072
    dx = (np.roll(hm, -1, 1) - np.roll(hm, 1, 1)) / (2 * px_m)
    dy = (np.roll(hm, -1, 0) - np.roll(hm, 1, 0)) / (2 * px_m)
    nrm = np.dstack([-dx, -dy, np.ones_like(hm)])
    nrm /= np.linalg.norm(nrm, axis=2, keepdims=True)
    save_png("T_DST_MiG25_Skin_Normal", np.dstack([nrm * 0.5 + 0.5, np.ones_like(H)]))
    rough = 0.38 + 0.30 * n
    mask = np.dstack([np.full_like(H, 0.55), 1.0 - 0.45 * grime, np.zeros_like(H), 1.0 - rough])
    save_png("T_DST_MiG25_Skin_Mask", mask)


def main():
    os.makedirs(os.path.join(OUT, "Textures"), exist_ok=True)
    ns = runpy.run_path(os.path.join(HERE, "mig25_build.py"))
    ns["build"]()
    arr = ns["PANEL_ARRAYS"]
    skin_textures(arr, arr["height"].shape[0])
    # insignia: copy the generated PNG (straight alpha) into the Unity folder
    ins = bpy.data.images["T_DST_Insignia_BaseColor"]
    px = np.empty(ins.size[0] * ins.size[1] * 4, np.float32)
    ins.pixels.foreach_get(px)
    rgba = px.reshape(ins.size[1], ins.size[0], 4)
    rgb = rgba[..., :3]
    rgb = np.where(rgb <= 0.0031308, rgb * 12.92, 1.055 * np.power(np.clip(rgb, 0, 1), 1 / 2.4) - 0.055)
    save_png("T_DST_MiG25_Insignia", np.dstack([rgb, rgba[..., 3]]))

    col = bpy.data.collections["MiG25"]
    vl = bpy.context.view_layer
    for o in vl.objects:
        o.select_set(False)
    for o in col.objects:
        o.select_set(True)
    vl.objects.active = bpy.data.objects["MiG25_Root"]
    # nose to Blender +Y for the export (see header)
    from mathutils import Matrix
    import math
    Rz = Matrix.Rotation(math.pi, 4, "Z")
    air = bpy.data.objects["MiG25_Airframe"]
    air.data.transform(Rz)
    for o in list(air.children):
        o.matrix_world = Rz @ o.matrix_world
    bpy.context.view_layer.update()
    fbx = os.path.join(OUT, "SM_DST_Vehicle_MiG25.fbx")
    bpy.ops.export_scene.fbx(
        filepath=fbx, use_selection=True, object_types={"EMPTY", "MESH"},
        apply_scale_options="FBX_SCALE_ALL", axis_forward="-Z", axis_up="Y",
        bake_space_transform=False, bake_anim=False, mesh_smooth_type="FACE",
        use_mesh_modifiers=True, use_tspace=True, add_leaf_bones=False)
    print("exported", fbx)
    ns["build"]()          # back to the authored orientation (nose -Y)


if __name__ == "__main__":
    main()
