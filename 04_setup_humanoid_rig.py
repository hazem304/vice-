# ============================================================================
# Project: Vice Coast Open-World (Vice City Style Vertical Slice)
# Module:  Module 3 — Character Controller & Combat System (Blender 4.x -> Unity)
# File:    BlenderScripts/04_setup_humanoid_rig.py
# Author:  Lead Game Architect
# Purpose: Procedurally generates a stylized Vice City protagonist base mesh
#          and constructs a complete Unity Humanoid Avatar compatible Armature
#          (T-Pose, exact Unity Mecanim bone naming & hierarchy), applies
#          automatic weights, and exports directly as FBX to Unity.
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
    "character_name": "ViceProtagonist_Humanoid",
    "character_height": 1.80,
    "unity_export_dir": os.path.abspath(
        os.path.join(os.path.expanduser("~"), "ViceCoast_OpenWorld", "Assets", "_Project", "Art", "Models", "Characters")
    ),
    "auto_export_fbx": True,
}


# ============================================================================
# HELPERS & MATERIALS
# ============================================================================

def clear_scene():
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    for block in (bpy.data.meshes, bpy.data.armatures, bpy.data.materials):
        for item in list(block):
            if item.users == 0:
                block.remove(item)


def create_material(name, color, roughness=0.6):
    if name in bpy.data.materials:
        return bpy.data.materials[name]
    mat = bpy.data.materials.new(name=name)
    mat.use_nodes = True
    bsdf = mat.node_tree.nodes.get("Principled BSDF")
    if bsdf is not None:
        bsdf.inputs["Base Color"].default_value = (*color, 1.0)
        bsdf.inputs["Roughness"].default_value = roughness
    return mat


def add_limb_segment(bm, start, end, radius_start, radius_end, mat_index, segments=10):
    """Builds a tapered cylinder segment between two 3D points inside a BMesh."""
    p0 = Vector(start)
    p1 = Vector(end)
    axis = p1 - p0
    length = axis.length
    if length < 1e-4:
        return

    z_dir = axis.normalized()
    up_ref = Vector((0.0, 1.0, 0.0)) if abs(z_dir.z) > 0.9 else Vector((0.0, 0.0, 1.0))
    x_dir = up_ref.cross(z_dir).normalized()
    y_dir = z_dir.cross(x_dir).normalized()

    ring0 = []
    ring1 = []
    for i in range(segments):
        ang = (i / segments) * math.tau
        c, s = math.cos(ang), math.sin(ang)
        offset = x_dir * c + y_dir * s
        ring0.append(bm.verts.new(p0 + offset * radius_start))
        ring1.append(bm.verts.new(p1 + offset * radius_end))

    for i in range(segments):
        nxt = (i + 1) % segments
        f = bm.faces.new((ring0[i], ring0[nxt], ring1[nxt], ring1[i]))
        f.material_index = mat_index

    cap0 = bm.faces.new(list(reversed(ring0)))
    cap0.material_index = mat_index
    cap1 = bm.faces.new(ring1)
    cap1.material_index = mat_index


