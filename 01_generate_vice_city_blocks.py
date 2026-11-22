# ============================================================================
# Project: Vice Coast Open-World (Vice City Style Vertical Slice)
# Module:  Module 2 — Procedural City & World Streaming (Blender 4.x -> Unity)
# File:    BlenderScripts/01_generate_vice_city_blocks.py
# Author:  Lead Game Architect
# Purpose: Procedurally generates coastal Miami / Ocean Drive style city chunks:
#          - Multi-lane asphalt roads with yellow double centerlines & crosswalks
#          - Raised concrete sidewalks with beveled curbs
#          - Modular traffic lights with Red/Amber/Green emissive lenses
#          - Art Deco street lamps with warm night glow
#          - Curved tropical palm trees with bark rings and fanned fronds
#          - Automatic UV unwrapping & batch FBX/GLB export directly to Unity
# Compatibility: Blender 4.0+ (Tested with Principled BSDF 4.x socket names)
# ============================================================================

import math
import os
import random
import bpy
import bmesh
from mathutils import Euler, Matrix, Vector


# ============================================================================
# CONFIGURATION (Matches Unity ChunkStreamingManager Grid Dimensions)
# ============================================================================

CONFIG = {
    "seed": 305,                     # Miami area code seed for deterministic city
    "grid_x": 3,                     # Number of chunks along X (3x3 = 9 chunks)
    "grid_y": 3,                     # Number of chunks along Y
    "chunk_size": 80.0,              # Meters per city block chunk
    "road_width": 16.0,              # Total road width (4 lanes: 2 each direction)
    "sidewalk_width": 4.0,           # Sidewalk width on each side of the block
    "sidewalk_height": 0.22,         # Curb height in meters
    "palm_spacing": 14.0,            # Distance between boulevard palm trees
    "lamp_spacing": 20.0,            # Distance between Art Deco street lamps
    "export_format": "FBX",          # "FBX" or "GLB"
    "unity_export_dir": os.path.abspath(
        os.path.join(os.path.expanduser("~"), "ViceCoast_OpenWorld", "Assets", "_Project", "Art", "Models", "CityChunks")
    ),
    "auto_export": True,
}


# ============================================================================
# MATERIAL LIBRARY (Blender 4.x Principled BSDF Compatible)
# ============================================================================

def get_or_create_material(name, base_color, roughness=0.5, metallic=0.0, emission_color=None, emission_strength=0.0):
    """Creates or retrieves a PBR material compatible with Blender 4.x and Unity URP."""
    if name in bpy.data.materials:
        return bpy.data.materials[name]

    mat = bpy.data.materials.new(name=name)
    mat.use_nodes = True
    nodes = mat.node_tree.nodes
    bsdf = nodes.get("Principled BSDF")

    if bsdf is not None:
        bsdf.inputs["Base Color"].default_value = (*base_color, 1.0)
        bsdf.inputs["Roughness"].default_value = roughness
        bsdf.inputs["Metallic"].default_value = metallic

        if emission_color is not None and emission_strength > 0.0:
            # Blender 4.x uses "Emission Color" and "Emission Strength"
            if "Emission Color" in bsdf.inputs:
                bsdf.inputs["Emission Color"].default_value = (*emission_color, 1.0)
            elif "Emission" in bsdf.inputs:
                bsdf.inputs["Emission"].default_value = (*emission_color, 1.0)

            if "Emission Strength" in bsdf.inputs:
                bsdf.inputs["Emission Strength"].default_value = emission_strength

    return mat


