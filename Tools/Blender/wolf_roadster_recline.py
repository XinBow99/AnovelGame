"""Maned-wolf player: Roadster "reclining Buddha" drive loop + steer poses.

Run inside blender/SK_DST_Player_ManedWolf.blend (uses its text block `player_pose_lib`).
Clip space = Roadster vehicle space at game (half) scale: Blender +X = car left, -Y = nose.
The Roadster seats/yoke come from Tools/Blender/roadster_build.py (real size) x 0.5:
cushions at x +-0.15, top z 0.158, low bolsters top z 0.20, yoke hub (0.15, -0.085, 0.35)
with the column axis tilted 20 deg (local +Y points back/up toward the driver).

Pose: lying across both seats on its LEFT side (belly toward the dash, head over the driver
seat), left elbow on the cushion with the paw cupping the cheek, right forepaw palming the
yoke hub (one-pawed driving), hind legs stacked over the passenger seat, tail draped.
(The statue lies on its right side; mirrored here because the yoke is on the left.)

Writes actions PL_Roadster_Recline (0-90, loop), PL_Roadster_Recline_SteerLeft/Right
(1-frame poses, paw twisted +-60 deg round the column), NLA tracks with the same names,
meta in `player_actions_meta`, and exports
unity_export/Player/SK_DST_Player_ManedWolf_Roadster.fbx (armature only, same settings as
the main player FBX so the bone paths match).
"""
import bpy, math, os
from mathutils import Vector, Matrix

ns = {}
exec(bpy.data.texts["player_pose_lib"].as_string(), ns)
P = ns["P"]
ARM = ns["ARM"]

HUB = Vector((0.15, -0.085, 0.35))
AX = Vector((0.0, 0.9397, 0.3420))           # yoke column axis (toward the driver)
FACE = HUB + AX * 0.012                       # rear face of the hub
Z0 = 0.255                                    # spine height lying on the low bolsters
FL_PAW = (Vector((0.295, 0.185, 0.262)), Vector((0.30, 0.175, 0.30)), Vector((0.28, 0.22, 0.12)))
OUT = r"C:\Users\asdew\OneDrive\桌面\projects\DUST\unity_export\Player\SK_DST_Player_ManedWolf_Roadster.fbx"
CLIPS = ["PL_Roadster_Recline", "PL_Roadster_Recline_SteerLeft", "PL_Roadster_Recline_SteerRight"]


def recline_body(breathe=0.0, ear=0.0, tail_sway=0.0):
    ns["reset"]()
    ns["body"]((-0.15, 0.235, Z0 + 0.002 * breathe), yaw=90, roll=90)   # head to +X, left side down
    # in the rolled body: pitch- = toward the belly (= the road), yaw- = toward world up
    for n, p, y in (("Spine1", -4 + breathe * 0.8, 0), ("Spine2", -4, 0), ("Chest", -2, -6),
                    ("Neck1", -10, -30), ("Neck2", -14, -26), ("Head", -26, -4)):
        ns["bend"](n, p, y)
    ns["bend"]("Ear_L", 20 + ear, 0, -10)
    ns["bend"]("Ear_R", 20 - ear * 0.5, 0, 10)
    ns["aim_chain"](["Tail1", "Tail2", "Tail3", "Tail4"],
                    [(-0.26, 0.27, 0.235), (-0.31, 0.26, 0.21),
                     (-0.335, 0.21 + tail_sway * 0.01, 0.20), (-0.34, 0.15 + tail_sway * 0.02, 0.195)])


def head_fix(look=0.0, tilt=-22.0, down=-4.0):
    """Head in world terms: facing the road, cheek tilted down onto the left paw."""
    bpy.context.view_layer.update()
    ns["set_world"](P["Head"], ns["world_rot"](look, down, tilt))
    bpy.context.view_layer.update()


def recline_legs(steer=0.0, breathe=0.0):
    set_leg = ns["set_leg"]
    R = Matrix.Rotation(math.radians(steer), 3, AX)   # + = left turn (top of the yoke to +X)
    set_leg("FR", FACE + R @ Vector((0, 0.035, -0.02)), FACE + R @ Vector((0, -0.008, 0.012)),
            Vector((0.24, 0.22, 0.22)))
    lift = Vector((0, 0, 0.003 * breathe))
    set_leg("FL", FL_PAW[0] + lift, FL_PAW[1] + lift, FL_PAW[2])
    set_leg("HL", Vector((-0.31, 0.11, 0.20)), Vector((-0.345, 0.08, 0.192)), Vector((-0.16, -0.25, 0.21)))
    set_leg("HR", Vector((-0.29, 0.10, 0.255)), Vector((-0.325, 0.07, 0.25)), Vector((-0.16, -0.25, 0.29)))


