#!/usr/bin/env python3
"""Build a compact spell metadata catalog from the CC-BY-4.0 SRD 5.2.1 PDF.

The output intentionally stores spell-header metadata only. It does not copy full
spell descriptions. Mechanical auto-resolution is marked unsupported until a
spell is explicitly implemented and tested in the deterministic engine.
"""
from __future__ import annotations

import argparse
import json
import re
import subprocess
import tempfile
from pathlib import Path

LEVEL_RE = re.compile(r"^Level\s+([1-9])\s+([A-Za-z]+)\s*(?:\(.*)?$")
CANTRIP_RE = re.compile(r"^([A-Za-z]+)\s+Cantrip\s*(?:\(.*)?$")
PAGE_RE = re.compile(r"^(\d{1,3})$")
RANGE_FEET_RE = re.compile(r"^(\d+)\s+feet(?:\b|$)", re.I)


# Explicit, source-verified deterministic implementations. These are intentionally
# narrow. A spell remains unsupported unless the engine can resolve its mechanical
# effect without inventing rules.
DETERMINISTIC_OVERRIDES: dict[str, dict] = {
    "spell.cure_wounds": {
        "requires_target": True,
        "resolution": "healing",
        "healing_expression": "2d8",
        "extra_healing_per_slot_expression": "2d8",
        "add_spellcasting_ability_modifier_to_healing": True,
    },
    "spell.healing_word": {
        "requires_target": True,
        "resolution": "healing",
        "healing_expression": "2d4",
        "extra_healing_per_slot_expression": "2d4",
        "add_spellcasting_ability_modifier_to_healing": True,
    },
    "spell.fire_bolt": {
        "requires_target": True,
        "resolution": "attack",
        "damage_expression": "1d10",
        "damage_type": "Fire",
        "cantrip_damage_scaling": True,
    },
    "spell.sacred_flame": {
        "requires_target": True,
        "resolution": "save",
        "save_ability": "dexterity",
        "damage_expression": "1d8",
        "damage_type": "Radiant",
        "cantrip_damage_scaling": True,
        "ignore_half_and_three_quarters_cover_on_save": True,
    },
    "spell.spare_the_dying": {
        "requires_target": True,
        "resolution": "stabilize",
        "cantrip_range_doubling": True,
    },
    "spell.guiding_bolt": {
        "requires_target": True,
        "resolution": "attack",
        "damage_expression": "4d6",
        "damage_type": "Radiant",
        "extra_damage_per_slot_expression": "1d6",
        "next_attack_against_target_has_advantage": True,
        "effect_expires_at_end_of_caster_next_turn": True,
    },
    "spell.hold_person": {
        "requires_target": True,
        "resolution": "save",
        "save_ability": "wisdom",
        "required_target_creature_type": "Humanoid",
        "condition_on_failed_save": "Paralyzed",
        "repeat_save_at_end_of_turn": True,
    },
    "spell.magic_missile": {
        "requires_target": True,
        "resolution": "projectile_auto",
        "damage_expression": "1d4+1",
        "damage_type": "Force",
        "base_projectiles": 3,
        "extra_projectiles_per_slot": 1,
        "requires_visible_target": True,
    },
    "spell.scorching_ray": {
        "requires_target": True,
        "resolution": "projectile_attack",
        "damage_expression": "2d6",
        "damage_type": "Fire",
        "base_projectiles": 3,
        "extra_projectiles_per_slot": 1,
    },
    "spell.bless": {
        "resolution": "multi_buff",
        "base_targets": 3,
        "extra_targets_per_slot": 1,
        "attack_roll_bonus_expression": "1d4",
        "saving_throw_bonus_expression": "1d4",
    },
    "spell.burning_hands": {
        "resolution": "area_save",
        "save_ability": "dexterity",
        "damage_expression": "3d6",
        "damage_type": "Fire",
        "half_damage_on_successful_save": True,
        "extra_damage_per_slot_expression": "1d6",
        "area_shape": "cone",
        "area_size_feet": 15,
        "area_origin": "self",
        "environmental_effect": "Unattended flammable objects in the cone ignite.",
    },
    "spell.fireball": {
        "resolution": "area_save",
        "save_ability": "dexterity",
        "damage_expression": "8d6",
        "damage_type": "Fire",
        "half_damage_on_successful_save": True,
        "extra_damage_per_slot_expression": "1d6",
        "area_shape": "sphere",
        "area_size_feet": 20,
        "area_origin": "point",
        "environmental_effect": "Unattended flammable objects in the sphere ignite.",
    },
    "spell.thunderwave": {
        "resolution": "area_save",
        "save_ability": "constitution",
        "damage_expression": "2d8",
        "damage_type": "Thunder",
        "half_damage_on_successful_save": True,
        "extra_damage_per_slot_expression": "1d8",
        "area_shape": "cube",
        "area_size_feet": 15,
        "area_origin": "self",
        "push_feet_on_failed_save": 10,
        "environmental_effect": "Unsecured objects fully inside the cube are pushed 10 feet away, and the boom is audible within 300 feet.",
    },
    "spell.fog_cloud": {
        "resolution": "persistent_area",
        "area_shape": "sphere",
        "area_size_feet": 20,
        "extra_area_size_per_slot_feet": 20,
        "area_origin": "point",
        "battlefield_trigger": "none",
        "battlefield_heavily_obscured": True,
        "battlefield_blocks_line_of_sight": True,
        "environmental_effect": "The fog lasts for the duration or until a strong wind disperses it.",
    },
    "spell.ray_of_frost": {
        "requires_target": True,
        "resolution": "attack",
        "damage_expression": "1d8",
        "damage_type": "Cold",
        "cantrip_damage_scaling": True,
        "speed_modifier_feet": -10,
        "effect_expires_at_start_of_caster_next_turn": True,
    },
    "spell.shatter": {
        "resolution": "area_save",
        "save_ability": "constitution",
        "damage_expression": "3d8",
        "damage_type": "Thunder",
        "half_damage_on_successful_save": True,
        "extra_damage_per_slot_expression": "1d8",
        "area_shape": "sphere",
        "area_size_feet": 10,
        "area_origin": "point",
        "save_disadvantage_creature_type": "Construct",
        "environmental_effect": "Nonmagical objects in the area that are not worn or carried also take the damage.",
    },
    "spell.shield_of_faith": {
        "resolution": "multi_buff",
        "base_targets": 1,
        "armor_class_bonus": 2,
    },
    # SRD 5.2.1 p. 144. The only SRD line-shaped area implemented in the engine:
    # a 100-foot-long, 5-foot-wide Line originating from the caster.
    "spell.lightning_bolt": {
        "resolution": "area_save",
        "save_ability": "dexterity",
        "damage_expression": "8d6",
        "damage_type": "Lightning",
        "half_damage_on_successful_save": True,
        "extra_damage_per_slot_expression": "1d6",
        "area_shape": "line",
        "area_size_feet": 100,
        "area_width_feet": 5,
        "area_origin": "self",
    },
    # SRD 5.2.1 p. 117. The frozen-statue clause applies only to a creature the
    # spell kills outright, so it is recorded as a narrative environmental effect
    # rather than a combat-mechanical condition.
    "spell.cone_of_cold": {
        "resolution": "area_save",
        "save_ability": "constitution",
        "damage_expression": "8d8",
        "damage_type": "Cold",
        "half_damage_on_successful_save": True,
        "extra_damage_per_slot_expression": "1d8",
        "area_shape": "cone",
        "area_size_feet": 60,
        "area_origin": "self",
        "environmental_effect": "A creature killed by this spell becomes a frozen statue until it thaws.",
    },
    # SRD 5.2.1 p. 115. Upcasting adds 2d8 per slot level, not the more common 1d8.
    "spell.circle_of_death": {
        "resolution": "area_save",
        "save_ability": "constitution",
        "damage_expression": "8d8",
        "damage_type": "Necrotic",
        "half_damage_on_successful_save": True,
        "extra_damage_per_slot_expression": "2d8",
        "area_shape": "sphere",
        "area_size_feet": 60,
        "area_origin": "point",
    },
    # SRD 5.2.1 p. 143. A touch-range single-target save spell, not an attack roll.
    "spell.inflict_wounds": {
        "requires_target": True,
        "resolution": "save",
        "save_ability": "constitution",
        "damage_expression": "2d10",
        "damage_type": "Necrotic",
        "half_damage_on_successful_save": True,
        "extra_damage_per_slot_expression": "1d10",
    },
    # SRD 5.2.1 p. 145. Upcasting adds targets rather than increasing the speed bonus.
    "spell.longstrider": {
        "resolution": "multi_buff",
        "base_targets": 1,
        "extra_targets_per_slot": 1,
        "speed_modifier_feet": 10,
    },
    # SRD 5.2.1 p. 147. Up to six creatures in a 30-foot-radius Sphere. The target
    # cap does not grow when upcast; only the healing dice do.
    "spell.mass_cure_wounds": {
        "resolution": "multi_heal",
        "base_targets": 6,
        "extra_targets_per_slot": 0,
        "healing_expression": "5d8",
        "extra_healing_per_slot_expression": "1d8",
        "add_spellcasting_ability_modifier_to_healing": True,
        "area_shape": "sphere",
        "area_size_feet": 30,
        "area_origin": "point",
    },
    # SRD 5.2.1 p. 148. Up to six creatures chosen individually; no area geometry.
    "spell.mass_healing_word": {
        "resolution": "multi_heal",
        "base_targets": 6,
        "extra_targets_per_slot": 0,
        "healing_expression": "2d4",
        "extra_healing_per_slot_expression": "1d4",
        "add_spellcasting_ability_modifier_to_healing": True,
    },
    # SRD 5.2.1 p. 139. Flat healing with no dice, and it ends three conditions.
    # The SRD text does not include a disease clause, so none is implemented.
    "spell.heal": {
        "requires_target": True,
        "resolution": "healing",
        "healing_expression": "70",
        "extra_healing_per_slot_expression": "10",
        "add_spellcasting_ability_modifier_to_healing": False,
        "conditions_ended_on_target": "Blinded,Deafened,Poisoned",
    },
    "spell.spike_growth": {
        "resolution": "persistent_area",
        "area_shape": "sphere",
        "area_size_feet": 20,
        "area_origin": "point",
        "battlefield_trigger": "move_within",
        "damage_expression": "2d4",
        "damage_type": "Piercing",
        "battlefield_difficult_terrain": True,
        "environmental_effect": "The ground is camouflaged to look natural; creatures that did not see the area when cast can Search to recognize the hazard before entering it.",
    },
}