def build_material_palette():
    """Builds the shared Vice Coast urban material dictionary."""
    return {
        "asphalt": get_or_create_material("MAT_Vice_Asphalt", (0.07, 0.075, 0.085), roughness=0.55),
        "lane_yellow": get_or_create_material("MAT_Vice_LaneYellow", (0.95, 0.72, 0.08), roughness=0.35),
        "lane_white": get_or_create_material("MAT_Vice_LaneWhite", (0.92, 0.92, 0.94), roughness=0.35),
        "sidewalk": get_or_create_material("MAT_Vice_Sidewalk", (0.76, 0.74, 0.70), roughness=0.78),
        "grass_strip": get_or_create_material("MAT_Vice_Grass", (0.14, 0.36, 0.16), roughness=0.88),
        "metal_dark": get_or_create_material("MAT_Vice_MetalDark", (0.12, 0.14, 0.16), roughness=0.35, metallic=0.85),
        "lamp_glow": get_or_create_material(
            "MAT_Vice_LampGlow", (1.0, 0.92, 0.75), roughness=0.1,
            emission_color=(1.0, 0.85, 0.60), emission_strength=8.0
        ),
        "signal_red": get_or_create_material(
            "MAT_Vice_SignalRed", (0.8, 0.05, 0.05), roughness=0.2,
            emission_color=(1.0, 0.08, 0.05), emission_strength=6.0
        ),
        "signal_amber": get_or_create_material(
            "MAT_Vice_SignalAmber", (0.9, 0.55, 0.02), roughness=0.2,
            emission_color=(1.0, 0.62, 0.05), emission_strength=5.0
        ),
        "signal_green": get_or_create_material(
            "MAT_Vice_SignalGreen", (0.05, 0.85, 0.25), roughness=0.2,
            emission_color=(0.08, 1.0, 0.35), emission_strength=6.0
        ),
        "palm_bark": get_or_create_material("MAT_Vice_PalmBark", (0.34, 0.25, 0.18), roughness=0.92),
        "palm_frond": get_or_create_material("MAT_Vice_PalmFrond", (0.12, 0.42, 0.14), roughness=0.58),
    }


# ============================================================================
# SCENE & COLLECTION HELPERS
# ============================================================================

def clear_scene():
    """Clears all objects and orphan data blocks for a clean procedural build."""
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)

    for block in (bpy.data.meshes, bpy.data.materials, bpy.data.collections):
        for item in list(block):
            if item.users == 0:
                block.remove(item)


def get_or_create_collection(name, parent=None):
    """Creates a clean collection hierarchy per city chunk."""
    if name in bpy.data.collections:
        return bpy.data.collections[name]

    col = bpy.data.collections.new(name)
    if parent is None:
        bpy.context.scene.collection.children.link(col)
    else:
        parent.children.link(col)
    return col


def link_object_to_collection(obj, collection):
    """Ensures an object belongs exclusively to the target chunk collection."""
    for existing_col in list(obj.users_collection):
        existing_col.objects.unlink(obj)
    collection.objects.link(obj)


def apply_box_uv(mesh, scale=0.25):
    """Applies fast, distortion-free tri-planar box UVs directly via BMesh."""
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


def create_box_object(name, size, location, material, collection, uv_scale=0.25):
    """Creates a UV-mapped box mesh at the given world position."""
    mesh = bpy.data.meshes.new(name + "_Mesh")
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0)
    bmesh.ops.scale(bm, vec=Vector(size), verts=bm.verts)
    bm.to_mesh(mesh)
    bm.free()

    apply_box_uv(mesh, scale=uv_scale)

    obj = bpy.data.objects.new(name, mesh)
    obj.location = Vector(location)
    if material is not None:
        obj.data.materials.append(material)

    link_object_to_collection(obj, collection)
    return obj


# ============================================================================
# PROCEDURAL STREET PROPS (TRAFFIC LIGHTS, STREET LAMPS, PALM TREES)
# ============================================================================

def create_traffic_light(name, location, rot_z_deg, mats, collection):
    """Creates a Miami-style cantilevered traffic light pole with 3 emissive lenses."""
    root = bpy.data.objects.new(name, None)
    root.empty_display_type = "PLAIN_AXES"
    root.location = Vector(location)
    root.rotation_euler = Euler((0.0, 0.0, math.radians(rot_z_deg)), "XYZ")
    link_object_to_collection(root, collection)

    # Vertical pole
    pole = create_box_object(f"{name}_Pole", (0.24, 0.24, 6.2), (0.0, 0.0, 3.1), mats["metal_dark"], collection)
    pole.parent = root

    # Horizontal cantilever arm overhanging the road
    arm = create_box_object(f"{name}_Arm", (5.4, 0.20, 0.20), (2.7, 0.0, 5.85), mats["metal_dark"], collection)
    arm.parent = root

    # Signal housing box
    housing = create_box_object(f"{name}_Housing", (0.38, 0.42, 1.15), (4.5, 0.0, 5.25), mats["metal_dark"], collection)
    housing.parent = root

    # Red / Amber / Green emissive lenses
    lens_specs = [
        ("Red", 5.58, mats["signal_red"]),
        ("Amber", 5.25, mats["signal_amber"]),
        ("Green", 4.92, mats["signal_green"]),
    ]
    for label, z_pos, mat in lens_specs:
        lens = create_box_object(f"{name}_{label}", (0.24, 0.08, 0.24), (4.5, -0.22, z_pos), mat, collection)
        lens.parent = root

    return root


