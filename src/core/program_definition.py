"""
core/program_definition.py
==========================
ProgramDefinition class — describes a single named programmatic space type.

Previously part of TileDefinition. Now a standalone class.
"""

import System.Drawing as sd


class ProgramDefinition(object):
    """
    Describes a single named programmatic space type.

    Attributes
    ----------
    name        : str
    color       : System.Drawing.Color   alpha always 255
    voxel_count : int                    -1 = unlimited
    """

    def __init__(self, name, color=None, voxel_count=-1):
        if not isinstance(name, str) or not name.strip():
            raise ValueError("name must be a non-empty string.")
        self.name = name.strip()

        if color is None:
            self.color = sd.Color.FromArgb(255, 255, 255, 255)
        elif isinstance(color, sd.Color):
            self.color = sd.Color.FromArgb(255, color.R, color.G, color.B)
        else:
            raise TypeError(
                "color must be a System.Drawing.Color, got {}.".format(
                    type(color).__name__))

        vc = int(voxel_count) if voxel_count is not None else -1
        if vc == 0:
            raise ValueError(
                "voxel_count=0 means this program can never be assigned any "
                "voxels. Use -1 for unlimited or a positive integer.")
        self.voxel_count = vc

    def summary(self):
        limit = str(self.voxel_count) if self.voxel_count >= 0 else "unlimited"
        return (
            "ProgramDefinition\n"
            "  name        : {}\n"
            "  color       : R={} G={} B={}\n"
            "  voxel_count : {}"
        ).format(self.name, self.color.R, self.color.G, self.color.B, limit)

    def __repr__(self):
        limit = str(self.voxel_count) if self.voxel_count >= 0 else "unlimited"
        return "ProgramDefinition(name={!r}, color=({},{},{}), voxel_count={})".format(
            self.name, self.color.R, self.color.G, self.color.B, limit)
