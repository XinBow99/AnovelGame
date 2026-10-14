# 三輻射三足動物草模：骨架 + 腳 + 翻面步態（STriDER 式）。
# 在 DST_Creature_Tripod.blend 裡執行；身體零件（TRI_Body / TRI_Ridge* / TRI_Eye*）要先存在、放在原點。
# 參數用 globals 傳：BODY_Z, HIP_R, FOOT_R, L1, L2, STEPS
import bpy, math
from mathutils import Vector, Matrix, Quaternion

G = globals()
BODY_Z = G.get("BODY_Z", 0.40)
HIP_R = G.get("HIP_R", 0.135)
FOOT_R = G.get("FOOT_R", 0.42)
L1, L2 = G.get("L1", 0.36), G.get("L2", 0.44)
STEPS = G.get("STEPS", 6)
STEP, PAUSE, LIFT = 14, 5, 0.10

col = bpy.data.collections["TRI_Blockout"]; scn = bpy.context.scene
M_SKIN = bpy.data.materials["M_TRI_Skin"]; M_RIDGE = bpy.data.materials["M_TRI_Ridge"]; M_PAD = bpy.data.materials["M_TRI_Pad"]
LEG_ANG = [math.radians(60 + 120 * k) for k in range(3)]   # 腳在瓣與瓣之間


def P(r, a, z): return Vector((r * math.cos(a), r * math.sin(a), z))


def knee_of(hip, foot, out):
    d = foot - hip; L = d.length; dn = d.normalized()
    a = (L1 * L1 - L2 * L2 + L * L) / (2 * L); h = math.sqrt(max(0, L1 * L1 - a * a))
    up = (out - dn * out.dot(dn)).normalized()
    return hip + dn * a + up * h


# ---- 骨架
arm = bpy.data.armatures.new("TRI_Rig"); arm.display_type = "STICK"
rig = bpy.data.objects.new("TRI_Rig", arm); col.objects.link(rig)
bpy.context.view_layer.objects.active = rig
bpy.ops.object.mode_set(mode="EDIT")
eb = arm.edit_bones
root = eb.new("Root"); root.head = (0, 0, 0); root.tail = (0, 0.15, 0)
bod = eb.new("Body"); bod.head = (0, 0, BODY_Z); bod.tail = (0, 0, BODY_Z + 0.15); bod.parent = root
rest = {}
for k, a in enumerate(LEG_ANG):
    hip, foot = P(HIP_R, a, BODY_Z), P(FOOT_R, a, 0.02)
    knee = knee_of(hip, foot, Vector((0.35 * math.cos(a), 0.35 * math.sin(a), 1)).normalized())
    rest[k] = (hip, knee, foot)
    up = eb.new(f"Leg{k}_Upper"); up.head = hip; up.tail = knee; up.parent = bod
    lo = eb.new(f"Leg{k}_Lower"); lo.head = knee; lo.tail = foot; lo.parent = up; lo.use_connect = True
    ft = eb.new(f"Foot{k}_IK"); ft.head = foot; ft.tail = foot + Vector((0, 0, 0.06)); ft.parent = root
    pl = eb.new(f"Pole{k}"); pl.head = P(0.5, a, 1.2); pl.tail = P(0.5, a, 1.26); pl.parent = root
bpy.ops.object.mode_set(mode="POSE")
for k in range(3):
    c = rig.pose.bones[f"Leg{k}_Lower"].constraints.new("IK")
    c.target = rig; c.subtarget = f"Foot{k}_IK"; c.pole_target = rig; c.pole_subtarget = f"Pole{k}"
    c.pole_angle = math.radians(-90); c.chain_count = 2
bpy.ops.object.mode_set(mode="OBJECT")


# ---- 腳的網格（草模：剛性綁在骨頭上）
def _own(o, m):
    for cc in o.users_collection: cc.objects.unlink(o)
    col.objects.link(o); o.data.materials.append(m); bpy.ops.object.shade_smooth(); return o


def seg(name, a, b, r1, r2, m):
    d = b - a
    bpy.ops.mesh.primitive_cone_add(vertices=16, radius1=r1, radius2=r2, depth=d.length, location=(a + b) / 2)
    o = bpy.context.active_object; o.name = name
    o.rotation_mode = "QUATERNION"; o.rotation_quaternion = Vector((0, 0, 1)).rotation_difference(d.normalized())
    return _own(o, m)


def ball(name, p, r, m, scale=(1, 1, 1)):
    bpy.ops.mesh.primitive_uv_sphere_add(radius=r, location=p, segments=16, ring_count=10)
    o = bpy.context.active_object; o.name = name; o.scale = scale
    return _own(o, m)


def attach(o, bone):
    bpy.context.view_layer.update(); mw = o.matrix_world.copy()
    o.parent = rig; o.parent_type = "BONE"; o.parent_bone = bone
    bpy.context.view_layer.update(); o.matrix_world = mw


