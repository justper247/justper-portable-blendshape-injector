// ============================================================
//  Portable BlendShape Injector  (v1.2.1) - inspector
//
//  Customer facing, so it answers one question by default: will
//  this work when I upload? It checks by actually generating the
//  shape, because the animation that drives it stays silent when
//  the shape is missing.
//
//  Everything technical lives under Details, for support.
// ============================================================

#if UNITY_EDITOR
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
#if VRC_SDK_VRCSDK3
using VRC.SDK3.Avatars.Components;
#endif

namespace Justper.PortableBlendShapes
{
    [CustomEditor(typeof(PortableBlendShapeInjector))]
    public sealed class PortableBlendShapeInjectorInspector : Editor
    {
        SerializedProperty dataProperty;
        SerializedProperty additionalDeformationsProperty;
        SerializedProperty operationProperty;
        SerializedProperty removedFractionProperty;
        SerializedProperty keepEmptySubMeshesProperty;
        SerializedProperty detectionModeProperty;
        SerializedProperty manualTargetProperty;
        SerializedProperty ignoreRenderersProperty;
        SerializedProperty existingShapesProperty;
        SerializedProperty loggingProperty;
        SerializedProperty useRecommendedProperty;
        SerializedProperty matchDistanceProperty;
        SerializedProperty normalAlignmentProperty;
        SerializedProperty coverageProperty;
        SerializedProperty confidenceProperty;
        SerializedProperty marginProperty;

        List<DeformationCheck> checks;
        bool checkedThisSelection;
        bool showDetails;

        /// <summary>
        /// What generating the shape on one mesh would actually produce. Detection
        /// only measures how far each sample point is from the nearest vertex,
        /// which on a reduced mesh says more about vertex spacing than about fit.
        /// </summary>
        sealed class Preflight
        {
            public string path;
            public bool alreadyPresent;
            public string error;
            public TransferTier tier;
            public string exactPathRejection;
            public float meanSurfaceGap;
            public int affectedVertices;
            public int prunedVertices;

            // Region removal. Planned rather than built, so selecting the component
            // stays instant on a body mesh carrying a hundred blendshapes.
            public bool removing;
            public bool overLimit;
            public MeshRegionRemover.Report removal;
        }

        sealed class DeformationCheck
        {
            public PortableBlendShapeData data;
            public DetectionResult analysis;
            public List<Preflight> preflight;
        }

        void OnEnable()
        {
            dataProperty = serializedObject.FindProperty("data");
            additionalDeformationsProperty = serializedObject.FindProperty("additionalDeformations");
            operationProperty = serializedObject.FindProperty("operation");
            removedFractionProperty = serializedObject.FindProperty("maximumRemovedFraction");
            keepEmptySubMeshesProperty = serializedObject.FindProperty("keepEmptySubMeshes");
            detectionModeProperty = serializedObject.FindProperty("detectionMode");
            manualTargetProperty = serializedObject.FindProperty("manualTargetRenderer");
            ignoreRenderersProperty = serializedObject.FindProperty("ignoreRenderers");
            existingShapesProperty = serializedObject.FindProperty("existingShapes");
            loggingProperty = serializedObject.FindProperty("diagnosticLogging");
            useRecommendedProperty = serializedObject.FindProperty("useRecommendedTolerances");
            matchDistanceProperty = serializedObject.FindProperty("maximumMatchDistance");
            normalAlignmentProperty = serializedObject.FindProperty("minimumNormalAlignment");
            coverageProperty = serializedObject.FindProperty("minimumDetectionCoverage");
            confidenceProperty = serializedObject.FindProperty("minimumConfidence");
            marginProperty = serializedObject.FindProperty("requiredConfidenceMargin");
            checkedThisSelection = false;
        }

