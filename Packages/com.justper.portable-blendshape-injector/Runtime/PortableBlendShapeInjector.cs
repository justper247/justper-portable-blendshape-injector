// ============================================================
//  Portable BlendShape Injector  (v1.2.3) - runtime marker
//
//  Place this next to an accessory that needs the wearer's body
//  mesh changed. During Play Mode preview and avatar builds the
//  editor processor finds the body automatically and works on a
//  temporary copy of that mesh.
//
//  Two operations share one baked deformation asset:
//
//   - Add BlendShape generates the shape on the body, for an
//     accessory that needs the body to move out of its way.
//
//   - Remove Region deletes the geometry the shape covers, for
//     an accessory that replaces a part of the body outright.
//     A shrink-to-nothing shape leaves its vertices collapsed
//     against the skin, where a PhysBone swing pushes them back
//     through the surface; deleting them is the only real fix.
//
//  The customer's FBX, importer, prefab, materials, controllers
//  and meshes are never modified.
//
//  Injection logic: Editor/PortableBlendShapeInjectorProcessor.cs
//  Removal logic:   Editor/MeshRegionRemover.cs
// ============================================================

using System.Collections.Generic;
using UnityEngine;
#if VRC_SDK_VRCSDK3
using VRC.SDKBase;
#endif

namespace Justper.PortableBlendShapes
{
    [AddComponentMenu("Justper/Portable BlendShape Injector")]
    [DisallowMultipleComponent]
    public class PortableBlendShapeInjector : MonoBehaviour
#if VRC_SDK_VRCSDK3
        , IEditorOnly
#endif
    {
        public const string ToolVersion = "1.2.3";

        public enum OperationMode
        {
            /// <summary>Generate the baked shape on the body mesh.</summary>
            AddBlendShape = 0,

            /// <summary>Delete the geometry the baked shape covers.</summary>
            RemoveRegion = 1
        }

        public enum DetectionMode
        {
            /// <summary>Find the body mesh by matching the baked surface. Recommended.</summary>
            Automatic = 0,

            /// <summary>Only use the renderer assigned below.</summary>
            Manual = 1
        }

        public enum ExistingShapePolicy
        {
            /// <summary>Leave a same-named shape that already exists on the body alone.</summary>
            UseExisting = 0,

            /// <summary>Stop the build when the body already has a same-named shape.</summary>
            Fail = 1
        }

        [Header("Deformation")]
        [Tooltip("Baked deformation asset describing the region this accessory affects.")]
        public PortableBlendShapeData data;

        [Tooltip("More baked blendshapes to process with the same target and safety settings.")]
        public List<PortableBlendShapeData> additionalDeformations = new List<PortableBlendShapeData>();

        [Tooltip("Add BlendShape generates the shape on the body. " +
                 "Remove Region deletes the geometry the shape covers.")]
        public OperationMode operation = OperationMode.AddBlendShape;

        [Header("Target")]
        [Tooltip("Automatic matches the baked surface against the avatar's meshes. " +
                 "Manual uses only the renderer below.")]
        public DetectionMode detectionMode = DetectionMode.Automatic;

        [Tooltip("Body mesh to use in Manual mode, or as a fallback if automatic detection is unclear.")]
        public SkinnedMeshRenderer manualTargetRenderer;

        [Tooltip("Meshes that must never be treated as the body.")]
        public List<SkinnedMeshRenderer> ignoreRenderers = new List<SkinnedMeshRenderer>();

        [Header("Safety")]
        [Tooltip("What to do when the body already has a blendshape with this name.")]
        public ExistingShapePolicy existingShapes = ExistingShapePolicy.UseExisting;

        [Tooltip("Print the selected mesh, match quality, and generated vertex counts during builds.")]
        public bool diagnosticLogging = true;

        [Header("Region Removal")]
        [Range(0.01f, 0.9f)]
        [Tooltip("Stop the build if removing the region would delete more than this share of the " +
                 "body mesh. A wrong match usually shows up as an implausibly large deletion, and " +
                 "an upload is the worst place to discover one.")]
        public float maximumRemovedFraction = 0.35f;

        [Tooltip("Keep submeshes that lose all of their triangles. Dropping one renumbers every " +
                 "material slot after it, which breaks material swap animations. Leave this on " +
                 "unless the avatar has no such animations and the saved slot matters.")]
        public bool keepEmptySubMeshes = true;

        [Header("Matching")]
        [Tooltip("Uses the distance and normal limits stored in the deformation asset. " +
                 "Turn off only when a specific avatar needs different limits.")]
        public bool useRecommendedTolerances = true;

        [Min(0.0001f)]
        [Tooltip("Maximum distance, in metres, between a body vertex and the baked surface.")]
        public float maximumMatchDistance = 0.01f;

        [Range(-1f, 1f)]
        [Tooltip("Minimum agreement between body and baked surface normals. " +
                 "Stops the shape from leaking onto a nearby opposite-facing surface.")]
        public float minimumNormalAlignment = 0.3f;

        [Range(0.1f, 1f)]
        [Tooltip("Fraction of the baked surface a mesh must cover before it can be treated as the body.")]
        public float minimumDetectionCoverage = 0.8f;

        [Range(0.1f, 1f)]
        [Tooltip("Minimum match confidence required before a mesh is accepted.")]
        public float minimumConfidence = 0.65f;

        [Range(0f, 0.5f)]
        [Tooltip("How far ahead of the runner-up the best match must be when both cover the same area.")]
        public float requiredConfidenceMargin = 0.1f;

        public float EffectiveMatchDistance
        {
            get
            {
                if (useRecommendedTolerances && data != null && data.recommendedMatchDistance > 0f)
                    return data.recommendedMatchDistance;
                return maximumMatchDistance;
            }
        }

        public float EffectiveMatchDistanceFor(PortableBlendShapeData deformation)
        {
            if (useRecommendedTolerances && deformation != null && deformation.recommendedMatchDistance > 0f)
                return deformation.recommendedMatchDistance;
            return maximumMatchDistance;
        }

        public float EffectiveNormalAlignment
        {
            get
            {
                if (useRecommendedTolerances && data != null)
                    return data.recommendedNormalAlignment;
                return minimumNormalAlignment;
            }
        }

        public float EffectiveNormalAlignmentFor(PortableBlendShapeData deformation)
        {
            if (useRecommendedTolerances && deformation != null)
                return deformation.recommendedNormalAlignment;
            return minimumNormalAlignment;
        }

        public List<PortableBlendShapeData> GetDeformations()
        {
            var result = new List<PortableBlendShapeData>();
            if (data != null) result.Add(data);
            if (additionalDeformations == null) return result;

            foreach (var deformation in additionalDeformations)
                if (deformation != null && !result.Contains(deformation)) result.Add(deformation);
            return result;
        }

        public string ShapeName
        {
            get { return data != null ? data.blendShapeName : ""; }
        }

        void Reset()
        {
            if (ignoreRenderers == null)
                ignoreRenderers = new List<SkinnedMeshRenderer>();
            if (additionalDeformations == null)
                additionalDeformations = new List<PortableBlendShapeData>();
        }
    }
}
