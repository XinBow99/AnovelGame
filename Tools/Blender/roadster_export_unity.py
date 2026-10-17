# Roadster (issue #8) → Unity. Run inside the Blender that has SM_DST_Vehicle_Roadster.blend open
# (MCP: runpy.run_path(this, run_name="__main__")), or headless:
#   blender -b SM_DST_Vehicle_Roadster.blend --factory-startup -P roadster_export_unity.py -- [out_dir]
# 1. Rebuilds the model from roadster_build.py (so the export always matches the script).
# 2. Joins everything that never moves into RD_Chassis; keeps the moving / FX parts separate:
#    RD_Wheel_FL/FR/RL/RR (origin = hub, spin about local X), RD_Yoke (column), RD_Wing (+ its
#    two struts as children; origin = leading edge, pitches about local X), RD_FanRotor_0..3
#    (spin about local Z = aft), RD_Nozzle_* (origin = exit centre), lamps (emission control).
# 3. Writes roadster_layout.json in UNITY space (half scale, nose +Z, +X = car right):
#    wheels, nozzles (exit point + exhaust direction), fan-jets, wing pivot, seat eye, COM.
#    Unity reads this instead of duplicating the tables — Blender stays the single source.
# 4. Exports SM_DST_Vehicle_Roadster.fbx at REAL size; Unity's importer applies ×0.5.
#    Same axis handling as the MiG-25: the car is turned 180° about Z for the export so that
#    Unity's bakeAxisConversion puts the nose on +Z; composite mapping of an authored Blender
#    point (x, y, z) → Unity (-x, z, -y) × 0.5.
import bpy, os, sys, runpy, json, math
from mathutils import Matrix, Vector

HERE = os.path.dirname(os.path.abspath(__file__)) if "__file__" in globals() else \
    r"C:\Users\asdew\My project\Tools\Blender"
OUT = r"C:\Users\asdew\My project\Assets\DUST\Vehicles\Roadster"
if "--" in sys.argv and len(sys.argv) > sys.argv.index("--") + 1:
    OUT = sys.argv[sys.argv.index("--") + 1]

SCALE = 0.5
KEEP = ("RD_Wheel_", "RD_Yoke", "RD_Wing", "RD_FanRotor_", "RD_Nozzle_", "RD_HeadLamp", "RD_HeadSlits",
        "RD_TailLamp")


def to_unity(p):
    return [round(-p[0] * SCALE, 5), round(p[2] * SCALE, 5), round(-p[1] * SCALE, 5)]


def dir_unity(d):
    v = Vector((-d[0], d[2], -d[1])).normalized()
    return [round(v.x, 5), round(v.y, 5), round(v.z, 5)]


def join(name, objs):
    objs = [o for o in objs if o.type == "MESH"]
    for o in objs:   # apply modifiers (solidify on the carbon plates) before joining
        if o.modifiers:
            with bpy.context.temp_override(object=o, active_object=o, selected_objects=[o]):
                for m in list(o.modifiers):
                    bpy.ops.object.modifier_apply(modifier=m.name)
    with bpy.context.temp_override(active_object=objs[0], selected_editable_objects=objs,
                                   object=objs[0], selected_objects=objs):
        bpy.ops.object.join()
    ob = objs[0]
    ob.name = name
    ob.data.name = name
    return ob


def layout(ns):
    """Everything Unity needs, read from the build script's own tables."""
    wf, wr = ns["WHEEL_F"], ns["WHEEL_R"]
    wheels = []
    for nm, wd, y in (("FL", wf, ns["AXLE_F"]), ("FR", wf, ns["AXLE_F"]),
                      ("RL", wr, ns["AXLE_R"]), ("RR", wr, ns["AXLE_R"])):
        side = 1 if nm[1] == "L" else -1
        wheels.append(dict(name="RD_Wheel_" + nm, pos=to_unity((side * wd["x"], y, wd["r"])),
                           radius=round(wd["r"] * SCALE, 5), width=round(wd["w"] * SCALE, 5),
                           front=nm[0] == "F", left=nm[1] == "L"))
    nozzles = []
    for name, (c, d, r, L) in ns["NOZZLES"].items():
        group = "rear" if name.startswith("Rear") else "down" if name.startswith("Down") else "side"
        nozzles.append(dict(name=name, obj="RD_Nozzle_" + name, group=group, pos=to_unity(c),
                            dir=dir_unity(d), radius=round(r * SCALE, 5)))
    fans = [dict(name="RD_FanRotor_%d" % i, pos=to_unity((x, ns["FAN_Y"], ns["FAN_Z"])),
                 dir=dir_unity((0, 1, 0)), radius=round(ns["FAN_R"] * SCALE, 5))
            for i, x in enumerate(ns["FAN_X"])]
    le = ns["WING_LE"]
    return dict(
        scale=SCALE, length=round(4.31 * SCALE, 4), width=2.0 * SCALE, height=round(1.105 * SCALE, 4),
        wheels=wheels, nozzles=nozzles, fans=fans,
        wing=dict(obj="RD_Wing", pivot=to_unity((0, le[0], le[1])), chord=round(ns["WING_CHORD"] * SCALE, 4),
                  span=round(ns["WING_SPAN"] * SCALE, 4)),
        # seated eye of the single seat (driver on the car's left), and a low, slightly rear COM
        seatEye=to_unity(ns["EYE"]),
        com=to_unity((0.0, 0.15, 0.36)),
    )


def main():
    os.makedirs(OUT, exist_ok=True)
    ns = runpy.run_path(os.path.join(HERE, "roadster_build.py"))
    col = ns["build"]()
    # single root so Unity gets one hierarchy
    root = bpy.data.objects.new("RD_Root", None)
    col.objects.link(root)
    keep = [o for o in col.objects if o is not root and o.name.startswith(KEEP)]
    static = [o for o in col.objects if o is not root and o not in keep and o.parent is None]
    chassis = join("RD_Chassis", static)
    for o in [chassis] + [k for k in keep if k.parent is None]:
        o.parent = root
        o.matrix_parent_inverse = Matrix.Identity(4)
    bpy.context.view_layer.update()

    data = layout(ns)
    with open(os.path.join(OUT, "roadster_layout.json"), "w", encoding="utf-8") as f:
        json.dump(data, f, indent=1)
    print("wrote layout", len(data["nozzles"]), "nozzles")

    # nose to Blender +Y for the export (see header)
    Rz = Matrix.Rotation(math.pi, 4, "Z")
    root.matrix_world = Rz @ root.matrix_world
    bpy.context.view_layer.update()
    vl = bpy.context.view_layer
    for o in vl.objects:
        o.select_set(False)
    for o in col.objects:
        o.select_set(True)
    vl.objects.active = root
    fbx = os.path.join(OUT, "SM_DST_Vehicle_Roadster.fbx")
    bpy.ops.export_scene.fbx(
        filepath=fbx, use_selection=True, object_types={"EMPTY", "MESH"},
        apply_scale_options="FBX_SCALE_ALL", axis_forward="-Z", axis_up="Y",
        bake_space_transform=False, bake_anim=False, mesh_smooth_type="FACE",
        use_mesh_modifiers=True, use_tspace=False, add_leaf_bones=False)
    print("exported", fbx)
    ns["build"]()          # back to the authored, unjoined model (nose -Y)


if __name__ == "__main__":
    main()
