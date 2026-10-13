# Blender 背景執行：把每個 FBX 切成 Voronoi 碎塊，輸出成 <name>_Frac.fbx（每塊一個物件，位置對齊原模型）
# 用法：blender -b --factory-startup -P fracture.py -- <out_dir> <src.fbx>:<pieces> ...
import bpy, bmesh, sys, os, random, zlib
from mathutils import Vector
from mathutils.bvhtree import BVHTree

args = sys.argv[sys.argv.index("--") + 1:]
out_dir = args[0]
jobs = [a.rsplit(":", 1) for a in args[1:]]


def clear():
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete()
    for m in list(bpy.data.meshes):
        bpy.data.meshes.remove(m)


def inside(bvh, p):
    # 往 +Z 打射線數交點數（奇數 = 在裡面）；再用另一個方向確認一次
    def count(d):
        n, o = 0, p.copy()
        for _ in range(64):
            hit, nrm, idx, dist = bvh.ray_cast(o, d)
            if hit is None:
                break
            n += 1
            o = hit + d * 1e-4
        return n
    return count(Vector((0.0, 0.0, 1.0))) % 2 == 1 and count(Vector((0.31, 0.77, -0.55)).normalized()) % 2 == 1


def seeds_for(obj, n, rng):
    me = obj.data
    bm = bmesh.new(); bm.from_mesh(me)
    bvh = BVHTree.FromBMesh(bm)
    lo = Vector([min(v.co[i] for v in bm.verts) for i in range(3)])
    hi = Vector([max(v.co[i] for v in bm.verts) for i in range(3)])
    bm.free()
    pts = []
    tries = 0
    while len(pts) < n and tries < n * 400:
        tries += 1
        p = Vector([rng.uniform(lo[i], hi[i]) for i in range(3)])
        if not inside(bvh, p):
            continue
        pts.append(p)
    if len(pts) < 2:      # 開口的網格（判斷不出裡外）：就在包圍盒裡撒
        pts = [Vector([rng.uniform(lo[i], hi[i]) for i in range(3)]) for _ in range(n)]
    return pts, lo, hi


def fracture(obj, n, rng):
    pts, lo, hi = seeds_for(obj, n, rng)
    size = max((hi - lo).length, 0.01)
    uv_scale = 1.0 / 2.0       # 切面的 UV：每 2 m 一張貼圖
    pieces = []
    for i, p in enumerate(pts):
        bm = bmesh.new(); bm.from_mesh(obj.data)
        for j, q in enumerate(pts):
            if i == j:
                continue
            mid = (p + q) * 0.5
            nrm = (q - p).normalized()
            geom = bm.verts[:] + bm.edges[:] + bm.faces[:]
            res = bmesh.ops.bisect_plane(bm, geom=geom, plane_co=mid, plane_no=nrm, clear_outer=True, dist=1e-5)
            # 只補落在切面上的開口（原本就開著的洞，例如陶罐的口，不要補）；scanfill 會處理環狀的切面（中空的東西）
            on = lambda v: abs((v.co - mid).dot(nrm)) < 1e-4
            cut = [e for e in bm.edges if e.is_boundary and on(e.verts[0]) and on(e.verts[1])]
            if len(cut) >= 3:
                res = bmesh.ops.triangle_fill(bm, use_beauty=True, use_dissolve=False, edges=cut, normal=nrm)
                # 切面朝外：被切掉的那一側（clear_outer 清掉的是法線那一側）
                caps = [f for f in res["geom"] if isinstance(f, bmesh.types.BMFace)]
                for f in caps:
                    f.normal_update()
                flip = [f for f in caps if f.normal.dot(nrm) < 0]
                if flip:
                    bmesh.ops.reverse_faces(bm, faces=flip)
            if not bm.faces:
                break
        if len(bm.faces) < 4:
            bm.free(); continue
        uv = bm.loops.layers.uv.active
        if uv:
            for f in bm.faces:
                if all(l[uv].uv.length_squared == 0 for l in f.loops) or f.tag:
                    pass
            # 沒有 UV 的面（新補的）給盒狀投影
            for f in bm.faces:
                us = [l[uv].uv.copy() for l in f.loops]
                if len(set((round(u.x, 6), round(u.y, 6)) for u in us)) <= 1:
                    a = max(range(3), key=lambda k: abs(f.normal[k]))
                    ax = [k for k in range(3) if k != a]
                    for l in f.loops:
                        l[uv].uv = (l.vert.co[ax[0]] * uv_scale, l.vert.co[ax[1]] * uv_scale)
        me = bpy.data.meshes.new(f"{obj.name}_p{i:02d}")
        bm.to_mesh(me); bm.free()
        for m in obj.data.materials:
            me.materials.append(m)
        po = bpy.data.objects.new(me.name, me)
        bpy.context.collection.objects.link(po)
        po.matrix_world = obj.matrix_world.copy()
        pieces.append(po)
    return pieces