def create_street_lamp(name, location, rot_z_deg, mats, collection):
    """Creates an Art Deco coastal boulevard street lamp."""
    root = bpy.data.objects.new(name, None)
    root.empty_display_type = "PLAIN_AXES"
    root.location = Vector(location)
    root.rotation_euler = Euler((0.0, 0.0, math.radians(rot_z_deg)), "XYZ")
    link_object_to_collection(root, collection)

    base = create_box_object(f"{name}_Base", (0.45, 0.45, 0.7), (0.0, 0.0, 0.35), mats["metal_dark"], collection)
    base.parent = root

    pole = create_box_object(f"{name}_Pole", (0.16, 0.16, 5.5), (0.0, 0.0, 3.1), mats["metal_dark"], collection)
    pole.parent = root

    arm = create_box_object(f"{name}_Arm", (1.6, 0.12, 0.12), (0.8, 0.0, 5.75), mats["metal_dark"], collection)
    arm.parent = root

    bulb = create_box_object(f"{name}_Bulb", (0.55, 0.28, 0.16), (1.45, 0.0, 5.62), mats["lamp_glow"], collection)
    bulb.parent = root

    return root


def create_palm_tree(name, location, rng, mats, collection):
    """Procedurally builds a curved tropical palm tree with tapered trunk segments and arched fronds."""
    mesh = bpy.data.meshes.new(name + "_Mesh")
    bm = bmesh.new()

    trunk_segments = 11
    total_height = rng.uniform(7.5, 11.0)
    lean_angle = rng.uniform(0.0, math.tau)
    lean_amount = rng.uniform(0.9, 2.1)
    base_radius = rng.uniform(0.28, 0.36)
    top_radius = 0.14

    ring_verts = []
    sides = 7
    top_center = Vector((0.0, 0.0, 0.0))

    # Build curved segmented trunk
    for i in range(trunk_segments + 1):
        t = i / trunk_segments
        z = t * total_height
        curve = (t ** 1.7) * lean_amount
        cx = math.cos(lean_angle) * curve
        cy = math.sin(lean_angle) * curve
        radius = base_radius * (1.0 - t) + top_radius * t
        # Subtle bark ring bulge
        if i % 2 == 1 and 0 < i < trunk_segments:
            radius *= 1.07

        ring = []
        for s in range(sides):
            ang = (s / sides) * math.tau
            v = bm.verts.new((cx + math.cos(ang) * radius, cy + math.sin(ang) * radius, z))
            ring.append(v)
        ring_verts.append(ring)
        top_center = Vector((cx, cy, z))

    for i in range(trunk_segments):
        r0 = ring_verts[i]
        r1 = ring_verts[i + 1]
        for s in range(sides):
            sn = (s + 1) % sides
            face = bm.faces.new((r0[s], r0[sn], r1[sn], r1[s]))
            face.material_index = 0

    # Build arched palm fronds radiating from crown
    frond_count = rng.randint(9, 12)
    for f_idx in range(frond_count):
        azimuth = (f_idx / frond_count) * math.tau + rng.uniform(-0.18, 0.18)
        frond_len = rng.uniform(2.6, 3.8)
        frond_width = rng.uniform(0.42, 0.58)
        droop = rng.uniform(0.9, 1.55)
        lift = rng.uniform(0.45, 0.95)

        dir_xy = Vector((math.cos(azimuth), math.sin(azimuth), 0.0))
        side_xy = Vector((-math.sin(azimuth), math.cos(azimuth), 0.0))

        steps = 5
        prev_l = prev_r = None
        for st in range(steps + 1):
            u = st / steps
            width_profile = math.sin(u * math.pi) * frond_width
            if st == 0:
                width_profile = 0.08
            elif st == steps:
                width_profile = 0.02

            z_offset = (lift * math.sin(u * math.pi * 0.75)) - (droop * (u ** 2.0))
            spine = top_center + dir_xy * (u * frond_len) + Vector((0.0, 0.0, z_offset))

            vl = bm.verts.new(spine - side_xy * width_profile)
            vr = bm.verts.new(spine + side_xy * width_profile)

            if prev_l is not None:
                face = bm.faces.new((prev_l, prev_r, vr, vl))
                face.material_index = 1

            prev_l, prev_r = vl, vr

    bm.normal_update()
    bm.to_mesh(mesh)
    bm.free()

    apply_box_uv(mesh, scale=0.4)

    obj = bpy.data.objects.new(name, mesh)
    obj.location = Vector(location)
    obj.data.materials.append(mats["palm_bark"])
    obj.data.materials.append(mats["palm_frond"])

    link_object_to_collection(obj, collection)
    return obj


