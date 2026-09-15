// ============================================================
//  Portable BlendShape Injector  (v1.2.1) - build processor
//
//  Generates the baked blendshape on the wearer's body mesh, or
//  deletes the geometry that shape covers, during Play Mode
//  preview and avatar builds.
//
//  Order -21000, ahead of BlendShape Mesh Merge (-20000) and
//  the avatar build systems that read blendshape names later
//  (NDMF -11000, VRCFury -10000). Running before the merge
//  matters for both operations: afterwards the accessory's own
//  vertices sit inside the affected region, where they would be
//  moved by their own correction shape, or deleted along with
//  the body geometry they were brought in to replace.
//
//  Nothing on disk is modified. The generated mesh is a copy
//  assigned only to the temporary build avatar.
// ============================================================

#if UNITY_EDITOR && VRC_SDK_VRCSDK3
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase.Editor.BuildPipeline;
using Debug = UnityEngine.Debug;

namespace Justper.PortableBlendShapes
{
    public static class PortableBlendShapeInjectorProcessor
    {
        const string LogPrefix = "[PortableBlendShapeInjector] ";
        public const string GeneratedFolder = "Assets/PortableBlendShapeInjector.Generated";
        const string GeneratedSessionKey = "PortableBlendShapeInjector.ActiveGeneratedFolder";
        static string generatedSessionFolder;

        [InitializeOnLoadMethod]
        static void ScheduleStaleGeneratedCleanup()
        {
            EditorApplication.delayCall += CleanupStaleGeneratedFolders;
        }

        static void CleanupStaleGeneratedFolders()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += CleanupStaleGeneratedFolders;
                return;
            }
            if (Application.isPlaying || !AssetDatabase.IsValidFolder(GeneratedFolder)) return;

