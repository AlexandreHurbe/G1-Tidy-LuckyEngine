"""
Replace the three tapered-block items (Item_1..Item_3) in Assets/Scenes/Tidy.hscene with copies of the cafe's own
"ToGoCup" entity (the object the cafe's grasp code was calibrated on: five stacked cylinders, radius 3.1-4.4 cm,
lid lip at 12.1 cm, friction 0.6, density 598), named Item_1..Item_3 so TidyWaiter needs no change.

Run with the editor showing a DIFFERENT scene (or closed):   python -I Tools/swap_items_to_cup.py
Writes Tidy.hscene.before_cup next to the scene as a backup, and refuses to run twice (it looks for the backup).
"""
import random
import re
import shutil
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]    # the folder that holds both RobotCafe and this project
CAFE_SCENE = ROOT / 'RobotCafe' / 'Assets' / 'Scenes' / 'Cafe.hscene'
TIDY = ROOT / 'G1RoomNav' / 'Assets' / 'Scenes' / 'Tidy.hscene'
PACK_REGISTRY = ROOT / 'G1RoomNav' / 'Assets' / 'Cafe' / 'Cafe.hzr'
BACKUP = TIDY.with_name('Tidy.hscene.before_cup')
X = [-0.6, 0.0, 0.6]            # first positions on the counter; TidyWaiter re-places them at the start of each episode


def split(lines):
    ent_at = next(i for i, l in enumerate(lines) if l.rstrip() == 'Entities:')
    trailer = next(i for i, l in enumerate(lines) if l.startswith('SceneAudio:'))
    starts = [i for i in range(ent_at + 1, trailer) if lines[i].startswith('  - Entity: ')]
    blocks = []
    for k, s in enumerate(starts):
        e = starts[k + 1] if k + 1 < len(starts) else trailer
        tag = next((l.split('Tag:')[1].strip() for l in lines[s:e] if l.startswith('      Tag: ')), '')
        blocks.append((s, e, lines[s].split('Entity:')[1].strip(), tag))
    return ent_at, trailer, blocks


def main():
    if BACKUP.exists():
        sys.exit(f'{BACKUP.name} exists: the swap was already done (restore it to undo)')
    cafe_text = CAFE_SCENE.read_text(encoding='utf8')
    cafe = cafe_text.split('\r\n' if '\r\n' in cafe_text else '\n')
    _, _, cb = split(cafe)
    cups = [b for b in cb if b[3] == 'ToGoCup']
    if len(cups) != 1:
        sys.exit(f'expected one ToGoCup in the cafe scene, found {len(cups)}')
    cup_block = cafe[cups[0][0]:cups[0][1]]

    text = TIDY.read_text(encoding='utf8')
    nl = '\r\n' if '\r\n' in text else '\n'
    lines = text.split(nl)
    ent_at, trailer, blocks = split(lines)
    items = [b for b in blocks if b[3] in ('Item_1', 'Item_2', 'Item_3')]
    if len(items) != 3:
        sys.exit(f'expected Item_1..Item_3 in Tidy.hscene, found {len(items)}')
    used = {b[2] for b in blocks}

    # every asset handle the cup refers to must be known to the copied pack registry
    known = set(re.findall(r'Handle: (\d+)', PACK_REGISTRY.read_text(encoding='utf8')))
    wanted = set(re.findall(r'AssetID: (\d+)', '\n'.join(cup_block))) | set(re.findall(r'^\s+\d+: (\d{10,})$', '\n'.join(cup_block), flags=re.M))
    missing = sorted(h for h in wanted if h not in known and h != '0')
    print('cup asset handles:', len(wanted), 'missing from Cafe.hzr:', missing or 'none')
    if missing:
        sys.exit('stopping: a handle is not in the copied pack')

    shutil.copy2(TIDY, BACKUP)
    drop = {i for b in items for i in range(b[0], b[1])}
    kept = [l for i, l in enumerate(lines) if i not in drop]
    ent_at, trailer, _ = split(kept)

    rng = random.Random(7)
    new_blocks = []
    for n, x in zip((1, 2, 3), X):
        new_id = str(rng.getrandbits(63))
        while new_id in used:
            new_id = str(rng.getrandbits(63))
        used.add(new_id)
        b = list(cup_block)
        b[0] = f'  - Entity: {new_id}'
        for k, l in enumerate(b):
            if l.startswith('      Tag: '):
                b[k] = f'      Tag: Item_{n}'
            elif l.startswith('    Parent: '):
                b[k] = '    Parent: 0'
            elif l.startswith('      Position: ['):
                b[k] = f'      Position: [{x}, 1.0005, -2.41]'
        new_blocks.extend(b)
    out = kept[:trailer] + new_blocks + kept[trailer:]
    TIDY.write_text(nl.join(out), encoding='utf8')
    print(f'replaced 3 block items with 3 ToGoCup copies; backup at {BACKUP.name}')


if __name__ == '__main__':
    main()
