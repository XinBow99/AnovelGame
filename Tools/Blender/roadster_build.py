# Roadster (issue #8) — procedural model at real size (Blender metres).
# Nose points to -Y, Z up, ground at z=0. Unity export scales x0.5 (half-size world).
# Tesla has not published dimensions. Overall length is ASSUMED 4.30 m and width 2.00 m;
# everything else is measured relative to those off the MotorTrend Oct 2026 gallery
# (side profile, front, rear, top 3/4) and Tesla's own Hero/Gravity images:
# sharp wedge, white upper body over a black lower band, flat V-creased hood with one thin
# light bar and corner slit lamps, flat roof ~1.10 m, straight fastback between two sail
# fins with recessed rear glass, black tail with two-line taillight, 4 cold-gas nozzles,
# 4 ducted fan-jets in the rear tray, carbon fender plates, 6 split-spoke wheels.
import bpy, bmesh, math
from mathutils import Vector, Matrix
from mathutils.bvhtree import BVHTree

COL_NAME = "Roadster"
PREFIX = "RD_"

# axles / wheels (side photo: wheelbase 2.86 m, tyre ~0.66 m)
AXLE_F, AXLE_R = -1.39, 1.47
WHEEL_F = dict(r=0.330, w=0.26, x=0.850)
WHEEL_R = dict(r=0.330, w=0.31, x=0.830)
ARCH_GAP = 0.030


# ---------------------------------------------------------------- materials
def mat(name, color, metal=0.0, rough=0.5, alpha=1.0, coat=0.0, emit=None, strength=0.0):
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
    if coat:
        bsdf.inputs["Coat Weight"].default_value = coat
        bsdf.inputs["Coat Roughness"].default_value = 0.08
    if emit:
        bsdf.inputs["Emission Color"].default_value = (*emit, 1.0)
        bsdf.inputs["Emission Strength"].default_value = strength
    if alpha < 1.0:
        bsdf.inputs["Alpha"].default_value = alpha
        try:
            m.surface_render_method = "BLENDED"
        except Exception:
            pass
    m.diffuse_color = (*color, alpha)
    return m


def materials():
    return dict(
        paint=mat("M_DST_Roadster_Paint", (0.80, 0.80, 0.78), metal=0.45, rough=0.34, coat=1.0),
        glass=mat("M_DST_Roadster_Glass", (0.06, 0.045, 0.02), metal=0.75, rough=0.04, alpha=0.97),
        carbon=mat("M_DST_Roadster_Carbon", (0.018, 0.018, 0.02), rough=0.3, coat=0.6),
        titanium=mat("M_DST_Roadster_Titanium", (0.56, 0.54, 0.51), metal=1.0, rough=0.32),
        black=mat("M_DST_Roadster_Black", (0.03, 0.03, 0.032), rough=0.6),
        rubber=mat("M_DST_Roadster_Rubber", (0.025, 0.025, 0.025), rough=0.85),
        spoke=mat("M_DST_Roadster_Spoke", (0.62, 0.62, 0.64), metal=1.0, rough=0.22),
        brass=mat("M_DST_Roadster_Brass", (0.72, 0.55, 0.33), metal=1.0, rough=0.28),
        # rear outlets: dark warm heat-tinted metal, not bright silver (Gravity photo)
        burnt=mat("M_DST_Roadster_Burnt", (0.30, 0.235, 0.17), metal=1.0, rough=0.42),
        # fan-jet hubs: dark graphite metal
        graphite=mat("M_DST_Roadster_Graphite", (0.07, 0.07, 0.075), metal=0.8, rough=0.38),
        head=mat("M_DST_Roadster_HeadLamp", (0.9, 0.95, 1.0), emit=(0.85, 0.92, 1.0), strength=30),
        tail=mat("M_DST_Roadster_TailLamp", (0.5, 0.02, 0.02), emit=(1.0, 0.03, 0.02), strength=25),
        interior=mat("M_DST_Roadster_Interior", (0.16, 0.16, 0.165), rough=0.55),
        screen=mat("M_DST_Roadster_Screen", (0.01, 0.012, 0.015), rough=0.1,
                   emit=(0.25, 0.35, 0.45), strength=2),
    )


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
        if me.users == 0 and (me.name.startswith(PREFIX) or me.name.startswith("_tmp")):
            bpy.data.meshes.remove(me)
    return col


def new_obj(name, bm, mats, col, smooth=True, recalc=True, sharp_angle=40):
    if recalc:  # outward normals everywhere — Unity culls back faces
        bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    ngons = [f for f in bm.faces if len(f.verts) > 4]   # FBX tangents need tris/quads
    if ngons:
        bmesh.ops.triangulate(bm, faces=ngons)
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    for m in mats:
        me.materials.append(m)
    if smooth:
        for p in me.polygons:
            p.use_smooth = True
        me.set_sharp_from_angle(angle=math.radians(sharp_angle))
    ob = bpy.data.objects.new(name, me)
    col.objects.link(ob)
    return ob


def catmull(table, y):
    """table: [(y, tuple)] sorted. Centripetal-free uniform Catmull-Rom, clamped ends."""
    ys = [k for k, _ in table]
    if y <= ys[0]:
        return table[0][1]
    if y >= ys[-1]:
        return table[-1][1]
    i = max(j for j in range(len(ys) - 1) if ys[j] <= y)
    p0 = table[max(i - 1, 0)][1]
    p1, p2 = table[i][1], table[i + 1][1]
    p3 = table[min(i + 2, len(table) - 1)][1]
    t = (y - ys[i]) / (ys[i + 1] - ys[i])
    t2, t3 = t * t, t * t * t
    return tuple(0.5 * (2 * b + (-a + c) * t + (2 * a - 5 * b + 4 * c - d) * t2
                        + (-a + 3 * b - 3 * c + d) * t3) for a, b, c, d in zip(p0, p1, p2, p3))


def loft_rings(bm, rings, cap_start=True, cap_end=True):
    vr = [[bm.verts.new(p) for p in ring] for ring in rings]
    faces = []
    for a, b in zip(vr[:-1], vr[1:]):
        n = len(a)
        for i in range(n):
            j = (i + 1) % n
            faces.append(bm.faces.new((a[i], a[j], b[j], b[i])))
    caps = []
    if cap_start:
        caps.append(bm.faces.new(list(reversed(vr[0]))))
    if cap_end:
        caps.append(bm.faces.new(vr[-1]))
    return vr, faces, caps


def mirror_half(half):
    """half: list of (x>=0, z) from top centre round the outside to bottom centre.
    Returns a closed ring (x, z) going around the full section."""
    right = half
    left = [(-x, z) for x, z in reversed(half[1:-1])]
    return right + left