for k in range(3):
    hip, knee, foot = rest[k]
    attach(ball(f"TRI_Hip{k}", hip, 0.026, M_RIDGE), f"Leg{k}_Upper")
    attach(seg(f"TRI_Thigh{k}", hip, knee, 0.024, 0.016, M_SKIN), f"Leg{k}_Upper")
    attach(ball(f"TRI_Knee{k}", knee, 0.018, M_RIDGE), f"Leg{k}_Lower")
    attach(seg(f"TRI_Shin{k}", knee, foot + Vector((0, 0, 0.012)), 0.015, 0.007, M_SKIN), f"Leg{k}_Lower")
    attach(ball(f"TRI_Foot{k}", foot, 0.026, M_PAD, (1.3, 1.3, 0.35)), f"Foot{k}_IK")
for o in list(col.objects):
    if o.name.startswith(("TRI_Body", "TRI_Ridge", "TRI_Eye")):
        o.location.z += BODY_Z; attach(o, "Body")

# ---- 翻面步態：每一步甩一隻腳越過另外兩腳的連線（鏡射），身體繞那條邊翻 180°（後緣往下、往前）
want = Vector((1, 0, 0))
def smooth(t): return t * t * (3 - 2 * t)
feet = [Vector((FOOT_R * math.cos(a), FOOT_R * math.sin(a), 0.02)) for a in LEG_ANG]
R0 = Matrix.Identity(3); c0 = Vector((0, 0, BODY_Z)); keys = []; frame = 1
keys.append((frame, c0.copy(), R0.copy(), [f.copy() for f in feet]))
for step in range(STEPS):
    best = None; cen = (feet[0] + feet[1] + feet[2]) / 3
    for s in range(3):
        a, b = [i for i in range(3) if i != s]
        e = feet[b] - feet[a]; n = Vector((e.y, -e.x, 0)).normalized()
        if n.dot(feet[s] - feet[a]) > 0: n = -n
        score = n.dot(want) - 0.5 * n.y * cen.y          # 偏離中線就往回修
        if best is None or score > best[0]: best = (score, s, a, b, n)
    _, s, a, b, n = best
    e = (feet[b] - feet[a]).normalized()
    proj = feet[a] + e * (feet[s] - feet[a]).dot(e)
    target = proj * 2 - feet[s]; target.z = 0.02; start_s = feet[s].copy()
    newfeet = [f.copy() for f in feet]; newfeet[s] = target
    c_to = (newfeet[0] + newfeet[1] + newfeet[2]) / 3; c_to.z = BODY_Z
    u = n.cross(Vector((0, 0, 1))).normalized()
    for i in range(1, STEP + 1):
        t = i / STEP; st = smooth(t)
        rot = Quaternion(u, math.pi * st).to_matrix() @ R0
        pos = c0.lerp(c_to, st); pos.z = BODY_Z + 0.03 * math.sin(math.pi * t)
        sf = start_s.lerp(target, st); sf.z = 0.02 + LIFT * math.sin(math.pi * t)
        fr = [f.copy() for f in feet]; fr[s] = sf
        keys.append((frame + i, pos, rot, fr))
    feet = newfeet; R0 = Quaternion(u, math.pi).to_matrix() @ R0; c0 = c_to
    frame += STEP; keys.append((frame + PAUSE, c0.copy(), R0.copy(), [f.copy() for f in feet])); frame += PAUSE

pb = rig.pose.bones
for b in pb: b.rotation_mode = "QUATERNION"
rest_body = rig.data.bones["Body"].matrix_local.to_3x3().to_4x4()
for (f, pos, rot, fr) in keys:
    scn.frame_set(f)
    pb["Body"].matrix = Matrix.Translation(pos) @ rot.to_4x4() @ rest_body
    pb["Body"].keyframe_insert("location", frame=f); pb["Body"].keyframe_insert("rotation_quaternion", frame=f)
    center = Vector((pos.x, pos.y, 0))
    for k in range(3):
        fb = pb[f"Foot{k}_IK"]
        fb.matrix = Matrix.Translation(fr[k]) @ rig.data.bones[f"Foot{k}_IK"].matrix_local.to_3x3().to_4x4()
        fb.keyframe_insert("location", frame=f)
        out = Vector((fr[k].x - center.x, fr[k].y - center.y, 0))
        out = out.normalized() if out.length > 1e-4 else Vector((1, 0, 0))
        pp = pb[f"Pole{k}"]
        pp.matrix = Matrix.Translation(center + out * 0.15 + Vector((0, 0, 1.2))) @ rig.data.bones[f"Pole{k}"].matrix_local.to_3x3().to_4x4()
        pp.keyframe_insert("location", frame=f)
    bpy.context.view_layer.update()
scn.frame_start = 1; scn.frame_end = frame
cam = bpy.data.objects["Camera"]; cam.animation_data_clear()
if cam.constraints: cam.constraints[0].target = rig
cam.location = (0.2, -2.0, 0.9); cam.keyframe_insert("location", frame=1)
cam.location = (c0.x + 0.2, c0.y - 2.0, 0.9); cam.keyframe_insert("location", frame=frame)
print("tri_blockout ok frames", frame, "end", tuple(round(x, 2) for x in c0), "knee z", round(rest[0][1].z, 3))
