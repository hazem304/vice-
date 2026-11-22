# ============================================================================
# Project: Vice Coast Open-World (Vice City Style Vertical Slice)
# Module:  Module 4 — Advanced Vehicle Physics & Traffic AI (Blender 4.x -> Unity)
# File:    BlenderScripts/03_generate_sports_car_modular.py
# Author:  Lead Game Architect
# Purpose: Procedurally generates a Vice City coastal sports supercar with:
#          - Sculpted aerodynamic chassis + glass canopy + rear spoiler + neon tailbar
#          - 4 separated Wheel meshes (Wheel_FL, Wheel_FR, Wheel_RL, Wheel_RR)
#            with accurate origins for Unity WheelCollider syncing
#          - 2 separated Hinged Door meshes (Door_L, Door_R) with pivot origins
#            positioned at the front A-pillar hinge for enter/exit animations
#          - Auto UV unwrapping and Unity-ready FBX/GLB export.
# Compatibility: Blender 4.0+
# ============================================================================

import math
import os
import bpy
import bmesh
from mathutils import Vector


# ============================================================================
# CONFIGURATION
# ============================================================================

CONFIG = {
    "vehicle_name": "Vice_Cheetah_GT",
    "paint_color": (0.94, 0.10, 0.42),   # Signature Vice Sunset Magenta-Red
    "export_format": "FBX",              # "FBX" or "GLB"
    "unity_export_dir": os.path.abspath(
        os.path.join(os.path.expanduser("~"), "ViceCoast_OpenWorld", "Assets", "_Project", "Art", "Models", "Vehicles")
    ),
    "auto_export": True,
}


# ============================================================================
# MATERIALS (Blender 4.x Principled BSDF)
# ============================================================================

def clear_scene():
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    for block in (bpy.data.meshes, bpy.data.materials):
        for item in list(block):
            if item.users == 0:
                block.remove(item)


def make_mat(name, color, roughness=0.4, metallic=0.0, coat=0.0, emission=None, emission_strength=0.0):
    if name in bpy.data.materials:
        return bpy.data.materials[name]
    mat = bpy.data.materials.new(name=name)
    mat.use_nodes = True
    bsdf = mat.node_tree.nodes.get("Principled BSDF")
    if bsdf is not None:
        bsdf.inputs["Base Color"].default_value = (*color, 1.0)
        bsdf.inputs["Roughness"].default_value = roughness
        bsdf.inputs["Metallic"].default_value = metallic
        if coat > 0.0 and "Coat Weight" in bsdf.inputs:
            bsdf.inputs["Coat Weight"].default_value = coat
        if emission is not None and emission_strength > 0.0:
            if "Emission Color" in bsdf.inputs:
                bsdf.inputs["Emission Color"].default_value = (*emission, 1.0)
            if "Emission Strength" in bsdf.inputs:
                bsdf.inputs["Emission Strength"].default_value = emission_strength
    return mat


def build_car_materials():
    return {
        "paint": make_mat("MAT_Car_VicePaint", CONFIG["paint_color"], roughness=0.18, metallic=0.72, coat=1.0),
        "carbon": make_mat("MAT_Car_CarbonTrim", (0.06, 0.06, 0.07), roughness=0.35, metallic=0.35),
        "glass": make_mat("MAT_Car_TintedGlass", (0.04, 0.07, 0.11), roughness=0.06, metallic=0.25, coat=1.0),
        "tire": make_mat("MAT_Car_TireRubber", (0.04, 0.04, 0.045), roughness=0.82),
        "rim": make_mat("MAT_Car_AlloyRim", (0.82, 0.84, 0.88), roughness=0.18, metallic=0.95),
        "caliper": make_mat("MAT_Car_BrakeCaliper", (0.95, 0.72, 0.05), roughness=0.3, metallic=0.5),
        "headlight": make_mat(
            "MAT_Car_HeadlightLED", (0.95, 0.98, 1.0), roughness=0.1,
            emission=(0.95, 0.98, 1.0), emission_strength=14.0
        ),
        "taillight": make_mat(
            "MAT_Car_TaillightNeon", (1.0, 0.05, 0.08), roughness=0.1,
            emission=(1.0, 0.04, 0.06), emission_strength=16.0
        ),
    }


