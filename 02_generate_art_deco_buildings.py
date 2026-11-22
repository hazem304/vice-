# ============================================================================
# Project: Vice Coast Open-World (Vice City Style Vertical Slice)
# Module:  Module 2 — Procedural City & World Streaming (Blender 4.x -> Unity)
# File:    BlenderScripts/02_generate_art_deco_buildings.py
# Author:  Lead Game Architect
# Purpose: Procedurally generates modular Miami / Vice City architecture:
#          1. Pastel Art Deco Neon Hotels (stepped ziggurat roofs, eyebrow
#             window ledges, glowing cyan/magenta neon trims, entrance canopy)
#          2. Coastal Glass Skyscrapers (recessed floor slabs, curtain-wall
#             glass, rooftop helipad & beacon lights)
#          3. Beachfront Tropical Villas (balconies, pools, terracotta roofs)
#          Includes automatic box UV unwrapping & batch FBX/GLB export to Unity.
# Compatibility: Blender 4.0+
# ============================================================================

import math
import os
import random
import bpy
import bmesh
from mathutils import Vector


# ============================================================================
# CONFIGURATION
# ============================================================================

CONFIG = {
    "seed": 1986,
    "hotel_count": 4,
    "skyscraper_count": 3,
    "villa_count": 4,
    "spacing_x": 38.0,
    "export_format": "FBX",  # "FBX" or "GLB"
    "unity_export_dir": os.path.abspath(
        os.path.join(os.path.expanduser("~"), "ViceCoast_OpenWorld", "Assets", "_Project", "Art", "Models", "Buildings")
    ),
    "auto_export": True,
}


# ============================================================================
# ART DECO & NEON MATERIAL PALETTE (Blender 4.x)
# ============================================================================

def create_pbr_mat(name, color, roughness=0.5, metallic=0.0, emission=None, emission_strength=0.0):
    if name in bpy.data.materials:
        return bpy.data.materials[name]

    mat = bpy.data.materials.new(name=name)
    mat.use_nodes = True
    bsdf = mat.node_tree.nodes.get("Principled BSDF")
    if bsdf is not None:
        bsdf.inputs["Base Color"].default_value = (*color, 1.0)
        bsdf.inputs["Roughness"].default_value = roughness
        bsdf.inputs["Metallic"].default_value = metallic
        if emission is not None and emission_strength > 0.0:
            if "Emission Color" in bsdf.inputs:
                bsdf.inputs["Emission Color"].default_value = (*emission, 1.0)
            if "Emission Strength" in bsdf.inputs:
                bsdf.inputs["Emission Strength"].default_value = emission_strength
    return mat


def build_architecture_materials():
    return {
        "pastel_coral": create_pbr_mat("MAT_ArtDeco_Coral", (0.95, 0.56, 0.54), roughness=0.72),
        "pastel_mint": create_pbr_mat("MAT_ArtDeco_Mint", (0.56, 0.88, 0.78), roughness=0.72),
        "pastel_cream": create_pbr_mat("MAT_ArtDeco_Cream", (0.96, 0.92, 0.82), roughness=0.75),
        "pastel_lavender": create_pbr_mat("MAT_ArtDeco_Lavender", (0.76, 0.68, 0.92), roughness=0.72),
        "trim_white": create_pbr_mat("MAT_ArtDeco_TrimWhite", (0.95, 0.96, 0.98), roughness=0.45),
        "concrete_dark": create_pbr_mat("MAT_Urban_ConcreteDark", (0.22, 0.24, 0.27), roughness=0.80),
        "glass_blue": create_pbr_mat("MAT_Skyscraper_Glass", (0.08, 0.22, 0.34), roughness=0.08, metallic=0.35),
        "window_lit": create_pbr_mat(
            "MAT_Window_WarmLit", (1.0, 0.88, 0.65), roughness=0.15,
            emission=(1.0, 0.82, 0.52), emission_strength=3.5
        ),
        "neon_pink": create_pbr_mat(
            "MAT_Neon_HotPink", (1.0, 0.12, 0.58), roughness=0.1,
            emission=(1.0, 0.08, 0.55), emission_strength=14.0
        ),
        "neon_cyan": create_pbr_mat(
            "MAT_Neon_Cyan", (0.08, 0.92, 1.0), roughness=0.1,
            emission=(0.05, 0.90, 1.0), emission_strength=14.0
        ),
        "neon_gold": create_pbr_mat(
            "MAT_Neon_SunsetGold", (1.0, 0.65, 0.12), roughness=0.1,
            emission=(1.0, 0.58, 0.08), emission_strength=12.0
        ),
        "terracotta": create_pbr_mat("MAT_Villa_Terracotta", (0.72, 0.28, 0.16), roughness=0.78),
        "pool_water": create_pbr_mat("MAT_Villa_PoolWater", (0.06, 0.68, 0.84), roughness=0.05, metallic=0.1),
    }