def lathe(bm, profile, segs=32, axis="X", cx=0.0, cy=0.0, cz=0.0, close=False):
    """profile: list of (axial, radius). Revolves around the given axis through (cx,cy,cz)."""
    rings = []
    for a, r in profile:
        ring = []
        for k in range(segs):
            t = 2 * math.pi * k / segs
            u, v = r * math.cos(t), r * math.sin(t)
            if axis == "X":
                ring.append(bm.verts.new((cx + a, cy + u, cz + v)))
            elif axis == "Y":
                ring.append(bm.verts.new((cx + u, cy + a, cz + v)))
            else:
                ring.append(bm.verts.new((cx + u, cy + v, cz + a)))
        rings.append(ring)
    for ra, rb in zip(rings[:-1], rings[1:]):
        for k in range(segs):
            j = (k + 1) % segs
            bm.faces.new((ra[k], ra[j], rb[j], rb[k]))
    if close:
        bm.faces.new(rings[0])
        bm.faces.new(list(reversed(rings[-1])))
    return rings


def box(bm, c, size, rot=None):
    m = Matrix.Translation(Vector(c))
    if rot:
        m = m @ rot
    bmesh.ops.create_cube(bm, size=1.0, matrix=m @ Matrix.Diagonal((*size, 1)))


def apply_bool(ob, cutter, op="DIFFERENCE", transfer=False):
    mod = ob.modifiers.new("bool", "BOOLEAN")
    mod.operation = op
    mod.object = cutter
    mod.solver = "EXACT"
    if transfer:                       # cut walls take the cutter's material
        mod.material_mode = "TRANSFER"
    with bpy.context.temp_override(object=ob, active_object=ob, selected_objects=[ob]):
        bpy.ops.object.modifier_apply(modifier=mod.name)


def set_origin(ob, p):
    p = Vector(p)
    ob.data.transform(Matrix.Translation(-p))
    ob.matrix_world = Matrix.Translation(p)


def parent_keep(child, par):
    child.parent = par
    child.matrix_parent_inverse = par.matrix_world.inverted()


# ---------------------------------------------------------------- body
# Hard-surface wedge (MotorTrend gallery, Oct 2026: near-orthographic side profile, straight
# front view, rear view, top 3/4). Stations are measured off the side photo at 3.93 mm/px
# (overall length fixed at 4.30 m) and the front/rear photos at 2.0 m overall width.
# Each station is a half section of 8 points, linearly interpolated so panels stay flat:
#   p0 centre top (hood / deck), p1 hood-plane edge (the V crease), p2 fender ridge,
#   p3 shoulder (top of the side plane), p4 white/black split line, p5 sill outer,
#   p6 sill inner, p7 centre bottom.
BODY = [
    (-2.155, ((0, .440), (.70, .440), (.80, .432), (.86, .425), (.86, .415), (.85, .405), (.80, .400), (0, .400))),
    (-2.080, ((0, .465), (.70, .465), (.84, .500), (.93, .470), (.92, .245), (.90, .130), (.84, .110), (0, .110))),
    (-1.750, ((0, .540), (.62, .550), (.86, .650), (.98, .630), (.97, .240), (.94, .115), (.88, .100), (0, .100))),
    (-1.390, ((0, .600), (.58, .620), (.86, .740), (1.0, .720), (.99, .225), (.95, .105), (.89, .090), (0, .090))),
    (-1.000, ((0, .660), (.56, .680), (.86, .770), (.99, .730), (.98, .215), (.95, .100), (.89, .090), (0, .090))),
    (-0.800, ((0, .690), (.55, .710), (.85, .785), (.98, .740), (.97, .220), (.95, .100), (.89, .090), (0, .090))),
    (-0.300, ((0, .740), (.55, .760), (.82, .800), (.96, .750), (.95, .250), (.93, .100), (.87, .090), (0, .090))),
    (0.500, ((0, .790), (.55, .810), (.82, .840), (.97, .780), (.97, .300), (.94, .105), (.88, .090), (0, .090))),
    (1.150, ((0, .830), (.52, .850), (.84, .870), (1.0, .830), (1.0, .345), (.97, .115), (.91, .100), (0, .100))),
    (1.470, ((0, .840), (.50, .860), (.85, .880), (1.0, .850), (1.0, .370), (.97, .130), (.91, .110), (0, .110))),
    # behind the rear wheels the lower body pulls in (black side pods), so from behind the tyres
    # stand out as solid outer masses either side of the central opening (rear photos)
    (1.900, ((0, .850), (.50, .858), (.88, .868), (.99, .850), (.96, .560), (.80, .300), (.74, .250), (0, .250))),
    (2.060, ((0, .852), (.50, .856), (.88, .862), (.95, .850), (.90, .600), (.74, .320), (.70, .270), (0, .270))),
]
# Tail (Tesla Gravity / MotorTrend rear photos): the body does NOT end in a flat plate. The last
# ring is closed by a cap that bulges back in plan (TAIL_BULGE at the centre, wrapping forward
# into the wheel arches at the sides); white panel above TAIL_SPLIT (light bar + plate), black
# below, where one big chamfered opening (REAR_OPEN) houses the nozzle row and the 4 fan-jets.
TAIL_Y, TAIL_BULGE, TAIL_SPLIT = 2.060, 0.11, 0.585
REAR_OPEN = [(-0.44, 0.525), (0.44, 0.525), (0.60, 0.425), (0.60, 0.17), (0.55, 0.12),
             (-0.55, 0.12), (-0.60, 0.17), (-0.60, 0.425)]          # opening outline (x, z)
REAR_FRAME = [(-0.475, 0.565), (0.475, 0.565), (0.648, 0.448), (0.648, 0.150), (0.585, 0.085),
              (-0.585, 0.085), (-0.648, 0.150), (-0.648, 0.448)]    # frame outer outline
FRAME_FRONT, FRAME_BACK, OPEN_BACK = 2.200, 2.040, 1.960
SHELF_Z = 0.385          # divider between the nozzle row (above) and the fan bays (below)


def body_section(y):
    """Linear interpolation of the 8-point half section at y (keeps planar facets)."""
    if y <= BODY[0][0]:
        return BODY[0][1]
    for (y0, s0), (y1, s1) in zip(BODY[:-1], BODY[1:]):
        if y <= y1:
            t = (y - y0) / (y1 - y0)
            return tuple((lerp(a[0], b[0], t), lerp(a[1], b[1], t)) for a, b in zip(s0, s1))
    return BODY[-1][1]


def lerp(a, b, t):
    return a + (b - a) * t