# ============================================================================
# CHUNK GENERATOR
# ============================================================================

def generate_city_chunk(gx, gy, mats, rng):
    """Generates one complete 80m x 80m Vice Coast street block chunk."""
    chunk_name = f"ViceChunk_{gx}_{gy}"
    col = get_or_create_collection(chunk_name)

    size = CONFIG["chunk_size"]
    road_w = CONFIG["road_width"]
    sw_w = CONFIG["sidewalk_width"]
    sw_h = CONFIG["sidewalk_height"]

    origin_x = gx * size
    origin_y = gy * size

    # 1. Full asphalt road deck base (sits cleanly at Z = 0)
    create_box_object(
        f"{chunk_name}_RoadDeck",
        (size, size, 0.20),
        (origin_x, origin_y, -0.10),
        mats["asphalt"],
        col,
        uv_scale=0.15,
    )

    # 2. Yellow double centerlines along chunk road edges (X and Y axes)
    half = size * 0.5
    inner_len = size - road_w
    for offset in (-0.18, 0.18):
        # South road border (runs along X)
        create_box_object(
            f"{chunk_name}_CenterLineX_{offset:+.2f}",
            (inner_len, 0.14, 0.02),
            (origin_x, origin_y - half + offset, 0.015),
            mats["lane_yellow"],
            col,
        )
        # West road border (runs along Y)
        create_box_object(
            f"{chunk_name}_CenterLineY_{offset:+.2f}",
            (0.14, inner_len, 0.02),
            (origin_x - half + offset, origin_y, 0.015),
            mats["lane_yellow"],
            col,
        )

    # 3. Crosswalk zebra stripes near the intersection corner
    stripe_count = 6
    for i in range(stripe_count):
        stripe_offset = -road_w * 0.35 + (i / (stripe_count - 1)) * (road_w * 0.70)
        create_box_object(
            f"{chunk_name}_CrosswalkX_{i}",
            (2.8, 0.55, 0.02),
            (origin_x - half + road_w * 0.65, origin_y - half + stripe_offset, 0.015),
            mats["lane_white"],
            col,
        )
        create_box_object(
            f"{chunk_name}_CrosswalkY_{i}",
            (0.55, 2.8, 0.02),
            (origin_x - half + stripe_offset, origin_y - half + road_w * 0.65, 0.015),
            mats["lane_white"],
            col,
        )

    # 4. Raised sidewalk island in the center of the chunk
    island_size = size - road_w
    create_box_object(
        f"{chunk_name}_SidewalkIsland",
        (island_size, island_size, sw_h),
        (origin_x, origin_y, sw_h * 0.5),
        mats["sidewalk"],
        col,
        uv_scale=0.25,
    )

    # 5. Inner buildable parcel pad (slight grass/plaza border where Module 2 buildings sit)
    parcel_size = island_size - (sw_w * 2.0)
    create_box_object(
        f"{chunk_name}_ParcelBase",
        (parcel_size, parcel_size, 0.06),
        (origin_x, origin_y, sw_h + 0.03),
        mats["grass_strip"],
        col,
        uv_scale=0.3,
    )

    # 6. Traffic lights at the 4 corners of the sidewalk island
    corner_inset = (island_size * 0.5) - 1.2
    corners = [
        (-corner_inset, -corner_inset, 180.0),
        (corner_inset, -corner_inset, 270.0),
        (corner_inset, corner_inset, 0.0),
        (-corner_inset, corner_inset, 90.0),
    ]
    for idx, (cx, cy, rot_deg) in enumerate(corners):
        create_traffic_light(
            f"{chunk_name}_TrafficLight_{idx}",
            (origin_x + cx, origin_y + cy, sw_h),
            rot_deg,
            mats,
            col,
        )

    # 7. Boulevard palm trees & Art Deco street lamps lining the sidewalks
    perimeter_edge = (island_size * 0.5) - 1.6
    span = island_size - 10.0

    palm_steps = max(2, int(span / CONFIG["palm_spacing"]))
    for i in range(palm_steps + 1):
        t = (i / palm_steps) - 0.5
        pos = t * span
        # South & North edges
        create_palm_tree(f"{chunk_name}_PalmS_{i}", (origin_x + pos, origin_y - perimeter_edge, sw_h), rng, mats, col)
        create_palm_tree(f"{chunk_name}_PalmN_{i}", (origin_x + pos, origin_y + perimeter_edge, sw_h), rng, mats, col)
        # West & East edges (skip corners to avoid overlap)
        if 0 < i < palm_steps:
            create_palm_tree(f"{chunk_name}_PalmW_{i}", (origin_x - perimeter_edge, origin_y + pos, sw_h), rng, mats, col)
            create_palm_tree(f"{chunk_name}_PalmE_{i}", (origin_x + perimeter_edge, origin_y + pos, sw_h), rng, mats, col)

    lamp_steps = max(2, int(span / CONFIG["lamp_spacing"]))
    for i in range(lamp_steps + 1):
        t = (i / lamp_steps) - 0.5
        pos = t * (span - 4.0)
        create_street_lamp(f"{chunk_name}_LampS_{i}", (origin_x + pos, origin_y - perimeter_edge - 0.4, sw_h), 270.0, mats, col)
        create_street_lamp(f"{chunk_name}_LampN_{i}", (origin_x + pos, origin_y + perimeter_edge + 0.4, sw_h), 90.0, mats, col)

    return col


