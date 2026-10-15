# MiG-25 (issue #7) — procedural blockout at real size (Blender metres).
# Nose points to -Y, Z up, ground at z=0 (gear down). Unity export scales ×0.5 (half-size world).
# Sizes (sourced): span 14.015 m, wing area 61.4 m² (lennundusmuuseum.ee, avia.pro);
# overall length ~23 m incl. nose probe (Hasegawa 1/72 kit = 32.0 x 19.5 cm, kit measured
# from Belenko's MiG-25P; 19.75 m in some tables is wrong). Fins: 8° cant, 54° LE sweep,
# 3.05 m tall; stabilator LE 50°22', span 8.8 m; wing LE 41°-42°30'; wheels nose 2x 700x200,
# main 1300x360 (avia.pro). Anhedral ~4° (militaryperiscope) — inferred where not sourced.
# s = station along body keys (layout drawn at 19.75 m, stretched by L to 22.3 m body);
# Blender y = s*L - 11.6, so body runs y -11.6..+10.7, probe ahead of that.
import bpy, bmesh, math
from mathutils import Vector

COL_NAME = "MiG25"
N = 32  # points per cross-section


L = 22.3 / 19.75


def y_of(s):
    return s * L - 11.6


def S(m):
    """real metres along the body -> station units"""
    return m / L


# ---------------------------------------------------------------- materials
def mat(name, color, metal=0.0, rough=0.5, alpha=1.0, emit=None):
    old = bpy.data.materials.get(name)
    if old:  # rebuild from scratch so re-running the script never stacks nodes
        old.name = name + "_old"
        bpy.data.materials.remove(old)
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    bsdf = next(n for n in m.node_tree.nodes if n.type == "BSDF_PRINCIPLED")
    bsdf.inputs["Base Color"].default_value = (*color, 1.0)
    bsdf.inputs["Metallic"].default_value = metal
    bsdf.inputs["Roughness"].default_value = rough
    if alpha < 1.0:
        bsdf.inputs["Alpha"].default_value = alpha
        try:
            m.surface_render_method = "BLENDED"
        except Exception:
            pass
    m.diffuse_color = (*color, alpha)
    return m


def weathered_steel(name, color):
    """Dull Soviet grey with noise-driven grime/roughness variation."""
    m = mat(name, color, metal=0.55, rough=0.5)
    nt = m.node_tree
    bsdf = next(n for n in nt.nodes if n.type == "BSDF_PRINCIPLED")
    tc = nt.nodes.new("ShaderNodeTexCoord")
    noise = nt.nodes.new("ShaderNodeTexNoise")
    noise.inputs["Scale"].default_value = 1.6
    noise.inputs["Detail"].default_value = 8.0
    ramp = nt.nodes.new("ShaderNodeValToRGB")
    ramp.color_ramp.elements[0].position = 0.35
    ramp.color_ramp.elements[0].color = (color[0] * 0.72, color[1] * 0.70, color[2] * 0.66, 1)
    ramp.color_ramp.elements[1].position = 0.7
    ramp.color_ramp.elements[1].color = (*color, 1)
    rr = nt.nodes.new("ShaderNodeMapRange")
    rr.inputs["To Min"].default_value = 0.38
    rr.inputs["To Max"].default_value = 0.68
    nt.links.new(tc.outputs["Object"], noise.inputs["Vector"])
    nt.links.new(noise.outputs["Fac"], ramp.inputs["Fac"])
    nt.links.new(ramp.outputs["Color"], bsdf.inputs["Base Color"])
    nt.links.new(noise.outputs["Fac"], rr.inputs["Value"])
    nt.links.new(rr.outputs["Result"], bsdf.inputs["Roughness"])
    return m


# ---------------------------------------------------------------- helpers
def collection():
    col = bpy.data.collections.get(COL_NAME)
    if col:
        for o in list(col.objects):
            bpy.data.objects.remove(o, do_unlink=True)
    else:
        col = bpy.data.collections.new(COL_NAME)
        bpy.context.scene.collection.children.link(col)
    for me in list(bpy.data.meshes):           # old builds' meshes, so names stay clean (no .003)
        if me.users == 0 and (me.name.startswith("MiG25_") or me.name.startswith("_join_tmp")):
            bpy.data.meshes.remove(me)
    return col


def new_obj(name, bm, materials, col, smooth=True, recalc=True):
    if recalc:  # outward normals everywhere — Unity culls back faces
        bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    # caps are n-gons; FBX can't export tangents for them (normal maps would break in Unity)
    ngons = [f for f in bm.faces if len(f.verts) > 4]
    if ngons:
        bmesh.ops.triangulate(bm, faces=ngons)
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    for m in materials:
        me.materials.append(m)
    if smooth:
        for p in me.polygons:
            p.use_smooth = True
    ob = bpy.data.objects.new(name, me)
    col.objects.link(ob)
    return ob


def superellipse(cx, cz, a, b, n, count=N, flat_bottom=None):
    """Closed ring in the XZ plane. n=2 ellipse, n>2 squarer."""
    pts = []
    for i in range(count):
        t = 2 * math.pi * i / count
        c, s = math.cos(t), math.sin(t)
        x = a * math.copysign(abs(c) ** (2.0 / n), c)
        z = b * math.copysign(abs(s) ** (2.0 / n), s)
        if flat_bottom is not None:
            z = max(z, -b * flat_bottom)
        pts.append((cx + x, cz + z))
    return pts


def loft(bm, sections, cap_start=True, cap_end=True, shear=None, mat_caps=(0, 0)):
    """sections: list of (s, ring[(x,z)...]); same count each. shear(s,x,z)->dy."""
    rings = []
    for s, ring in sections:
        r = []
        for x, z in ring:
            dy = shear(s, x, z) if shear else 0.0
            r.append(bm.verts.new((x, y_of(s) + dy, z)))
        rings.append(r)
    faces = []
    for a, b in zip(rings[:-1], rings[1:]):
        n = len(a)
        for i in range(n):
            j = (i + 1) % n
            faces.append(bm.faces.new((a[i], a[j], b[j], b[i])))
    caps = []
    if cap_start:
        f = bm.faces.new(list(reversed(rings[0])))
        f.material_index = mat_caps[0]
        caps.append(f)
    if cap_end:
        f = bm.faces.new(rings[-1])
        f.material_index = mat_caps[1]
        caps.append(f)
    return rings, faces, caps


def lerp(a, b, t):
    return a + (b - a) * t


def interp(table, s):
    """table: list of (s, value or tuple) sorted by s; smoothstep between keys."""
    if s <= table[0][0]:
        return table[0][1]
    for (s0, v0), (s1, v1) in zip(table[:-1], table[1:]):
        if s <= s1:
            t = (s - s0) / (s1 - s0)
            t = t * t * (3 - 2 * t)
            if isinstance(v0, tuple):
                return tuple(lerp(x, y, t) for x, y in zip(v0, v1))
            return lerp(v0, v1, t)
    return table[-1][1]


def airfoil(chord, thick, count=12, u0=0.0, u1=1.0):
    """Thin symmetric lenticular section between chord fractions u0..u1 (closed ring):
    list of (x along chord, z). u0/u1 let control surfaces be cut off the trailing edge."""
    n = max(2, round(count * (u1 - u0)))
    us = [u0 + (u1 - u0) * i / n for i in range(n + 1)]
    T = [thick * 4 * u * (1 - u) ** 1.15 * 1.1 for u in us]
    up = [(u * chord, t / 2) for u, t in zip(us, T)]
    lo = [(u * chord, -t / 2) for u, t in zip(us, T)][::-1]
    if u1 >= 0.999:
        lo = lo[1:]       # sharp trailing edge: upper/lower meet
    if u0 <= 0.001:
        lo = lo[:-1]      # leading edge point shared
    return up + lo


def section(root, tip, t):
    return {k: lerp(root[k], tip[k], t) for k in ("le_s", "chord", "thick")} | {
        "px": lerp(root["pos"][0], tip["pos"][0], t), "pz": lerp(root["pos"][1], tip["pos"][1], t)}


