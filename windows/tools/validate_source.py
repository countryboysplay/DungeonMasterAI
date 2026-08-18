#!/usr/bin/env python3
"""Fast source-level validation for environments without the .NET SDK.

This is deliberately not a substitute for `dotnet build`. It catches malformed XML,
obvious C# delimiter/lexical damage, duplicate DM tool names, and WPF Command bindings
that do not have matching ICommand properties on MainViewModel.
"""
from __future__ import annotations

from pathlib import Path
import json
import re
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]


def strip_csharp_noncode(text: str) -> tuple[str, str]:
    out: list[str] = []
    i = 0
    state = "code"
    while i < len(text):
        c = text[i]
        d = text[i + 1] if i + 1 < len(text) else ""
        if state == "code":
            if c == "/" and d == "/":
                state = "line"
                out.extend("  ")
                i += 2
                continue
            if c == "/" and d == "*":
                state = "block"
                out.extend("  ")
                i += 2
                continue
            if c in "$@" and d in "$@" and i + 2 < len(text) and text[i + 2] == '"':
                state = "verbatim" if "@" in c + d else "string"
                out.extend("   ")
                i += 3
                continue
            if c in "$@" and d == '"':
                state = "verbatim" if c == "@" else "string"
                out.extend("  ")
                i += 2
                continue
            if c == '"':
                state = "string"
                out.append(" ")
                i += 1
                continue
            if c == "'":
                state = "char"
                out.append(" ")
                i += 1
                continue
            out.append(c)
            i += 1
            continue
        if state == "line":
            if c == "\n":
                state = "code"
                out.append("\n")
            else:
                out.append(" ")
            i += 1
            continue
        if state == "block":
            if c == "*" and d == "/":
                state = "code"
                out.extend("  ")
                i += 2
            else:
                out.append("\n" if c == "\n" else " ")
                i += 1
            continue
        if state == "string":
            if c == "\\":
                out.extend("  " if i + 1 < len(text) else " ")
                i += 2
                continue
            if c == '"':
                state = "code"
                out.append(" ")
                i += 1
                continue
            out.append("\n" if c == "\n" else " ")
            i += 1
            continue
        if state == "verbatim":
            if c == '"' and d == '"':
                out.extend("  ")
                i += 2
                continue
            if c == '"':
                state = "code"
                out.append(" ")
                i += 1
                continue
            out.append("\n" if c == "\n" else " ")
            i += 1
            continue
        if state == "char":
            if c == "\\":
                out.extend("  " if i + 1 < len(text) else " ")
                i += 2
                continue
            if c == "'":
                state = "code"
            out.append(" ")
            i += 1
    return "".join(out), state


# Build output is generated, not authored. Including it made every reported metric depend on
# whether the tree had been built, and fed machine-generated files to the delimiter checker.
GENERATED_DIRS = {"bin", "obj", "artifacts", ".vs"}


def sources(pattern: str) -> list[Path]:
    """All authored files matching pattern, excluding build output."""
    return sorted(
        path
        for path in ROOT.rglob(pattern)
        if not GENERATED_DIRS.intersection(path.relative_to(ROOT).parts)
    )