        public override void OnInspectorGUI()
        {
            var injector = (PortableBlendShapeInjector)target;
            serializedObject.Update();

            // Checked once per selection rather than behind a button: a customer
            // should not have to know to press anything.
            if (!checkedThisSelection && !Application.isPlaying)
            {
                checkedThisSelection = true;
                Analyze(injector);
            }

            EditorGUILayout.LabelField("Portable BlendShape Injector  v" +
                                       PortableBlendShapeInjector.ToolVersion, EditorStyles.miniLabel);

            DrawDeformationList();

            // Not tucked under Details: which of the two things this component does
            // is the first decision, and getting it wrong is silently destructive.
            EditorGUILayout.PropertyField(operationProperty, new GUIContent("Operation"));

            DrawStatus(injector);

            EditorGUILayout.Space();
            showDetails = EditorGUILayout.Foldout(showDetails, "Details", true);
            if (showDetails) DrawDetails(injector);

            if (!serializedObject.ApplyModifiedProperties()) return;
            checkedThisSelection = false;       // settings changed, check again
        }

        void DrawDeformationList()
        {
            EditorGUILayout.LabelField("Blendshapes", EditorStyles.boldLabel);
            DrawDeformationRow(dataProperty, 1, false);

            for (int i = 0; i < additionalDeformationsProperty.arraySize; i++)
                DrawDeformationRow(additionalDeformationsProperty.GetArrayElementAtIndex(i), i + 2, true);

            if (!GUILayout.Button("Add Blendshape")) return;
            int index = additionalDeformationsProperty.arraySize;
            additionalDeformationsProperty.InsertArrayElementAtIndex(index);
            additionalDeformationsProperty.GetArrayElementAtIndex(index).objectReferenceValue = null;
        }

        void DrawDeformationRow(SerializedProperty property, int number, bool removable)
        {
            var data = property.objectReferenceValue as PortableBlendShapeData;
            string name = data != null && !string.IsNullOrEmpty(data.blendShapeName)
                ? data.blendShapeName
                : "Blendshape " + number;

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PropertyField(property, new GUIContent(name));
                if (!removable || !GUILayout.Button("-", GUILayout.Width(24f))) return;

                int index = number - 2;
                int oldSize = additionalDeformationsProperty.arraySize;
                additionalDeformationsProperty.DeleteArrayElementAtIndex(index);
                if (additionalDeformationsProperty.arraySize == oldSize)
                    additionalDeformationsProperty.DeleteArrayElementAtIndex(index);
            }
        }

        // --------------------------------------------------------
        //  Status
        // --------------------------------------------------------

        void DrawStatus(PortableBlendShapeInjector injector)
        {
            var deformations = injector.GetDeformations();
            if (deformations.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "Assign at least one deformation asset.", MessageType.Error);
                return;
            }

            foreach (var deformation in deformations)
            {
                string invalid;
                if (deformation.IsValid(out invalid)) continue;
                EditorGUILayout.HelpBox("'" + deformation.name + "': " + invalid, MessageType.Error);
                return;
            }

            if (Application.isPlaying)
            {
                EditorGUILayout.HelpBox("Not checked while the game is running.", MessageType.None);
                return;
            }

            if (FindAvatarRoot(injector) == null)
            {
                EditorGUILayout.HelpBox(
                    "Drag this prefab onto your avatar. Its " + deformations.Count +
                    (deformations.Count == 1 ? " deformation" : " deformations") +
                    " will be applied automatically when you upload.", MessageType.Info);
                return;
            }