def chord_point(root, tip, t, u, side=1, vertical=False, cant=0.0):
    """Point on the mean surface at span fraction t, chord fraction u (hinge lines, pivots)."""
    c = section(root, tip, t)
    x, z = c["px"], c["pz"]
    if vertical:
        x += (c["pz"] - root["pos"][1]) * math.tan(math.radians(cant))
    return Vector((side * x, y_of(c["le_s"] + u * c["chord"]), z))


def surface(name, col, mats, root, tip, steps=6, vertical=False, mirror=True, cant=0.0,
            t0=0.0, t1=1.0, u0=0.0, u1=1.0, sides=None):
    """Lifting surface lofted root->tip, optionally only span t0..t1 and chord u0..u1.
    root/tip: dict(le_s, chord, thick, pos=(x,z)). Mirror across X when mirror=True."""
    bm = bmesh.new()
    sides = sides or ([1, -1] if mirror else [1])
    steps = max(1, round(steps * (t1 - t0)))
    for side in sides:
        rings = []
        for k in range(steps + 1):
            c = section(root, tip, lerp(t0, t1, k / steps))
            ring = []
            for cx, cz in airfoil(c["chord"], c["thick"], u0=u0, u1=u1):
                if vertical:
                    # span goes up (z); thickness along x; cant tilts outward
                    x = c["px"] + cz + (c["pz"] - root["pos"][1]) * math.tan(math.radians(cant))
                    z = c["pz"]
                else:
                    x, z = c["px"], c["pz"] + cz
                ring.append(bm.verts.new((side * x, y_of(c["le_s"] + cx), z)))
            rings.append(ring)
        for a, b in zip(rings[:-1], rings[1:]):
            n = len(a)
            for i in range(n):
                j = (i + 1) % n
                bm.faces.new((a[i], a[j], b[j], b[i]))
        bm.faces.new(rings[-1])
        bm.faces.new(list(reversed(rings[0])))
    return new_obj(name, bm, mats, col)


# ---------------------------------------------------------------- moving parts
HINGES = {}  # object name -> (pivot, hinge axis = local X, up hint)
# Rotation sign conventions in Blender (measured by rotating each part +30° about local X).
# Unity's FBX import flips X (Blender +X -> Unity -X), so re-check signs there once.
#   Flap/Aileron*/Stabilator  +  = trailing edge UP (same sign both sides; ailerons: L=+a, R=-a)
#   Rudder_L/_R               +  = trailing edge toward -X (same sign both = yaw together)
#   CanopyHood                -  = opens (hinged on starboard sill, -X)
#   NoseGear / MainGear_*     -  = retract forward (MiG-25 legs fold forward), + swings aft
#   MainDoorOuter_L, NoseDoor_L  + = swing inboard (close); _R sides mirrored (- closes)
#   MainDoorInner_*           same pattern as outer doors
#   IntakeRamp_*              -  = ramp lowers (more compression)
#   Nozzle_*                  no rotation: scale local Y/Z (<1 = nozzle closes)
#   *Wheel*                   spin about local X; children of their gear leg


def hinge(name, pivot, axis, up=(0, 0, 1)):
    HINGES[name] = (Vector(pivot), Vector(axis).normalized(), Vector(up))


def apply_pivot(ob, pivot, axis, up):
    """Move the origin to the pivot and turn the object so its local X is the hinge axis,
    keeping the mesh where it is. Unity then rotates every moving part about local X."""
    from mathutils import Matrix
    X = axis.normalized()
    Z = (up - X * up.dot(X)).normalized()
    Y = Z.cross(X)
    R = Matrix((X, Y, Z)).transposed()
    M = Matrix.Translation(pivot) @ R.to_4x4()
    ob.data.transform(M.inverted())
    ob.matrix_world = M


def parent_keep(child, par):
    child.parent = par
    child.matrix_parent_inverse = par.matrix_world.inverted()


def join_static(name, objs, col):
    """Merge the non-moving parts into one mesh (keeps materials, UVs, smoothing)."""
    mats, bm = [], bmesh.new()
    for o in objs:
        tb = bmesh.new()
        tb.from_mesh(o.data)
        tb.transform(o.matrix_world)
        remap = []
        for s in o.material_slots:
            if s.material not in mats:
                mats.append(s.material)
            remap.append(mats.index(s.material))
        for f in tb.faces:
            f.material_index = remap[f.material_index] if remap else 0
        tmp = bpy.data.meshes.new("_join_tmp")
        tb.to_mesh(tmp)
        tb.free()
        bm.from_mesh(tmp)
        bpy.data.meshes.remove(tmp)
    for o in objs:
        bpy.data.objects.remove(o, do_unlink=True)
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    for m in mats:
        me.materials.append(m)
    ob = bpy.data.objects.new(name, me)
    col.objects.link(ob)
    return ob


# ---------------------------------------------------------------- insignia
INSIGNIA_PNG = "C:/Users/asdew/OneDrive/桌面/projects/DUST/blender/textures/T_DST_Insignia_BaseColor.png"


def insignia_image(res=512):
    """DUST faction roundel (invented, no real-world nation): umber rim, sand field,
    bone-white sun sinking behind a dark dune, meteor streak. Flat matte paint, chipped."""
    import numpy as np, os
    ys, xs = np.mgrid[0:res, 0:res]
    x = (xs + 0.5) / res * 2 - 1
    y = (ys + 0.5) / res * 2 - 1          # +y = up (Blender images are bottom-up)
    r = np.hypot(x, y)
    umber = np.array([0.20, 0.12, 0.07])
    sand = np.array([0.74, 0.55, 0.30])
    bone = np.array([0.90, 0.86, 0.76])
    dune2 = np.array([0.47, 0.30, 0.16])
    col = np.zeros((res, res, 3)) + sand
    sun = np.hypot(x + 0.08, y + 0.02) < 0.34
    col[sun] = bone
    back_dune = y < -0.02 + 0.20 * np.sin(x * 2.2 + 0.9) - 0.12 * x
    col[back_dune] = dune2
    front_dune = y < -0.30 + 0.16 * np.cos(x * 1.8 - 0.6)
    col[front_dune] = umber
    # meteor: streak falling from upper-right, head clear of the sun
    d = np.array([-0.70, -0.42]); d /= np.linalg.norm(d)
    p0 = np.array([0.66, 0.62])
    rel_x, rel_y = x - p0[0], y - p0[1]
    along = rel_x * d[0] + rel_y * d[1]
    across = np.abs(-rel_x * d[1] + rel_y * d[0])
    streak = (along > 0) & (along < 0.50) & (across < 0.006 + 0.045 * (along / 0.50) ** 1.6)
    col[streak] = bone
    head = np.hypot(x - (p0[0] + d[0] * 0.52), y - (p0[1] + d[1] * 0.52)) < 0.06
    col[head] = bone
    col[(r > 0.84) & (r <= 1.0)] = umber
    col[(r > 0.80) & (r <= 0.84)] = bone
    # weathering: value noise chips + fade
    rng = np.random.default_rng(25)
    def vnoise(cells):
        g = rng.random((cells + 1, cells + 1))
        gx, gy = (xs / res) * cells, (ys / res) * cells
        i, j = gx.astype(int), gy.astype(int)
        fx, fy = gx - i, gy - j
        fx, fy = fx * fx * (3 - 2 * fx), fy * fy * (3 - 2 * fy)
        a = g[j, i] * (1 - fx) + g[j, i + 1] * fx
        b = g[j + 1, i] * (1 - fx) + g[j + 1, i + 1] * fx
        return a * (1 - fy) + b * fy
    n = vnoise(6) * 0.5 + vnoise(24) * 0.3 + vnoise(90) * 0.2
    chip = vnoise(40) * 0.5 + vnoise(120) * 0.5
    col *= (0.82 + 0.25 * n)[..., None]
    alpha = (r <= 1.0).astype(float)
    alpha[(chip > 0.76) & (n > 0.55)] = 0.0                     # paint chipped to bare metal
    alpha *= np.clip((1.0 - r) * res / 3, 0, 1)  # soft edge
    rgba = np.dstack([np.clip(col, 0, 1), alpha]).astype(np.float32)
    img = bpy.data.images.get("T_DST_Insignia_BaseColor")
    if img is None or img.size[0] != res:
        img = bpy.data.images.new("T_DST_Insignia_BaseColor", res, res, alpha=True)
    img.pixels.foreach_set(rgba.ravel())
    os.makedirs(os.path.dirname(INSIGNIA_PNG), exist_ok=True)
    img.filepath_raw = INSIGNIA_PNG
    img.file_format = "PNG"
    img.save()
    return img


