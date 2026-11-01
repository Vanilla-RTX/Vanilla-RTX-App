"""
The Vanilla RTX App's install-time material upgrader, run by LazuriteToolchain out of process.

Usage: python -I material_upgrade.py <site-packages> <command> [args]
  info                                    lazurite's version and the formats it reads
  roundtrip <file>...                     read and write back each file; byte-identical or not
  upgrade <vanilla dir> <out dir> <preset file>...
                                          classify each preset material against the game's material of the same
                                          name; upgraded files are written to <out dir>

Prints exactly one JSON object on stdout, whatever happens; diagnostics go to stderr.

The upgrade rule: the result is the game's own material for this exact version (the container, every record and
every shader the preset doesn't ship) with only the preset's shader bytecode moved in. Mojang's container is
therefore never rebuilt from an older one, which is what makes layout changes that keep the same format number
harmless. What the preset's shaders receive from the game - buffers, uniforms, passes, variants, each shader's
inputs and bgfx uniforms - has to be unchanged, or the bytecode was compiled against an interface this game no
longer has and no repackaging can fix it.
"""

import io
import json
import os
import sys

PROTOCOL = 1


def emit(obj):
    sys.stdout.write(json.dumps(obj))
    sys.stdout.flush()


def fail(message):
    emit({"protocol": PROTOCOL, "error": message})
    sys.exit(1)


if len(sys.argv) < 3:
    fail("usage: material_upgrade.py <site-packages> <command> [args]")

# -I keeps the user's environment and site-packages out; the toolchain's own site and this folder go in explicitly.
sys.path.insert(0, os.path.abspath(sys.argv[1]))
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

try:
    import lazurite_patched  # noqa: F401  (patches lazurite before anything reads a material)
    from importlib.metadata import version as package_version
    from lazurite.material import Material
except Exception as e:
    fail(f"lazurite failed to load: {type(e).__name__}: {e}")


def read(raw):
    material = Material()
    material.read(io.BytesIO(raw))
    return material


def write(material):
    out = io.BytesIO()
    material.write(out)
    return out.getvalue()


def shaders_by_key(material):
    """Every shader keyed by what the game selects it by: pass, variant flags, stage, platform (+ an occurrence
    number for the rare duplicate)."""
    keyed = {}
    for shader_pass in material.passes:
        for variant in shader_pass.variants:
            flags = tuple(sorted(variant.flags.items()))
            seen = {}
            for shader in variant.shaders:
                base = (shader_pass.name, flags, shader.stage.name, shader.platform.name)
                n = seen.get(base, 0)
                seen[base] = n + 1
                keyed[base + (n,)] = shader
    return keyed


def describe_key(key):
    pass_name, flags, stage, platform, _ = key
    flag_text = ", ".join(f"{k}={v}" for k, v in flags)
    return f"{pass_name} [{flag_text}] {stage} {platform}" if flag_text else f"{pass_name} {stage} {platform}"


def buffer_signature(b):
    custom = (b.custom_type_info.struct, b.custom_type_info.size) if b.custom_type_info else None
    return (b.register_slot, b.access.name, b.unordered_access, b.type.name, b.texture_format, b.slot_count,
            b.binding_slot, custom)


def uniform_signature(u):
    return (u.type.name, u.count)


def input_signature(i):
    return (i.name, i.type.name, i.semantic.index, i.semantic.sub_index, i.per_instance)


def bgfx_signature(shader):
    s = shader.bgfx_shader
    return (tuple((u.name, u.type_bits, u.count, u.reg_index, u.reg_count) for u in s.uniforms),
            tuple(s.group_size), tuple(s.attributes))


