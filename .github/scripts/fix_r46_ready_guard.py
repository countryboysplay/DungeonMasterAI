from pathlib import Path

path = Path('windows/src/DungeonMasterAI.Engine/Spellcasting.cs')
text = path.read_text(encoding='utf-8')
old_guard = '        if (resolution is "area_save" or "multi_buff" or "persistent_area")'
new_guard = '        if (resolution is "multi_buff" or "persistent_area")'
if text.count(old_guard) != 1:
    raise SystemExit(f'Expected exactly one stale area-save Ready guard, found {text.count(old_guard)}.')
text = text.replace(old_guard, new_guard, 1)
text = text.replace(
    'Readying {spell.Name}\'s area or multi-target resolution is not implemented yet.',
    'Readying {spell.Name}\'s multi-target or persistent-area resolution is not implemented yet.',
    1,
)
path.write_text(text, encoding='utf-8')
