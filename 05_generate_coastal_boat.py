# ============================================================================
# Project: Vice Coast Open-World (Vice City Style Vertical Slice)
# Module:  Expansion — Coastal Speedboat 3D Asset (Blender 4.x -> Unity)
# File:    BlenderScripts/05_generate_coastal_boat.py
# Author:  Lead Game Architect
# Purpose: Procedurally generates a Miami Vice-style offshore racing speedboat
#          (sleek V-hull, twin outboard engines, tinted wrap-around windshield,
#          cockpit seats, neon cyan deck trim) with auto UVs & FBX/GLB export.
# Compatibility: Blender 4.0+
# ============================================================================

import os
import bpy
import bmesh
from mathutils import Vector

CONFIG = {
    "boat_name": "Vice_Squalo_Speedboat",
    "export_format": "FBX",
    "unity_export_dir": os.path.abspath(
        os.path.join(os.path.expanduser("~"), "ViceCoast_OpenWorld", "Assets", "_Project", "Art", "Models", "Vehicles")
    ),
    "auto_export": True,
}


def clear_scene():
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    for block in (bpy.data.meshes, bpy.data.materials):
        for item in list(block):
            if item.users == 0:
                block.remove(item)


def make_mat(name, color, roughness=0.25, metallic=0.1, emission=None, emission_strength=0.0):
    if name in bpy.data.materials:
        return bpy.data.materials[name]
    mat = bpy.data.materials.new(name=name)
    mat.use_nodes = True
    bsdf = mat.node_tree.nodes.get("Principled BSDF")
    if bsdf:
        bsdf.inputs["Base Color"].default_value = (*color, 1.0)
        bsdf.inputs["Roughness"].default_value = roughness
        bsdf.inputs["Metallic"].default_value = metallic
        if emission and emission_strength > 0.0:
            if "Emission Color" in bsdf.inputs:
                bsdf.inputs["Emission Color"].default_value = (*emission, 1.0)
            if "Emission Strength" in bsdf.inputs:
                bsdf.inputs["Emission Strength"].default_value = emission_strength
    return mat


def add_box(bm, center, size, mat_idx):
    ret = bmesh.ops.create_cube(bm, size=1.0)
    verts = ret["verts"]
    bmesh.ops.scale(bm, vec=Vector(size), verts=verts)
    bmesh.ops.translate(bm, vec=Vector(center), verts=verts)
    vset = set(verts)
    for f in bm.faces:
        if all(v in vset for v in f.verts):
            f.material_index = mat_idx


def apply_box_uv(mesh, scale=0.35):
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


def build_speedboat():
    mats = [
        make_mat("MAT_Boat_WhiteHull", (0.95, 0.96, 0.98), roughness=0.18, metallic=0.15),
        make_mat("MAT_Boat_ViceStripe", (0.04, 0.78, 0.86), roughness=0.22, metallic=0.35),
        make_mat("MAT_Boat_SeatCoral", (0.94, 0.36, 0.42), roughness=0.55),
        make_mat("MAT_Boat_Windshield", (0.05, 0.12, 0.18), roughness=0.06, metallic=0.35),
        make_mat("MAT_Boat_EngineDark", (0.10, 0.11, 0.13), roughness=0.32, metallic=0.85),
        make_mat("MAT_Boat_NeonTrim", (0.08, 0.95, 1.0), roughness=0.1, emission=(0.08, 0.95, 1.0), emission_strength=10.0),
    ]

    bm = bmesh.new()
    # Main V-hull sections (facing -Y in Blender -> +Z in Unity)
    add_box(bm, (0.0, 0.4, 0.38), (2.25, 4.4, 0.75), 0)
    add_box(bm, (0.0, -2.3, 0.44), (1.55, 1.8, 0.65), 0)
    add_box(bm, (0.0, -3.5, 0.50), (0.78, 1.1, 0.52), 0)
    # Racing cyan hull stripe & glowing night trim
    add_box(bm, (0.0, 0.0, 0.62), (2.32, 4.8, 0.16), 1)
    add_box(bm, (0.0, 0.0, 0.74), (2.34, 4.6, 0.05), 5)
    # Tinted wrap-around windshield
    add_box(bm, (0.0, -0.55, 1.02), (1.85, 0.72, 0.42), 3)
    # Cockpit seats & sunpad
    add_box(bm, (-0.48, 0.65, 0.82), (0.62, 0.68, 0.48), 2)
    add_box(bm, (0.48, 0.65, 0.82), (0.62, 0.68, 0.48), 2)
    add_box(bm, (0.0, 1.85, 0.84), (1.92, 1.25, 0.28), 2)
    # Twin outboard racing motors at stern (+Y)
    add_box(bm, (-0.48, 2.85, 0.55), (0.36, 0.52, 0.95), 4)
    add_box(bm, (0.48, 2.85, 0.55), (0.36, 0.52, 0.95), 4)

    bm.normal_update()
    mesh = bpy.data.meshes.new(CONFIG["boat_name"] + "_Mesh")
    bm.to_mesh(mesh)
    bm.free()
    apply_box_uv(mesh)

    obj = bpy.data.objects.new(CONFIG["boat_name"], mesh)
    for m in mats:
        obj.data.materials.append(m)
    bpy.context.scene.collection.objects.link(obj)
    return obj


def main():
    clear_scene()
    boat = build_speedboat()

    if CONFIG["auto_export"]:
        try:
            os.makedirs(CONFIG["unity_export_dir"], exist_ok=True)
            bpy.ops.object.select_all(action="DESELECT")
            boat.select_set(True)
            bpy.context.view_layer.objects.active = boat
            filepath = os.path.join(CONFIG["unity_export_dir"], f"{boat.name}.fbx")
            bpy.ops.export_scene.fbx(
                filepath=filepath,
                use_selection=True,
                axis_forward="-Z",
                axis_up="Y",
                apply_scale_options="FBX_SCALE_ALL",
            )
            print(f"[ViceCity Boat] Exported speedboat -> {filepath}")
        except Exception as exc:
            print(f"[ViceCity Boat] Export warning: {exc}")


if __name__ == "__main__":
    main()