# ---------------------------------------------------------------- panels / welds / rivets
TEX_DIR = "C:/Users/asdew/OneDrive/桌面/projects/DUST/blender/textures/"
PANEL_TILE = 4.0  # metres covered by one texture tile
PANEL_ARRAYS = {}


def panel_images(res=2048, seed=25):
    """Tiling skin detail for a welded stainless airframe.
    Height: 0.5 base, grooves at plate seams, raised ripple beads on welded seams, domed rivets
    along riveted seams. Tint: per-plate tone shift (stainless plates differ) + grime in seams."""
    import numpy as np
    rng = np.random.default_rng(seed)
    px = res / PANEL_TILE                       # pixels per metre
    ys, xs = np.mgrid[0:res, 0:res].astype(np.float32)
    H = np.full((res, res), 0.5, np.float32)
    tint = np.ones((res, res), np.float32)
    grime = np.zeros((res, res), np.float32)

    def wrapd(a, b):                            # wrapped pixel distance
        d = np.abs(a - b) % res
        return np.minimum(d, res - d)

    rows = [0.0, 1.05, 1.9, 2.95]               # horizontal seams (m); tile closes at 4.0
    kinds = ["rivet2", "weld", "rivet1", "weld"]
    row_of = np.zeros((res, res), np.int32)
    for i, r0 in enumerate(rows):
        row_of[ys >= r0 * px] = i
    # horizontal seams
    for r0, kind in zip(rows, kinds):
        d = wrapd(ys, r0 * px)
        H = np.where(d < 1.6, 0.30, H)
        grime += np.exp(-(d / 6.0) ** 2)
        if kind == "weld":
            bead = (d > 1.6) & (d < 5.0)
            H = np.where(bead, 0.58 + 0.04 * np.sin(xs * 0.9), H)
        else:
            sp = 0.075 * px
            offs = (-0.022, 0.022) if kind == "rivet2" else (0.022,)
            for o in offs:
                du = xs % sp
                du = np.minimum(du, sp - du)
                dv = wrapd(ys, r0 * px + o * px)
                rr = np.hypot(du, dv)
                H = np.where(rr < 3.0, np.maximum(H, 0.5 + 0.22 * np.sqrt(np.clip(1 - (rr / 3.0) ** 2, 0, 1))), H)
    # vertical seams per row: partition 4 m into plates 0.9–1.7 m wide, random phase
    for i in range(len(rows)):
        while True:
            w = rng.uniform(0.9, 1.7, size=3)
            w = np.append(w, PANEL_TILE - w.sum())
            if 0.8 < w[-1] < 1.8:
                break
        cuts = (np.cumsum(w) + rng.uniform(0, PANEL_TILE)) % PANEL_TILE
        m = row_of == i
        for k, c in enumerate(cuts):
            d = wrapd(xs, c * px)
            H = np.where(m & (d < 1.4), 0.32, H)
            grime += np.where(m, np.exp(-(d / 5.0) ** 2) * 0.8, 0)
            if k % 2 == 0:                      # riveted single row
                sp = 0.07 * px
                dv = ys % sp
                dv = np.minimum(dv, sp - dv)
                rr = np.hypot(wrapd(xs, c * px + 0.02 * px), dv)
                H = np.where(m & (rr < 2.6), np.maximum(H, 0.5 + 0.2 * np.sqrt(np.clip(1 - (rr / 2.6) ** 2, 0, 1))), H)
        # plate tones
        edges = np.sort(cuts)
        cell = np.searchsorted(edges, (xs / px) % PANEL_TILE)
        tones = rng.uniform(0.9, 1.08, size=len(edges) + 1)
        tones[-1] = tones[0]                     # first and last cell are the same wrapped plate
        tint = np.where(m, tones[cell], tint)
    grime = np.clip(grime, 0, 1)
    tint = tint * (1.0 - 0.35 * grime)

    def save(name, arr, rgb=False):
        img = bpy.data.images.get(name)
        if img is None or img.size[0] != res:
            if img:
                bpy.data.images.remove(img)
            img = bpy.data.images.new(name, res, res, alpha=False, float_buffer=False)
        a = np.clip(arr, 0, 1).astype(np.float32)
        rgba = np.dstack([a, a, a, np.ones_like(a)])
        img.pixels.foreach_set(rgba.ravel())
        img.filepath_raw = TEX_DIR + name + ".png"
        img.file_format = "PNG"
        img.save()
        img.colorspace_settings.name = "Non-Color"
        return img

    PANEL_ARRAYS.update(height=H, tint=tint, grime=grime)  # reused by the Unity texture export
    return save("T_DST_MiG25_PanelHeight", H), save("T_DST_MiG25_PanelTint", tint)


def add_panel_detail(m, h_img, t_img):
    nt = m.node_tree
    bsdf = next(n for n in nt.nodes if n.type == "BSDF_PRINCIPLED")
    ramp = next(n for n in nt.nodes if n.type == "VALTORGB")
    uv = nt.nodes.new("ShaderNodeUVMap")
    th = nt.nodes.new("ShaderNodeTexImage"); th.image = h_img
    tt = nt.nodes.new("ShaderNodeTexImage"); tt.image = t_img
    for t in (th, tt):
        nt.links.new(uv.outputs["UV"], t.inputs["Vector"])
    bump = nt.nodes.new("ShaderNodeBump")
    bump.inputs["Strength"].default_value = 0.6
    bump.inputs["Distance"].default_value = 0.004
    nt.links.new(th.outputs["Color"], bump.inputs["Height"])
    nt.links.new(bump.outputs["Normal"], bsdf.inputs["Normal"])
    mul = nt.nodes.new("ShaderNodeMix")
    mul.data_type = "RGBA"
    mul.blend_type = "MULTIPLY"
    mul.inputs["Factor"].default_value = 1.0
    a_in = next(s for s in mul.inputs if s.name == "A" and s.type == "RGBA")
    b_in = next(s for s in mul.inputs if s.name == "B" and s.type == "RGBA")
    out = next(s for s in mul.outputs if s.type == "RGBA")
    nt.links.new(ramp.outputs["Color"], a_in)
    nt.links.new(tt.outputs["Color"], b_in)
    nt.links.new(out, bsdf.inputs["Base Color"])


def cube_uv(ob, tile=PANEL_TILE):
    """Box-project UVs in world metres (dominant normal axis), 1 UV unit = one texture tile."""
    me = ob.data
    bm = bmesh.new()
    bm.from_mesh(me)
    uv = bm.loops.layers.uv.verify()
    mw = ob.matrix_world
    for f in bm.faces:
        n = f.normal
        ax = max(range(3), key=lambda i: abs(n[i]))
        for l in f.loops:
            p = mw @ l.vert.co
            if ax == 0:
                u, v = p.y, p.z
            elif ax == 1:
                u, v = p.x, p.z
            else:
                u, v = p.y, p.x
            l[uv].uv = (u / tile, v / tile)
    bm.to_mesh(me)
    bm.free()


LEG_WHEELS = {}  # wheel object -> gear leg it hangs on