def compare_named(kind, preset_items, game_items, signature, problems):
    preset = {x.name: signature(x) for x in preset_items}
    game = {x.name: signature(x) for x in game_items}
    for name in sorted(preset.keys() - game.keys()):
        problems.append(f"the game no longer has {kind} {name}")
    for name in sorted(game.keys() - preset.keys()):
        problems.append(f"the game has a new {kind}, {name}")
    for name in sorted(preset.keys() & game.keys()):
        if preset[name] != game[name]:
            problems.append(f"{kind} {name} changed")


def interface_problems(preset, game):
    """Everything about what the preset's shaders receive that differs from this game's. Empty means the preset's
    bytecode was compiled against the interface this game still has."""
    problems = []
    if preset.name != game.name:
        problems.append(f"material name {preset.name!r} is not {game.name!r}")
    compare_named("buffer", preset.buffers, game.buffers, buffer_signature, problems)
    compare_named("uniform", preset.uniforms, game.uniforms, uniform_signature, problems)

    preset_passes = {p.name: p for p in preset.passes}
    game_passes = {p.name: p for p in game.passes}
    for name in sorted(preset_passes.keys() - game_passes.keys()):
        problems.append(f"the game no longer has pass {name}")
    for name in sorted(game_passes.keys() - preset_passes.keys()):
        problems.append(f"the game has a new pass, {name}")
    for name in sorted(preset_passes.keys() & game_passes.keys()):
        a, b = preset_passes[name], game_passes[name]
        if set(a.flag_domain) != set(b.flag_domain):
            problems.append(f"pass {name}'s variant flags changed")
        flags_a = {tuple(sorted(v.flags.items())) for v in a.variants}
        flags_b = {tuple(sorted(v.flags.items())) for v in b.variants}
        if flags_a != flags_b:
            problems.append(f"pass {name}'s variants changed")

    game_shaders = shaders_by_key(game)
    for key, shader in shaders_by_key(preset).items():
        theirs = game_shaders.get(key)
        if theirs is None:
            if key[0] in game_passes:
                problems.append(f"the game has no shader for {describe_key(key)}")
            continue
        if [input_signature(i) for i in shader.inputs] != [input_signature(i) for i in theirs.inputs]:
            problems.append(f"{describe_key(key)}: its inputs changed")
        if bgfx_signature(shader) != bgfx_signature(theirs):
            problems.append(f"{describe_key(key)}: the uniforms it is handed changed")
    return problems


def describe_changes(preset, game, preset_shaders, game_shaders):
    """What upgrading changed, in words a log line can carry."""
    changes = []
    if preset.version != game.version:
        changes.append(f"format {preset.version} to {game.version}")
    game_passes = {p.name: p for p in game.passes}
    records = 0
    for p in preset.passes:
        q = game_passes[p.name]
        # Each in its own format's encoding: re-encoding one format's platform string in another's is exactly what
        # lazurite_patched refuses to do.
        if (p.supported_platforms.get_bit_string(preset.version) != q.supported_platforms.get_bit_string(game.version)
                or p.output_binding_signature != q.output_binding_signature
                or p.fallback_pass != q.fallback_pass or p.default_blend_mode != q.default_blend_mode):
            records += 1
    if records:
        changes.append(f"{records} of {len(preset.passes)} pass records")
    rehashed = sum(1 for key, shader in preset_shaders.items() if shader.hash != game_shaders[key].hash
                   or shader.bgfx_shader.hash != game_shaders[key].bgfx_shader.hash)
    if rehashed:
        changes.append(f"{rehashed} shader record hashes")
    missing = len(game_shaders) - len(preset_shaders)
    if missing > 0:
        changes.append(f"{missing} shaders the preset leaves out taken from the game")
    if getattr(preset, "_vrs_buffer_field", True) != getattr(game, "_vrs_buffer_field", True):
        changes.append("buffer record layout")
    return changes


