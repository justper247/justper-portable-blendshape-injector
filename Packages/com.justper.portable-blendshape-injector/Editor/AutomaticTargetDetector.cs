// ============================================================
//  Portable BlendShape Injector  (v1.2.1) - target detection
//
//  Finds the body mesh by matching the baked surface against
//  every skinned mesh on the avatar. Names are worth almost
//  nothing; geometry decides.
//
//  Detection never modifies anything. When the result is
//  unclear the build is stopped with the full candidate list,
//  because the animation system that drives the shape fails
//  silently when the shape is missing.
// ============================================================

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace Justper.PortableBlendShapes
{
    public sealed class DetectionSettings
    {
        public float maximumMatchDistance = 0.01f;
        public float minimumNormalAlignment = 0.3f;
        public float minimumCoverage = 0.8f;
        public float minimumConfidence = 0.65f;
        public float requiredMargin = 0.1f;

        /// <summary>At or below this overlap two meshes are treated as different parts of one body.</summary>
        public float disjointOverlap = 0.15f;

        /// <summary>At or above this overlap two meshes are competing for the same surface.</summary>
        public float competingOverlap = 0.5f;
    }

    public sealed class DetectionCandidate
    {
        public SkinnedMeshRenderer renderer;
        public string path = "";
        public bool activeInHierarchy;
        public Mesh mesh;

        public float coverage;
        public float rootMeanSquareDistance;
        public float maximumDistance;
        public float normalAgreement;
        public bool skinnedToReferenceBone;
        public bool nameLooksLikeBody;
        public float score;
        public bool[] matchedControls;
        public int matchedCount;
        public string rejectedReason;
        public bool meshNotReadable;

        public string Describe()
        {
            if (!string.IsNullOrEmpty(rejectedReason))
                return path + "  -  skipped: " + rejectedReason;
            return string.Format(
                "{0}  -  confidence {1:0.00}, covers {2:0.#}% of the shape, average gap {3:0.##} mm, normals {4:0.00}{5}",
                path, score, coverage * 100f, rootMeanSquareDistance * 1000f, normalAgreement,
                meshNotReadable ? "  (Read/Write is disabled on this model)" : "");
        }
    }

    public sealed class DetectionResult
    {
        public bool success;
        public string error;
        public string warning;
        public List<SkinnedMeshRenderer> targets = new List<SkinnedMeshRenderer>();
        public List<DetectionCandidate> candidates = new List<DetectionCandidate>();
        public Transform referenceBone;
        public string referenceBoneNote = "";

        public DetectionCandidate Best
        {
            get { return candidates.Count > 0 ? candidates[0] : null; }
        }
    }

    public static class AutomaticTargetDetector
    {
        public static DetectionResult Detect(PortableBlendShapeInjector injector, GameObject avatarRoot)
        {
            return Detect(injector, injector != null ? injector.data : null, avatarRoot);
        }

        public static DetectionResult Detect(PortableBlendShapeInjector injector,
            PortableBlendShapeData data, GameObject avatarRoot)
        {
            var result = new DetectionResult();
            if (injector == null || data == null)
            {
                result.error = "No deformation asset is assigned.";
                return result;
            }
            if (avatarRoot == null)
            {
                result.error = "The injector is not inside an avatar.";
                return result;
            }

            string invalid;
            if (!data.IsValid(out invalid))
            {
                result.error = invalid;
                return result;
            }

            result.referenceBone = FindReferenceBone(avatarRoot, data.referenceBoneName,
                out result.referenceBoneNote);

            var settings = new DetectionSettings
            {
                // Detection compares each sample point to the nearest vertex, which
                // on a reduced mesh can sit further away than the surface itself.
                // Transfer keeps the tighter limit.
                maximumMatchDistance = Mathf.Max(injector.EffectiveMatchDistanceFor(data),
                    data.medianEdgeLength * 1.5f),
                minimumNormalAlignment = injector.EffectiveNormalAlignmentFor(data),
                minimumCoverage = injector.minimumDetectionCoverage,
                minimumConfidence = injector.minimumConfidence,
                requiredMargin = injector.requiredConfidenceMargin
            };

            // Manual mode still scores the chosen mesh so the inspector can warn.
            if (injector.detectionMode == PortableBlendShapeInjector.DetectionMode.Manual)
            {
                if (injector.manualTargetRenderer == null)
                {
                    result.error = "Detection is set to Manual but no body mesh is assigned.";
                    return result;
                }
                var manual = Score(injector.manualTargetRenderer, avatarRoot, data, result.referenceBone,
                    settings);
                result.candidates.Add(manual);
                if (!string.IsNullOrEmpty(manual.rejectedReason))
                {
                    result.error = "The assigned body mesh cannot be used: " + manual.rejectedReason;
                    return result;
                }
                result.targets.Add(injector.manualTargetRenderer);
                result.success = true;
                if (manual.coverage < settings.minimumCoverage)
                    result.warning = string.Format(
                        "The assigned mesh only covers {0:0.#}% of the baked shape. Check the result.",
                        manual.coverage * 100f);
                return result;
            }

            var excluded = BuildExclusionSet(injector, avatarRoot);
            var renderers = avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            foreach (var renderer in renderers)
            {
                if (renderer == null) continue;
                if (excluded.Contains(renderer)) continue;
                if (IsEditorOnly(renderer.transform, avatarRoot.transform)) continue;
                if (renderer.sharedMesh == null || renderer.sharedMesh.vertexCount == 0) continue;
                result.candidates.Add(Score(renderer, avatarRoot, data, result.referenceBone, settings));
            }

            Decide(result, settings);
            return result;
        }

        // --------------------------------------------------------
        //  Decision (pure, so the self tests can drive it directly)
        // --------------------------------------------------------

        public static void Decide(DetectionResult result, DetectionSettings settings)
        {
            var usable = new List<DetectionCandidate>();
            foreach (var candidate in result.candidates)
                if (string.IsNullOrEmpty(candidate.rejectedReason)) usable.Add(candidate);

            usable.Sort((a, b) => b.score.CompareTo(a.score));
            result.candidates.Sort((a, b) => b.score.CompareTo(a.score));

            if (usable.Count == 0)
            {
                result.error = "No mesh on this avatar could be read and scored." +
                               DescribeCandidates(result);
                return;
            }

            var best = usable[0];
            if (best.score < settings.minimumConfidence)
            {
                result.error = string.Format(
                    "No mesh on this avatar matches the baked shape well enough " +
                    "(best was {0:0.00} confidence covering {1:0.#}%, needs {2:0.00}). " +
                    "This accessory may not fit this avatar. Assign the body mesh manually to override.",
                    best.score, best.coverage * 100f, settings.minimumConfidence) +
                    DescribeCandidates(result);
                return;
            }

            var accepted = new List<DetectionCandidate> { best };
            var union = (bool[])best.matchedControls.Clone();

            // Coverage is required of the accepted set rather than of each mesh,
            // so a body split across several meshes is still recognised.
            for (int i = 1; i < usable.Count; i++)
            {
                var candidate = usable[i];
                if (candidate.score < settings.minimumConfidence) continue;

                float overlap = Overlap(candidate.matchedControls, union);

                if (overlap <= settings.disjointOverlap)
                {
                    // A body split across several meshes. Both are real targets.
                    accepted.Add(candidate);
                    for (int c = 0; c < union.Length; c++)
                        union[c] = union[c] || candidate.matchedControls[c];
                    continue;
                }

                if (overlap >= settings.competingOverlap)
                {
                    if (best.score - candidate.score >= settings.requiredMargin) continue;

                    // A spare copy of the same mesh is the one case worth resolving:
                    // identical mesh asset, exactly one of them active.
                    if (best.mesh == candidate.mesh && best.activeInHierarchy != candidate.activeInHierarchy)
                    {
                        var active = best.activeInHierarchy ? best : candidate;
                        result.warning = "Two copies of the same body mesh were found. Using the active one: " +
                                         active.path;
                        accepted[0] = active;
                        best = active;
                        continue;
                    }

                    result.error = string.Format(
                        "Two meshes match the baked shape equally well ({0:0.00} against {1:0.00}). " +
                        "Refusing to guess. Set Detection to Manual and assign the body mesh.",
                        best.score, candidate.score) + DescribeCandidates(result);
                    return;
                }

                result.error = string.Format(
                    "'{0}' partly overlaps the mesh already chosen ('{1}'), so the body cannot be " +
                    "identified safely. Set Detection to Manual and assign the body mesh.",
                    candidate.path, best.path) + DescribeCandidates(result);
                return;
            }

            int covered = 0;
            for (int i = 0; i < union.Length; i++) if (union[i]) covered++;
            float unionCoverage = union.Length > 0 ? (float)covered / union.Length : 0f;

            if (unionCoverage < settings.minimumCoverage)
            {
                result.error = string.Format(
                    "The best matching mesh only covers {0:0.#}% of the baked shape, and needs {1:0.#}%. " +
                    "This accessory may not fit this avatar. Assign the body mesh manually to override.",
                    unionCoverage * 100f, settings.minimumCoverage * 100f) + DescribeCandidates(result);
                return;
            }

            foreach (var candidate in accepted) result.targets.Add(candidate.renderer);
            result.success = true;
        }

        public static float Overlap(bool[] a, bool[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return 0f;
            int countA = 0, countB = 0, both = 0;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i]) countA++;
                if (b[i]) countB++;
                if (a[i] && b[i]) both++;
            }
            int smaller = Mathf.Min(countA, countB);
            return smaller == 0 ? 0f : (float)both / smaller;
        }

        // --------------------------------------------------------
        //  Scoring
        // --------------------------------------------------------

        public static DetectionCandidate Score(SkinnedMeshRenderer renderer, GameObject avatarRoot,
            PortableBlendShapeData data, Transform referenceBone, DetectionSettings settings)
        {
            var candidate = new DetectionCandidate
            {
                renderer = renderer,
                path = BuildPath(renderer.transform, avatarRoot.transform),
                activeInHierarchy = renderer.gameObject.activeInHierarchy && renderer.enabled,
                mesh = renderer.sharedMesh,
                meshNotReadable = renderer.sharedMesh != null && !renderer.sharedMesh.isReadable
            };

            string error;
            var sample = ReferenceMeshSample.Build(renderer, referenceBone, out error);
            if (sample == null)
            {
                candidate.rejectedReason = error;
                return candidate;
            }

            Vector3[] controls = data.GetControlPositions();
            Vector3[] controlNormals = data.GetControlNormals();
            var hash = new PointHash(sample.positions, settings.maximumMatchDistance);

            candidate.matchedControls = new bool[data.controlCount];
            double squaredSum = 0.0;
            double normalSum = 0.0;
            int matched = 0;

            for (int control = 0; control < data.controlCount; control++)
            {
                float distanceSquared;
                int vertex = hash.FindNearest(controls[control], settings.maximumMatchDistance,
                    out distanceSquared);
                if (vertex < 0) continue;

                matched++;
                candidate.matchedControls[control] = true;
                squaredSum += distanceSquared;
                float distance = Mathf.Sqrt(distanceSquared);
                if (distance > candidate.maximumDistance) candidate.maximumDistance = distance;

                Vector3 targetNormal = sample.normals[vertex];
                Vector3 controlNormal = controlNormals[control];
                if (targetNormal.sqrMagnitude > 1e-12f && controlNormal.sqrMagnitude > 1e-12f)
                    normalSum += Vector3.Dot(targetNormal.normalized, controlNormal.normalized);
            }

            candidate.matchedCount = matched;
            candidate.coverage = data.controlCount > 0 ? (float)matched / data.controlCount : 0f;
            candidate.rootMeanSquareDistance = matched > 0 ? Mathf.Sqrt((float)(squaredSum / matched)) : 0f;
            candidate.normalAgreement = matched > 0 ? (float)(normalSum / matched) : 0f;
            candidate.skinnedToReferenceBone = IsSkinnedTo(renderer, referenceBone);
            candidate.nameLooksLikeBody = LooksLikeBody(renderer.name);
            candidate.score = Confidence(candidate, settings);
            return candidate;
        }

        /// <summary>
        /// Geometry is worth 95% of the score. The name is worth 1%, which can
        /// break a tie but can never carry a mesh that does not fit.
        /// </summary>
        public static float Confidence(DetectionCandidate candidate, DetectionSettings settings)
        {
            if (candidate.matchedCount == 0) return 0f;

            float distanceScore = settings.maximumMatchDistance > 0f
                ? Mathf.Clamp01(1f - candidate.rootMeanSquareDistance / settings.maximumMatchDistance)
                : 0f;

            float alignmentRange = 1f - settings.minimumNormalAlignment;
            float normalScore = alignmentRange > 1e-4f
                ? Mathf.Clamp01((candidate.normalAgreement - settings.minimumNormalAlignment) / alignmentRange)
                : Mathf.Clamp01(candidate.normalAgreement);

            float score = 0.50f * candidate.coverage +
                          0.25f * normalScore +
                          0.20f * distanceScore;
            if (candidate.skinnedToReferenceBone) score += 0.04f;
            if (candidate.nameLooksLikeBody) score += 0.01f;
            return Mathf.Clamp01(score);
        }

        // --------------------------------------------------------
        //  Avatar inspection helpers
        // --------------------------------------------------------

        public static Transform FindReferenceBone(GameObject avatarRoot, string boneName, out string note)
        {
            note = "";
            var animator = avatarRoot.GetComponent<Animator>();
            if (animator == null) animator = avatarRoot.GetComponentInChildren<Animator>(true);

            HumanBodyBones humanBone;
            bool parsed = TryParseHumanBone(boneName, out humanBone);

            if (animator != null && animator.isHuman && parsed)
            {
                var bone = animator.GetBoneTransform(humanBone);
                if (bone != null) return bone;
                note = "The avatar rig has no " + boneName + " bone mapped.";
            }
            else if (animator == null || !animator.isHuman)
            {
                note = "The avatar has no humanoid rig, so the " + boneName + " bone was matched by name.";
            }

            foreach (var transform in avatarRoot.GetComponentsInChildren<Transform>(true))
            {
                if (string.Equals(transform.name, boneName, StringComparison.OrdinalIgnoreCase))
                    return transform;
            }

            note = "No '" + boneName + "' bone was found on this avatar. " +
                   "Matching falls back to mesh space, which is less reliable.";
            return null;
        }

        static bool TryParseHumanBone(string name, out HumanBodyBones bone)
        {
            bone = HumanBodyBones.Head;
            if (string.IsNullOrEmpty(name)) return false;
            foreach (HumanBodyBones value in Enum.GetValues(typeof(HumanBodyBones)))
            {
                if (value == HumanBodyBones.LastBone) continue;
                if (!string.Equals(value.ToString(), name, StringComparison.OrdinalIgnoreCase)) continue;
                bone = value;
                return true;
            }
            return false;
        }

        static bool IsSkinnedTo(SkinnedMeshRenderer renderer, Transform bone)
        {
            if (bone == null || renderer == null) return false;
            var bones = renderer.bones;
            if (bones == null) return false;
            foreach (var candidate in bones)
            {
                if (candidate == null) continue;
                if (candidate == bone || candidate.IsChildOf(bone)) return true;
            }
            return false;
        }

        static bool LooksLikeBody(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string lower = name.ToLowerInvariant();
            return lower.Contains("body") || lower.Contains("head") || lower.Contains("face");
        }

        static bool IsEditorOnly(Transform transform, Transform root)
        {
            for (var current = transform; current != null; current = current.parent)
            {
                if (current.CompareTag("EditorOnly")) return true;
                if (current == root) break;
            }
            return false;
        }

        static HashSet<SkinnedMeshRenderer> BuildExclusionSet(PortableBlendShapeInjector injector,
            GameObject avatarRoot)
        {
            var excluded = new HashSet<SkinnedMeshRenderer>();

            // The accessory this injector belongs to.
            foreach (var renderer in injector.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                excluded.Add(renderer);

            if (injector.ignoreRenderers != null)
                foreach (var renderer in injector.ignoreRenderers)
                    if (renderer != null) excluded.Add(renderer);

            // Anything another injector on this avatar already owns.
            foreach (var other in avatarRoot.GetComponentsInChildren<PortableBlendShapeInjector>(true))
            {
                if (other == injector) continue;
                foreach (var renderer in other.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    excluded.Add(renderer);
            }

            // Accessory meshes queued for merging are never the body.
            foreach (var renderer in CollectMergeSources(avatarRoot))
                excluded.Add(renderer);

            return excluded;
        }

        static Type mergeMarkerType;
        static bool mergeMarkerSearched;

        /// <summary>
        /// Reads BlendShape Mesh Merge source lists through reflection so this tool
        /// keeps working when that tool is not installed.
        /// </summary>
        static IEnumerable<SkinnedMeshRenderer> CollectMergeSources(GameObject avatarRoot)
        {
            var found = new List<SkinnedMeshRenderer>();
            if (!mergeMarkerSearched)
            {
                mergeMarkerSearched = true;
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { mergeMarkerType = assembly.GetType("BlendShapeMerge.BlendShapeMeshMerge", false); }
                    catch (Exception) { mergeMarkerType = null; }
                    if (mergeMarkerType != null) break;
                }
            }
            if (mergeMarkerType == null) return found;

            var field = mergeMarkerType.GetField("sourceRenderers",
                BindingFlags.Public | BindingFlags.Instance);
            if (field == null) return found;

            foreach (var component in avatarRoot.GetComponentsInChildren(mergeMarkerType, true))
            {
                var list = field.GetValue(component) as IEnumerable<SkinnedMeshRenderer>;
                if (list == null) continue;
                foreach (var renderer in list)
                    if (renderer != null) found.Add(renderer);
            }
            return found;
        }

        public static string BuildPath(Transform transform, Transform root)
        {
            var stack = new List<string>();
            for (var current = transform; current != null && current != root; current = current.parent)
                stack.Add(current.name);
            stack.Reverse();
            return stack.Count == 0 ? transform.name : string.Join("/", stack.ToArray());
        }

        static string DescribeCandidates(DetectionResult result)
        {
            if (result.candidates.Count == 0) return "";
            var builder = new StringBuilder();
            builder.AppendLine();
            builder.AppendLine("Meshes considered:");
            int shown = 0;
            foreach (var candidate in result.candidates)
            {
                builder.AppendLine("  " + candidate.Describe());
                if (++shown >= 12) break;
            }
            return builder.ToString();
        }
    }
}
#endif