def pose_at(f, n=90, steer=0.0):
    ph = 2 * math.pi * f / n
    br = math.sin(ph * 2)                              # two slow breaths per loop
    ear = 25 * math.sin(math.pi * (f - 44) / 10) if 44 <= f <= 54 else 0.0   # one ear twitch
    recline_body(breathe=br, ear=ear, tail_sway=math.sin(ph))
    head_fix(look=4 * math.sin(ph), tilt=-22 + 1.5 * br, down=-4 + math.sin(ph + 1))
    recline_legs(steer=steer, breathe=br)


def key_all(f):
    ns["key_body"](f)
    for l in ("FR", "FL", "HL", "HR"):
        ns["key_leg"](l, f)


def make(name, f0, f1, steer=0.0, n=90):
    ns["clear_clip"](name)
    if bpy.data.actions.get(name):
        bpy.data.actions.remove(bpy.data.actions[name])
    ns["new_clip"](name)
    loop = f1 - f0 > 3
    for f in range(f0, f1 + 1, 3 if loop else 1):
        ns["goto"](f)
        pose_at(f % n if loop else 0, n, steer)
        key_all(f)
    if loop:                                           # last frame = first frame
        ns["goto"](f1)
        pose_at(0, n, steer)
        key_all(f1)
    return ns["bake"](name, f0, f1)


def main(export=True):
    for pb in P:
        for c in pb.constraints:
            c.mute = False
    make(CLIPS[0], 0, 90)
    make(CLIPS[1], 0, 1, steer=60)
    make(CLIPS[2], 0, 1, steer=-60)
    for pb in P:
        for c in pb.constraints:
            c.mute = True
    for n in ns["CTRL"]:
        o = bpy.data.objects[n]
        if o.animation_data:
            o.animation_data.action = None
    ns["write_meta"](CLIPS[0], {"vehicle": "Roadster", "kind": "Ride", "frames": [0, 90], "fps": 30, "loop": True,
                                "desc": "Roadster：像臥佛一樣側躺在兩個座位上，左前腳撐著臉頰，右前腳掌單手按在方向盤中央開車"})
    ns["write_meta"](CLIPS[1], {"vehicle": "Roadster", "kind": "Ride", "frames": [0, 1], "pose": True, "steer_deg": 60,
                                "fps": 30, "desc": "右前腳掌貼著方向盤中央往左轉 60°"})
    ns["write_meta"](CLIPS[2], {"vehicle": "Roadster", "kind": "Ride", "frames": [0, 1], "pose": True, "steer_deg": -60,
                                "fps": 30, "desc": "右前腳掌貼著方向盤中央往右轉 60°"})
    ad = ARM.animation_data
    ad.action = None
    for n in CLIPS:
        tr = ad.nla_tracks.get(n)
        if tr:
            ad.nla_tracks.remove(tr)
        tr = ad.nla_tracks.new()
        tr.name = n
        tr.strips.new(n, 0, bpy.data.actions[n])
    if export:
        sc = bpy.context.scene
        sc.render.fps, sc.render.fps_base = 30, 1.0
        for tr in ad.nla_tracks:
            tr.mute = tr.name not in CLIPS
        for o in bpy.context.selected_objects:
            o.select_set(False)
        ARM.hide_set(False)
        ARM.select_set(True)
        bpy.context.view_layer.objects.active = ARM
        bpy.ops.export_scene.fbx(
            filepath=OUT, use_selection=True, object_types={"ARMATURE"},
            add_leaf_bones=False, bake_anim=True, bake_anim_use_all_actions=False, bake_anim_use_nla_strips=True,
            bake_anim_force_startend_keying=True, bake_anim_simplify_factor=0.0, axis_forward="-Z", axis_up="Y",
            bake_space_transform=False, apply_scale_options="FBX_SCALE_ALL", mesh_smooth_type="FACE",
            use_armature_deform_only=True)
        for tr in ad.nla_tracks:
            tr.mute = True
        print("exported", OUT, os.path.getsize(OUT) // 1024, "KB")


if __name__ == "__main__":
    main()