def apply_box_uv(mesh, scale=0.45):
    bm = bmesh.new()
    bm.from_mesh(mesh)
    uv_layer = bm.loops.layers.uv.verify()
    for face in bm.faces:
        n = face.normal
        ax, ay, az = abs(n.x), abs(n.y), abs(n.z)
        for loop in face.loops:
            co = loop.vert.co
            if az >= ax and az >= ay:
                u, v = co.x * scale, co.y * scale
            elif ax >= ay:
                u, v = co.y * scale, co.z * scale
            else:
                u, v = co.x * scale, co.z * scale
            loop[uv_layer].uv = (u, v)
    bm.to_mesh(mesh)
    bm.free()


def add_box(bm, center, size, mat_idx):
    ret = bmesh.ops.create_cube(bm, size=1.0)
    verts = ret["verts"]
    bmesh.ops.scale(bm, vec=Vector(size), verts=verts)
    bmesh.ops.translate(bm, vec=Vector(center), verts=verts)
    vset = set(verts)
    for f in bm.faces:
        if all(v in vset for v in f.verts):
            f.material_index = mat_idx


# ============================================================================
# CHASSIS, HINGED DOORS & MODULAR WHEELS
# ============================================================================

def build_chassis(root_obj, mats):
    """Builds the main wedge supercar body facing -Y in Blender (+Z in Unity)."""
    bm = bmesh.new()
    palette = [mats["paint"], mats["carbon"], mats["glass"], mats["headlight"], mats["taillight"]]

    # Lower underbody & diffuser
    add_box(bm, (0.0, 0.0, 0.34), (1.88, 4.38, 0.24), 1)
    # Main mid-body sculpted deck (front nose to rear haunches)
    add_box(bm, (0.0, -1.25, 0.62), (1.92, 1.80, 0.40), 0)  # Front hood
    add_box(bm, (0.0, 1.25, 0.68), (1.98, 1.75, 0.50), 0)   # Rear engine deck
    add_box(bm, (0.0, 0.0, 0.52), (1.56, 1.45, 0.28), 1)    # Cabin floor tub (leaves room for doors)

    # Cockpit glass canopy & roof
    add_box(bm, (0.0, 0.10, 1.03), (1.46, 1.72, 0.44), 2)
    add_box(bm, (0.0, 0.15, 1.25), (1.36, 1.35, 0.08), 0)

    # Rear GT Spoiler & twin struts
    add_box(bm, (0.0, 2.02, 1.18), (1.94, 0.32, 0.06), 1)
    add_box(bm, (-0.58, 1.98, 1.04), (0.08, 0.22, 0.28), 1)
    add_box(bm, (0.58, 1.98, 1.04), (0.08, 0.22, 0.28), 1)

    # Emissive LED headlights (front = -Y) & full-width Vice neon taillight bar (rear = +Y)
    add_box(bm, (-0.66, -2.16, 0.64), (0.42, 0.08, 0.12), 3)
    add_box(bm, (0.66, -2.16, 0.64), (0.42, 0.08, 0.12), 3)
    add_box(bm, (0.0, 2.14, 0.78), (1.82, 0.08, 0.11), 4)

    bm.normal_update()
    mesh = bpy.data.meshes.new("Car_Chassis_Mesh")
    bm.to_mesh(mesh)
    bm.free()
    apply_box_uv(mesh)

    obj = bpy.data.objects.new("Car_Chassis", mesh)
    for m in palette:
        obj.data.materials.append(m)
    bpy.context.scene.collection.objects.link(obj)
    obj.parent = root_obj
    return obj


def build_hinged_door(name, side_sign, root_obj, mats):
    """
    Creates an independent left or right door whose local origin (0,0,0) sits
    strictly at the front A-pillar hinge so Unity rotates it cleanly on local Y.
    """
    bm = bmesh.new()
    palette = [mats["paint"], mats["glass"], mats["carbon"]]

    door_len = 1.28
    # Geometry extends backward (+Y) from the hinge at Y=0
    add_box(bm, (0.0, door_len * 0.5, 0.0), (0.12, door_len, 0.44), 0)       # Door panel
    add_box(bm, (side_sign * 0.07, door_len * 0.78, 0.06), (0.04, 0.18, 0.05), 2)  # Door handle
    add_box(bm, (-side_sign * 0.04, door_len * 0.5, 0.34), (0.05, door_len * 0.82, 0.28), 1)  # Window glass

    bm.normal_update()
    mesh = bpy.data.meshes.new(name + "_Mesh")
    bm.to_mesh(mesh)
    bm.free()
    apply_box_uv(mesh)

    obj = bpy.data.objects.new(name, mesh)
    for m in palette:
        obj.data.materials.append(m)
    bpy.context.scene.collection.objects.link(obj)
    # Place hinge right behind front wheel arch
    obj.location = Vector((side_sign * 0.90, -0.62, 0.66))
    obj.parent = root_obj
    return obj