def upgrade_one(preset_path, vanilla_dir, out_dir):
    name = os.path.basename(preset_path)
    result = {"material": name}
    try:
        preset_raw = open(preset_path, "rb").read()
        preset = read(preset_raw)
    except Exception as e:
        result.update(case="unreadable", side="preset", reason=f"{type(e).__name__}: {e}")
        return result
    result["preset_format"] = preset.version

    try:
        game_raw = open(os.path.join(vanilla_dir, name), "rb").read()
        game = read(game_raw)
    except Exception as e:
        result.update(case="unreadable", side="game", reason=f"{type(e).__name__}: {e}")
        return result
    result["game_format"] = game.version
    try:
        if write(game) != game_raw:
            result.update(case="unreadable", side="game", reason="the game's material doesn't write back unchanged")
            return result
    except Exception as e:
        result.update(case="unreadable", side="game", reason=f"{type(e).__name__}: {e}")
        return result

    problems = interface_problems(preset, game)
    if problems:
        result.update(case="interface_changed", problems=problems[:40], problem_count=len(problems))
        return result

    preset_shaders, game_shaders = shaders_by_key(preset), shaders_by_key(game)
    vanilla_code = {key: s.bgfx_shader.shader_bytes for key, s in game_shaders.items()}
    replaced = 0
    for key, shader in preset_shaders.items():
        target = game_shaders[key]
        if target.bgfx_shader.shader_bytes != shader.bgfx_shader.shader_bytes:
            replaced += 1
        target.bgfx_shader.shader_bytes = shader.bgfx_shader.shader_bytes
    merged_raw = write(game)
    result["replaced_shaders"] = replaced

    if merged_raw == preset_raw:
        result["case"] = "current"
        return result

    # Proof: the merged file read back, with the game's bytecode put back, is the game's file byte for byte.
    try:
        proof = read(merged_raw)
        proof_shaders = shaders_by_key(proof)
        if proof_shaders.keys() != vanilla_code.keys():
            raise ValueError("the shader set changed")
        for key, code in vanilla_code.items():
            proof_shaders[key].bgfx_shader.shader_bytes = code
        if write(proof) != game_raw:
            raise ValueError("differs from the game's material beyond shader bytecode")
    except Exception as e:
        result.update(case="unreadable", side="merged", reason=f"the upgraded file failed its check: {e}")
        return result

    with open(os.path.join(out_dir, name), "wb") as f:
        f.write(merged_raw)
    result["case"] = "upgraded"
    result["changes"] = describe_changes(preset, read(game_raw), preset_shaders, shaders_by_key(read(game_raw)))
    return result


def main():
    command, args = sys.argv[2], sys.argv[3:]
    if command == "info":
        emit({"protocol": PROTOCOL, "lazurite": package_version("lazurite"),
              "formats": [Material.INITIAL_VERSION, Material.LATEST_VERSION]})
    elif command == "roundtrip":
        files = []
        for path in args:
            entry = {"path": path}
            try:
                raw = open(path, "rb").read()
                entry["ok"] = write(read(raw)) == raw
                if not entry["ok"]:
                    entry["reason"] = "written back differently"
            except Exception as e:
                entry.update(ok=False, reason=f"{type(e).__name__}: {e}")
            files.append(entry)
        emit({"protocol": PROTOCOL, "files": files})
    elif command == "upgrade":
        vanilla_dir, out_dir, preset_files = args[0], args[1], args[2:]
        os.makedirs(out_dir, exist_ok=True)
        results = []
        for path in preset_files:
            # A line on stderr as each material starts, so the app can say which one it is on.
            print(f"progress {os.path.basename(path)}", file=sys.stderr, flush=True)
            try:
                results.append(upgrade_one(path, vanilla_dir, out_dir))
            except Exception as e:
                results.append({"material": os.path.basename(path), "case": "unreadable", "side": "converter",
                                "reason": f"{type(e).__name__}: {e}"})
        emit({"protocol": PROTOCOL, "lazurite": package_version("lazurite"), "materials": results})
    else:
        fail(f"unknown command {command!r}")


if __name__ == "__main__":
    main()
