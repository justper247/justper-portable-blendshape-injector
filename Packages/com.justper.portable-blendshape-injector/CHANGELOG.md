# Changelog

## 1.2.3 - 2026-09-16

- Hid technical matching thresholds from the normal Inspector.
- Automatic matching still uses the same recommended limits and safety checks.

## 1.2.2 - 2026-09-15

- Removes duplicate legacy scripts when upgrading from the embedded Unity package to VCC.
- Keeps baked deformation assets, prefabs, and creator-only authoring tools in the old folder.

## Creator tools - 2026-08-24

- Added a Tool Only Update export option that excludes baked and product assets.
- CHANGELOG.md is now always excluded from customer packages.

## 1.2.1 - 2026-08-24

- The Inspector now shows each baked blendshape name beside its deformation asset.
- Added simple Add Blendshape and remove buttons.

## 1.2.0 - 2026-08-24

- One component can now apply multiple deformation assets.
- Added an Additional Blendshapes list to the Inspector.
- Each listed blendshape is checked and matched independently.

## 1.1.0 - 2026-08-18

- The baker now chooses the reference bone from the selected blendshape's moving area.
- Removed the manual Reference Bone field.
- Added name search to the blendshape picker.
- Added Remove Region mode for accessories that replace part of the body.

## 1.0.1 - 2026-08-05

- Play Mode preview now ignores disabled avatars elsewhere in the scene.
- Inactive accessories inside the active avatar are still processed normally.

## 1.0.0 - 2026-07-31

- First release.
- Bakes one blendshape's affected region into a compact deformation asset:
  moving vertices, a zero-delta boundary ring, and the triangles between them.
  No UVs, bone weights, materials, or unrelated vertices are stored.
- Regenerates the shape on the wearer's body during Play Mode preview and at
  upload, without matching vertex count, vertex order, or topology.
- Three transfer paths, most exact first: identity by index, positional by
  coincident vertices, and closest point on the baked surface.
- Stores the shape relative to the Head bone's bind pose, so the avatar's pose,
  rotation, and scene placement do not affect the result.
- Finds the body by geometry. The mesh name is worth 1% of the score.
- Supports a body split across several meshes when each covers a different part
  of the shape.
- Discards generated movement that is not connected to the main deformed area
  over the body's own surface, so geometry that merely sits close behind it -
  teeth, a tongue, an inner mouth - is left alone. A separate piece the shape
  genuinely moves is kept, because it covers part of the baked shape that
  nothing else covers.
- Stops the build, with the full candidate list, when detection is unclear,
  when the result is implausible, or when the target mesh cannot be written.
- Keeps every existing blendshape, UV channel, bone influence, material slot,
  and bind pose by copying the mesh rather than rebuilding it.
- Runs at -21000, before BlendShape Mesh Merge. That tool, from 1.8.1, calls
  this one first so the order also holds in Play Mode.
- Self tests under Tools > Justper > Portable BlendShape Injector > Run Self Tests.