def build_body(col, M):
    bm = bmesh.new()
    # rings at every station plus extra rings over the wheel arches (for the boolean)
    ys = sorted(set([y for y, _ in BODY] +
                    [AXLE_F + d for d in (-0.45, -0.3, -0.15, 0, 0.15, 0.3, 0.45)] +
                    [AXLE_R + d for d in (-0.45, -0.3, -0.15, 0, 0.15, 0.3, 0.42)]))
    rings = [[(x, y, z) for x, z in mirror_half(list(body_section(y)))] for y in ys]
    vr, _, _ = loft_rings(bm, rings, cap_end=False)
    # tail cap: rings shrunk toward the centre line in x and pushed back on a parabola, so the
    # rear panel curves round to the sides instead of ending in a flat plate
    last = vr[-1]
    prev = last
    for s in (0.86, 0.68, 0.48, 0.28, 0.10):
        ring = [bm.verts.new((v.co.x * s, TAIL_Y + TAIL_BULGE * (1.0 - s * s), v.co.z)) for v in last]
        n = len(ring)
        for i in range(n):
            j = (i + 1) % n
            bm.faces.new((prev[i], prev[j], ring[j], ring[i]))
        prev = ring
    bm.faces.new(prev)
    # materials: 0 white paint, 1 black composite, 2 carbon
    bm.normal_update()
    for f in bm.faces:
        c = f.calc_center_median()
        sec = body_section(c.y)
        f.material_index = 0
        if c.y > TAIL_Y - 0.01 and f.normal.y > 0.3:
            # white panel over a black lower tail; the white edge sweeps up toward the sides
            split = TAIL_SPLIT + 0.13 * min(1.0, abs(c.x) / 0.9) ** 2
            f.material_index = 0 if c.z > split else 1
        elif f.normal.y < -0.35 and c.y < -2.07:
            f.material_index = 1                   # front fascia under the light bar
        elif c.z < sec[4][1] + 0.004:
            f.material_index = 1                   # lower body band / sills / floor
    ob = new_obj(PREFIX + "Body", bm, [M["paint"], M["black"], M["carbon"]], col, smooth=False)
    # wheel arches: short cylinders each side (not across the whole car)
    for y, wd in ((AXLE_F, WHEEL_F), (AXLE_R, WHEEL_R)):
        for side in (1, -1):
            cb = bmesh.new()
            r = wd["r"] + ARCH_GAP
            x_in = wd["x"] - wd["w"] / 2 - 0.035
            lathe(cb, [(x_in, r), (1.40, r)], segs=48, axis="X", cx=0.0, cy=y, cz=wd["r"], close=True)
            if side < 0:
                cb.transform(Matrix.Diagonal((-1, 1, 1, 1)))
            cut = new_obj("_tmp_arch", cb, [M["black"]], col, smooth=False)
            apply_bool(ob, cut, transfer=True)
            bpy.data.objects.remove(cut, do_unlink=True)
    # rear opening: one deep chamfered cavity for the nozzle row + fan bays (walls black)
    cut = new_obj("_tmp_open", prism(REAR_OPEN, OPEN_BACK, 2.45), [M["black"]], col, smooth=False)
    apply_bool(ob, cut, transfer=True)
    bpy.data.objects.remove(cut, do_unlink=True)
    ob.data.set_sharp_from_angle(angle=math.radians(SHARP))
    for p in ob.data.polygons:
        p.use_smooth = True
    return ob


SHARP = 20          # facet edges sharper than this stay hard (cyber look)


def prism(outline, y0, y1):
    """Closed prism: polygon outline (x, z) extruded along Y from y0 to y1 (a bmesh)."""
    bm = bmesh.new()
    a = [bm.verts.new((x, y0, z)) for x, z in outline]
    b = [bm.verts.new((x, y1, z)) for x, z in outline]
    n = len(outline)
    for i in range(n):
        j = (i + 1) % n
        bm.faces.new((a[i], a[j], b[j], b[i]))
    bm.faces.new(list(reversed(a)))
    bm.faces.new(b)
    return bm


def frame_ring(bm, outer, inner, y0, y1):
    """Thick frame between two outlines (same vertex count), from y0 (back) to y1 (front)."""
    n = len(outer)
    of = [bm.verts.new((x, y1, z)) for x, z in outer]
    inf = [bm.verts.new((x, y1, z)) for x, z in inner]
    ob = [bm.verts.new((x, y0, z)) for x, z in outer]
    ib = [bm.verts.new((x, y0, z)) for x, z in inner]
    for i in range(n):
        j = (i + 1) % n
        bm.faces.new((of[i], of[j], inf[j], inf[i]))      # front face
        bm.faces.new((ob[i], ob[j], of[j], of[i]))        # outer wall
        bm.faces.new((inf[i], inf[j], ib[j], ib[i]))      # inner wall
        bm.faces.new((ib[i], ib[j], ob[j], ob[i]))        # back face


# Greenhouse (y, base half-width, roof half-width, roof z), side photo: windscreen foot right
# behind the front wheel (y -1.12) rising ~21 deg to a flat roof (1.08-1.10 m) from y -0.30
# to 0.50, then a straight fastback toward the tail (0.855 m at 2.16); rear photo: two narrow
# sail fins (outer edge ~0.46) with the rear glass recessed between them.
CABIN = [
    (-1.120, (0.80, 0.30, 0.715)),
    (-0.800, (0.80, 0.46, 0.840)),
    (-0.300, (0.78, 0.55, 1.080)),
    (0.500, (0.74, 0.54, 1.100)),
    (1.200, (0.68, 0.49, 0.997)),
    (1.820, (0.63, 0.46, 0.905)),      # sails end where the stowed wing begins
]
RECESS = [(-0.85, (0.10, 0.0)), (0.75, (0.10, 0.0)), (0.95, (0.26, 0.05)), (1.40, (0.32, 0.09)),
          (1.82, (0.34, 0.08))]


def table_lin(table, y):
    if y <= table[0][0]:
        return table[0][1]
    for (y0, v0), (y1, v1) in zip(table[:-1], table[1:]):
        if y <= y1:
            t = (y - y0) / (y1 - y0)
            return tuple(lerp(a, b, t) for a, b in zip(v0, v1))
    return table[-1][1]