def build_wheel(name, location, root_obj, mats):
    """Creates a centered wheel mesh (tire + alloy rim + brake caliper) ready for Unity WheelCollider."""
    bm = bmesh.new()
    palette = [mats["tire"], mats["rim"], mats["caliper"]]

    # Tire cylinder aligned along X axis
    tire_ret = bmesh.ops.create_cone(
        bm, cap_ends=True, cap_tris=False, segments=22,
        radius1=0.36, radius2=0.36, depth=0.28
    )
    bmesh.ops.rotate(bm, verts=tire_ret["verts"], cent=(0, 0, 0), matrix=Vector((0, 1, 0)).to_track_quat("Z", "Y").to_matrix())
    # Rotate cylinder from Z-axis to X-axis cleanly
    for v in tire_ret["verts"]:
        v.co = Vector((v.co.y, v.co.x, v.co.z))
    tire_set = set(tire_ret["verts"])
    for f in bm.faces:
        if all(v in tire_set for v in f.verts):
            f.material_index = 0

    # Metallic alloy rim
    rim_ret = bmesh.ops.create_cone(
        bm, cap_ends=True, cap_tris=False, segments=16,
        radius1=0.23, radius2=0.23, depth=0.30
    )
    for v in rim_ret["verts"]:
        v.co = Vector((v.co.z, v.co.y, v.co.x))
    rim_set = set(rim_ret["verts"])
    for f in bm.faces:
        if all(v in rim_set for v in f.verts):
            f.material_index = 1

    # Brake caliper accent
    add_box(bm, (0.0, 0.14, 0.08), (0.14, 0.10, 0.16), 2)

    bm.normal_update()
    mesh = bpy.data.meshes.new(name + "_Mesh")
    bm.to_mesh(mesh)
    bm.free()
    apply_box_uv(mesh)

    obj = bpy.data.objects.new(name, mesh)
    for m in palette:
        obj.data.materials.append(m)
    bpy.context.scene.collection.objects.link(obj)
    obj.location = Vector(location)
    obj.parent = root_obj
    return obj


def export_vehicle(root_obj, export_dir, fmt="FBX"):
    os.makedirs(export_dir, exist_ok=True)
    bpy.ops.object.select_all(action="DESELECT")
    root_obj.select_set(True)
    for child in root_obj.children_recursive:
        child.select_set(True)

    if fmt.upper() == "GLB":
        filepath = os.path.join(export_dir, f"{root_obj.name}.glb")
        bpy.ops.export_scene.gltf(filepath=filepath, use_selection=True, export_format="GLB", export_yup=True)
    else:
        filepath = os.path.join(export_dir, f"{root_obj.name}.fbx")
        bpy.ops.export_scene.fbx(
            filepath=filepath,
            use_selection=True,
            axis_forward="-Z",
            axis_up="Y",
            apply_scale_options="FBX_SCALE_ALL",
        )
    print(f"[ViceCity Vehicle] Exported modular sports car -> {filepath}")


def main():
    clear_scene()
    mats = build_car_materials()

    root = bpy.data.objects.new(CONFIG["vehicle_name"], None)
    root.empty_display_type = "ARROWS"
    bpy.context.scene.collection.objects.link(root)

    build_chassis(root, mats)
    build_hinged_door("Door_L", 1.0, root, mats)
    build_hinged_door("Door_R", -1.0, root, mats)

    # Wheelbase coordinates matching Unity WheelCollider setup
    wheel_specs = [
        ("Wheel_FL", (0.94, -1.36, 0.36)),
        ("Wheel_FR", (-0.94, -1.36, 0.36)),
        ("Wheel_RL", (0.96, 1.34, 0.36)),
        ("Wheel_RR", (-0.96, 1.34, 0.36)),
    ]
    for wname, wloc in wheel_specs:
        build_wheel(wname, wloc, root, mats)

    if CONFIG["auto_export"]:
        try:
            export_vehicle(root, CONFIG["unity_export_dir"], CONFIG["export_format"])
        except Exception as exc:
            print(f"[ViceCity Vehicle] Export warning: {exc}")

    print("[ViceCity Vehicle] Modular Vice_Cheetah_GT generation complete.")


if __name__ == "__main__":
    main()
