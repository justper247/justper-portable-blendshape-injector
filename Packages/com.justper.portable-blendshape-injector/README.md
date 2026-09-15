# Portable BlendShape Injector

Portable BlendShape Injector carries an accessory's body-correction blendshapes with the accessory and adds them to a VRChat avatar automatically.

It finds the matching body by geometry and transfers the baked shapes to compatible edited body meshes. Shapes are added only to a temporary copy during Play Mode or upload, so the avatar's original assets are never changed. Existing shapes are left as-is by default.

## Use

1. Drag the accessory prefab onto your avatar.
2. Select it and check the component's status.
3. Upload.

Add more deformation assets under **Additional Blendshapes** when one accessory needs several shapes.

- **Ready:** No action needed.
- **Warning:** Check where the accessory meets the body.
- **Error:** Follow the Inspector message. Open **Details** to select the body manually if needed.