def build_protagonist_base_mesh(name):
    """Creates a clean T-Pose humanoid base mesh styled with Vice City tropical jacket & chinos."""
    bm = bmesh.new()

    # Materials: 0=Skin, 1=Vice Teal Jacket, 2=White Tee/Pants, 3=Sneakers, 4=Hair
    mats = [
        create_material("MAT_Char_Skin", (0.78, 0.56, 0.44), roughness=0.55),
        create_material("MAT_Char_ViceJacket", (0.08, 0.68, 0.66), roughness=0.68),
        create_material("MAT_Char_Chinos", (0.88, 0.86, 0.80), roughness=0.75),
        create_material("MAT_Char_Sneakers", (0.14, 0.14, 0.16), roughness=0.45),
        create_material("MAT_Char_Hair", (0.09, 0.07, 0.06), roughness=0.65),
    ]

    # Pelvis & Torso
    add_limb_segment(bm, (0.0, 0.0, 0.88), (0.0, 0.0, 1.06), 0.16, 0.15, 2, segments=12)
    add_limb_segment(bm, (0.0, 0.0, 1.05), (0.0, 0.0, 1.44), 0.16, 0.21, 1, segments=12)

    # Neck & Head
    add_limb_segment(bm, (0.0, 0.0, 1.44), (0.0, 0.0, 1.54), 0.06, 0.055, 0, segments=10)
    ret = bmesh.ops.create_uvsphere(bm, u_segments=14, v_segments=10, radius=0.115)
    bmesh.ops.scale(bm, vec=Vector((0.88, 0.96, 1.12)), verts=ret["verts"])
    bmesh.ops.translate(bm, vec=Vector((0.0, 0.0, 1.66)), verts=ret["verts"])
    vset = set(ret["verts"])
    for f in bm.faces:
        if all(v in vset for v in f.verts):
            f.material_index = 4 if f.calc_center_median().z > 1.71 else 0

    # Arms (T-Pose along +/- X)
    for sign in (-1.0, 1.0):
        add_limb_segment(bm, (sign * 0.16, 0.0, 1.40), (sign * 0.44, 0.0, 1.40), 0.075, 0.06, 1)
        add_limb_segment(bm, (sign * 0.44, 0.0, 1.40), (sign * 0.70, 0.0, 1.40), 0.055, 0.045, 0)
        add_limb_segment(bm, (sign * 0.70, 0.0, 1.40), (sign * 0.82, 0.0, 1.40), 0.045, 0.035, 0)

    # Legs & Feet
    for sign in (-1.0, 1.0):
        add_limb_segment(bm, (sign * 0.10, 0.0, 0.92), (sign * 0.10, 0.0, 0.50), 0.095, 0.07, 2)
        add_limb_segment(bm, (sign * 0.10, 0.0, 0.50), (sign * 0.10, 0.0, 0.09), 0.068, 0.05, 2)
        add_limb_segment(bm, (sign * 0.10, 0.02, 0.05), (sign * 0.10, -0.18, 0.04), 0.055, 0.042, 3)

    bm.normal_update()
    mesh = bpy.data.meshes.new(name + "_Mesh")
    bm.to_mesh(mesh)
    bm.free()

    obj = bpy.data.objects.new(name + "_MeshObj", mesh)
    bpy.context.scene.collection.objects.link(obj)
    for mat in mats:
        obj.data.materials.append(mat)

    return obj


# ============================================================================
# UNITY HUMANOID AVATAR ARMATURE BUILDER
# ============================================================================

