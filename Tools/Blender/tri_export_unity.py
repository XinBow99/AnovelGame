# 沙陶翻盤獸 → Unity（背景執行，不存檔）：
#   blender -b DST_Creature_Tripod_Redesign.blend --factory-startup -P tri_export_unity.py -- <輸出資料夾> [貼圖大小]
# 1. 每個部件展 UV（58 個部件一起排進同一張圖）
# 2. 合併之前用 Cycles 把程序材質烘成貼圖：顏色、粗糙度、切線空間法線（裂紋、甲片的凹凸）。
#    材質用的是物件座標 / Generated 座標，合併後座標會變，所以一定要先烘。
# 3. 合併成一個蒙皮網格 TRI_Mesh（保留 5 個材質欄位），跟 TRI_Rig 一起匯出 SK_DST_Tripod.fbx
# 4. 輸出 T_DST_Tripod_BaseColor.png / T_DST_Tripod_Normal.png / T_DST_Tripod_Mask.png（HDRP 遮罩：R 金屬、G AO、B 細節、A 光滑度）
import bpy, sys, os, math

args = sys.argv[sys.argv.index("--") + 1:]
out_dir = args[0]
size = int(args[1]) if len(args) > 1 else 2048
os.makedirs(out_dir, exist_ok=True)

col = bpy.data.collections["TRI_Redesign"]
rig = bpy.data.objects["TRI_Rig"]
if rig.animation_data: rig.animation_data.action = None
meshes = [o for o in col.objects if o.type == "MESH" and any(m.type == "ARMATURE" for m in o.modifiers)]
scene = bpy.context.scene
vl = bpy.context.view_layer


def select(objs, active):
    for o in vl.objects: o.select_set(False)
    for o in objs: o.select_set(True)
    vl.objects.active = active


# ---- 1. UV
for o in meshes:
    if not o.data.uv_layers: o.data.uv_layers.new(name="UVMap")
select(meshes, meshes[0])
bpy.ops.object.mode_set(mode="EDIT")
bpy.ops.mesh.select_all(action="SELECT")
bpy.ops.uv.smart_project(angle_limit=math.radians(66), island_margin=0.003, scale_to_bounds=False)
bpy.ops.uv.pack_islands(margin=0.003)
bpy.ops.object.mode_set(mode="OBJECT")
print("uv done")

# ---- 2. 烘焙
try:
    scene.render.engine = "CYCLES"
except TypeError as e:
    print("engine", e)
scene.cycles.device = "CPU"
scene.cycles.samples = 6
scene.render.bake.margin = 6
mats = {m for o in meshes for m in o.data.materials if m}


def bake(kind, name, colorspace, **kw):
    img = bpy.data.images.new(name, size, size, alpha=False, float_buffer=False)
    img.colorspace_settings.name = colorspace
    nodes = []
    for m in mats:
        n = m.node_tree.nodes.new("ShaderNodeTexImage"); n.image = img
        m.node_tree.nodes.active = n; nodes.append((m, n))
    select(meshes, meshes[0])
    bpy.ops.object.bake(type=kind, use_clear=True, margin=6, **kw)
    for m, n in nodes: m.node_tree.nodes.remove(n)
    print("baked", kind)
    return img


base = bake("DIFFUSE", "T_DST_Tripod_BaseColor", "sRGB", pass_filter={"COLOR"})
rough = bake("ROUGHNESS", "T_DST_Tripod_Rough", "Non-Color")
normal = bake("NORMAL", "T_DST_Tripod_Normal", "Non-Color", normal_space="TANGENT")


def save(img, path):
    img.filepath_raw = path; img.file_format = "PNG"; img.save()
    print("saved", path)


save(base, os.path.join(out_dir, "T_DST_Tripod_BaseColor.png"))
save(normal, os.path.join(out_dir, "T_DST_Tripod_Normal.png"))
# HDRP 遮罩：R 金屬 0、G AO 1、B 細節遮罩 0、A 光滑度 = 1 − 粗糙度
px = list(rough.pixels)
mask = bpy.data.images.new("T_DST_Tripod_Mask", size, size, alpha=True)
mask.colorspace_settings.name = "Non-Color"
out = [0.0] * len(px)
for i in range(0, len(px), 4):
    out[i] = 0.0; out[i + 1] = 1.0; out[i + 2] = 0.0; out[i + 3] = 1.0 - px[i]
mask.pixels = out
save(mask, os.path.join(out_dir, "T_DST_Tripod_Mask.png"))

# ---- 3. 合併、匯出
select(meshes, meshes[0])
bpy.ops.object.join()
body = vl.objects.active
body.name = "TRI_Mesh"; body.data.name = "TRI_Mesh"
for m in body.modifiers:
    if m.type == "ARMATURE": m.object = rig
print("joined", len(meshes), "verts", len(body.data.vertices), "mats", [m.name for m in body.data.materials], "uv", [u.name for u in body.data.uv_layers])
select([body, rig], body)
fbx = os.path.join(out_dir, "SK_DST_Tripod.fbx")
bpy.ops.export_scene.fbx(
    filepath=fbx, use_selection=True, object_types={"ARMATURE", "MESH"},
    apply_scale_options="FBX_SCALE_ALL", axis_forward="-Z", axis_up="Y",
    add_leaf_bones=False, use_armature_deform_only=False, bake_anim=False,
    mesh_smooth_type="FACE", use_mesh_modifiers=False, use_tspace=True)
print("exported", fbx)