def main() -> int:
    errors: list[str] = []
    xml_files = sources("*.xaml") + sources("*.csproj") + [ROOT / "Directory.Build.props"]
    for path in xml_files:
        try:
            ET.parse(path)
        except Exception as exc:  # noqa: BLE001 - diagnostic tool
            errors.append(f"XML {path.relative_to(ROOT)}: {exc}")

    csharp_files = sources("*.cs")
    pairs = {")": "(", "]": "[", "}": "{"}
    for path in csharp_files:
        stripped, state = strip_csharp_noncode(path.read_text(encoding="utf-8", errors="replace"))
        if state not in {"code", "line"}:
            errors.append(f"C# lexical end state {state}: {path.relative_to(ROOT)}")
        stack: list[str] = []
        for char in stripped:
            if char in "([{":
                stack.append(char)
            elif char in ")]}":
                if not stack or stack[-1] != pairs[char]:
                    errors.append(f"C# delimiter mismatch: {path.relative_to(ROOT)}")
                    break
                stack.pop()
        if stack:
            errors.append(f"C# unclosed delimiter {stack[-1]}: {path.relative_to(ROOT)}")

    router = (ROOT / "src/DungeonMasterAI.Engine/DmToolRouter.cs").read_text(encoding="utf-8")
    tool_names = re.findall(r'Tool\("([^"]+)"', router)
    duplicates = sorted({name for name in tool_names if tool_names.count(name) > 1})
    if duplicates:
        errors.append("Duplicate DM tools: " + ", ".join(duplicates))

    # MainViewModel is a partial class split across MainViewModel.cs and MainViewModel.*.cs.
    # Reading only the root file reported every command declared in a partial as missing.
    vm = "\n".join(
        path.read_text(encoding="utf-8")
        for path in sorted((ROOT / "src/DungeonMasterAI.App").glob("MainViewModel*.cs"))
    )
    # Accept both block-bodied ("public ICommand X { get; }") and expression-bodied
    # ("public ICommand X => ...") declarations. Requiring a brace missed every "=>" command.
    commands = set(re.findall(r"public\s+ICommand\s+(\w+)\s*(?:\{|=>)", vm))
    xaml = "\n".join(path.read_text(encoding="utf-8") for path in sources("*.xaml"))
    bindings = set(re.findall(r'Command="\{Binding\s+(?:Path=)?(\w+)', xaml))
    missing_commands = sorted(bindings - commands)
    if missing_commands:
        errors.append("Missing ICommand properties: " + ", ".join(missing_commands))

    # The inverse direction is the r57 shell regression: commands the view model exposes that no
    # XAML binds, i.e. features the user cannot reach. Reported as a metric here and enforced as a
    # hard gate by GuiSmokeTests, which can also see code-behind wiring that a text scan cannot.
    unreachable_commands = sorted(commands - bindings)

    spell_catalog_path = ROOT / "src/DungeonMasterAI.App/Assets/Rules/srd_spells.json"
    implemented_spells = 0
    try:
        catalog = json.loads(spell_catalog_path.read_text(encoding="utf-8"))
        spells = catalog.get("spells", [])
        implemented_spells = sum(1 for spell in spells if spell.get("resolution") != "unsupported")
        if len(spells) != 316:
            errors.append(f"SRD spell catalog expected 316 entries, found {len(spells)}")
        by_key = {spell.get("key"): spell for spell in spells}
        required = {
            "spell.cure_wounds": {"resolution": "healing", "healing_expression": "2d8", "add_spellcasting_ability_modifier_to_healing": True},
            "spell.healing_word": {"resolution": "healing", "healing_expression": "2d4", "add_spellcasting_ability_modifier_to_healing": True},
            "spell.fire_bolt": {"resolution": "attack", "damage_expression": "1d10", "cantrip_damage_scaling": True},
            "spell.sacred_flame": {"resolution": "save", "save_ability": "dexterity", "cantrip_damage_scaling": True, "ignore_half_and_three_quarters_cover_on_save": True},
            "spell.spare_the_dying": {"resolution": "stabilize", "cantrip_range_doubling": True},
            "spell.guiding_bolt": {"resolution": "attack", "damage_expression": "4d6", "next_attack_against_target_has_advantage": True},
            "spell.hold_person": {"resolution": "save", "save_ability": "wisdom", "condition_on_failed_save": "Paralyzed", "repeat_save_at_end_of_turn": True, "required_target_creature_type": "Humanoid"},
            "spell.magic_missile": {"resolution": "projectile_auto", "damage_expression": "1d4+1", "damage_type": "Force", "base_projectiles": 3, "extra_projectiles_per_slot": 1, "requires_visible_target": True},
            "spell.scorching_ray": {"resolution": "projectile_attack", "damage_expression": "2d6", "damage_type": "Fire", "base_projectiles": 3, "extra_projectiles_per_slot": 1},
            "spell.bless": {"resolution": "multi_buff", "base_targets": 3, "extra_targets_per_slot": 1, "attack_roll_bonus_expression": "1d4", "saving_throw_bonus_expression": "1d4"},
            "spell.burning_hands": {"resolution": "area_save", "save_ability": "dexterity", "damage_expression": "3d6", "area_shape": "cone", "area_size_feet": 15, "area_origin": "self"},
            "spell.fireball": {"resolution": "area_save", "save_ability": "dexterity", "damage_expression": "8d6", "area_shape": "sphere", "area_size_feet": 20, "area_origin": "point"},
            "spell.thunderwave": {"resolution": "area_save", "save_ability": "constitution", "damage_expression": "2d8", "area_shape": "cube", "area_size_feet": 15, "area_origin": "self", "push_feet_on_failed_save": 10},
            "spell.fog_cloud": {"resolution": "persistent_area", "area_shape": "sphere", "area_size_feet": 20, "extra_area_size_per_slot_feet": 20, "area_origin": "point", "battlefield_heavily_obscured": True, "battlefield_blocks_line_of_sight": True},
            "spell.ray_of_frost": {"resolution": "attack", "damage_expression": "1d8", "damage_type": "Cold", "cantrip_damage_scaling": True, "speed_modifier_feet": -10, "effect_expires_at_start_of_caster_next_turn": True},
            "spell.shatter": {"resolution": "area_save", "save_ability": "constitution", "damage_expression": "3d8", "damage_type": "Thunder", "area_shape": "sphere", "area_size_feet": 10, "area_origin": "point", "save_disadvantage_creature_type": "Construct"},
            "spell.shield_of_faith": {"resolution": "multi_buff", "base_targets": 1, "armor_class_bonus": 2},
        }
        for key, expected in required.items():
            actual = by_key.get(key)
            if actual is None:
                errors.append(f"Missing required deterministic SRD spell override: {key}")
                continue
            for field, value in expected.items():
                if actual.get(field) != value:
                    errors.append(f"SRD spell override {key}.{field}: expected {value!r}, found {actual.get(field)!r}")
    except Exception as exc:  # noqa: BLE001 - diagnostic tool
        errors.append(f"SRD spell catalog validation failed: {exc}")

    print(f"C# files: {len(csharp_files)}")
    print(f"View model commands: {len(commands)}")
    print(f"XAML command bindings: {len(bindings)}")
    print(f"Commands not bound in XAML: {len(unreachable_commands)}")
    if unreachable_commands:
        print("  " + ", ".join(unreachable_commands))
    print(f"DM tools: {len(tool_names)} unique: {len(set(tool_names))}")
    print(f"Deterministic SRD spells: {implemented_spells}")
    print(f"Errors: {len(errors)}")
    for error in errors:
        print("ERROR:", error)
    if errors:
        return 1
    print("SOURCE-LEVEL VALIDATION PASSED (not a substitute for dotnet build)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