            if (checks == null) return;
            foreach (var check in checks)
            {
                if (deformations.Count > 1)
                    EditorGUILayout.LabelField(check.data.blendShapeName, EditorStyles.boldLabel);
                DrawStatusForDeformation(injector, check.data, check.analysis, check.preflight);
            }
        }

        void DrawStatusForDeformation(PortableBlendShapeInjector injector,
            PortableBlendShapeData data, DetectionResult analysis, List<Preflight> preflight)
        {
            bool removing = injector.operation == PortableBlendShapeInjector.OperationMode.RemoveRegion;
            if (analysis == null) return;

            if (!analysis.success)
            {
                EditorGUILayout.HelpBox(FirstLine(analysis.error), MessageType.Error);
                return;
            }

            foreach (var entry in preflight)
            {
                if (string.IsNullOrEmpty(entry.error)) continue;
                EditorGUILayout.HelpBox(entry.error, MessageType.Error);
                return;
            }

            foreach (var entry in preflight)
            {
                if (!entry.overLimit) continue;
                EditorGUILayout.HelpBox(string.Format(
                    "Removing '{0}' would delete {1:0.#}% of '{2}' ({3} of {4} vertices), more than the " +
                    "{5:0.#}% this component allows. That usually means the wrong mesh was identified. " +
                    "Check the body mesh under Details before raising the limit.",
                    data.blendShapeName, entry.removal.RemovedFraction * 100f, entry.path,
                    entry.removal.removedVertexCount, entry.removal.sourceVertexCount,
                    injector.maximumRemovedFraction * 100f), MessageType.Error);
                return;
            }

            bool warn = false;
            var summary = new StringBuilder();

            if (removing)
            {
                var totals = new MeshRegionRemover.Report();
                foreach (var entry in preflight)
                {
                    if (entry.removal == null) continue;
                    totals.removedVertexCount += entry.removal.removedVertexCount;
                    totals.sourceVertexCount += entry.removal.sourceVertexCount;
                    totals.sourceTriangleCount += entry.removal.sourceTriangleCount;
                    totals.keptTriangleCount += entry.removal.keptTriangleCount;
                }

                summary.AppendFormat(
                    "Ready. When you upload, {0} vertices ({1:0.#}% of the body) and {2} triangles are " +
                    "deleted where '{3}' sits. Nothing on disk changes.",
                    totals.removedVertexCount, totals.RemovedFraction * 100f,
                    totals.sourceTriangleCount - totals.keptTriangleCount, data.blendShapeName);
            }
            else
            {
                summary.Append("Ready. '").Append(data.blendShapeName)
                    .Append("' will be added to the body when you upload.");
            }

            foreach (var entry in preflight)
            {
                if (entry.alreadyPresent)
                {
                    summary.Length = 0;
                    summary.Append("This avatar's body already has '").Append(data.blendShapeName)
                        .Append("', so it will be left as it is.");
                    continue;
                }

                if (entry.removal != null && entry.removal.emptiedSubMeshes.Count > 0 &&
                    !injector.keepEmptySubMeshes)
                {
                    warn = true;
                    summary.AppendFormat(
                        "\n\n{0} submesh(es) lose every triangle and will be dropped, which moves the " +
                        "material slots after them. Any animation that swaps a material by slot on this " +
                        "mesh needs rechecking.", entry.removal.emptiedSubMeshes.Count);
                }

                if (entry.meanSurfaceGap <= 0.002f) continue;
                warn = true;
                summary.AppendFormat(
                    "\n\nThis body's surface differs from the one the accessory was made for by about " +
                    "{0:0.#} mm. Check where the accessory meets the body.", entry.meanSurfaceGap * 1000f);
            }

            if (!string.IsNullOrEmpty(analysis.warning))
            {
                warn = true;
                summary.Append("\n\n").Append(analysis.warning);
            }

            EditorGUILayout.HelpBox(summary.ToString(), warn ? MessageType.Warning : MessageType.Info);
        }

        // --------------------------------------------------------
        //  Details
        // --------------------------------------------------------

        void DrawDetails(PortableBlendShapeInjector injector)
        {
            EditorGUI.indentLevel++;

            EditorGUILayout.PropertyField(detectionModeProperty, new GUIContent("Find Body Mesh"));
            if (injector.detectionMode == PortableBlendShapeInjector.DetectionMode.Manual)
                EditorGUILayout.PropertyField(manualTargetProperty, new GUIContent("Body Mesh"));

            if (GUILayout.Button("Check Again")) Analyze(injector);

            if (checks != null)
            {
                foreach (var check in checks) DrawCheckDetails(check, checks.Count > 1);
            }

            if (injector.operation == PortableBlendShapeInjector.OperationMode.RemoveRegion)
            {
                EditorGUILayout.PropertyField(removedFractionProperty,
                    new GUIContent("Most That May Go"));
                EditorGUILayout.PropertyField(keepEmptySubMeshesProperty,
                    new GUIContent("Keep Empty Submeshes"));
            }
            else
            {
                EditorGUILayout.PropertyField(existingShapesProperty,
                    new GUIContent("If The Shape Exists"));
            }

            EditorGUILayout.PropertyField(ignoreRenderersProperty,
                new GUIContent("Never Use These Meshes"), true);
            EditorGUILayout.PropertyField(loggingProperty, new GUIContent("Log Details"));
            EditorGUILayout.PropertyField(useRecommendedProperty,
                new GUIContent("Use Recommended Limits"));
            using (new EditorGUI.DisabledScope(useRecommendedProperty.boolValue))
            {
                EditorGUILayout.PropertyField(matchDistanceProperty, new GUIContent("Match Distance"));
                EditorGUILayout.PropertyField(normalAlignmentProperty, new GUIContent("Normal Agreement"));
            }
            EditorGUILayout.PropertyField(coverageProperty, new GUIContent("Required Coverage"));
            EditorGUILayout.PropertyField(confidenceProperty, new GUIContent("Required Confidence"));
            EditorGUILayout.PropertyField(marginProperty, new GUIContent("Required Lead"));

            EditorGUI.indentLevel--;
        }

        static void DrawCheckDetails(DeformationCheck check, bool showName)
        {
            if (check.analysis == null || !check.analysis.success || check.preflight == null) return;
            if (showName) EditorGUILayout.LabelField(check.data.blendShapeName, EditorStyles.boldLabel);

            var report = new StringBuilder();
            foreach (var entry in check.preflight)
            {
                report.Append(entry.path).Append(": ");
                if (entry.alreadyPresent)
                {
                    report.AppendLine("already has this shape, it will be left alone.");
                    continue;
                }
                if (!string.IsNullOrEmpty(entry.error))
                {
                    report.AppendLine(entry.error);
                    continue;
                }

                if (entry.tier == TransferTier.Identity || entry.tier == TransferTier.Positional)
                    report.AppendFormat("exact copy ({0}), {1} vertices move.\n",
                        entry.tier, entry.affectedVertices);
                else
                    report.AppendFormat("rebuilt onto a different mesh, {0} vertices move, " +
                                        "average surface gap {1:0.##} mm.\n",
                        entry.affectedVertices, entry.meanSurfaceGap * 1000f);

                if (entry.removal != null)
                    report.AppendFormat("  Removes {0} of {1} vertices ({2:0.#}%).\n",
                        entry.removal.removedVertexCount, entry.removal.sourceVertexCount,
                        entry.removal.RemovedFraction * 100f);
            }

            var best = check.analysis.Best;
            if (best != null)
                report.AppendFormat("Identified by shape: confidence {0:0.00}, covering {1:0.#}%.",
                    best.score, best.coverage * 100f);
            EditorGUILayout.HelpBox(report.ToString().TrimEnd(), MessageType.None);
        }

        // --------------------------------------------------------
        //  Work
        // --------------------------------------------------------

        void Analyze(PortableBlendShapeInjector injector)
        {
            checks = null;
            var deformations = injector.GetDeformations();
            if (deformations.Count == 0) return;

            var avatarRoot = FindAvatarRoot(injector);
            if (avatarRoot == null) return;

            checks = new List<DeformationCheck>();
            foreach (var data in deformations)
            {
                var check = new DeformationCheck { data = data };
                checks.Add(check);
                var analysis = AutomaticTargetDetector.Detect(injector, data, avatarRoot);
                check.analysis = analysis;
                if (!analysis.success) continue;

            var preflight = new List<Preflight>();
            check.preflight = preflight;
            bool removing = injector.operation == PortableBlendShapeInjector.OperationMode.RemoveRegion;
            var settings = new TransferSettings
            {
                maximumMatchDistance = injector.EffectiveMatchDistanceFor(data),
                minimumNormalAlignment = injector.EffectiveNormalAlignmentFor(data)
            };

            foreach (var renderer in analysis.targets)
            {
                if (renderer == null) continue;
                var entry = new Preflight
                {
                    path = AutomaticTargetDetector.BuildPath(renderer.transform, avatarRoot.transform),
                    removing = removing
                };
                preflight.Add(entry);

                var mesh = renderer.sharedMesh;
                if (!removing && mesh != null && mesh.GetBlendShapeIndex(data.blendShapeName) >= 0)
                {
                    entry.alreadyPresent = true;
                    continue;
                }
                if (mesh != null && !mesh.isReadable)
                {
                    entry.error = "The body mesh '" + mesh.name + "' has Read/Write disabled, so it " +
                                  "cannot be changed. Select the model in the Project window, tick " +
                                  "Read/Write Enabled in its Model import settings, and press Apply.";
                    continue;
                }

                string readError;
                var sample = ReferenceMeshSample.Build(renderer, analysis.referenceBone, out readError);
                if (sample == null)
                {
                    entry.error = readError;
                    continue;
                }

                var result = DeformationFieldEvaluator.Evaluate(data, sample, settings);
                if (!result.success)
                {
                    entry.error = result.error;
                    continue;
                }

                entry.error = DeformationFieldEvaluator.CheckPlausible(data, result, entry.path);
                entry.tier = result.tier;
                entry.exactPathRejection = result.exactPathRejection;
                entry.meanSurfaceGap = result.meanSurfaceDistance;
                entry.affectedVertices = result.affectedVertexCount;
                entry.prunedVertices = result.prunedVertexCount;

                if (!removing || entry.error != null) continue;

                string planError;
                var plan = MeshRegionRemover.TryPlan(mesh, result.regionMembership, out planError);
                if (plan == null)
                {
                    entry.error = planError;
                    continue;
                }

                entry.removal = plan.report;
                entry.overLimit = plan.report.RemovedFraction > injector.maximumRemovedFraction;
            }
            }
        }

        static string FirstLine(string message)
        {
            if (string.IsNullOrEmpty(message)) return "";
            int end = message.IndexOf("\nMeshes considered:", System.StringComparison.Ordinal);
            return end > 0 ? message.Substring(0, end) : message;
        }

        static GameObject FindAvatarRoot(PortableBlendShapeInjector injector)
        {
#if VRC_SDK_VRCSDK3
            var descriptor = injector.GetComponentInParent<VRCAvatarDescriptor>(true);
            if (descriptor != null) return descriptor.gameObject;
#endif
            var animator = injector.GetComponentInParent<Animator>();
            return animator != null ? animator.gameObject : null;
        }
    }

    [CustomEditor(typeof(PortableBlendShapeData))]
    public sealed class PortableBlendShapeDataInspector : Editor
    {
        public override void OnInspectorGUI()
        {
            var data = (PortableBlendShapeData)target;

            string invalid;
            if (!data.IsValid(out invalid))
            {
                EditorGUILayout.HelpBox(invalid, MessageType.Error);
                return;
            }

            EditorGUILayout.LabelField("Blendshape", data.blendShapeName);
            EditorGUILayout.LabelField("Reference Bone", data.referenceBoneName + "  (" +
                                                          data.referenceSpace + ")");
            EditorGUILayout.LabelField("Sample Points", data.controlCount.ToString());
            EditorGUILayout.LabelField("Triangles", data.patchTriangleCount.ToString());
            EditorGUILayout.LabelField("Pieces Of Geometry", data.regionCount.ToString());
            EditorGUILayout.LabelField("Frames", data.frames.Count.ToString());
            EditorGUILayout.LabelField("Largest Movement",
                (data.maximumSourceDisplacement * 1000f).ToString("0.##") + " mm");
            EditorGUILayout.LabelField("Match Distance",
                (data.recommendedMatchDistance * 1000f).ToString("0.##") + " mm");

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Baked From", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Mesh", data.sourceMeshName);
            EditorGUILayout.LabelField("Renderer", data.sourceRendererName);
            EditorGUILayout.LabelField("Moving Vertices", data.sourceAffectedVertexCount.ToString());
            EditorGUILayout.LabelField("Tool Version", data.toolVersion);
            EditorGUILayout.LabelField("Baked", data.bakeDateUtc);
            EditorGUILayout.LabelField("Content Hash", data.contentHash);
        }
    }
}
#endif