# ============================================================================
# GEOMETRY & UV HELPERS
# ============================================================================

def clear_scene():
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    for block in (bpy.data.meshes, bpy.data.materials, bpy.data.collections):
        for item in list(block):
            if item.users == 0:
                block.remove(item)


def get_or_create_collection(name):
    if name in bpy.data.collections:
        return bpy.data.collections[name]
    col = bpy.data.collections.new(name)
    bpy.context.scene.collection.children.link(col)
    return col


def add_box_to_bmesh(bm, center, size, mat_index):
    """Appends an axis-aligned box with assigned material index into a BMesh."""
    ret = bmesh.ops.create_cube(bm, size=1.0)
    verts = ret["verts"]
    bmesh.ops.scale(bm, vec=Vector(size), verts=verts)
    bmesh.ops.translate(bm, vec=Vector(center), verts=verts)

    vert_set = set(verts)
    for face in bm.faces:
        if all(v in vert_set for v in face.verts):
            face.material_index = mat_index


def apply_triplanar_uv(mesh, uv_scale=0.2):
    """Automatically unwraps every face using world-scale tri-planar box projection."""
    bm = bmesh.new()
    bm.from_mesh(mesh)
    uv_layer = bm.loops.layers.uv.verify()

    for face in bm.faces:
        n = face.normal
        ax, ay, az = abs(n.x), abs(n.y), abs(n.z)
        for loop in face.loops:
            co = loop.vert.co
            if az >= ax and az >= ay:
                u, v = co.x * uv_scale, co.y * uv_scale
            elif ax >= ay:
                u, v = co.y * uv_scale, co.z * uv_scale
            else:
                u, v = co.x * uv_scale, co.z * uv_scale
            loop[uv_layer].uv = (u, v)

    bm.to_mesh(mesh)
    bm.free()


def finalize_building_mesh(name, bm, materials, location, collection):
    """Converts a BMesh into a UV-unwrapped Unity-ready Blender Object."""
    bm.normal_update()
    mesh = bpy.data.meshes.new(name + "_Mesh")
    bm.to_mesh(mesh)
    bm.free()

    apply_triplanar_uv(mesh, uv_scale=0.22)

    obj = bpy.data.objects.new(name, mesh)
    obj.location = Vector(location)
    for mat in materials:
        obj.data.materials.append(mat)

    for existing_col in list(obj.users_collection):
        existing_col.objects.unlink(obj)
    collection.objects.link(obj)
    return obj


# ============================================================================
# 1. ART DECO NEON HOTEL GENERATOR (OCEAN DRIVE STYLE)
# ============================================================================