def assemble(col):
    """Hierarchy for Unity: MiG25_Root > MiG25_Airframe (all static parts joined) > moving parts
    (origin = pivot, rotate about local X); wheels are children of their gear legs."""
    for name, (p, ax, up) in HINGES.items():
        apply_pivot(bpy.data.objects[name], p, ax, up)
    for w in LEG_WHEELS:
        c = bpy.data.objects[w]
        bb = [Vector(v) for v in c.bound_box]
        apply_pivot(c, sum(bb, Vector()) / 8, Vector((1, 0, 0)), Vector((0, 0, 1)))
    moving = set(HINGES) | set(LEG_WHEELS)
    static = [o for o in col.objects if o.type == "MESH" and o.name not in moving]
    airframe = join_static("MiG25_Airframe", static, col)
    root = bpy.data.objects.new("MiG25_Root", None)
    root.empty_display_type = "ARROWS"
    col.objects.link(root)
    parent_keep(airframe, root)
    for name in HINGES:
        parent_keep(bpy.data.objects[name], airframe)
    for w, leg in LEG_WHEELS.items():
        parent_keep(bpy.data.objects[w], bpy.data.objects[leg])
    return col


# ---------------------------------------------------------------- cockpit
COCKPIT_S0, COCKPIT_S1 = 5.25, 6.75     # opening under the canopy (stations)
COCKPIT_HALF = 0.35                     # half width of the opening (m)
PANEL_PNG = "C:/Users/asdew/OneDrive/桌面/projects/DUST/blender/textures/T_DST_MiG25_Panel.png"


def panel_image(w=512, h=256):
    """Instrument panel: Soviet turquoise-grey panel, black round gauges with white ticks and
    needles, a dark green radar scope top centre. Rows/positions loosely after MiG-25P panel photos."""
    import numpy as np, os
    ys, xs = np.mgrid[0:h, 0:w].astype(np.float32)
    col = np.zeros((h, w, 3), np.float32) + np.array([0.20, 0.42, 0.42])     # turquoise (linear)
    # panel seams / screws
    for sx in (w * 0.33, w * 0.67):
        col[np.abs(xs - sx) < 1.2] *= 0.6
    rng = np.random.default_rng(3)

    def gauge(cx, cy, r, dial=(0.02, 0.02, 0.02)):
        d = np.hypot(xs - cx, ys - cy)
        ring = (d < r + 2.5) & (d >= r)
        col[ring] = (0.08, 0.08, 0.08)
        face = d < r
        col[face] = dial
        ang = np.arctan2(ys - cy, xs - cx)
        ticks = face & (d > r * 0.78) & (np.abs(np.sin(ang * 12)) < 0.12)
        col[ticks] = (0.85, 0.85, 0.8)
        a = rng.uniform(-2.4, 0.6)
        u = np.array([np.cos(a), np.sin(a)])
        t = (xs - cx) * u[0] + (ys - cy) * u[1]
        n = np.abs(-(xs - cx) * u[1] + (ys - cy) * u[0])
        needle = face & (t > 0) & (t < r * 0.8) & (n < 1.6)
        col[needle] = (0.95, 0.9, 0.6)

    # radar scope (top centre), attitude indicator below it
    gauge(w * 0.5, h * 0.30, 46, dial=(0.02, 0.12, 0.05))
    gauge(w * 0.5, h * 0.74, 30, dial=(0.05, 0.05, 0.08))
    for row, cy in ((0, h * 0.28), (1, h * 0.62)):
        for cx in (w * 0.10, w * 0.22, w * 0.78, w * 0.90):
            gauge(cx, cy, 22 if row == 0 else 19)
    for cx in (w * 0.16, w * 0.84):
        gauge(cx, h * 0.88, 14)
    # warning lights strip
    for i in range(10):
        x0 = w * 0.30 + i * w * 0.04
        lit = (xs > x0) & (xs < x0 + w * 0.03) & (ys > h * 0.92) & (ys < h * 0.97)
        col[lit] = (0.5, 0.08, 0.05) if i % 3 == 0 else (0.45, 0.4, 0.1)
    rgba = np.dstack([np.clip(col, 0, 1), np.ones((h, w), np.float32)])
    img = bpy.data.images.get("T_DST_MiG25_Panel")
    if img is None or img.size[0] != w:
        img = bpy.data.images.new("T_DST_MiG25_Panel", w, h, alpha=False)
    img.pixels.foreach_set(np.ascontiguousarray(rgba[::-1], dtype=np.float32).ravel())   # numpy row 0 = top of the panel
    os.makedirs(os.path.dirname(PANEL_PNG), exist_ok=True)
    img.filepath_raw = PANEL_PNG
    img.file_format = "PNG"
    img.save()
    return img


def build_cockpit(col, M, ZC):
    """Open cockpit under the canopy: tub, instrument panel + glare shield, side consoles,
    K-36-style ejection seat, control stick and throttle. Real size, nose -Y."""
    def box(bm, s0, s1, x0, x1, z0, z1, mat=0):
        vs = [bm.verts.new((x, y_of(s), z)) for s in (s0, s1) for x in (x0, x1) for z in (z0, z1)]
        q = lambda a, b, c, d: bm.faces.new((vs[a], vs[b], vs[c], vs[d]))
        for f in (q(0, 1, 3, 2), q(4, 6, 7, 5), q(0, 4, 5, 1), q(2, 3, 7, 6), q(0, 2, 6, 4), q(1, 5, 7, 3)):
            f.material_index = mat

    def cyl(bm, p0, p1, r, mat=0, segs=10):
        p0, p1 = Vector(p0), Vector(p1)
        q = Vector((0, 0, 1)).rotation_difference((p1 - p0).normalized())
        rings = [[bm.verts.new(p + q @ Vector((r * math.cos(2 * math.pi * i / segs), r * math.sin(2 * math.pi * i / segs), 0))) for i in range(segs)] for p in (p0, p1)]
        for i in range(segs):
            j = (i + 1) % segs
            bm.faces.new((rings[0][i], rings[0][j], rings[1][j], rings[1][i])).material_index = mat
        bm.faces.new(list(reversed(rings[0]))).material_index = mat
        bm.faces.new(rings[1]).material_index = mat

    bm = bmesh.new()
    H = COCKPIT_HALF
    top, floor = ZC + 0.80, ZC - 0.12
    # tub: U-section between the bulkheads (walls inset 1 cm from the skin opening)
    prof = [(-H + 0.01, top), (-H + 0.01, ZC + 0.12), (-H + 0.05, floor), (H - 0.05, floor), (H - 0.01, ZC + 0.12), (H - 0.01, top)]
    rings = [[bm.verts.new((x, y_of(s), z)) for x, z in prof] for s in (COCKPIT_S0, COCKPIT_S1)]
    for i in range(len(prof) - 1):
        bm.faces.new((rings[0][i], rings[0][i + 1], rings[1][i + 1], rings[1][i]))
    bm.faces.new(rings[0])                         # front bulkhead
    bm.faces.new(list(reversed(rings[1])))         # rear bulkhead
    # side consoles (dark) and glare shield
    for side in (1, -1):
        box(bm, 5.55, 6.55, side * (H - 0.13), side * (H - 0.01), ZC + 0.14, ZC + 0.30, mat=2)
    box(bm, COCKPIT_S0, COCKPIT_S0 + 0.22, -H + 0.02, H - 0.02, ZC + 0.66, ZC + 0.74, mat=2)
    # reflector gunsight on the glare shield
    box(bm, COCKPIT_S0 + 0.05, COCKPIT_S0 + 0.15, -0.05, 0.05, ZC + 0.74, ZC + 0.84, mat=2)
    # ejection seat: pan, back (raked), headrest, yellow-black handle between the knees
    box(bm, 6.10, 6.50, -0.22, 0.22, ZC - 0.08, ZC + 0.10, mat=2)
    box(bm, 6.48, 6.66, -0.22, 0.22, ZC + 0.10, ZC + 0.82, mat=2)
    box(bm, 6.52, 6.72, -0.16, 0.16, ZC + 0.82, ZC + 1.02, mat=2)
    box(bm, 6.08, 6.12, -0.06, 0.06, ZC + 0.10, ZC + 0.18, mat=3)
    # control stick + grip, throttle on the left console (pilot's left = +X)
    cyl(bm, (0, y_of(5.95), floor), (0, y_of(5.90), ZC + 0.40), 0.018, mat=2)
    box(bm, 5.87, 5.93, -0.025, 0.025, ZC + 0.40, ZC + 0.52, mat=2)
    cyl(bm, (H - 0.08, y_of(6.05), ZC + 0.30), (H - 0.08, y_of(5.98), ZC + 0.42), 0.014, mat=2)
    new_obj("MiG25_Cockpit", bm, [M["cockpit"], M["panel"], M["seat"], M["handle"]], col, smooth=False, recalc=False)

    # instrument panel: one quad tilted back ~15°, UV 0..1 over the panel texture
    bm = bmesh.new()
    uv = bm.loops.layers.uv.new()
    zb, zt = ZC + 0.22, ZC + 0.66
    sb, st = COCKPIT_S0 + 0.02, COCKPIT_S0 + 0.02 + (zt - zb) * math.tan(math.radians(15)) / L
    vs = [bm.verts.new((x, y_of(s), z)) for x, s, z in ((H - 0.02, sb, zb), (-H + 0.02, sb, zb), (-H + 0.02, st, zt), (H - 0.02, st, zt))]
    f = bm.faces.new(vs)
    for l, (u, v) in zip(f.loops, ((0, 0), (1, 0), (1, 1), (0, 1))):
        l[uv].uv = (u, v)
    new_obj("MiG25_InstrumentPanel", bm, [M["panel"]], col, smooth=False, recalc=False)