def build_cabin(col, M):
    bm = bmesh.new()
    rings = []
    ys = sorted(set([y for y, _ in CABIN] + [y for y, _ in RECESS if y > CABIN[0][0]]))
    for y in ys:
        wb, wt, zr = table_lin(CABIN, y)
        wr, d = table_lin(RECESS, y)
        wr = min(wr, wt - 0.06)
        sec = body_section(y)
        zt = sec[0][1] - 0.02                     # centre: sunk into the deck
        # the glass starts on the beltline = the body's fender ridge (side + front photos)
        wb = min(wb, sec[2][0] - 0.004)
        zbelt = sec[2][1] - 0.008
        zr = max(zr, zbelt + 0.02)
        half = [
            (0.0, zr - d),
            (wr, zr - d),                         # floor of the rear-glass trough
            (wr + 0.025, zr - 0.004),             # sail inner edge
            (wt, zr - 0.02),                      # roof / sail outer crease
            (wt + (wb - wt) * 0.55, zr - (zr - zbelt) * 0.50),
            (wb, zbelt),
            (wb - 0.01, zt),
            (0.0, zt),
        ]
        rings.append([(x, y, z) for x, z in mirror_half(half)])
    loft_rings(bm, rings)
    # glass = windscreen + side glass + roof panel + recessed rear glass; paint = sails, pillars
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)       # while it is still closed
    for f in bm.faces:
        c = f.calc_center_median()
        wr, d = table_lin(RECESS, c.y)
        f.material_index = 0
        if c.y > 0.9 and abs(c.x) < wr + 0.004 and d > 0.02:
            f.material_index = 1                  # rear glass
        elif c.y < 0.95 and c.z > table_lin(CABIN, c.y)[2] - 0.30:
            f.material_index = 1                  # windscreen, side glass, glass roof
        if c.z < body_section(c.y)[0][1] + 0.03:
            f.material_index = 2                  # black cowl / base trim
    # open shell: drop the floor (it hid the cockpit and mirrored the sky from the driver's eye)
    floor = [f for f in bm.faces if len(f.verts) <= 4
             and f.calc_center_median().z < body_section(f.calc_center_median().y)[2][1] - 0.012]
    bmesh.ops.delete(bm, geom=floor, context="FACES")
    # inner skin for the opaque parts (headliner, pillars, sails seen from the seat): an inward
    # copy with flipped normals; glass stays one layer and renders two-sided
    opaque = [f for f in bm.faces if f.material_index != 1]
    dup = bmesh.ops.duplicate(bm, geom=opaque)
    inner = [g for g in dup["geom"] if isinstance(g, bmesh.types.BMFace)]
    vs = {v for f in inner for v in f.verts}
    bm.normal_update()
    for v in vs:
        v.co -= v.normal * 0.008
    bmesh.ops.reverse_faces(bm, faces=inner)
    for f in inner:
        f.material_index = 3
    ob = new_obj(PREFIX + "Canopy", bm, [M["paint"], M["glass"], M["black"], M["interior"]], col,
                 smooth=False, recalc=False)
    ob.data.set_sharp_from_angle(angle=math.radians(SHARP))
    for p in ob.data.polygons:
        p.use_smooth = True
    return ob


# Cockpit opening cut into the body under the canopy (driver sees the tub; you see the seat
# through the glass). Real metres: x +-0.70, y -0.80..1.05, from the tub floor up.
COCKPIT = dict(x=0.70, y0=-0.80, y1=1.05, z0=0.22)
SEAT_X = 0.30        # driver on the car's left (+X); passenger seat mirrored at -SEAT_X
EYE = (SEAT_X, 0.38, 0.93)


# ---------------------------------------------------------------- lights
def surface_strip(col, name, mat_, pts, body_bvh, width=0.025, depth=0.012, outward=0.004):
    """Thin ribbon laid onto the body: each point is ray-cast along its direction onto the
    surface, then the strip is built on the hit points (follows curvature, never floats)."""
    bm = bmesh.new()
    ribbon = []
    for p, dirn in pts:
        p = Vector(p)
        dirn = Vector(dirn).normalized()
        hit = body_bvh.ray_cast(p - dirn * 0.3, dirn, 2.0)
        loc, nrm = (hit[0], hit[1]) if hit[0] is not None else (p, -dirn)
        ribbon.append((loc + nrm * outward, nrm))
    rows = []
    for i, (loc, nrm) in enumerate(ribbon):
        a = ribbon[max(i - 1, 0)][0]
        b = ribbon[min(i + 1, len(ribbon) - 1)][0]
        u = nrm.cross((b - a).normalized()).normalized() * width * 0.5
        rows.append([bm.verts.new(loc + u), bm.verts.new(loc - u),
                     bm.verts.new(loc - u - nrm * depth), bm.verts.new(loc + u - nrm * depth)])
    for a, b in zip(rows[:-1], rows[1:]):
        for k in range(4):
            j = (k + 1) % 4
            bm.faces.new((a[k], a[j], b[j], b[k]))
    bm.faces.new(list(reversed(rows[0])))
    bm.faces.new(rows[-1])
    return new_obj(name, bm, [mat_], col, sharp_angle=30)


def build_lights(col, M, body):
    bvh = BVHTree.FromObject(body, bpy.context.evaluated_depsgraph_get())
    # one thin light bar along the hood's leading edge, between the V creases
    bm = bmesh.new()
    box(bm, (0, -2.150, 0.441), (1.40, 0.016, 0.010))
    head = new_obj(PREFIX + "HeadLamp", bm, [M["head"]], col, smooth=False)
    # slit headlamps where the hood edge meets the fender plane (front corners)
    bm = bmesh.new()
    for s in (1, -1):
        a = Vector((s * 0.68, -2.135, 0.452))   # short wedge lying on the corner plane
        b = Vector((s * 0.82, -2.05, 0.492))
        mid = (a + b) / 2
        d = b - a
        rot = Matrix.Rotation(math.atan2(d.x, -d.y), 4, "Z") @ Matrix.Rotation(-math.atan2(d.z, d.xy.length), 4, "X")
        box(bm, mid, (0.016, d.length, 0.012), rot)
    slit = new_obj(PREFIX + "HeadSlits", bm, [M["head"]], col, smooth=False)
    # tail: everything follows the curved rear panel (projected onto it, not boxes on a plate):
    # black band, the bright red bar at the top edge, a thin bright metal edge under it
    def across(z, half, k=33):
        return [((-half + 2 * half * i / (k - 1), TAIL_Y + 0.5, z), (0, -1, 0)) for i in range(k)]
    surface_strip(col, PREFIX + "TailBand", M["black"], across(0.808, 0.90), bvh, width=0.062, outward=0.002)
    tail = surface_strip(col, PREFIX + "TailLamp", M["tail"], across(0.818, 0.89), bvh, width=0.020, outward=0.005)
    surface_strip(col, PREFIX + "TailEdge", M["titanium"], across(0.770, 0.88), bvh, width=0.010, outward=0.003)
    # plate plinth (US plate 305 x 152 mm; also the scale ruler used on the Tesla photos)
    hit = bvh.ray_cast(Vector((0, TAIL_Y + 0.5, 0.665)), Vector((0, -1, 0)), 1.0)
    py = hit[0].y if hit[0] is not None else TAIL_Y + TAIL_BULGE
    bm = bmesh.new()
    box(bm, (0, py + 0.006, 0.665), (0.315, 0.014, 0.16))
    new_obj(PREFIX + "PlatePlinth", bm, [M["titanium"]], col, smooth=False)
    return head, tail