            string active = SessionState.GetString(GeneratedSessionKey, "");
            string prefix = GeneratedFolder + "/Build_";
            foreach (string guid in AssetDatabase.FindAssets("", new[] { GeneratedFolder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                string parent = System.IO.Path.GetDirectoryName(path);
                if (parent != null) parent = parent.Replace('\\', '/');
                if (parent != GeneratedFolder || !path.StartsWith(prefix, StringComparison.Ordinal) ||
                    path == active || !AssetDatabase.IsValidFolder(path))
                    continue;
                AssetDatabase.DeleteAsset(path);
            }
            DeleteGeneratedRootIfEmpty();
        }

        // --------------------------------------------------------
        //  Entry point
        // --------------------------------------------------------

        public static List<PortableBlendShapeInjector> GetOwnedMarkers(VRCAvatarDescriptor descriptor)
        {
            return descriptor.GetComponentsInChildren<PortableBlendShapeInjector>(true)
                .Where(marker => marker != null &&
                                 marker.GetComponentInParent<VRCAvatarDescriptor>(true) == descriptor)
                .ToList();
        }

        /// <summary>
        /// Generates every injector's blendshape on this avatar. Throws on any
        /// unrecoverable problem: the animation that drives the shape fails
        /// silently when the shape is missing, so a wrong result must never be
        /// allowed to reach an upload. Returns the number of meshes changed.
        /// </summary>
        public static int ProcessAvatar(GameObject avatarRoot, bool saveAssets)
        {
            if (avatarRoot == null) return 0;

            var descriptor = avatarRoot.GetComponent<VRCAvatarDescriptor>();
            if (descriptor == null) descriptor = avatarRoot.GetComponentInChildren<VRCAvatarDescriptor>(true);
            if (descriptor == null) return 0;

            var markers = GetOwnedMarkers(descriptor);
            if (markers.Count == 0) return 0;

            var stopwatch = Stopwatch.StartNew();
            int changed = 0;
            foreach (var marker in markers)
            {
                if (marker == null) continue;
                changed += ProcessMarker(marker, descriptor.gameObject, saveAssets);
                UnityEngine.Object.DestroyImmediate(marker);
            }

            if (saveAssets && changed > 0) AssetDatabase.SaveAssets();
            stopwatch.Stop();
            if (changed > 0)
                Debug.Log(LogPrefix + "Processed " + changed + " mesh(es) in " +
                          stopwatch.ElapsedMilliseconds + " ms.", avatarRoot);
            return changed;
        }

        static int ProcessMarker(PortableBlendShapeInjector marker, GameObject avatarRoot, bool saveAssets)
        {
            var deformations = marker.GetDeformations();
            if (deformations.Count == 0)
                throw new Exception("'" + marker.name + "' has no deformation asset assigned. " +
                                    "The accessory cannot adjust the body without it.");

            int changed = 0;
            foreach (var data in deformations)
                changed += ProcessDeformation(marker, data, avatarRoot, saveAssets);
            return changed;
        }

        static int ProcessDeformation(PortableBlendShapeInjector marker, PortableBlendShapeData data,
            GameObject avatarRoot, bool saveAssets)
        {
            string invalid;
            if (!data.IsValid(out invalid))
                throw new Exception("'" + marker.name + "' / '" + data.name + "': " + invalid);

            var detection = AutomaticTargetDetector.Detect(marker, data, avatarRoot);
            if (!detection.success)
                throw new Exception("Could not identify the body mesh for '" + data.blendShapeName +
                                    "'. " + detection.error);

            if (!string.IsNullOrEmpty(detection.warning))
                Debug.LogWarning(LogPrefix + detection.warning, marker);
            if (marker.diagnosticLogging && !string.IsNullOrEmpty(detection.referenceBoneNote))
                Debug.LogWarning(LogPrefix + detection.referenceBoneNote, marker);

            int changed = 0;
            foreach (var renderer in detection.targets)
            {
                if (InjectInto(marker, data, renderer, detection.referenceBone, avatarRoot, saveAssets))
                    changed++;
            }
            return changed;
        }

        static bool InjectInto(PortableBlendShapeInjector marker, PortableBlendShapeData data,
            SkinnedMeshRenderer renderer, Transform referenceBone, GameObject avatarRoot, bool saveAssets)
        {
            var mesh = renderer.sharedMesh;
            string path = AutomaticTargetDetector.BuildPath(renderer.transform, avatarRoot.transform);
            bool removing = marker.operation == PortableBlendShapeInjector.OperationMode.RemoveRegion;

            // Only meaningful while generating the shape. When the region is being
            // deleted, a same-named shape already on the body says nothing about
            // whether the geometry it covers is still there.
            if (!removing && HasBlendShape(mesh, data.blendShapeName))
            {
                if (marker.existingShapes == PortableBlendShapeInjector.ExistingShapePolicy.Fail)
                    throw new Exception("'" + path + "' already has a blendshape called '" +
                                        data.blendShapeName + "'. The injector is set to stop in this case.");
                Debug.Log(LogPrefix + "'" + path + "' already has '" + data.blendShapeName +
                          "'. Keeping the existing shape.", renderer);
                return false;
            }

            if (!mesh.isReadable)
                throw new Exception("The mesh '" + mesh.name + "' on '" + path + "' cannot be modified " +
                                    "because Read/Write is disabled. Select the model in the Project " +
                                    "window, enable Read/Write Enabled in its Model import settings, " +
                                    "and press Apply.");

            string error;
            var sample = ReferenceMeshSample.Build(renderer, referenceBone, out error);
            if (sample == null) throw new Exception("Could not read '" + path + "': " + error);

            if (sample.referenceSpace != data.referenceSpace && marker.diagnosticLogging)
                Debug.LogWarning(LogPrefix + "The deformation was baked in " + data.referenceSpace +
                                 " space but this avatar resolves to " + sample.referenceSpace +
                                 " space. " + sample.spaceNote, renderer);

            var settings = new TransferSettings
            {
                maximumMatchDistance = marker.EffectiveMatchDistance,
                minimumNormalAlignment = marker.EffectiveNormalAlignment
            };

            var result = DeformationFieldEvaluator.Evaluate(data, sample, settings);
            if (!result.success)
                throw new Exception("Could not generate '" + data.blendShapeName + "' on '" + path +
                                    "'. " + result.error);

            string implausible = DeformationFieldEvaluator.CheckPlausible(data, result, path);
            if (implausible != null) throw new Exception(implausible);

            MeshRegionRemover.Report removal = null;
            Mesh clone;

            if (removing) clone = BuildWithoutRegion(marker, data, mesh, result, path, out removal);
            else
            {
                string buildError;
                clone = GeneratedMeshBuilder.CreateWithAddedShape(mesh, data, result, out buildError);
                if (clone == null)
                    throw new Exception("Could not add '" + data.blendShapeName + "' to '" + path + "'. " +
                                        buildError);
            }

            // Both paths keep every existing shape at its original index, but the
            // renderer's weight array is rebuilt when the mesh changes.
            int previousShapeCount = mesh.blendShapeCount;
            var weights = new float[previousShapeCount];
            for (int i = 0; i < previousShapeCount; i++) weights[i] = renderer.GetBlendShapeWeight(i);

            if (saveAssets) SaveGeneratedMesh(clone, avatarRoot.name);
            else clone.hideFlags = HideFlags.DontSave;

            renderer.sharedMesh = clone;
            for (int i = 0; i < previousShapeCount; i++) renderer.SetBlendShapeWeight(i, weights[i]);

            if (removing)
            {
                if (removal.droppedSubMeshes.Count > 0)
                {
                    TrimMaterials(renderer, removal.droppedSubMeshes);
                    Debug.LogWarning(LogPrefix + "Removing '" + data.blendShapeName + "' emptied " +
                                     removal.droppedSubMeshes.Count + " submesh(es) on '" + path +
                                     "', so the material slots after them have moved up. Any animation " +
                                     "that swaps a material by slot index on this renderer needs " +
                                     "rechecking.", renderer);
                }
                ReportRemoval(marker, data, path, renderer, result, removal, clone);
                return true;
            }

            if (!HasBlendShape(renderer.sharedMesh, data.blendShapeName))
                throw new Exception("'" + data.blendShapeName + "' was not present on '" + path +
                                    "' after generation. Aborting rather than uploading a broken avatar.");

            if (marker.diagnosticLogging)
            {
                Debug.Log(string.Format(
                    LogPrefix + "'{0}' on '{1}': {2} match, {3} of {4} vertices moved, " +
                    "largest movement {5:0.##} mm, average surface gap {6:0.##} mm, {7} frame(s).",
                    data.blendShapeName, path, result.tier, result.affectedVertexCount,
                    clone.vertexCount, result.maximumDisplacement * 1000f,
                    result.meanSurfaceDistance * 1000f, data.frames.Count), renderer);
            }
            return true;
        }

        // --------------------------------------------------------
        //  Region removal
        // --------------------------------------------------------

        static Mesh BuildWithoutRegion(PortableBlendShapeInjector marker, PortableBlendShapeData data,
            Mesh mesh, TransferResult result, string path, out MeshRegionRemover.Report removal)
        {
            removal = null;

            if (result.regionVertexCount == 0)
                throw new Exception("The region '" + data.blendShapeName + "' did not resolve onto any " +
                                    "geometry of '" + path + "', so there is nothing to remove. The " +
                                    "deformation asset may have been baked from a different body.");

            var options = new MeshRegionRemover.Options
            {
                keepEmptySubMeshes = marker.keepEmptySubMeshes
            };

            string removeError;
            var clone = MeshRegionRemover.CreateWithRemovedRegion(mesh, result.regionMembership,
                "_Without_" + data.blendShapeName, options, out removal, out removeError);
            if (clone == null)
                throw new Exception("Could not remove '" + data.blendShapeName + "' from '" + path +
                                    "'. " + removeError);

            // A mismatched body usually fails detection outright, but when it does
            // not the damage shows up here as an implausibly large deletion. An
            // upload is the worst possible place to notice that.
            if (removal.RemovedFraction > marker.maximumRemovedFraction)
            {
                UnityEngine.Object.DestroyImmediate(clone);
                throw new Exception(string.Format(
                    "Removing '{0}' from '{1}' would delete {2:0.#}% of the mesh ({3} of {4} vertices), " +
                    "more than the {5:0.#}% this injector allows. Either this is not the right body " +
                    "mesh, or the limit needs raising deliberately.",
                    data.blendShapeName, path, removal.RemovedFraction * 100f,
                    removal.removedVertexCount, removal.sourceVertexCount,
                    marker.maximumRemovedFraction * 100f));
            }

            return clone;
        }

        static void TrimMaterials(SkinnedMeshRenderer renderer, List<int> droppedSubMeshes)
        {
            var materials = renderer.sharedMaterials;
            if (materials == null || materials.Length == 0) return;

            var dropped = new HashSet<int>(droppedSubMeshes);
            var kept = new List<Material>(materials.Length);
            for (int i = 0; i < materials.Length; i++)
                if (!dropped.Contains(i)) kept.Add(materials[i]);
            renderer.sharedMaterials = kept.ToArray();
        }

        static void ReportRemoval(PortableBlendShapeInjector marker, PortableBlendShapeData data,
            string path, SkinnedMeshRenderer renderer, TransferResult result,
            MeshRegionRemover.Report removal, Mesh clone)
        {
            if (!marker.diagnosticLogging) return;

            var message = new System.Text.StringBuilder();
            message.AppendFormat(
                LogPrefix + "Removed '{0}' from '{1}': {2} match, {3} of {4} vertices deleted " +
                "({5:0.#}%), {6} of {7} triangles kept.",
                data.blendShapeName, path, result.tier, removal.removedVertexCount,
                removal.sourceVertexCount, removal.RemovedFraction * 100f,
                removal.keptTriangleCount, removal.sourceTriangleCount);

            if (result.tier == TransferTier.Surface)
                message.AppendFormat(" Average surface gap {0:0.##} mm.",
                    result.meanSurfaceDistance * 1000f);
            if (removal.orphanedByRemoval > 0)
                message.AppendFormat(" {0} vertex(es) removed for being left in no triangle.",
                    removal.orphanedByRemoval);
            if (removal.preExistingOrphans > 0)
                message.AppendFormat(" {0} unused vertex(es) were already present and were dropped too.",
                    removal.preExistingOrphans);
            if (removal.emptiedSubMeshes.Count > 0)
                message.AppendFormat(" {0} submesh(es) lost every triangle and were {1}.",
                    removal.emptiedSubMeshes.Count,
                    marker.keepEmptySubMeshes ? "kept empty to preserve material slots" : "dropped");
            if (removal.emptiedBlendShapes.Count > 0)
                message.AppendFormat(" {0} blendshape(s) no longer move anything but were kept, " +
                                     "because animations address them by name: {1}.",
                    removal.emptiedBlendShapes.Count,
                    string.Join(", ", removal.emptiedBlendShapes.ToArray()));

            Debug.Log(message.ToString(), renderer);
        }

        static bool HasBlendShape(Mesh mesh, string name)
        {
            if (mesh == null || string.IsNullOrEmpty(name)) return false;
            return mesh.GetBlendShapeIndex(name) >= 0;
        }

        // --------------------------------------------------------
        //  Generated asset ownership
        // --------------------------------------------------------

        static string GetGeneratedSessionFolder()
        {
            if (!string.IsNullOrEmpty(generatedSessionFolder) &&
                AssetDatabase.IsValidFolder(generatedSessionFolder))
                return generatedSessionFolder;

            string recovered = SessionState.GetString(GeneratedSessionKey, "");
            if (!string.IsNullOrEmpty(recovered) &&
                recovered.StartsWith(GeneratedFolder + "/Build_", StringComparison.Ordinal) &&
                AssetDatabase.IsValidFolder(recovered))
            {
                generatedSessionFolder = recovered;
                return generatedSessionFolder;
            }

            if (!AssetDatabase.IsValidFolder(GeneratedFolder))
                AssetDatabase.CreateFolder("Assets", GeneratedFolder.Substring("Assets/".Length));

            string folderName = "Build_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            AssetDatabase.CreateFolder(GeneratedFolder, folderName);
            generatedSessionFolder = GeneratedFolder + "/" + folderName;
            SessionState.SetString(GeneratedSessionKey, generatedSessionFolder);
            return generatedSessionFolder;
        }

        static void SaveGeneratedMesh(Mesh mesh, string avatarName)
        {
            string folder = GetGeneratedSessionFolder();
            string safeName = avatarName;
            foreach (char invalid in System.IO.Path.GetInvalidFileNameChars())
                safeName = safeName.Replace(invalid, '_');
            string path = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + safeName + "_" +
                                                                mesh.name + ".asset");
            AssetDatabase.CreateAsset(mesh, path);
        }