def generate_art_deco_hotel(name, location, mats, rng, collection):
    """Generates a multi-story Ocean Drive Art Deco hotel with pastel walls & glowing neon trims."""
    bm = bmesh.new()

    wall_mat = rng.choice([mats["pastel_coral"], mats["pastel_mint"], mats["pastel_cream"], mats["pastel_lavender"]])
    neon_primary = rng.choice([mats["neon_pink"], mats["neon_cyan"]])
    neon_secondary = mats["neon_cyan"] if neon_primary == mats["neon_pink"] else mats["neon_pink"]

    palette = [
        wall_mat,             # 0: Main pastel stucco
        mats["trim_white"],   # 1: Eyebrow ledges & cornices
        mats["glass_blue"],   # 2: Unlit windows
        mats["window_lit"],   # 3: Warm lit hotel windows
        neon_primary,         # 4: Primary neon tubes
        neon_secondary,       # 5: Secondary neon accents
        mats["concrete_dark"],# 6: Ground plinth & roof cap
    ]

    floors = rng.randint(4, 7)
    floor_h = 3.6
    width = rng.uniform(18.0, 24.0)
    depth = rng.uniform(14.0, 18.0)
    total_h = floors * floor_h

    # Main hotel massing & ground plinth
    add_box_to_bmesh(bm, (0.0, 0.0, 0.4), (width + 0.6, depth + 0.6, 0.8), 6)
    add_box_to_bmesh(bm, (0.0, 0.0, total_h * 0.5), (width, depth, total_h), 0)

    # Art Deco stepped ziggurat central tower on front facade
    tower_w = width * 0.34
    tower_h = total_h + 4.8
    add_box_to_bmesh(bm, (0.0, -depth * 0.5 - 0.35, tower_h * 0.5), (tower_w, 1.2, tower_h), 0)
    add_box_to_bmesh(bm, (0.0, -depth * 0.5 - 0.35, tower_h + 0.9), (tower_w * 0.65, 1.0, 1.8), 1)
    # Vertical glowing neon spire strip
    add_box_to_bmesh(bm, (0.0, -depth * 0.5 - 0.98, tower_h * 0.56), (0.35, 0.14, tower_h * 0.72), 4)

    # Signature Art Deco entrance canopy with neon rim
    add_box_to_bmesh(bm, (0.0, -depth * 0.5 - 2.2, 3.6), (tower_w + 3.2, 4.2, 0.45), 1)
    add_box_to_bmesh(bm, (0.0, -depth * 0.5 - 4.35, 3.6), (tower_w + 3.3, 0.14, 0.22), 5)

    # Horizontal Art Deco "eyebrow" sunshade ledges & recessed windows per floor
    window_cols = max(4, int(width / 3.4))
    for fl in range(1, floors):
        z_floor = fl * floor_h + 1.8
        # Eyebrow ledge wrapping the front
        add_box_to_bmesh(bm, (0.0, -depth * 0.5 - 0.45, z_floor + 0.95), (width + 0.9, 0.9, 0.22), 1)

        # Windows along front & back
        for col_idx in range(window_cols):
            wx = -width * 0.42 + (col_idx / max(1, window_cols - 1)) * (width * 0.84)
            if abs(wx) < tower_w * 0.52:
                continue  # Leave center clear for the Art Deco spire
            w_mat = 3 if rng.random() < 0.45 else 2
            add_box_to_bmesh(bm, (wx, -depth * 0.5 - 0.05, z_floor), (1.75, 0.18, 1.85), w_mat)
            add_box_to_bmesh(bm, (wx, depth * 0.5 + 0.05, z_floor), (1.75, 0.18, 1.85), w_mat)

    # Rooftop neon perimeter band
    add_box_to_bmesh(bm, (0.0, -depth * 0.5 - 0.08, total_h - 0.35), (width + 0.3, 0.16, 0.24), 4)
    add_box_to_bmesh(bm, (0.0, 0.0, total_h + 0.35), (width + 0.4, depth + 0.4, 0.7), 1)

    return finalize_building_mesh(name, bm, palette, location, collection)


# ============================================================================
# 2. COASTAL GLASS SKYSCRAPER GENERATOR (DOWNTOWN MIAMI STYLE)
# ============================================================================

