"""
Build the starting point for the combined "Tidy" scene: a scene file containing the RobotCafe G1 (Inspire hands),
a handful of the cafe's furniture props, and the scene settings (with path tracing switched off), plus the asset
folders those need, copied into G1RoomNav.

Why file surgery: neither the cafe G1 nor the cafe props are in the Content Vault, so they cannot be spawned with
spawn_robot or import_and_spawn_mesh (which would lose the textures and the colliders). They live in the RobotCafe
project's scene as entities. The G1 is a root entity with ~120 child entities (one per link); each prop is ONE entity
holding a mesh, a compound collider and a free joint, so it copies cleanly. This script copies exactly those
entities into a new scene. Build-TidyScene.ps1 then adds the room (ground, walls, counter), items, lights and a
camera through the editor.

Run (editor closed or showing another scene):   python -I Tools/make_tidy_base.py
Safe to re-run: it never overwrites an existing asset folder, and it rewrites Assets/Scenes/Tidy.hscene.

"""
import re
import shutil
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]    # the folder that holds both RobotCafe and this project
CAFE = ROOT / 'RobotCafe'
MINE = ROOT / 'G1RoomNav'

SOURCE_SCENE = CAFE / 'Assets' / 'Scenes' / 'Cafe.hscene'          # the original cafe scene: has the robot AND every prop
OUT_SCENE = MINE / 'Assets' / 'Scenes' / 'Tidy.hscene'
ROBOT_SRC = CAFE / 'Assets' / 'Robots' / 'UnitreeG1'
ROBOT_DST = MINE / 'Assets' / 'Robots' / 'UnitreeG1'
PACK_SRC = CAFE / 'Assets' / 'Cafe'                                  # meshes, textures, materials, Cafe.hzr (the pack's registry)
PACK_DST = MINE / 'Assets' / 'Cafe'
BLOCK_SRC = CAFE / 'Assets' / 'Props' / 'Block' / 'Frustum.gltf'
BLOCK_DST = MINE / 'Assets' / 'Props' / 'Block' / 'Frustum.gltf'

ROBOT_TAG = 'UnitreeG1'
START_POSITION = (-3.0, 0.0, 0.5)      # where the robot starts (x, y, z); it faces +X
MAX_EPISODE_SECONDS = 900              # the recorder's per-episode limit: a tidy-up takes minutes

# The cafe props to bring over: (tag in the cafe scene, authored position here, static?).
# Authored positions only matter for the first moment of Play (the controller reshuffles at once), so they are
# spread to the room's edges where nothing overlaps. All y values stay at the cafe's floor height.
# static=True removes the free joint: the dining table must not drift when the robot brushes it.
PROPS = [
    ('TableCommunal_01', (1.2, 0.0008, 0.8), True),      # the dining table (a slab on legs: the cafe's own placement surface)
    ('Chair_01',         (-3.4, 0.0008, -2.2), False),
    ('Chair_02',         (-3.4, 0.0008, -1.2), False),
    ('Chair_03',         (3.4, 0.0008, -2.2), False),
    ('Chair_04',         (3.4, 0.0008, -1.2), False),
    ('BarStool_01',      (-2.2, 0.0008, -2.2), False),
    ('BarStool_02',      (3.4, 0.0008, 2.4), False),
    ('TableSquare_01',   (-3.0, 0.0008, 2.2), False),
    ('TableBistro_01',   (3.0, 0.0008, 1.4), False),
    ('FiddleFig_01',     (3.5, 0.0008, 0.2), False),
    ('SnakePlant_01',    (-3.6, 0.0008, 1.6), False),
]


def copy_tree_once(src, dst, label):
    if dst.exists():
        print(f'{label}: {dst} already exists, leaving it')
        return
    shutil.copytree(src, dst)
    size = sum(f.stat().st_size for f in dst.rglob('*') if f.is_file()) / 1e6
    print(f'{label}: copied {src.name} ({size:.0f} MB) -> {dst}')