        public static void CleanupGeneratedAssets()
        {
            string folder = generatedSessionFolder;
            if (string.IsNullOrEmpty(folder)) folder = SessionState.GetString(GeneratedSessionKey, "");

            string ownedPrefix = GeneratedFolder + "/Build_";
            if (!string.IsNullOrEmpty(folder) &&
                folder.StartsWith(ownedPrefix, StringComparison.Ordinal) &&
                AssetDatabase.IsValidFolder(folder))
                AssetDatabase.DeleteAsset(folder);

            generatedSessionFolder = null;
            SessionState.EraseString(GeneratedSessionKey);
            DeleteGeneratedRootIfEmpty();
        }

        static void DeleteGeneratedRootIfEmpty()
        {
            if (AssetDatabase.IsValidFolder(GeneratedFolder) &&
                AssetDatabase.FindAssets("", new[] { GeneratedFolder }).Length == 0)
                AssetDatabase.DeleteAsset(GeneratedFolder);
        }
    }

    // ------------------------------------------------------------
    //  Upload hooks
    // ------------------------------------------------------------

    public class PortableBlendShapeUploadHook : IVRCSDKPreprocessAvatarCallback
    {
        // Before BlendShape Mesh Merge (-20000), NDMF (-11000) and VRCFury (-10000).
        public int callbackOrder { get { return -21000; } }

        public bool OnPreprocessAvatar(GameObject avatarGameObject)
        {
            try
            {
                PortableBlendShapeInjectorProcessor.ProcessAvatar(avatarGameObject,
                    saveAssets: !Application.isPlaying);
                return true;
            }
            catch (Exception e)
            {
                PortableBlendShapeInjectorProcessor.CleanupGeneratedAssets();
                Debug.LogError("[PortableBlendShapeInjector] " + e.Message, avatarGameObject);
                Debug.LogException(e);
                return false;
            }
        }
    }

    public class PortableBlendShapeUploadCleanup : IVRCSDKPostprocessAvatarCallback
    {
        public int callbackOrder { get { return 1024; } }
        public void OnPostprocessAvatar()
        {
            PortableBlendShapeInjectorProcessor.CleanupGeneratedAssets();
        }
    }

    // ------------------------------------------------------------
    //  Play-mode hooks (Gesture Manager / Av3Emulator preview)
    // ------------------------------------------------------------

    public class PortableBlendShapePlayModeHook : IProcessSceneWithReport
    {
        // Ahead of BlendShape Mesh Merge's play-mode hook, which runs at 0.
        public int callbackOrder { get { return -1000; } }

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            if (report != null) return;             // real player build, not play mode
            if (!Application.isPlaying) return;
            foreach (var root in scene.GetRootGameObjects())
                PortableBlendShapePlayMode.ProcessHierarchy(root);
        }
    }

    [InitializeOnLoad]
    public static class PortableBlendShapePlayMode
    {
        static PortableBlendShapePlayMode()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state != PlayModeStateChange.EnteredPlayMode) return;
                // Fallback for setups where OnProcessScene did not fire. Markers
                // handled there are already destroyed, so this is a no-op then.
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    if (!scene.isLoaded) continue;
                    foreach (var root in scene.GetRootGameObjects())
                        ProcessHierarchy(root);
                }
            };
        }

        internal static void ProcessHierarchy(GameObject root)
        {
            foreach (var descriptor in root.GetComponentsInChildren<VRCAvatarDescriptor>(true))
            {
                // Do not preview avatars disabled in the scene while another
                // avatar is being tested by Gesture Manager or Av3Emulator.
                if (!descriptor.gameObject.activeInHierarchy) continue;
                if (PortableBlendShapeInjectorProcessor.GetOwnedMarkers(descriptor).Count == 0) continue;
                try
                {
                    // Unity restores Play Mode scene changes on exit, including when
                    // fast Enter Play Mode skips a full Scene Reload.
                    PortableBlendShapeInjectorProcessor.ProcessAvatar(descriptor.gameObject,
                        saveAssets: false);
                }
                catch (Exception e)
                {
                    Debug.LogError("[PortableBlendShapeInjector] Play mode preview failed: " + e.Message,
                        descriptor);
                    Debug.LogException(e);
                }
            }
        }
    }
}
#endif