def generate_skyscraper(name, location, mats, rng, collection):
    """Generates a multi-tiered modern coastal skyscraper with curtain-wall glass and rooftop helipad."""
    bm = bmesh.new()

    palette = [
        mats["concrete_dark"], # 0: Structural frame & floor slabs
        mats["glass_blue"],    # 1: Reflective glass facade
        mats["window_lit"],    # 2: Lit office strips
        mats["trim_white"],    # 3: Exterior ribs & helipad markings
        mats["neon_cyan"],     # 4: Crown architectural lighting
        mats["neon_gold"],     # 5: Aviation beacon / penthouse accent
    ]

    base_w = rng.uniform(22.0, 28.0)
    base_d = rng.uniform(22.0, 28.0)
    floors = rng.randint(14, 22)
    floor_h = 3.8
    total_h = floors * floor_h

    # Podium lobby (first 2 floors)
    podium_h = floor_h * 2.0
    add_box_to_bmesh(bm, (0.0, 0.0, podium_h * 0.5), (base_w + 3.0, base_d + 3.0, podium_h), 0)
    add_box_to_bmesh(bm, (0.0, 0.0, podium_h * 0.5), (base_w + 3.2, base_d + 3.2, podium_h - 1.0), 1)

    # Main glass tower shaft
    add_box_to_bmesh(bm, (0.0, 0.0, total_h * 0.5), (base_w - 0.4, base_d - 0.4, total_h), 1)

    # Horizontal spandrel floor slabs & random lit office bands
    for fl in range(2, floors):
        z = fl * floor_h
        add_box_to_bmesh(bm, (0.0, 0.0, z), (base_w + 0.25, base_d + 0.25, 0.48), 0)
        if rng.random() < 0.35:
            add_box_to_bmesh(bm, (0.0, 0.0, z + floor_h * 0.5), (base_w - 0.2, base_d - 0.2, 1.4), 2)

    # Vertical structural mullions
    for sx in (-0.45, -0.15, 0.15, 0.45):
        add_box_to_bmesh(bm, (sx * base_w, -base_d * 0.5, total_h * 0.5), (0.55, 0.65, total_h), 3)
        add_box_to_bmesh(bm, (sx * base_w, base_d * 0.5, total_h * 0.5), (0.55, 0.65, total_h), 3)

    # Setback crown penthouse + glowing architectural neon rim
    crown_h = 7.5
    crown_w = base_w * 0.72
    crown_d = base_d * 0.72
    add_box_to_bmesh(bm, (0.0, 0.0, total_h + crown_h * 0.5), (crown_w, crown_d, crown_h), 0)
    add_box_to_bmesh(bm, (0.0, 0.0, total_h + crown_h - 0.5), (crown_w + 0.35, crown_d + 0.35, 0.45), 4)

    # Rooftop Helipad ("H" marking + perimeter)
    roof_z = total_h + crown_h
    add_box_to_bmesh(bm, (0.0, 0.0, roof_z + 0.15), (10.5, 10.5, 0.3), 0)
    add_box_to_bmesh(bm, (-2.2, 0.0, roof_z + 0.32), (0.9, 5.2, 0.06), 3)
    add_box_to_bmesh(bm, (2.2, 0.0, roof_z + 0.32), (0.9, 5.2, 0.06), 3)
    add_box_to_bmesh(bm, (0.0, 0.0, roof_z + 0.32), (4.4, 0.9, 0.06), 3)
    add_box_to_bmesh(bm, (0.0, 0.0, roof_z + 4.0), (0.28, 0.28, 8.0), 5)

    return finalize_building_mesh(name, bm, palette, location, collection)


# ============================================================================
# 3. COASTAL TROPICAL VILLA GENERATOR (STAR ISLAND / BEACHFRONT STYLE)
# ============================================================================