for src, n in jobs:
    clear()
    bpy.ops.import_scene.fbx(filepath=src)
    meshes = [o for o in bpy.context.scene.objects if o.type == "MESH"]
    if not meshes:
        print("NO MESH", src); continue
    # 清掉新匯入時可能帶的 holes_fill 標記用的空 UV 等等，只處理第一個網格（這些 FBX 都是一個網格）
    obj = meshes[0]
    base = os.path.splitext(os.path.basename(src))[0]
    # 原本的 UV 預設全部不同；為了判斷「新補的面」，先把原本的面標記起來
    rng = random.Random(zlib.crc32(base.encode()))
    pieces = fracture(obj, int(n), rng)
    tris = sum(len(p.data.polygons) for p in pieces)
    bpy.data.objects.remove(obj)
    for o in bpy.context.scene.objects:
        o.select_set(o in pieces)
    out = os.path.join(out_dir, base + "_Frac.fbx")
    bpy.ops.export_scene.fbx(filepath=out, use_selection=True, object_types={"MESH"}, apply_scale_options="FBX_SCALE_UNITS",
                             mesh_smooth_type="FACE", use_mesh_modifiers=False, add_leaf_bones=False, bake_anim=False)
    print(f"OK {base}: {len(pieces)} pieces, {tris} faces -> {out}")
    # ---- 預覽：炸開的樣子（只看，不影響輸出）
    if os.environ.get("FRAC_PREVIEW"):
        c = sum((p.matrix_world @ (sum((v.co for v in p.data.vertices), Vector()) / max(1, len(p.data.vertices))) for p in pieces), Vector()) / max(1, len(pieces))
        for p in pieces:
            pc = p.matrix_world @ (sum((v.co for v in p.data.vertices), Vector()) / max(1, len(p.data.vertices)))
            p.location += (pc - c) * 0.6
        sc = bpy.context.scene
        sc.render.engine = "BLENDER_WORKBENCH"
        sc.display.shading.light = "STUDIO"
        sc.display.shading.color_type = "RANDOM"
        sc.render.resolution_x = 640; sc.render.resolution_y = 480
        cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam"))
        sc.collection.objects.link(cam); sc.camera = cam
        bpy.ops.object.select_all(action="DESELECT")
        for p in pieces: p.select_set(True)
        bpy.context.view_layer.update()
        cam.location = c + Vector((1.0, -1.6, 0.9)) * 1.0
        cam.rotation_euler = (c - cam.location).to_track_quat("-Z", "Y").to_euler()
        bpy.ops.view3d.camera_to_view_selected() if False else None
        # 把相機往後拉到看得到全部
        r = max((p.matrix_world @ v.co - c).length for p in pieces for v in p.data.vertices)
        d = (cam.location - c).normalized()
        cam.location = c + d * r * 2.6
        sc.render.filepath = os.path.join(os.environ["FRAC_PREVIEW"], base + ".png")
        bpy.ops.render.render(write_still=True)