# ============================================================================
# UNITY EXPORT PIPELINE (FBX / GLB PER CHUNK)
# ============================================================================

def export_collection_to_unity(collection, export_dir, fmt="FBX"):
    """Exports a single city chunk collection as a Unity-ready FBX or GLB file."""
    os.makedirs(export_dir, exist_ok=True)
    bpy.ops.object.select_all(action="DESELECT")

    for obj in collection.all_objects:
        obj.select_set(True)

    if fmt.upper() == "GLB":
        filepath = os.path.join(export_dir, f"{collection.name}.glb")
        bpy.ops.export_scene.gltf(
            filepath=filepath,
            use_selection=True,
            export_format="GLB",
            export_yup=True,
            export_apply=True,
        )
    else:
        filepath = os.path.join(export_dir, f"{collection.name}.fbx")
        bpy.ops.export_scene.fbx(
            filepath=filepath,
            use_selection=True,
            global_scale=1.0,
            apply_unit_scale=True,
            apply_scale_options="FBX_SCALE_ALL",
            axis_forward="-Z",
            axis_up="Y",
            bake_space_transform=True,
            mesh_smooth_type="FACE",
        )

    print(f"[ViceCity Blender] Exported {collection.name} -> {filepath}")


def main():
    rng = random.Random(CONFIG["seed"])
    clear_scene()
    mats = build_material_palette()

    generated_collections = []
    for gx in range(CONFIG["grid_x"]):
        for gy in range(CONFIG["grid_y"]):
            col = generate_city_chunk(gx, gy, mats, rng)
            generated_collections.append(col)

    if CONFIG["auto_export"]:
        try:
            for col in generated_collections:
                export_collection_to_unity(col, CONFIG["unity_export_dir"], CONFIG["export_format"])
        except Exception as exc:
            print(f"[ViceCity Blender] Export warning (verify Unity folder path): {exc}")

    print(f"[ViceCity Blender] Completed generating {len(generated_collections)} coastal city chunks.")


if __name__ == "__main__":
    main()