def generate_coastal_villa(name, location, mats, rng, collection):
    """Generates a two-story Miami coastal villa with balcony, terracotta roof, and turquoise pool."""
    bm = bmesh.new()

    wall_mat = rng.choice([mats["pastel_cream"], mats["pastel_coral"], mats["pastel_mint"]])
    palette = [
        wall_mat,             # 0: Stucco walls
        mats["trim_white"],   # 1: Pillars & balcony railing
        mats["glass_blue"],   # 2: Patio glass doors
        mats["terracotta"],   # 3: Mediterranean coastal roof
        mats["pool_water"],   # 4: Swimming pool surface
        mats["neon_gold"],    # 5: Warm exterior sconce glow
    ]

    # Ground floor volume
    add_box_to_bmesh(bm, (0.0, 0.0, 1.9), (15.0, 11.0, 3.8), 0)
    # Second floor volume (offset for open sun terrace)
    add_box_to_bmesh(bm, (-1.8, 0.8, 5.5), (11.2, 9.2, 3.4), 0)
    # Sloped/stepped terracotta roof cap
    add_box_to_bmesh(bm, (-1.8, 0.8, 7.6), (12.2, 10.2, 0.85), 3)
    add_box_to_bmesh(bm, (-1.8, 0.8, 8.25), (9.4, 7.6, 0.6), 3)

    # Glass sliding doors & balcony rails
    add_box_to_bmesh(bm, (0.0, -5.55, 1.6), (8.5, 0.2, 2.8), 2)
    add_box_to_bmesh(bm, (5.5, -2.5, 4.3), (3.8, 5.5, 1.0), 1)
    # Exterior warm wall sconces
    add_box_to_bmesh(bm, (-5.2, -5.6, 2.4), (0.35, 0.25, 0.65), 5)
    add_box_to_bmesh(bm, (5.2, -5.6, 2.4), (0.35, 0.25, 0.65), 5)

    # Backyard deck & turquoise swimming pool
    add_box_to_bmesh(bm, (0.0, -9.2, 0.15), (14.5, 6.2, 0.30), 1)
    add_box_to_bmesh(bm, (0.0, -9.2, 0.28), (9.5, 4.2, 0.12), 4)

    return finalize_building_mesh(name, bm, palette, location, collection)


# ============================================================================
# EXPORT PIPELINE
# ============================================================================

def export_single_building(obj, export_dir, fmt="FBX"):
    os.makedirs(export_dir, exist_ok=True)
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj

    # Temporarily center at origin for clean Unity prefab pivot
    original_loc = obj.location.copy()
    obj.location = Vector((0.0, 0.0, 0.0))

    if fmt.upper() == "GLB":
        filepath = os.path.join(export_dir, f"{obj.name}.glb")
        bpy.ops.export_scene.gltf(
            filepath=filepath,
            use_selection=True,
            export_format="GLB",
            export_yup=True,
            export_apply=True,
        )
    else:
        filepath = os.path.join(export_dir, f"{obj.name}.fbx")
        bpy.ops.export_scene.fbx(
            filepath=filepath,
            use_selection=True,
            global_scale=1.0,
            apply_unit_scale=True,
            apply_scale_options="FBX_SCALE_ALL",
            axis_forward="-Z",
            axis_up="Y",
            bake_space_transform=True,
        )

    obj.location = original_loc
    print(f"[ViceCity Buildings] Exported {obj.name} -> {filepath}")


def main():
    rng = random.Random(CONFIG["seed"])
    clear_scene()
    mats = build_architecture_materials()

    col_hotels = get_or_create_collection("Vice_ArtDeco_Hotels")
    col_towers = get_or_create_collection("Vice_Skyscrapers")
    col_villas = get_or_create_collection("Vice_Coastal_Villas")

    exported_objects = []

    for i in range(CONFIG["hotel_count"]):
        obj = generate_art_deco_hotel(f"ArtDeco_Hotel_{i+1:02d}", (i * CONFIG["spacing_x"], 0.0, 0.0), mats, rng, col_hotels)
        exported_objects.append(obj)

    for i in range(CONFIG["skyscraper_count"]):
        obj = generate_skyscraper(f"Downtown_Skyscraper_{i+1:02d}", (i * CONFIG["spacing_x"], 45.0, 0.0), mats, rng, col_towers)
        exported_objects.append(obj)

    for i in range(CONFIG["villa_count"]):
        obj = generate_coastal_villa(f"Coastal_Villa_{i+1:02d}", (i * CONFIG["spacing_x"], -40.0, 0.0), mats, rng, col_villas)
        exported_objects.append(obj)

    if CONFIG["auto_export"]:
        try:
            for obj in exported_objects:
                export_single_building(obj, CONFIG["unity_export_dir"], CONFIG["export_format"])
        except Exception as exc:
            print(f"[ViceCity Buildings] Export warning (verify Unity folder path): {exc}")

    print(f"[ViceCity Buildings] Generated {len(exported_objects)} modular Art Deco & coastal buildings.")


if __name__ == "__main__":
    main()