# ---------------------------------------------------------------- wheels
def build_wheel(col, M, name, wd, y, side):
    r, w = wd["r"], wd["w"]
    cx = side * wd["x"]
    rim_r = r - 0.075                       # low-profile tyre
    bm = bmesh.new()
    prof = [(-w / 2, rim_r + 0.005), (-w / 2, r - 0.03), (-w / 2 + 0.02, r - 0.004), (-w / 2 + 0.05, r),
            (w / 2 - 0.05, r), (w / 2 - 0.02, r - 0.004), (w / 2, r - 0.03), (w / 2, rim_r + 0.005)]
    prof += [(w / 2 - 0.01, rim_r), (-w / 2 + 0.01, rim_r)]
    prof = [(a * side, rr) for a, rr in prof]
    rings = lathe(bm, prof + [prof[0]], segs=48, axis="X", cx=cx, cy=y, cz=r)
    tyre = new_obj(name + "_Tyre", bm, [M["rubber"]], col, sharp_angle=50)
    # rim barrel + lip
    bm = bmesh.new()
    face = side * (w / 2 - 0.015)
    back = -side * (w / 2 - 0.02)
    lathe(bm, [(face, rim_r + 0.008), (face, rim_r - 0.012), (back, rim_r - 0.012), (back, rim_r)],
          segs=48, axis="X", cx=cx, cy=y, cz=r)
    # hub
    lathe(bm, [(face - side * 0.035, 0.0), (face - side * 0.035, 0.07), (face - side * 0.005, 0.065),
               (face, 0.04), (face + side * 0.002, 0.0)], segs=24, axis="X", cx=cx, cy=y, cz=r)
    # 6 split spokes (two blades each, splaying toward the rim) — dark, over a white aero disc
    for k in range(6):
        for split in (-1, 1):
            a = 2 * math.pi * k / 6
            a_out = a + split * 0.10
            p0 = Vector((0, math.cos(a), math.sin(a))) * 0.07
            p1 = Vector((0, math.cos(a_out), math.sin(a_out))) * (rim_r - 0.005)
            d = p1 - p0
            dirn = d.normalized()
            tang = Vector((1, 0, 0)).cross(dirn)
            mid = Vector((cx + side * (w / 2 - 0.04), y, r)) + (p0 + p1) / 2
            rot = Matrix((Vector((1, 0, 0)), dirn, tang)).transposed().to_4x4()
            box(bm, mid, (0.030, d.length, 0.016), rot)
    rim = new_obj(name + "_Rim", bm, [M["black"]], col, sharp_angle=35)
    bm = bmesh.new()   # aero disc sits behind the spokes
    lathe(bm, [(side * (w / 2 - 0.075), 0.06), (side * (w / 2 - 0.075), rim_r - 0.008)],
          segs=48, axis="X", cx=cx, cy=y, cz=r)
    aero = new_obj(name + "_Aero", bm, [M["paint"]], col, sharp_angle=35)
    # brake disc (stays with the wheel visually; fine for a blockout)
    bm = bmesh.new()
    lathe(bm, [(0, 0.08), (0, r - 0.11), (-side * 0.03, r - 0.11), (-side * 0.03, 0.08)],
          segs=40, axis="X", cx=cx - side * (w / 2 - 0.09), cy=y, cz=r)
    disc = new_obj(name + "_Disc", bm, [M["titanium"]], col, sharp_angle=30)
    # join into one wheel object pivoted at the hub
    parts = [tyre, rim, disc, aero]
    with bpy.context.temp_override(active_object=tyre, selected_editable_objects=parts,
                                   object=tyre, selected_objects=parts):
        bpy.ops.object.join()
    tyre.name = name
    tyre.data.name = name
    set_origin(tyre, (cx, y, r))
    return tyre


# ---------------------------------------------------------------- thrusters
# Layout per MotorTrend's thrust-system article (Oct 2026): 4 rear cold-gas nozzles fed by
# equal-length titanium lines; Rocket adds 4 downward nozzles "just behind the wheels" and
# 2 on the nose pointing left/right for yaw = 10, matching the issue. Rear nozzle row from
# the rear photo (plate = 305 mm ruler): pitch ~255 mm, ~170 mm below the plate centre.
# Rear down nozzles are the exposed tubes at the tray corners in Tesla's Gravity image.
NOZZLES = {
    # name: (exit centre, exhaust direction, exit radius, length)
    # rear row: short thin-walled tubes along the top of the opening, just proud of the frame
    "RearOuter_L": ((0.382, 2.218, 0.458), (0, 1, 0), 0.036, 0.25),
    "RearInner_L": ((0.127, 2.218, 0.458), (0, 1, 0), 0.036, 0.25),
    "RearInner_R": ((-0.127, 2.218, 0.458), (0, 1, 0), 0.036, 0.25),
    "RearOuter_R": ((-0.382, 2.218, 0.458), (0, 1, 0), 0.036, 0.25),
    "DownFront_L": ((0.78, -0.94, 0.082), (0, 0, -1), 0.036, 0.16),
    "DownFront_R": ((-0.78, -0.94, 0.082), (0, 0, -1), 0.036, 0.16),
    "DownRear_L": ((0.72, 1.97, 0.12), (0, 0, -1), 0.034, 0.14),
    "DownRear_R": ((-0.72, 1.97, 0.12), (0, 0, -1), 0.034, 0.14),
    "Side_L": ((0.935, -1.95, 0.320), (1, 0, 0), 0.036, 0.16),
    "Side_R": ((-0.935, -1.95, 0.320), (-1, 0, 0), 0.036, 0.16),
}
FAN_X = (-0.382, -0.127, 0.127, 0.382)
FAN_Z, FAN_R, FAN_Y = 0.272, 0.094, 2.000      # fan-jet centres sit in the bays below SHELF_Z


def frame_from(direction):
    """Matrix whose local +Z is `direction` (lathe axis 'Z' then points along exhaust)."""
    d = Vector(direction).normalized()
    up = Vector((0, 0, 1)) if abs(d.z) < 0.9 else Vector((0, 1, 0))
    x = up.cross(d).normalized()
    y = d.cross(x)
    return Matrix((x, y, d)).transposed().to_4x4()