# ---------------------------------------------------------------- build
def build():
    col = collection()
    HINGES.clear()
    M = {
        "skin": weathered_steel("M_MiG_Skin", (0.42, 0.44, 0.45)),
        "radome": mat("M_MiG_Radome", (0.16, 0.17, 0.16), rough=0.55),
        "dark": mat("M_MiG_DuctDark", (0.02, 0.02, 0.02), rough=0.9),
        "glass": mat("M_MiG_Canopy", (0.05, 0.07, 0.08), metal=0.0, rough=0.05, alpha=0.45),
        "nozzle": mat("M_MiG_Nozzle", (0.18, 0.15, 0.13), metal=0.9, rough=0.55),
        "tire": mat("M_MiG_Tire", (0.03, 0.03, 0.03), rough=0.85),
        "gear": mat("M_MiG_Gear", (0.55, 0.57, 0.55), metal=0.6, rough=0.4),
        "intake": mat("M_MiG_IntakeInner", (0.20, 0.21, 0.21), metal=0.5, rough=0.6),
        "frame": weathered_steel("M_MiG_Frame", (0.34, 0.35, 0.36)),
        "cockpit": mat("M_MiG_Cockpit", (0.20, 0.42, 0.42), rough=0.6),      # Soviet turquoise
        "seat": mat("M_MiG_CockpitDark", (0.05, 0.06, 0.06), rough=0.7),
        "handle": mat("M_MiG_EjectHandle", (0.8, 0.6, 0.05), rough=0.5),
        "panel": mat("M_MiG_Panel", (1, 1, 1), rough=0.5),
    }
    h_img, t_img = panel_images()
    pimg = panel_image()
    pnt = M["panel"].node_tree
    ptex = pnt.nodes.new("ShaderNodeTexImage")
    ptex.image = pimg
    pnt.links.new(ptex.outputs["Color"], next(n for n in pnt.nodes if n.type == "BSDF_PRINCIPLED").inputs["Base Color"])
    for k in ("skin", "frame"):
        add_panel_detail(M[k], h_img, t_img)
    ZC = 2.3  # fuselage centre height with gear down

    # --- centre fuselage: radome -> cockpit -> spine -> tail cone between nacelles
    body = [  # s, (half-width, half-height, centre z, exponent)
        (0.0, (0.03, 0.03, ZC + 0.05, 2.0)),
        (0.6, (0.24, 0.25, ZC + 0.05, 2.0)),
        (2.0, (0.46, 0.50, ZC + 0.03, 2.0)),
        (3.2, (0.62, 0.68, ZC + 0.01, 2.1)),
        (4.6, (0.72, 0.80, ZC, 2.3)),
        (6.5, (0.76, 0.84, ZC, 2.5)),
        (8.0, (0.78, 0.86, ZC + 0.02, 2.8)),
        (13.0, (0.72, 0.80, ZC + 0.05, 2.8)),
        (16.0, (0.45, 0.62, ZC + 0.05, 2.5)),
        (17.6, (0.18, 0.40, ZC + 0.08, 2.2)),
    ]
    stations = [0.0, 0.15, 0.35, 0.6, 1.0, 1.5, 2.0, 2.6, 3.2, 3.9, 4.6, 5.0, COCKPIT_S0, 5.5, 6.0, 6.5, COCKPIT_S1, 7.0, 8.0, 10.0, 12.0, 13.0, 14.5, 16.0, 17.0, 17.6]
    bm = bmesh.new()
    secs = []
    for s in stations:
        a, b, zc, n = interp(body, s)
        secs.append((s, superellipse(0, zc, a, b, n)))
    _, faces, _ = loft(bm, secs)
    # radome ~3.6 m (side photo)
    for f in faces:
        if f.calc_center_median().y < y_of(3.2):
            f.material_index = 1
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)   # while still closed (outward), then cut
    # cockpit opening: remove the top skin between the bulkheads (the canopy closes it)
    hole = [f for f in faces if y_of(COCKPIT_S0) < f.calc_center_median().y < y_of(COCKPIT_S1)
            and abs(f.calc_center_median().x) < COCKPIT_HALF and f.calc_center_median().z > ZC + 0.4]
    bmesh.ops.delete(bm, geom=hole, context="FACES")
    new_obj("MiG25_Fuselage", bm, [M["skin"], M["radome"]], col, recalc=False)
    build_cockpit(col, M, ZC)

    # --- nose probe (pitot), ~0.9 m ahead of radome tip
    bm = bmesh.new()
    probe = [(S(-0.9), 0.012), (S(-0.5), 0.02), (S(-0.05), 0.04), (0.05, 0.05)]
    loft(bm, [(s, superellipse(0, ZC + 0.05, r, r, 2.0, count=12)) for s, r in probe])
    new_obj("MiG25_Probe", bm, [M["gear"]], col)

    # --- canopy + dorsal spine
    canopy = [
        (5.0, (0.05, 0.02, ZC + 0.80)),
        (5.5, (0.34, 0.30, ZC + 0.78)),
        (6.1, (0.44, 0.44, ZC + 0.76)),
        (6.7, (0.43, 0.42, ZC + 0.76)),
        (7.2, (0.40, 0.34, ZC + 0.76)),
    ]
    # Photo of a MiG-25 hood: heavy riveted frames, one side pane each side, metal centre strip,
    # metal rear. Glass = windscreen + two side panes only.
    # Split at the windscreen/hood joint (s=5.95): windscreen fixed, hood opens sideways on a
    # starboard (-X) sill hinge.
    JOINT = 5.95
    cstations = [5.0, 5.25, 5.5, 5.75, JOINT, 6.15, 6.35, 6.55, 6.75, 6.95, 7.2]
    for part, sts in (("MiG25_Windscreen", [s for s in cstations if s <= JOINT]),
                      ("MiG25_CanopyHood", [s for s in cstations if s >= JOINT])):
        bm = bmesh.new()
        secs = []
        for s in sts:
            a, b, zc = interp(canopy, s)
            secs.append((s, superellipse(0, zc, a, b, 2.2, count=48, flat_bottom=0.0)))
        _, faces, _ = loft(bm, secs, cap_start=(part == "MiG25_Windscreen"), cap_end=(part != "MiG25_Windscreen"))
        # the flat-bottom sections leave a horizontal plate at the sill — that would be a lid over the
        # cockpit (seen from the pilot's eye), so drop those faces
        lid = [f for f in bm.faces if max(v.co.z for v in f.verts) - min(v.co.z for v in f.verts) < 0.004
               and len(f.verts) == 4]
        bmesh.ops.delete(bm, geom=lid, context="FACES")
        for f in bm.faces:
            c = f.calc_center_median()
            s = (c.y + 11.6) / L
            zc = interp(canopy, s)[2]
            if s > 6.75 or c.z < zc + 0.06:
                f.material_index = 1                       # metal rear / sill
            elif s > JOINT and abs(c.x) < 0.11:
                f.material_index = 1                       # centre strip over the hood
        # frame bands (windscreen base; hood front bow + glass rear edge) and sill rails
        for s0 in ((5.42,) if part == "MiG25_Windscreen" else (JOINT + 0.03, 6.75)):
            a, b, zc = interp(canopy, s0)
            r0 = superellipse(0, zc, a + 0.008, b + 0.008, 2.2, count=48, flat_bottom=0.0)
            _, ff, cc = loft(bm, [(s0 - 0.03, r0), (s0 + 0.03, r0)], cap_start=False, cap_end=False)   # open band (caps would wall off the cockpit)
            for f in ff + cc:
                f.material_index = 2
        for side in (1, -1):
            rail = []
            for s in sts[1:] if part == "MiG25_Windscreen" else sts:
                a, b, zc = interp(canopy, s)
                rail.append((s, superellipse(side * a, zc + 0.07, 0.022, 0.025, 2.0, count=8)))
            _, ff, cc = loft(bm, rail)
            for f in ff + cc:
                f.material_index = 2
        new_obj(part, bm, [M["glass"], M["skin"], M["frame"]], col)
    a, b, zc = interp(canopy, 6.55)
    hinge("MiG25_CanopyHood", (-a, y_of(6.55), zc + 0.07), (0, 1, 0))


    spine = [
        (7.0, (0.40, 0.34, ZC + 0.76)),
        (9.0, (0.42, 0.30, ZC + 0.78)),
        (13.5, (0.36, 0.22, ZC + 0.78)),
        (16.0, (0.10, 0.05, ZC + 0.70)),
    ]
    bm = bmesh.new()
    secs = []
    for s in [7.0, 8.0, 9.0, 11.0, 13.5, 15.0, 16.0]:
        a, b, zc = interp(spine, s)
        secs.append((s, superellipse(0, zc, a, b, 2.2, flat_bottom=0.0)))
    loft(bm, secs)
    new_obj("MiG25_Spine", bm, [M["skin"]], col)

    # --- intake ducts -> engine nacelles (left/right). Raked rectangular intakes.
    duct = [  # s, (centre x, centre z, half-w, half-h, exponent)
        (8.9, (1.42, ZC + 0.02, 0.64, 0.95, 6.0)),
        (10.0, (1.40, ZC + 0.03, 0.66, 0.92, 5.5)),
        (12.0, (1.30, ZC + 0.02, 0.72, 0.86, 4.5)),
        (15.5, (1.05, ZC, 0.78, 0.80, 3.0)),
        (18.4, (0.82, ZC, 0.74, 0.74, 2.0)),
        (19.4, (0.80, ZC, 0.66, 0.66, 2.0)),
    ]
    LIP = 8.9
    rake = lambda s, x, z: -(z - (ZC - 0.93)) * 1.35 if s < LIP + 0.01 else 0.0  # lip top sits forward
    for side in (1, -1):
        bm = bmesh.new()
        secs = []
        for s in [LIP, 9.2, 10.0, 11.0, 12.0, 14.0, 15.5, 17.0, 18.4, 19.0, 19.4]:
            cx, cz, a, b, n = interp(duct, s)
            ring = superellipse(side * cx, cz, a, b, n)
            # front photo: outer wall leans in toward the bottom (trapezoid); fades out by the nacelles
            lean = 0.16 * max(0.0, 1.0 - (s - LIP) / 7.0)
            ring = [(x + side * lean * (z - cz) * (1 if (x - side * cx) * side > 0 else 0), z) for x, z in ring]
            secs.append((s, ring))
        rings, _, _ = loft(bm, secs, cap_start=False, shear=rake, mat_caps=(1, 1))
        # recessed mouth: lip thickness, then a dark throat 1.8 m deep
        outer = rings[0]
        cxw = sum(v.co.x for v in outer) / len(outer)
        czw = sum(v.co.z for v in outer) / len(outer)
        inner = [bm.verts.new((cxw + (v.co.x - cxw) * 0.9, v.co.y + 0.04, czw + (v.co.z - czw) * 0.93)) for v in outer]
        throat = [bm.verts.new((v.co.x, v.co.y + 1.8 - (v.co.y - y_of(LIP)) * 0.6, v.co.z)) for v in inner]
        n = len(outer)
        for i in range(n):
            j = (i + 1) % n
            bm.faces.new((outer[i], outer[j], inner[j], inner[i])).material_index = 0
            bm.faces.new((inner[i], inner[j], throat[j], throat[i])).material_index = 1
        bm.faces.new(throat).material_index = 1
        top = [v for v in inner if v.co.z > czw + 0.75 * (max(w.co.z for w in inner) - czw)]
        x0, x1 = min(v.co.x for v in top), max(v.co.x for v in top)
        ztop = max(v.co.z for v in inner) - 0.03
        ylip = min(v.co.y for v in top) + 0.06
        bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
        new_obj("MiG25_Duct_" + ("L" if side > 0 else "R"), bm, [M["skin"], M["dark"]], col)

        # adjustable compression wedge: upper ramp hinged at the lip top, sloping down-aft
        bm = bmesh.new()
        prof = [(0.0, 0.0), (0.7, -0.22), (1.6, -0.62)]  # (aft, down) — two panels like the real ramp
        rows = [[bm.verts.new((x, ylip + a, ztop + d)) for a, d in prof] for x in (x0 + 0.02, x1 - 0.02)]
        for k in range(len(prof) - 1):
            bm.faces.new((rows[0][k], rows[1][k], rows[1][k + 1], rows[0][k + 1]))
        bmesh.ops.solidify(bm, geom=bm.faces[:], thickness=0.03)
        new_obj("MiG25_IntakeRamp_" + ("L" if side > 0 else "R"), bm, [M["intake"]], col, smooth=False)
        hinge("MiG25_IntakeRamp_" + ("L" if side > 0 else "R"), ((x0 + x1) / 2, ylip, ztop), (1, 0, 0))

        # nozzle: short tube past nacelle end, dark inside
        bm = bmesh.new()
        r0 = superellipse(side * 0.80, ZC, 0.62, 0.62, 2.0)
        r1 = superellipse(side * 0.80, ZC, 0.60, 0.60, 2.0)
        # open nozzle: outer shell -> lip ring -> inner tube -> back wall (turbine face, dark).
        # The exit used to be one solid dark cap, which hid the afterburner glow (Unity test).
        inner = superellipse(side * 0.80, ZC, 0.50, 0.50, 2.0)
        loft(bm, [(19.35, r0), (19.75, r1), (19.75, inner), (19.1, inner)], cap_start=False, cap_end=True, mat_caps=(1, 1))
        for f in bm.faces:
            if (f.calc_center_median().y - y_of(19.75)) < -0.02 and abs(f.normal.y) < 0.5 and                ((f.calc_center_median().x - side * 0.80) ** 2 + (f.calc_center_median().z - ZC) ** 2) < 0.52 ** 2:
                f.material_index = 1          # inside of the tube is soot-dark
        bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
        new_obj("MiG25_Nozzle_" + ("L" if side > 0 else "R"), bm, [M["nozzle"], M["dark"]], col)
        # pivot at the exit centre, local X = thrust axis: Unity scales local Y/Z to open/close it
        hinge("MiG25_Nozzle_" + ("L" if side > 0 else "R"), (side * 0.80, y_of(19.75), ZC), (0, 1, 0))

    # --- flat upper deck joining duct tops to the spine (MiG-25 top is a broad flat plate)
    deck = [  # s, (half-width, top z)
        (9.6, (1.30, ZC + 0.86)),
        (11.0, (1.85, ZC + 0.90)),
        (15.5, (1.75, ZC + 0.88)),
        (17.6, (0.9, ZC + 0.72)),
    ]
    bm = bmesh.new()
    secs = []
    for s in [9.6, 10.3, 11.0, 13.0, 15.5, 16.6, 17.6]:
        a, zt = interp(deck, s)
        secs.append((s, superellipse(0, zt - 0.14, a, 0.14, 8.0)))
    loft(bm, secs)
    new_obj("MiG25_Deck", bm, [M["skin"]], col)

    # --- wings: high-mounted on duct tops, ~5° anhedral, LE sweep ~42°
    # Root buried inside the deck (x=1.2) so the wing grows out of the body; upper surface flush
    # with the flat top deck (shoulder wing). Same LE line / tip as the exposed planform.
    sw = math.tan(math.radians(42.5))
    root_x, root_z = 1.2, ZC + 0.77
    semi = 7.0 - root_x
    wing_le = 12.0 - S((2.1 - root_x) * sw)  # photos: wing well aft, tailplane right behind it
    root_ch = 5.8 + (5.8 - 2.0) * (2.1 - root_x) / (7.0 - 2.1)
    WR = dict(le_s=wing_le, chord=S(root_ch), thick=0.26, pos=(root_x, root_z))
    WT = dict(le_s=wing_le + S(semi * sw), chord=S(2.0), thick=0.07,
              pos=(7.0, root_z - semi * math.tan(math.radians(4))))
    # Fixed box ahead of the hinge line + trailing-edge strip cut into flap / two-section aileron
    # (avia.pro: two-section ailerons per console). Hinge at 74% chord; 5 cm gaps between panels.
    HL = 0.74
    tx = lambda x: (x - root_x) / semi
    surface("MiG25_Wing", col, [M["skin"]], WR, WT, steps=8, u1=HL)
    te = [("fix", 1.2, 2.15), ("MiG25_Flap", 2.2, 4.15), ("MiG25_AileronIn", 4.2, 5.45),
          ("MiG25_AileronOut", 5.5, 6.75), ("fix", 6.8, 7.0)]
    for nm, xa, xb in te:
        for side in (1, -1):
            k = "_L" if side > 0 else "_R"
            name = ("MiG25_WingTE" + k + "_%.1f" % xa) if nm == "fix" else nm + k
            surface(name, col, [M["skin"]], WR, WT, steps=8, t0=tx(xa), t1=tx(xb), u0=HL, sides=[side])
            if nm != "fix":
                a = chord_point(WR, WT, tx(xa), HL, side)
                b = chord_point(WR, WT, tx(xb), HL, side)
                hinge(name, (a + b) / 2, (b - a) * side)       # local X ~ world +X on both sides
    # two shallow upper-surface fences per wing (stop at the hinge line)
    fb = bmesh.new()
    for side in (1, -1):
        for fx in (3.9, 5.4):
            t = (fx - root_x) / semi
            le = wing_le + S((fx - root_x) * sw)
            ch = lerp(S(root_ch), S(2.0), t)
            z0 = root_z + 0.11 * (1 - t) + 0.035 * t - (fx - root_x) * math.tan(math.radians(4))
            pts = [(le + ch * 0.05, 0.02), (le + ch * 0.25, 0.16), (le + ch * 0.68, 0.12), (le + ch * 0.72, 0.02)]
            vs = [[fb.verts.new((side * (fx + dx), y_of(s), z0 + dz)) for s, dz in pts] for dx in (-0.012, 0.012)]
            fb.faces.new(vs[0]); fb.faces.new(list(reversed(vs[1])))
            for i in range(3):
                fb.faces.new((vs[0][i + 1], vs[0][i], vs[1][i], vs[1][i + 1]))
    new_obj("MiG25_WingFences", fb, [M["skin"]], col, smooth=False)

    # --- all-moving tailplanes (stabilators), low on nacelle sides; one object per side,
    # pivot spigot at ~45% root chord, turning about the lateral axis
    SR = dict(le_s=17.3, chord=S(3.4), thick=0.16, pos=(1.55, ZC - 0.15))
    ST = dict(le_s=17.3 + S(2.85 * math.tan(math.radians(50.37))), chord=S(1.1), thick=0.05, pos=(4.4, ZC - 0.3))
    for side in (1, -1):
        name = "MiG25_Stabilator" + ("_L" if side > 0 else "_R")
        surface(name, col, [M["skin"]], SR, ST, steps=5, sides=[side])
        hinge(name, chord_point(SR, ST, 0.0, 0.45, side), (1, 0, 0))

    # --- twin vertical fins, canted outward 8°; rudder = aft 28% between 11% and 90% height
    fin_h = 3.05
    FR = dict(le_s=16.4, chord=S(4.2), thick=0.20, pos=(1.1, ZC + 0.70))
    FT = dict(le_s=16.4 + S(fin_h * math.tan(math.radians(54))), chord=S(1.25), thick=0.06,
              pos=(1.1, ZC + 0.70 + fin_h))
    FH = 0.72
    surface("MiG25_Fins", col, [M["skin"]], FR, FT, steps=6, vertical=True, cant=8.0, u1=FH)
    for t0, t1 in ((0.0, 0.10), (0.91, 1.0)):
        surface("MiG25_FinTE_%.2f" % t0, col, [M["skin"]], FR, FT, steps=6, vertical=True, cant=8.0,
                t0=t0, t1=t1, u0=FH)
    for side in (1, -1):
        name = "MiG25_Rudder" + ("_L" if side > 0 else "_R")
        surface(name, col, [M["skin"]], FR, FT, steps=6, vertical=True, cant=8.0, t0=0.11, t1=0.90,
                u0=FH, sides=[side])
        a = chord_point(FR, FT, 0.11, FH, side, vertical=True, cant=8.0)
        b = chord_point(FR, FT, 0.90, FH, side, vertical=True, cant=8.0)
        hinge(name, (a + b) / 2, b - a, up=(side, 0, 0))       # local X up the hinge line

    # --- ventral fins under nacelles, canted outward
    vb = bmesh.new()
    for side in (1, -1):
        pts = [(16.0, 0.0), (18.6, 0.0), (18.9, -0.75), (17.9, -0.80)]
        vs = []
        for dx in (-0.04, 0.04):
            for s, dz in pts:
                x = side * (1.35 + dx - dz * math.tan(math.radians(14)))
                vs.append(vb.verts.new((x, y_of(s), ZC - 0.55 + dz)))
        a, b = vs[:4], vs[4:]
        vb.faces.new(a)
        vb.faces.new(list(reversed(b)))
        for i in range(4):
            j = (i + 1) % 4
            vb.faces.new((a[j], a[i], b[i], b[j]))
    bmesh.ops.recalc_face_normals(vb, faces=vb.faces)
    new_obj("MiG25_VentralFins", vb, [M["skin"]], col, smooth=False)

    # --- landing gear (down). Wheels sourced: nose 2x 700x200, main 1300x360 (avia.pro);
    # levered (trailing-arm) struts. One object per wheel so Unity can spin them.
    def lathe(bm, prof, centre, segs=32):
        """Revolve [(radius, x-offset)] around the X axis through centre; closed ring sequence."""
        rings = []
        for r, dx in prof:
            ring = []
            for i in range(segs):
                a = 2 * math.pi * i / segs
                ring.append(bm.verts.new((centre[0] + dx, centre[1] + r * math.cos(a), centre[2] + r * math.sin(a))))
            rings.append(ring)
        for ra, rb in zip(rings[:-1], rings[1:]):
            for i in range(segs):
                j = (i + 1) % segs
                bm.faces.new((ra[i], ra[j], rb[j], rb[i]))
        return rings

    def wheel(name, x, s, z, R, w):
        bm = bmesh.new()
        hub_r = R * 0.62
        tire = [(hub_r, -w * 0.42), (R - w * 0.30, -w * 0.5), (R - w * 0.08, -w * 0.44), (R, -w * 0.22),
                (R, w * 0.22), (R - w * 0.08, w * 0.44), (R - w * 0.30, w * 0.5), (hub_r, w * 0.42)]
        rings = lathe(bm, tire, (x, y_of(s), z))
        for f in bm.faces:
            f.material_index = 0
        hub = [(hub_r, w * 0.42), (hub_r * 0.92, w * 0.30), (hub_r * 0.35, w * 0.34), (hub_r * 0.22, w * 0.46),
               (0.001, w * 0.46)]
        hub_b = [(r, -dx) for r, dx in hub]
        nt = len(bm.faces)
        lathe(bm, hub, (x, y_of(s), z))
        lathe(bm, hub_b, (x, y_of(s), z))
        for f in list(bm.faces)[nt:]:
            f.material_index = 1
        bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-4)
        bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
        return new_obj(name, bm, [M["tire"], M["gear"]], col)

    # Attach everything to the real underside: ray-cast down onto fuselage + ducts.
    from mathutils.bvhtree import BVHTree
    ub = bmesh.new()
    for nm in ("MiG25_Fuselage", "MiG25_Duct_L", "MiG25_Duct_R"):
        ub.from_mesh(bpy.data.objects[nm].data)
    under_bvh = BVHTree.FromBMesh(ub)
    ub.free()

    def under(x, y, sink=0.06):
        """z of the airframe underside at (x, y), pushed 'sink' metres up into the skin."""
        hit = under_bvh.ray_cast(Vector((x, y, ZC)), Vector((0, 0, -1)))
        if hit[0] is None:
            raise RuntimeError("no underside at %.2f %.2f" % (x, y))
        return hit[0].z + sink

    # Each moving assembly is its own mesh: leg (retracts about local X at its top pivot),
    # doors (hinge along their top edge), wheels (spin about local X, parented to their leg).
    def tube(bm, p0, p1, r, segs=12):
        p0, p1 = Vector(p0), Vector(p1)
        v = (p1 - p0)
        q = Vector((0, 0, 1)).rotation_difference(v.normalized())
        rings = []
        for p in (p0, p1):
            ring = []
            for i in range(segs):
                a = 2 * math.pi * i / segs
                ring.append(bm.verts.new(p + q @ Vector((r * math.cos(a), r * math.sin(a), 0))))
            rings.append(ring)
        for i in range(segs):
            j = (i + 1) % segs
            bm.faces.new((rings[0][i], rings[0][j], rings[1][j], rings[1][i]))
        bm.faces.new(list(reversed(rings[0])))
        bm.faces.new(rings[1])

    def plate(bm, c, size):
        r = bmesh.ops.create_cube(bm, size=1.0)
        for v in r["verts"]:
            v.co = Vector((c[0] + v.co.x * size[0], c[1] + v.co.y * size[1], c[2] + v.co.z * size[2]))

    def door(name, x, y, length, height):
        """Open bay door hanging from the underside; top edge follows the skin along its length."""
        zt = min(under(x, y - length / 2 + 0.05), under(x, y), under(x, y + length / 2 - 0.05))
        bm = bmesh.new()
        plate(bm, (x, y, zt - height / 2), (0.02, length, height))
        new_obj(name, bm, [M["gear"]], col, smooth=False)
        hinge(name, (x, y, zt - 0.06), (0, 1, 0))

    LEG_WHEELS.clear()
    # nose gear: twin wheels on a fork, oleo, forward drag brace, two bay doors
    ns, nr = 6.0, 0.35
    ny = y_of(ns)
    for side in (1, -1):
        wheel("MiG25_NoseWheel_" + ("L" if side > 0 else "R"), side * 0.15, ns, nr, nr, 0.2)
        LEG_WHEELS["MiG25_NoseWheel_" + ("L" if side > 0 else "R")] = "MiG25_NoseGear"
    bm = bmesh.new()
    ztop = under(0, ny + 0.06, 0.15)
    tube(bm, (-0.26, ny, nr), (0.26, ny, nr), 0.035)                    # axle
    tube(bm, (0, ny + 0.12, nr + 0.12), (0, ny + 0.10, 1.05), 0.055)     # lower oleo (chrome)
    tube(bm, (0, ny + 0.10, 1.0), (0, ny + 0.06, ztop), 0.08)            # upper cylinder
    tube(bm, (0, ny + 0.12, nr + 0.12), (0, ny, nr), 0.04)                # fork link to axle
    tube(bm, (0, ny + 0.08, 1.15), (0, ny - 0.75, under(0, ny - 0.75, 0.1)), 0.035)  # drag brace
    new_obj("MiG25_NoseGear", bm, [M["gear"]], col, smooth=False)
    hinge("MiG25_NoseGear", (0, ny + 0.06, ztop - 0.15), (1, 0, 0))
    for side in (1, -1):
        door("MiG25_NoseDoor_" + ("L" if side > 0 else "R"), side * 0.30, ny + 0.2, 1.2, 0.45)

    # main gear: trailing arm from oleo foot to axle, side brace to the duct, big outer door
    ms, mr = 12.6, 0.65
    my = y_of(ms)
    for side in (1, -1):
        k = "L" if side > 0 else "R"
        x = side * 1.62                                               # under the duct's flat bottom
        wheel("MiG25_MainWheel_" + k, x, ms, mr, mr, 0.36)
        LEG_WHEELS["MiG25_MainWheel_" + k] = "MiG25_MainGear_" + k
        sx = side * 1.30                                              # strut line, inboard of the tyre
        top = (sx, my - 0.35, under(sx, my - 0.35, 0.15))
        foot = (sx, my - 0.55, 0.95)
        bm = bmesh.new()
        tube(bm, top, foot, 0.10)                                     # oleo cylinder
        tube(bm, foot, (sx, my, mr), 0.07)                            # trailing arm
        tube(bm, (sx, my, mr), (x + side * 0.22, my, mr), 0.06)       # stub axle
        tube(bm, (side * 1.30, my - 0.45, 1.25), (side * 0.80, my - 0.1, under(side * 0.80, my - 0.1, 0.1)), 0.04)  # side brace
        new_obj("MiG25_MainGear_" + k, bm, [M["gear"]], col, smooth=False)
        hinge("MiG25_MainGear_" + k, (sx, my - 0.35, top[2] - 0.15), (1, 0, 0))
        door("MiG25_MainDoorOuter_" + k, side * 1.90, my - 0.1, 1.8, 0.45)
        door("MiG25_MainDoorInner_" + k, side * 0.92, my - 0.2, 1.4, 0.40)

    # --- DUST insignia decals on fin outer faces (follow the 8° fin cant)
    img = insignia_image()
    dm = bpy.data.materials.get("M_MiG_Insignia") or bpy.data.materials.new("M_MiG_Insignia")
    dm.use_nodes = True
    nt = dm.node_tree
    bsdf = next(n for n in nt.nodes if n.type == "BSDF_PRINCIPLED")
    tex = next((n for n in nt.nodes if n.type == "TEX_IMAGE"), None) or nt.nodes.new("ShaderNodeTexImage")
    tex.image = img
    nt.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
    nt.links.new(tex.outputs["Alpha"], bsdf.inputs["Alpha"])
    bsdf.inputs["Roughness"].default_value = 0.7
    try:
        dm.surface_render_method = "DITHERED"
    except Exception:
        pass
    cant = math.radians(8)
    h, D = 1.4, 1.15
    cy = y_of(16.4) + h * math.tan(math.radians(54)) + 1.35  # mid-chord at h
    for side in (1, -1):
        bm = bmesh.new()
        uv = bm.loops.layers.uv.new()
        C = Vector((side * (1.1 + h * math.tan(cant) + 0.078), cy, ZC + 0.70 + h))
        U = Vector((0, side, 0))
        V = Vector((side * math.sin(cant), 0, math.cos(cant)))
        ring = []
        for i in range(32):
            ang = 2 * math.pi * i / 32
            ring.append((math.cos(ang), math.sin(ang)))
        vs = [bm.verts.new(C + U * (x * D / 2) + V * (z * D / 2)) for x, z in ring]
        f = bm.faces.new(vs)
        for l, (x, z) in zip(f.loops, ring):
            l[uv].uv = (0.5 + x / 2, 0.5 + z / 2)
        f.normal_update()
        if f.normal.x * side < 0:
            bmesh.ops.reverse_faces(bm, faces=[f])
        new_obj("MiG25_Insignia_" + ("L" if side > 0 else "R"), bm, [dm], col, smooth=False, recalc=False)

    for ob in col.objects:
        if any(s.material and s.material.name in ("M_MiG_Skin", "M_MiG_Frame") for s in ob.material_slots):
            cube_uv(ob)
    return assemble(col)


if __name__ == "__main__":
    build()
    print("built", len(bpy.data.collections[COL_NAME].objects), "objects")