def copy_assets():
    copy_tree_once(ROBOT_SRC, ROBOT_DST, 'robot assets')
    copy_tree_once(PACK_SRC, PACK_DST, 'cafe pack   ')
    BLOCK_DST.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(BLOCK_SRC, BLOCK_DST)
    print(f'block mesh   : copied {BLOCK_SRC.name} -> {BLOCK_DST}')


def split_entities(lines):
    """Return (header_end, trailer_start, [(start, end, id, parent, tag)]) for the 'Entities:' list."""
    entities_at = next(i for i, l in enumerate(lines) if l.rstrip() == 'Entities:')
    trailer_at = next(i for i, l in enumerate(lines) if l.startswith('SceneAudio:'))
    starts = [i for i in range(entities_at + 1, trailer_at) if lines[i].startswith('  - Entity: ')]
    blocks = []
    for k, s in enumerate(starts):
        e = starts[k + 1] if k + 1 < len(starts) else trailer_at
        ent_id = lines[s].split('Entity:')[1].strip()
        parent = '0'
        tag = ''
        for l in lines[s:e]:
            if parent == '0' and l.startswith('    Parent: '):
                parent = l.split('Parent:')[1].strip()
            if not tag and l.startswith('      Tag: '):
                tag = l.split('Tag:')[1].strip()
        blocks.append((s, e, ent_id, parent, tag))
    return entities_at, trailer_at, blocks


def drop_section(block, key):
    """Remove one 4-space-indented component section (for example MujocoJointComponent) from an entity block."""
    out = []
    skipping = False
    for l in block:
        if l == f'    {key}:':
            skipping = True
            continue
        if skipping and re.match(r'^    \S', l):
            skipping = False
        if not skipping:
            out.append(l)
    return out


def place_in_transform(block, position, zero_rotation=True):
    """Set the entity's local Position (and clear its Rotation) inside its TransformComponent."""
    in_transform = False
    for k, l in enumerate(block):
        if l.strip() == 'TransformComponent:':
            in_transform = True
        elif in_transform and l.startswith('      Position: ['):
            block[k] = f'      Position: [{position[0]}, {position[1]}, {position[2]}]'
        elif in_transform and zero_rotation and l.startswith('      Rotation: ['):
            block[k] = '      Rotation: [0, 0, 0]'
            break
    return block