def pdf_to_raw_text(pdf: Path) -> str:
    with tempfile.NamedTemporaryFile(suffix=".txt", delete=False) as tmp:
        out = Path(tmp.name)
    try:
        subprocess.run(["pdftotext", "-raw", str(pdf), str(out)], check=True)
        return out.read_text(encoding="utf-8", errors="replace")
    finally:
        out.unlink(missing_ok=True)


def clean_line(line: str) -> str:
    return line.replace("\u00ad", "").strip()


# Page footers carry the only page numbers in the document, and different pdftotext
# releases emit them differently: older builds put the document title and the page
# number on separate lines, while newer builds keep them on one line and glue the
# form feed to the preceding text. Normalizing both layouts to the same canonical
# three-line shape keeps generated page provenance stable across poppler versions.
FOOTER_RE = re.compile(r"\f?System Reference Document 5\.2\.1[ \t]+(\d{1,3})[ \t]*$", re.M)


def normalize_page_footers(text: str) -> str:
    text = FOOTER_RE.sub(lambda m: f"\nSystem Reference Document 5.2.1\n{m.group(1)}", text)
    return text.replace("\f", "\n")


def parse_catalog(text: str) -> list[dict]:
    text = normalize_page_footers(text)
    start = text.find("\nSpell Descriptions\n")
    if start < 0:
        raise RuntimeError("Spell Descriptions section not found")
    end = text.find("\nRules Glossary\n", start)
    if end < 0:
        raise RuntimeError("Rules Glossary boundary not found")
    lines = [clean_line(x) for x in text[start:end].splitlines()]

    spells: list[dict] = []
    page = 107
    i = 0
    while i < len(lines):
        if lines[i] == "System Reference Document 5.2.1" and i + 1 < len(lines) and PAGE_RE.match(lines[i + 1]):
            page = int(lines[i + 1])
            i += 2
            continue

        name = lines[i]
        if not name or name in {"Spell Descriptions", "System Reference Document 5.2.1"}:
            i += 1
            continue

        # Spell header is name, then a level/school line that can wrap before Casting Time.
        header_parts = []
        j = i + 1
        while j < min(i + 5, len(lines)) and not lines[j].startswith("Casting Time:"):
            if lines[j] and lines[j] != "System Reference Document 5.2.1" and not PAGE_RE.match(lines[j]):
                header_parts.append(lines[j])
            j += 1
        if j >= len(lines) or not lines[j].startswith("Casting Time:") or not header_parts:
            i += 1
            continue

        header = " ".join(header_parts)
        level_match = re.match(r"^Level\s+([1-9])\s+([A-Za-z]+)\b", header)
        cantrip_match = re.match(r"^([A-Za-z]+)\s+Cantrip\b", header)
        if level_match:
            level = int(level_match.group(1))
            school = level_match.group(2)
        elif cantrip_match:
            level = 0
            school = cantrip_match.group(1)
        else:
            i += 1
            continue

        casting_time = lines[j].split(":", 1)[1].strip()
        j += 1
        if j >= len(lines) or not lines[j].startswith("Range:"):
            i += 1
            continue
        range_text = lines[j].split(":", 1)[1].strip()
        j += 1
        if j >= len(lines) or not lines[j].startswith("Components:"):
            i += 1
            continue
        components = lines[j].split(":", 1)[1].strip()
        j += 1
        # Material component text can wrap until Duration.
        while j < len(lines) and not lines[j].startswith("Duration:") and j < i + 12:
            if lines[j] == "System Reference Document 5.2.1" or PAGE_RE.match(lines[j]):
                j += 1
                continue
            components += " " + lines[j]
            j += 1
        if j >= len(lines) or not lines[j].startswith("Duration:"):
            i += 1
            continue
        duration = lines[j].split(":", 1)[1].strip()

        range_kind = "distance"
        range_feet = 0
        if range_text.lower().startswith("self"):
            range_kind = "self"
        elif range_text.lower().startswith("touch"):
            range_kind = "touch"
        else:
            rm = RANGE_FEET_RE.match(range_text)
            if rm:
                range_feet = int(rm.group(1))
            else:
                range_kind = range_text.lower()

        material = ""
        mm = re.search(r"\bM\s*\((.*)\)\s*$", components)
        if mm:
            material = mm.group(1).strip()

        key = "spell." + re.sub(r"[^a-z0-9]+", "_", name.lower()).strip("_")
        spells.append(
            {
                "key": key,
                "name": name,
                "level": level,
                "school": school,
                "casting_time": casting_time.replace(" or Ritual", ""),
                "range_kind": range_kind,
                "range_feet": range_feet,
                "requires_verbal": bool(re.search(r"(?:^|,\s*)V(?:,|\s|$)", components)),
                "requires_somatic": bool(re.search(r"(?:^|,\s*)S(?:,|\s|$)", components)),
                "requires_material": bool(re.search(r"(?:^|,\s*)M(?:\s|\(|,|$)", components)),
                "material_description": material,
                "duration": duration,
                "requires_concentration": duration.lower().startswith("concentration"),
                "ritual": "ritual" in casting_time.lower(),
                "requires_target": False,
                "resolution": "unsupported",
                "source_kind": "srd_5_2_1",
                "source_page": page,
                "source_reference": "SRD 5.2.1"
            }
        )
        i = j + 1

    # Deduplicate conservatively by normalized key, then apply only explicit
    # deterministic overrides that were verified against the SRD text.
    dedup: dict[str, dict] = {}
    for spell in spells:
        dedup.setdefault(spell["key"], spell)
    for key, override in DETERMINISTIC_OVERRIDES.items():
        if key in dedup:
            dedup[key].update(override)
    return list(dedup.values())


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("pdf", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    text = pdf_to_raw_text(args.pdf)
    spells = parse_catalog(text)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    payload = {
        "format_version": 1,
        "source": "System Reference Document 5.2.1",
        "license": "CC-BY-4.0",
        "spell_count": len(spells),
        "spells": spells,
    }
    args.output.write_text(json.dumps(payload, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"Wrote {len(spells)} spell metadata records to {args.output}")


if __name__ == "__main__":
    main()
