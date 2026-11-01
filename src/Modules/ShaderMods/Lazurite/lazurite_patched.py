"""
Runtime fixes for lazurite (0.11.0, 0.12.0) on newer game versions. Import this before using lazurite,
or run it as a drop-in for the lazurite CLI: `python scripts/lazurite_patched.py build ...`

Material format 26 (Preview 1.26.60xx) stores a 14-character supported-platforms bit string per pass,
while lazurite only knows 13 platforms for format >= 25. It silently truncated the string on write,
which corrupts every pass record and crashes the game when ray tracing is enabled.
Lazurite 0.12.0 knows the 14th platform (ESSL_320), but still pads or truncates any string of another length
(format-26 materials from before 1.26.60.28 have 13). We never change which platforms a pass supports, so the original
string is preserved verbatim.
"""

from lazurite.material.shader_pass.supported_platforms import SupportedPlatforms

_parse = SupportedPlatforms.parse_bit_string
_get = SupportedPlatforms.get_bit_string


def _parse_preserving(self, bit_string, version):
    result = _parse(self, bit_string, version)
    self._vrs_raw = (bit_string, _get(self, version))
    return result


def _get_preserving(self, version):
    current = _get(self, version)
    raw = getattr(self, "_vrs_raw", None)
    if raw is None:
        return current
    original, decoded = raw
    if current != decoded:
        raise RuntimeError(
            "A pass's supported platforms were changed, but lazurite can't encode them for this "
            f"material format (original bit string {original!r}). Update lazurite_patched.py."
        )
    return original


SupportedPlatforms.parse_bit_string = _parse_preserving
SupportedPlatforms.get_bit_string = _get_preserving


# Format 26 has two buffer record layouts under the same version number: Preview 1.26.60xx (and Release's RTXStub)
# store a 4-byte field after the binding slot (lazurite's "unknown"), Release 1.26.52xx's post-FX materials don't.
# Read with it; if that fails, read again without, and write the material back the way it was read.
import threading

from lazurite.material import Material
from lazurite.material.buffer import Buffer

_layout = threading.local()
_buffer_read = Buffer.read
_buffer_write = Buffer.write
_material_read = Material.read
_material_write = Material.write


def _buffer_version(version):
    return version if getattr(_layout, "buffer_field", True) or version < 26 else 25


def _buffer_read_layout(self, file, version):
    return _buffer_read(self, file, _buffer_version(version))


def _buffer_write_layout(self, file, version):
    return _buffer_write(self, file, _buffer_version(version))


def _material_read_layout(self, file):
    start = file.tell()
    try:
        _material_read(self, file)
        self._vrs_buffer_field = True
        return
    except Exception:
        file.seek(start)
    _layout.buffer_field = False
    try:
        _material_read(self, file)
        self._vrs_buffer_field = False
    finally:
        _layout.buffer_field = True


def _material_write_layout(self, file):
    _layout.buffer_field = getattr(self, "_vrs_buffer_field", True)
    try:
        return _material_write(self, file)
    finally:
        _layout.buffer_field = True


Buffer.read = _buffer_read_layout
Buffer.write = _buffer_write_layout
Material.read = _material_read_layout
Material.write = _material_write_layout


if __name__ == "__main__":
    from lazurite.cli import main

    main()