def create_unity_humanoid_armature(name):
    """
    Constructs a full Unity Mecanim Humanoid Avatar rig in T-Pose.
    Bone names match Unity's Auto-Mapping table so Avatar Configuration succeeds with zero manual fixes.
    """
    arm_data = bpy.data.armatures.new(name + "_Armature")
    arm_obj = bpy.data.objects.new(name + "_Rig", arm_data)
    bpy.context.scene.collection.objects.link(arm_obj)

    bpy.context.view_layer.objects.active = arm_obj
    bpy.ops.object.mode_set(mode="EDIT")
    edit_bones = arm_data.edit_bones

    def add_bone(bname, head, tail, parent_name=None, connect=False):
        bone = edit_bones.new(bname)
        bone.head = Vector(head)
        bone.tail = Vector(tail)
        if parent_name is not None:
            bone.parent = edit_bones[parent_name]
            bone.use_connect = connect
        return bone

    # Spine Chain (Core Mecanim Required Bones)
    add_bone("Hips",       (0.0, 0.0, 0.94), (0.0, 0.0, 1.04))
    add_bone("Spine",      (0.0, 0.0, 1.04), (0.0, 0.0, 1.19), "Hips", connect=True)
    add_bone("Chest",      (0.0, 0.0, 1.19), (0.0, 0.0, 1.34), "Spine", connect=True)
    add_bone("UpperChest", (0.0, 0.0, 1.34), (0.0, 0.0, 1.44), "Chest", connect=True)
    add_bone("Neck",       (0.0, 0.0, 1.44), (0.0, 0.0, 1.54), "UpperChest", connect=True)
    add_bone("Head",       (0.0, 0.0, 1.54), (0.0, 0.0, 1.76), "Neck", connect=True)

    # Left & Right Arms (Blender +X is Character Left when facing -Y)
    for prefix, sign in (("Left", 1.0), ("Right", -1.0)):
        add_bone(f"{prefix}Shoulder", (sign * 0.04, 0.0, 1.40), (sign * 0.16, 0.0, 1.40), "UpperChest", connect=False)
        add_bone(f"{prefix}UpperArm", (sign * 0.16, 0.0, 1.40), (sign * 0.44, 0.0, 1.40), f"{prefix}Shoulder", connect=True)
        add_bone(f"{prefix}LowerArm", (sign * 0.44, 0.0, 1.40), (sign * 0.70, 0.0, 1.40), f"{prefix}UpperArm", connect=True)
        add_bone(f"{prefix}Hand",     (sign * 0.70, 0.0, 1.40), (sign * 0.82, 0.0, 1.40), f"{prefix}LowerArm", connect=True)

    # Left & Right Legs
    for prefix, sign in (("Left", 1.0), ("Right", -1.0)):
        add_bone(f"{prefix}UpperLeg", (sign * 0.10, 0.0, 0.92), (sign * 0.10, 0.0, 0.50), "Hips", connect=False)
        add_bone(f"{prefix}LowerLeg", (sign * 0.10, 0.0, 0.50), (sign * 0.10, 0.0, 0.09), f"{prefix}UpperLeg", connect=True)
        add_bone(f"{prefix}Foot",     (sign * 0.10, 0.0, 0.09), (sign * 0.10, -0.12, 0.03), f"{prefix}LowerLeg", connect=True)
        add_bone(f"{prefix}ToeBase",  (sign * 0.10, -0.12, 0.03), (sign * 0.10, -0.20, 0.03), f"{prefix}Foot", connect=True)

    bpy.ops.object.mode_set(mode="OBJECT")
    arm_obj.show_in_front = True
    return arm_obj


def bind_mesh_to_armature(mesh_obj, arm_obj):
    """Parents the base mesh to the Humanoid Armature using Blender Automatic Weights."""
    bpy.ops.object.select_all(action="DESELECT")
    mesh_obj.select_set(True)
    arm_obj.select_set(True)
    bpy.context.view_layer.objects.active = arm_obj
    bpy.ops.object.parent_set(type="ARMATURE_AUTO")


def export_humanoid_fbx(mesh_obj, arm_obj, export_dir, char_name):
    """Exports the rigged character as a Unity Humanoid-ready FBX."""
    os.makedirs(export_dir, exist_ok=True)
    filepath = os.path.join(export_dir, f"{char_name}.fbx")

    bpy.ops.object.select_all(action="DESELECT")
    mesh_obj.select_set(True)
    arm_obj.select_set(True)
    bpy.context.view_layer.objects.active = arm_obj

    bpy.ops.export_scene.fbx(
        filepath=filepath,
        use_selection=True,
        object_types={"ARMATURE", "MESH"},
        add_leaf_bones=False,
        primary_bone_axis="Y",
        secondary_bone_axis="X",
        armature_nodetype="NULL",
        axis_forward="-Z",
        axis_up="Y",
        apply_scale_options="FBX_SCALE_ALL",
        bake_anim=False,
    )
    print(f"[ViceCity Rig] Exported Unity Humanoid FBX -> {filepath}")


def main():
    clear_scene()
    char_name = CONFIG["character_name"]
    mesh_obj = build_protagonist_base_mesh(char_name)
    arm_obj = create_unity_humanoid_armature(char_name)
    bind_mesh_to_armature(mesh_obj, arm_obj)

    if CONFIG["auto_export_fbx"]:
        try:
            export_humanoid_fbx(mesh_obj, arm_obj, CONFIG["unity_export_dir"], char_name)
        except Exception as exc:
            print(f"[ViceCity Rig] Export warning: {exc}")

    print("[ViceCity Rig] Unity Humanoid Avatar rig setup completed successfully.")


if __name__ == "__main__":
    main()