def build_rear_tube(col, M, name, exit_c, r, length):
    """Rear cold-gas outlet (Gravity photo): a short straight tube with a thin wall, a small
    chamfer on the lip and a long dark bore — not a ring on a plate. Smooth (48 segments),
    dark warm burnt metal. Built along +Z (= exhaust), origin = exit centre."""
    bm = bmesh.new()
    wall = 0.0055
    ro = r + wall
    prof = [(-length, ro), (-0.012, ro), (-0.003, ro - 0.0025), (0.0, r + 0.0015), (0.0, r),   # outside + chamfered lip
            (-0.010, r), (-length * 0.85, r * 0.92), (-length * 0.9, 0.0)]                     # bore, dark
    lathe(bm, prof, segs=48, axis="Z")
    for f in bm.faces:
        c = f.calc_center_median()
        f.material_index = 1 if math.hypot(c.x, c.y) < r + 0.0005 and c.z < -0.004 else 0
    ob = new_obj(PREFIX + "Nozzle_" + name, bm, [M["burnt"], M["black"]], col, sharp_angle=35)
    ob.data.transform(frame_from((0, 1, 0)))
    ob.matrix_world = Matrix.Translation(Vector(exit_c))
    return ob


def build_nozzle(col, M, name, exit_c, direction, r, length):
    """Convergent-divergent bell: titanium shell, dark throat, flange. Built along +Z
    (= exhaust direction) with the exit plane at z=0, then placed; origin = exit centre."""
    if name.startswith("Rear"):
        return build_rear_tube(col, M, name, exit_c, r, length)
    bm = bmesh.new()
    t = 0.008                                  # wall
    outer = [(-length, r * 0.62), (-length * 0.55, r * 0.70), (-length * 0.15, r * 1.02),
             (0.0, r + t * 1.6)]
    inner = [(0.0, r), (-length * 0.15, r * 0.88), (-length * 0.55, r * 0.46),
             (-length * 0.80, r * 0.40), (-length * 0.95, 0.0)]
    prof = [(z, rr) for z, rr in outer] + [(z, rr) for z, rr in inner]
    rings = lathe(bm, [(a, b) for a, b in prof[:-1]] + [(prof[-1][0], 0.001)], segs=32, axis="Z")
    # faces on the inner (throat) side get the dark material
    n_outer = len(outer)
    for f in bm.faces:
        c = f.calc_center_median()
        rad = math.hypot(c.x, c.y)
        f.material_index = 0
        if c.z < -0.002 and rad < r * 0.97 and c.z > -length * 0.98:
            # inner surface lies inside the outer shell at the same z
            zz = -c.z / length
            outer_r = r * (0.62 + (1.02 - 0.62) * (1 - zz))
            if rad < outer_r - t * 0.5:
                f.material_index = 1
    # mounting flange (square plate with rounded corners approximated by an octagon)
    flange_z = -length * 0.62
    lathe(bm, [(flange_z, r * 0.7), (flange_z, r * 1.55), (flange_z - 0.012, r * 1.55),
               (flange_z - 0.012, r * 0.7), (flange_z, r * 0.7)], segs=8, axis="Z")
    if name.startswith("DownRear"):
        # exposed feed tube with clamp rings running up into the body (Gravity photo)
        lathe(bm, [(-length, 0.022), (-0.40, 0.022), (-0.40, 0.0)], segs=16, axis="Z")
        for zc in (-length - 0.03, -length - 0.10, -length - 0.17):
            lathe(bm, [(zc, 0.022), (zc, 0.030), (zc - 0.014, 0.030), (zc - 0.014, 0.022),
                       (zc, 0.022)], segs=16, axis="Z")
    shell = M["titanium"]
    ob = new_obj(PREFIX + "Nozzle_" + name, bm, [shell, M["black"]], col, sharp_angle=50)
    ob.data.transform(frame_from(direction))
    ob.matrix_world = Matrix.Translation(Vector(exit_c))
    return ob