def main():
    for p in (SOURCE_SCENE, ROBOT_SRC, PACK_SRC, BLOCK_SRC):
        if not p.exists():
            sys.exit(f'missing {p}')
    copy_assets()

    text = SOURCE_SCENE.read_text(encoding='utf8')
    nl = '\r\n' if '\r\n' in text else '\n'
    lines = text.split(nl)
    entities_at, trailer_at, blocks = split_entities(lines)
    print(f'source scene: {len(blocks)} entities')
    by_tag = {}
    for b in blocks:
        by_tag.setdefault(b[4], []).append(b)

    # --- the robot: one top-level entity and everything under it
    roots = [b for b in blocks if b[4] == ROBOT_TAG and b[3] == '0']
    if len(roots) != 1:
        sys.exit(f'expected exactly one top-level entity tagged {ROBOT_TAG}, found {len(roots)}')
    root_id = roots[0][2]
    keep = {root_id}
    changed = True
    while changed:
        changed = False
        for _, _, ent_id, parent, _ in blocks:
            if parent in keep and ent_id not in keep:
                keep.add(ent_id)
                changed = True
    robot_blocks = [b for b in blocks if b[2] in keep]
    print(f'robot subtree: {len(robot_blocks)} entities (root {root_id})')

    # --- header: rename the scene, empty the camera bindings, no path tracing, video on with plain H.264
    header = lines[:entities_at + 1]
    header[0] = 'Scene: Tidy'
    for i, l in enumerate(header):
        if l.startswith('    Cameras:'):
            j = i + 1
            while j < len(header) and not re.match(r'^    \w+:', header[j]):
                j += 1
            header[i:j] = ['    Cameras:', '      []']
            break
    n_pt = sum(1 for l in header if l == '      PathTraced: true')
    header = [l.replace('      PathTraced: true', '      PathTraced: false') for l in header]
    header = [re.sub(r'MaxEpisodeDurationSeconds: \d+', f'MaxEpisodeDurationSeconds: {MAX_EPISODE_SECONDS}', l) for l in header]
    # The cafe scene records no video and asks for HEVC; this machine's GPU has no hardware encoder (the engine
    # falls back to FFmpeg) and no ray tracing, so use plain H.264 video like the navigation scene.
    header = [l.replace('EnableRecordVideo: false', 'EnableRecordVideo: true')
               .replace('VideoMethod: LibavVulkanHEVC', 'VideoMethod: LibavVulkanH264') for l in header]
    print(f'path tracing: switched {n_pt} viewport setting(s) to rasterised')

    # --- assemble the entity list: robot first, then the props
    out_blocks = []
    for s, e, ent_id, parent, tag in robot_blocks:
        block = lines[s:e]
        if ent_id == root_id:
            block = place_in_transform(block, START_POSITION, zero_rotation=False)
            block = ['    Parent: 0' if l.startswith('    Parent: ') else l for l in block]
        out_blocks.extend(block)

    prop_blocks = []
    for tag, position, static in PROPS:
        matches = by_tag.get(tag, [])
        if len(matches) != 1:
            sys.exit(f'expected exactly one entity tagged {tag} in the cafe scene, found {len(matches)}')
        s, e, ent_id, parent, _ = matches[0]
        block = lines[s:e]
        block = ['    Parent: 0' if l.startswith('    Parent: ') else l for l in block]
        block = place_in_transform(block, position)
        if static:
            block = drop_section(block, 'MujocoJointComponent')
        has_joint = any(l == '    MujocoJointComponent:' for l in block)
        prop_blocks.append((tag, ent_id, block))
        out_blocks.extend(block)
        print(f'   prop {tag:18s} -> ({position[0]}, {position[2]}) {"static" if static else ("free joint" if has_joint else "NO JOINT (will not shuffle)")}')

    out = header + out_blocks + lines[trailer_at:]
    OUT_SCENE.parent.mkdir(parents=True, exist_ok=True)
    OUT_SCENE.write_text(nl.join(out), encoding='utf8')
    print(f'wrote {OUT_SCENE} ({OUT_SCENE.stat().st_size / 1024:.0f} KB)')

    # --- check every asset handle the copied entities refer to is known to one of the copied pack registries
    copied_text = nl.join(out_blocks)
    used = set(re.findall(r'AssetID: (\d+)', copied_text)) | set(re.findall(r'Handle: (\d+)', copied_text))
    used |= set(re.findall(r'^\s+\d+: (\d{10,})$', copied_text, flags=re.M))          # material table entries
    known = set()
    for hzr in (ROBOT_DST / 'UnitreeG1.hzr', PACK_DST / 'Cafe.hzr'):
        known |= set(re.findall(r'Handle: (\d+)', hzr.read_text(encoding='utf8')))
    scene_ids = {ent_id for _, _, ent_id, _, _ in robot_blocks} | {b[1] for b in prop_blocks}
    unresolved = sorted(h for h in used if h not in known and h not in scene_ids and h != '0')   # 0 is the null handle (an unset slot)
    print(f'asset handles used by the copied entities: {len(used)}; unresolved: {len(unresolved)}')
    for h in unresolved[:20]:
        print('   unresolved:', h)
    if unresolved:
        print('   (an unresolved handle means a mesh or material the copied registries do not list; the editor will '
              'report it as missing when the scene opens. Copy the asset that owns it.)')


if __name__ == '__main__':
    main()
