// ============================================================
//  Portable BlendShape Injector  (v1.2.1) - deformation data
//
//  A compact, non-renderable record of one blendshape's effect
//  on one region of a source mesh. Stores only the affected
//  vertices plus a zero-delta boundary ring, expressed in a
//  reference-bone space so it can be re-applied to a different
//  copy of the same character.
//
//  Never stores UVs, bone weights, materials, whole-mesh
//  vertices, or references to the source asset.
// ============================================================

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Justper.PortableBlendShapes
{
    /// <summary>
    /// Baked deformation field produced by the Portable BlendShape baker.
    /// Fields are public because the baker lives in a separate editor
    /// assembly; treat everything as read-only outside of baking.
    /// </summary>
    public sealed class PortableBlendShapeData : ScriptableObject
    {
        public const int CurrentFormatVersion = 1;

        /// <summary>How reference space was derived from the source renderer.</summary>
        public enum ReferenceSpaceKind
        {
            /// <summary>Bind pose of the reference bone. Pose independent, preferred.</summary>
            BindPose = 0,

            /// <summary>Scene transforms at bake time. Depends on the authoring pose.</summary>
            CurrentTransforms = 1,

            /// <summary>Raw mesh local space. Only used when no reference bone exists.</summary>
            MeshLocal = 2
        }

        /// <summary>Control-point flag bits stored in <see cref="controlFlags"/>.</summary>
        public const byte FlagBoundary = 1;

        [Serializable]
        public sealed class Frame
        {
            [Tooltip("Blendshape frame weight, matching the source frame.")]
            public float weight = 100f;

            [HideInInspector] public byte[] deltaPositions;
            [HideInInspector] public byte[] deltaNormals;
            [HideInInspector] public byte[] deltaTangents;

            [NonSerialized] internal Vector3[] cachedPositions;
            [NonSerialized] internal Vector3[] cachedNormals;
            [NonSerialized] internal Vector3[] cachedTangents;
        }

        // --------------------------------------------------------
        //  Identity
        // --------------------------------------------------------

        public int formatVersion = CurrentFormatVersion;

        [Tooltip("Blendshape name generated on the target mesh.")]
        public string blendShapeName = "";

        [Tooltip("Humanoid bone whose bind pose defines reference space.")]
        public string referenceBoneName = "Head";

        public ReferenceSpaceKind referenceSpace = ReferenceSpaceKind.BindPose;

        // --------------------------------------------------------
        //  Field
        // --------------------------------------------------------

        public int controlCount;
        public int sourceVertexCount;
        public int regionCount;

        [Tooltip("Bounds of every control point in reference space.")]
        public Bounds referenceBounds;

        [Tooltip("Bounds of the moving (non-boundary) control points in reference space.")]
        public Bounds affectedBounds;

        [Tooltip("Suggested maximum distance between a target vertex and this surface patch.")]
        public float recommendedMatchDistance = 0.01f;

        [Tooltip("Suggested minimum dot product between target and reference surface normals.")]
        public float recommendedNormalAlignment = 0.3f;

        [HideInInspector] public byte[] controlPositions;
        [HideInInspector] public byte[] controlNormals;
        [HideInInspector] public byte[] controlSourceIndices;
        [HideInInspector] public byte[] controlFlags;
        [HideInInspector] public byte[] controlRegions;
        [HideInInspector] public byte[] patchTriangles;

        public List<Frame> frames = new List<Frame>();

        // --------------------------------------------------------
        //  Authoring statistics (diagnostics only)
        // --------------------------------------------------------

        public string toolVersion = "";
        public string bakeDateUtc = "";
        public string sourceRendererName = "";
        public string sourceMeshName = "";
        public int sourceAffectedVertexCount;
        public int patchTriangleCount;
        public float motionEpsilon;
        public int boundaryRings;
        public float medianEdgeLength;
        public float maximumSourceDisplacement;
        public string contentHash = "";

        // --------------------------------------------------------
        //  Decoded accessors
        // --------------------------------------------------------

        [NonSerialized] Vector3[] cachedControlPositions;
        [NonSerialized] Vector3[] cachedControlNormals;
        [NonSerialized] int[] cachedControlSourceIndices;
        [NonSerialized] int[] cachedPatchTriangles;

        public Vector3[] GetControlPositions()
        {
            if (cachedControlPositions == null)
                cachedControlPositions = PortableBlendShapeBlob.ToVector3(controlPositions, controlCount);
            return cachedControlPositions;
        }

        public Vector3[] GetControlNormals()
        {
            if (cachedControlNormals == null)
                cachedControlNormals = PortableBlendShapeBlob.ToVector3(controlNormals, controlCount);
            return cachedControlNormals;
        }

        public int[] GetControlSourceIndices()
        {
            if (cachedControlSourceIndices == null)
                cachedControlSourceIndices = PortableBlendShapeBlob.ToInt(controlSourceIndices);
            return cachedControlSourceIndices;
        }

        public int[] GetPatchTriangles()
        {
            if (cachedPatchTriangles == null)
                cachedPatchTriangles = PortableBlendShapeBlob.ToInt(patchTriangles);
            return cachedPatchTriangles;
        }

        public bool IsBoundary(int control)
        {
            return controlFlags != null && control >= 0 && control < controlFlags.Length &&
                   (controlFlags[control] & FlagBoundary) != 0;
        }

        public byte GetRegion(int control)
        {
            return controlRegions != null && control >= 0 && control < controlRegions.Length
                ? controlRegions[control]
                : (byte)0;
        }

        public Vector3[] GetFrameDeltaPositions(int frame)
        {
            var f = frames[frame];
            if (f.cachedPositions == null)
                f.cachedPositions = PortableBlendShapeBlob.ToVector3(f.deltaPositions, controlCount);
            return f.cachedPositions;
        }

        /// <summary>Returns null when the source blendshape had no normal deltas.</summary>
        public Vector3[] GetFrameDeltaNormals(int frame)
        {
            var f = frames[frame];
            if (f.deltaNormals == null || f.deltaNormals.Length == 0) return null;
            if (f.cachedNormals == null)
                f.cachedNormals = PortableBlendShapeBlob.ToVector3(f.deltaNormals, controlCount);
            return f.cachedNormals;
        }

        /// <summary>Returns null when the source blendshape had no tangent deltas.</summary>
        public Vector3[] GetFrameDeltaTangents(int frame)
        {
            var f = frames[frame];
            if (f.deltaTangents == null || f.deltaTangents.Length == 0) return null;
            if (f.cachedTangents == null)
                f.cachedTangents = PortableBlendShapeBlob.ToVector3(f.deltaTangents, controlCount);
            return f.cachedTangents;
        }

        public void ClearCaches()
        {
            cachedControlPositions = null;
            cachedControlNormals = null;
            cachedControlSourceIndices = null;
            cachedPatchTriangles = null;
            if (frames == null) return;
            foreach (var frame in frames)
            {
                if (frame == null) continue;
                frame.cachedPositions = null;
                frame.cachedNormals = null;
                frame.cachedTangents = null;
            }
        }

        // --------------------------------------------------------
        //  Validation
        // --------------------------------------------------------

        public bool IsValid(out string error)
        {
            error = null;

            if (formatVersion > CurrentFormatVersion)
            {
                error = "This deformation asset was baked by a newer version of the tool (format " +
                        formatVersion + "). Update Portable BlendShape Injector.";
                return false;
            }
            if (string.IsNullOrEmpty(blendShapeName))
            {
                error = "The deformation asset has no blendshape name.";
                return false;
            }
            if (controlCount <= 0)
            {
                error = "The deformation asset has no control points.";
                return false;
            }
            if (PortableBlendShapeBlob.Vector3Count(controlPositions) != controlCount ||
                PortableBlendShapeBlob.Vector3Count(controlNormals) != controlCount)
            {
                error = "The deformation asset's control point data is corrupt.";
                return false;
            }
            if (controlFlags == null || controlFlags.Length != controlCount)
            {
                error = "The deformation asset's control flags are corrupt.";
                return false;
            }
            if (frames == null || frames.Count == 0)
            {
                error = "The deformation asset has no blendshape frames.";
                return false;
            }

            float previous = float.NegativeInfinity;
            for (int i = 0; i < frames.Count; i++)
            {
                var frame = frames[i];
                if (frame == null)
                {
                    error = "Frame " + (i + 1) + " is missing.";
                    return false;
                }
                if (float.IsNaN(frame.weight) || float.IsInfinity(frame.weight))
                {
                    error = "Frame " + (i + 1) + " has an invalid weight.";
                    return false;
                }
                if (frame.weight <= previous)
                {
                    error = "Frame weights must strictly increase (frame " + (i + 1) + ").";
                    return false;
                }
                previous = frame.weight;

                if (PortableBlendShapeBlob.Vector3Count(frame.deltaPositions) != controlCount)
                {
                    error = "Frame " + (i + 1) + " has corrupt position deltas.";
                    return false;
                }
            }

            int[] triangles = GetPatchTriangles();
            if (triangles != null && triangles.Length > 0)
            {
                if (triangles.Length % 3 != 0)
                {
                    error = "The deformation asset's surface patch is corrupt.";
                    return false;
                }
                for (int i = 0; i < triangles.Length; i++)
                {
                    if (triangles[i] >= 0 && triangles[i] < controlCount) continue;
                    error = "The deformation asset's surface patch references a missing control point.";
                    return false;
                }
            }

            return true;
        }

        public bool HasSurfacePatch()
        {
            return patchTriangles != null && patchTriangles.Length >= 36;
        }
    }

    /// <summary>
    /// Little-endian float/int blob helpers. Blobs keep the asset roughly
    /// four times smaller than serialized Vector3 arrays and load faster.
    /// </summary>
    public static class PortableBlendShapeBlob
    {
        public static byte[] FromVector3(Vector3[] values)
        {
            if (values == null) return new byte[0];
            var floats = new float[values.Length * 3];
            for (int i = 0; i < values.Length; i++)
            {
                floats[i * 3] = values[i].x;
                floats[i * 3 + 1] = values[i].y;
                floats[i * 3 + 2] = values[i].z;
            }
            var bytes = new byte[floats.Length * 4];
            Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        public static Vector3[] ToVector3(byte[] blob, int expectedCount)
        {
            if (blob == null || blob.Length == 0) return new Vector3[Mathf.Max(0, expectedCount)];
            int count = blob.Length / 12;
            var floats = new float[count * 3];
            Buffer.BlockCopy(blob, 0, floats, 0, count * 12);
            var values = new Vector3[count];
            for (int i = 0; i < count; i++)
                values[i] = new Vector3(floats[i * 3], floats[i * 3 + 1], floats[i * 3 + 2]);
            return values;
        }

        public static int Vector3Count(byte[] blob)
        {
            return blob == null ? 0 : blob.Length / 12;
        }

        public static byte[] FromInt(int[] values)
        {
            if (values == null) return new byte[0];
            var bytes = new byte[values.Length * 4];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        public static int[] ToInt(byte[] blob)
        {
            if (blob == null || blob.Length == 0) return new int[0];
            var values = new int[blob.Length / 4];
            Buffer.BlockCopy(blob, 0, values, 0, values.Length * 4);
            return values;
        }
    }
}