def build_rear(col, M):
    """Rear mechanical opening (Tesla Gravity photo), built as one housed assembly instead of
    parts hung on a plate:
      * one thick frame with chamfered upper corners round the whole opening, standing proud
        of the curved tail panel; its sides run down into a heavy bottom lip
      * the lip sits in front of the fan bays and hides the bottom of the fans
      * a horizontal shelf splits the nozzle row (top) from the 4 fan bays (below); the bay
        dividers live INSIDE the frame, set back from its face
      * fan-jets: bullet-shaped hub standing forward, twisted blades recessed behind it in the
        shadow of a short shroud. Rotors spin about local Z (= aft).
    The fan-jets are the suction system: they pull air from under the floor and blow it out
    aft (downforce + some thrust)."""
    bm = bmesh.new()
    frame_ring(bm, REAR_FRAME, REAR_OPEN, FRAME_BACK, FRAME_FRONT)
    frame = new_obj(PREFIX + "RearFrame", bm, [M["black"]], col, smooth=False)
    # heavy bottom lip: profile in (y, z), extruded across x, ends chamfered inward
    bm = bmesh.new()
    lip = [(2.06, 0.085), (2.235, 0.085), (2.235, 0.165), (2.205, 0.212), (2.06, 0.212)]
    rows = []
    for x, inset in ((-0.585, 0.03), (-0.55, 0.0), (0.55, 0.0), (0.585, 0.03)):
        rows.append([bm.verts.new((x, y - inset, z)) for y, z in lip])
    for a, b in zip(rows[:-1], rows[1:]):
        n = len(a)
        for i in range(n):
            j = (i + 1) % n
            bm.faces.new((a[i], a[j], b[j], b[i]))
    bm.faces.new(list(reversed(rows[0])))
    bm.faces.new(rows[-1])
    lipo = new_obj(PREFIX + "RearLip", bm, [M["black"]], col, smooth=False)
    # inside the opening: shelf under the nozzle row, bay dividers set back from the frame face
    bm = bmesh.new()
    inner_w = 2 * 0.60
    box(bm, (0, (OPEN_BACK + 2.13) / 2, SHELF_Z), (inner_w, 2.13 - OPEN_BACK, 0.016))
    for x in (-0.255, 0.0, 0.255):
        box(bm, (x, (OPEN_BACK + 2.10) / 2, (0.12 + SHELF_Z) / 2), (0.014, 2.10 - OPEN_BACK, SHELF_Z - 0.12))
    bays = new_obj(PREFIX + "RearBays", bm, [M["black"]], col, smooth=False)
    # upper zone: a dark panel the outlet tubes come through, so only a short stub shows
    bm = bmesh.new()
    box(bm, (0, 2.135, (SHELF_Z + 0.525) / 2), (2 * 0.60, 0.024, 0.525 - SHELF_Z))
    panel = new_obj(PREFIX + "RearOutletPanel", bm, [M["black"]], col, smooth=False)
    for name, (c, d, r, L) in NOZZLES.items():
        if name.startswith("Rear"):
            cb = bmesh.new()
            lathe(cb, [(-0.1, r + 0.0075), (0.1, r + 0.0075)], segs=48, axis="Y", cx=c[0], cy=2.135, cz=c[2], close=True)
            cut = new_obj("_tmp_cut", cb, [], col, smooth=False)
            apply_bool(panel, cut)
            bpy.data.objects.remove(cut, do_unlink=True)
    fans = []
    for i, x in enumerate(FAN_X):
        c = Vector((x, FAN_Y, FAN_Z))
        place = Matrix.Translation(c) @ frame_from((0, 1, 0))      # local +Z = aft
        # short shroud: the blades sit inside it, in shadow
        bm = bmesh.new()
        lathe(bm, [(0.0, FAN_R + 0.012), (0.105, FAN_R + 0.012), (0.105, FAN_R), (0.0, FAN_R),
                   (0.0, FAN_R + 0.012)], segs=48, axis="Z")
        lathe(bm, [(-0.02, 0.0), (-0.02, FAN_R + 0.012), (-0.03, FAN_R + 0.012), (-0.03, 0.0)],
              segs=48, axis="Z")                                   # back plate
        duct = new_obj(PREFIX + "FanDuct_%d" % i, bm, [M["black"]], col, sharp_angle=40)
        duct.data.transform(place)
        # rotor (origin = fan centre, local Z = aft): bullet hub well forward of the blades
        bm = bmesh.new()
        hub = [(0.0, 0.0), (0.0, 0.040), (0.055, 0.040), (0.090, 0.034), (0.115, 0.024),
               (0.130, 0.012), (0.137, 0.0)]
        lathe(bm, hub, segs=40, axis="Z")
        hub_faces = set(bm.faces)
        # 9 twisted blades: root pitch 52 deg, tip 26 deg, swept, closed thin solids
        for k in range(9):
            a0 = 2 * math.pi * k / 9
            stations = []
            for s in range(5):
                u = s / 4
                rr = lerp(0.040, FAN_R - 0.004, u)
                a = a0 + 0.22 * u                      # sweep
                pitch = math.radians(lerp(52, 26, u))
                chord = lerp(0.030, 0.042, u)
                rad = Vector((math.cos(a), math.sin(a), 0))
                tan = Vector((-math.sin(a), math.cos(a), 0))
                axial = Vector((0, 0, 1))
                cdir = (tan * math.cos(pitch) + axial * math.sin(pitch)).normalized()
                nrm = rad.cross(cdir).normalized()
                p = rad * rr + axial * 0.030                  # blade disc 3 cm forward of the back plate
                le, te = p + cdir * chord / 2, p - cdir * chord / 2
                t = 0.0025
                stations.append([bm.verts.new(v) for v in (le + nrm * t, te + nrm * t, te - nrm * t, le - nrm * t)])
            for sa, sb in zip(stations[:-1], stations[1:]):
                for q in range(4):
                    w = (q + 1) % 4
                    bm.faces.new((sa[q], sa[w], sb[w], sb[q]))
            bm.faces.new(list(reversed(stations[0])))
            bm.faces.new(stations[-1])
        for f in bm.faces:
            f.material_index = 0 if f in hub_faces else 1
        rotor = new_obj(PREFIX + "FanRotor_%d" % i, bm, [M["graphite"], M["black"]], col, sharp_angle=40)
        rotor.matrix_world = place
        fans.append((duct, rotor))
    # diffuser: a plain ramped floor under the lip (no vertical strakes hanging below)
    bm = bmesh.new()
    v = [bm.verts.new(p) for p in ((-0.62, 1.50, 0.098), (0.62, 1.50, 0.098), (0.62, 2.07, 0.086), (-0.62, 2.07, 0.086))]
    bm.faces.new(v)
    for z in (0.100, 0.112):                                      # two thin horizontal louvres under the lip
        box(bm, (0, 2.20, z), (1.10, 0.05, 0.006))
    diff = new_obj(PREFIX + "Diffuser", bm, [M["carbon"]], col, smooth=False)
    return frame, fans, diff


def build_underbody(col, M):
    """Flat floor pan and front splitter. (The suction fans exhaust into the rear tray.)"""
    bm = bmesh.new()
    box(bm, (0, -0.25, 0.085), (1.56, 3.50, 0.010))          # floor pan
    box(bm, (0, -2.10, 0.105), (1.30, 0.14, 0.012))          # splitter blade
    for x in (-0.55, 0.55):                                   # splitter end fences
        box(bm, (x, -2.11, 0.16), (0.01, 0.15, 0.05))
    for x in (-0.40, 0.0, 0.40):                              # fan intake slots (floor, aft)
        box(bm, (x, 1.30, 0.079), (0.30, 0.20, 0.004))
    floor = new_obj(PREFIX + "Underbody", bm, [M["carbon"]], col, smooth=False)
    return floor


WING_LE = (1.82, 0.885)      # stowed leading edge (y, z); pivot for deployment
WING_CHORD, WING_SPAN = 0.34, 1.64


def build_wing(col, M):
    """Large active wing (Gravity photo): wide, cambered, tips turned down into end plates,
    on two swan-neck struts. Stowed it lies on the tail deck behind the sails; Unity raises it
    and pitches it about the leading edge (local X). Struts are children of the wing and
    reach down into the body, so they only show once it is raised."""
    bm = bmesh.new()
    half = WING_SPAN / 2
    # spanwise stations (x, dz): flat centre section, then the tips bend down
    xs = [(-half - 0.10, -0.16), (-half - 0.06, -0.07), (-half, 0.0)]
    xs += [(-half + 2 * half * k / 10, 0.0) for k in range(1, 10)]
    xs += [(half, 0.0), (half + 0.06, -0.07), (half + 0.10, -0.16)]
    foil = ((0, 0), (0.06, 0.55), (0.30, 1.0), (0.65, 0.70), (1.0, 0.10), (1.0, -0.05),
            (0.65, 0.05), (0.30, -0.15), (0.06, -0.35))
    rings = []
    inc = math.radians(-2)   # stowed: lies on the flat tail deck
    for x, dz in xs:
        ring = []
        for u, t in foil:
            yy = u * WING_CHORD
            zz = t * 0.042
            ring.append((x, WING_LE[0] + yy * math.cos(inc) - zz * math.sin(inc),
                         WING_LE[1] + dz + yy * math.sin(inc) + zz * math.cos(inc)))
        rings.append(ring)
    rv = [[bm.verts.new(p) for p in r] for r in rings]
    for a, b in zip(rv[:-1], rv[1:]):
        n = len(a)
        for i in range(n):
            j = (i + 1) % n
            bm.faces.new((a[i], b[i], b[j], a[j]))
    bm.faces.new(rv[0])
    bm.faces.new(list(reversed(rv[-1])))
    wing = new_obj(PREFIX + "Wing", bm, [M["paint"]], col, sharp_angle=SHARP)
    set_origin(wing, (0, *WING_LE))
    struts = []
    for side in (1, -1):
        bm = bmesh.new()
        # swan neck: hooks onto the wing, arcs aft and down into the deck
        path = [(1.98, 0.865), (2.05, 0.83), (2.07, 0.75), (2.04, 0.65), (1.98, 0.53)]
        for (y0, z0), (y1, z1) in zip(path[:-1], path[1:]):
            mid = Vector((side * 0.36, (y0 + y1) / 2, (z0 + z1) / 2))
            L = math.hypot(y1 - y0, z1 - z0)
            ang = math.atan2(z1 - z0, y1 - y0)
            box(bm, mid, (0.012, L + 0.01, 0.045), Matrix.Rotation(ang, 4, "X"))
        s = new_obj(PREFIX + "WingStrut_" + ("L" if side > 0 else "R"), bm, [M["titanium"]], col,
                    smooth=False)
        set_origin(s, (side * 0.36, 1.98, 0.865))
        parent_keep(s, wing)
        struts.append(s)
    return wing, struts


def build_cockpit(col, M):
    """Two seats (driver on the left = +X, passenger mirrored), carbon tub, low dash under the
    windscreen with two driver displays + a centre display, yoke on its own column object
    (steers about its local Y = column axis). Laid out round EYE: windscreen ~1 m ahead, yoke
    0.55 m ahead. The centre console stays at cushion height so the wolf can lie across both
    seats (reclining pose, one paw on the yoke)."""
    seat_x = SEAT_X
    c = COCKPIT
    bm = bmesh.new()
    box(bm, (0, (c["y0"] + c["y1"]) / 2, c["z0"] - 0.02), (2 * c["x"] - 0.02, c["y1"] - c["y0"], 0.04))   # tub floor
    box(bm, (0, c["y1"] - 0.02, 0.62), (2 * c["x"] - 0.02, 0.04, 0.80))     # rear bulkhead
    box(bm, (0, 0.10, 0.27), (0.12, 1.20, 0.10))                           # low centre console
    tub = new_obj(PREFIX + "Tub", bm, [M["carbon"]], col, smooth=False)
    bm = bmesh.new()
    back = Matrix.Rotation(math.radians(-25), 4, "X")        # top leans back (+Y)
    for sx in (seat_x, -seat_x):
        box(bm, (sx, 0.48, 0.27), (0.46, 0.46, 0.09))        # cushion
        box(bm, (sx, 0.74, 0.55), (0.46, 0.08, 0.60), back)  # reclined back
        for s in (1, -1):                                    # low side bolsters (the wolf lies across them)
            box(bm, (sx + s * 0.22, 0.55, 0.34), (0.05, 0.50, 0.12))
    seat = new_obj(PREFIX + "Seat", bm, [M["interior"]], col, smooth=False)
    bm = bmesh.new()
    box(bm, (0, c["y0"] + 0.12, 0.61), (2 * c["x"] - 0.04, 0.26, 0.18))   # dash under the windscreen
    dash = new_obj(PREFIX + "Dash", bm, [M["black"]], col, smooth=False)
    bm = bmesh.new()
    tilt = Matrix.Rotation(math.radians(-22), 4, "X")       # screens lean back toward the driver
    for dx in (-0.09, 0.09):                                 # two driver displays
        box(bm, (seat_x + dx, c["y0"] + 0.25, 0.745), (0.15, 0.010, 0.085), tilt)
    box(bm, (0.0, c["y0"] + 0.23, 0.76), (0.26, 0.010, 0.15), tilt)        # centre display
    screens = new_obj(PREFIX + "Screens", bm, [M["screen"]], col, smooth=False)
    # yoke: rim cut open at the top, two grips, hub; top tilted 20° forward, column into the dash
    bm = bmesh.new()
    pts = []
    for k in range(25):
        a = math.radians(-150 + 300 * k / 24)        # open at the top
        pts.append((0.17 * math.sin(a), 0.11 * -math.cos(a)))
    for i, (x, z) in enumerate(pts[:-1]):
        x2, z2 = pts[i + 1]
        mid = Vector(((x + x2) / 2, 0, (z + z2) / 2))
        L = math.hypot(x2 - x, z2 - z)
        ang = math.atan2(z2 - z, x2 - x)
        box(bm, mid, (L + 0.004, 0.028, 0.03), Matrix.Rotation(-ang, 4, "Y"))
    box(bm, (0, 0, 0), (0.14, 0.04, 0.09))                     # hub
    lathe(bm, [(-0.30, 0.02), (0.0, 0.02)], segs=10, axis="Y")  # column, forward into the dash
    yoke = new_obj(PREFIX + "Yoke", bm, [M["carbon"]], col, smooth=False)
    yoke.matrix_world = (Matrix.Translation((seat_x, EYE[1] - 0.55, 0.70))
                         @ Matrix.Rotation(math.radians(20), 4, "X"))
    return tub, seat, dash, yoke


def build():
    col = collection()
    M = materials()
    body = build_body(col, M)
    cabin = build_cabin(col, M)
    head, tail = build_lights(col, M, body)
    nozzles = [build_nozzle(col, M, n, *v) for n, v in NOZZLES.items()]
    bar, fans, diff = build_rear(col, M)
    floor = build_underbody(col, M)
    # open the skin behind every nozzle so you look into the throat, not at a body panel
    for name, (c, d, r, L) in NOZZLES.items():
        cb = bmesh.new()
        lathe(cb, [(-L * 0.9, r + 0.012), (0.08, r + 0.012)], segs=32, axis="Z", close=True)
        cb.transform(Matrix.Translation(Vector(c)) @ frame_from(d))
        cut = new_obj("_tmp_cut", cb, [], col, smooth=False)
        for target in (body, floor):
            apply_bool(target, cut)
        bpy.data.objects.remove(cut, do_unlink=True)
    # cockpit opening under the canopy; the cut walls take the interior material
    c = COCKPIT
    cb = bmesh.new()
    box(cb, (0, (c["y0"] + c["y1"]) / 2, (c["z0"] + 1.4) / 2), (2 * c["x"], c["y1"] - c["y0"], 1.4 - c["z0"]))
    cut = new_obj("_tmp_cockpit", cb, [M["interior"]], col, smooth=False)
    apply_bool(body, cut, transfer=True)
    bpy.data.objects.remove(cut, do_unlink=True)
    body.data.set_sharp_from_angle(angle=math.radians(SHARP))
    build_wing(col, M)
    build_cockpit(col, M)
    wheels = []
    for nm, wd, y in (("FL", WHEEL_F, AXLE_F), ("FR", WHEEL_F, AXLE_F),
                      ("RL", WHEEL_R, AXLE_R), ("RR", WHEEL_R, AXLE_R)):
        side = 1 if nm[1] == "L" else -1      # nose -Y => Blender +X is the car's left
        wheels.append(build_wheel(col, M, PREFIX + "Wheel_" + nm, wd, y, side))
    print("built", [o.name for o in col.objects])
    return col


if __name__ == "__main__":
    build()
