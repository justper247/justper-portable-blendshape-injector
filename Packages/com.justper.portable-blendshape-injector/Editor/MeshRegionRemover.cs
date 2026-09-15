// ============================================================
//  Portable BlendShape Injector  (v1.2.1) - region removal
//
//  Rebuilds a mesh with one region's geometry deleted.
//
//  Unlike the add-a-shape path this cannot copy the mesh with
//  Object.Instantiate, because the vertex count changes. Every
//  channel is therefore carried across by hand: all eight UV
//  channels at their original dimension, colours in their
//  original format, variable bone influences, and every frame
//  of every blendshape.
//
//  Two rules keep the result safe to upload:
//
//   - Blendshapes are always kept, even when a shape only moved
//     vertices that are now gone. Animator curves address shapes
//     by name and index, so dropping an emptied shape silently
//     breaks the FX layer that drives it.
//
//   - Submeshes are kept by default even when every triangle in
//     them is gone, because dropping one shifts every material
//     slot after it and breaks material swap animations, which
//     bind to m_Materials.Array.data[index].
// ============================================================

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace Justper.PortableBlendShapes
{
    public static class MeshRegionRemover
    {
        public sealed class Options
        {
            /// <summary>
            /// Keep submeshes that lost every triangle. Turning this off makes the
            /// mesh smaller but renumbers the material slots after the dropped one,
            /// so the caller must trim the renderer's material array to match.
            /// </summary>
            public bool keepEmptySubMeshes = true;
        }

        public sealed class Report
        {
            public int sourceVertexCount;
            public int keptVertexCount;
            public int removedVertexCount;

            public int sourceTriangleCount;
            public int keptTriangleCount;

            /// <summary>Vertices dropped because removal left them in no triangle.</summary>
            public int orphanedByRemoval;

            /// <summary>Vertices that were already in no triangle before removal.</summary>
            public int preExistingOrphans;

            /// <summary>Submesh indices that lost every triangle, in ascending order.</summary>
            public List<int> emptiedSubMeshes = new List<int>();

            /// <summary>Submesh indices actually dropped from the rebuilt mesh.</summary>
            public List<int> droppedSubMeshes = new List<int>();

            /// <summary>Blendshapes that no longer move any remaining vertex.</summary>
            public List<string> emptiedBlendShapes = new List<string>();

            public float RemovedFraction
            {
                get
                {
                    return sourceVertexCount > 0
                        ? (float)removedVertexCount / sourceVertexCount
                        : 0f;
                }
            }
        }

        /// <summary>
        /// What removal would do, worked out without building anything. Planning is
        /// a single pass over the index buffers, so the inspector can show a
        /// customer the exact result before an upload without paying for the
        /// blendshape copy that dominates the rebuild.
        /// </summary>
        public sealed class Plan
        {
            public Report report;

            internal Mesh source;
            internal int[] remap;
            internal List<int> keptVertices;
            internal List<int[]> keptIndices;
        }

        /// <summary>
        /// Works out which geometry survives removing <paramref name="remove"/> from
        /// <paramref name="source"/>. Returns null with a reason when the result
        /// would not be a usable mesh.
        /// </summary>
        public static Plan TryPlan(Mesh source, bool[] remove, out string error)
        {
            error = null;

            if (source == null)
            {
                error = "The target mesh is missing.";
                return null;
            }
            if (!source.isReadable)
            {
                error = "The mesh '" + source.name + "' cannot be read because Read/Write is " +
                        "disabled in its model import settings.";
                return null;
            }

            int sourceVertexCount = source.vertexCount;
            if (remove == null || remove.Length != sourceVertexCount)
            {
                error = "The region does not describe this mesh (" +
                        (remove == null ? 0 : remove.Length) + " flags for " + sourceVertexCount +
                        " vertices).";
                return null;
            }

            for (int sub = 0; sub < source.subMeshCount; sub++)
            {
                if (source.GetTopology(sub) == MeshTopology.Triangles) continue;
                error = "Submesh " + sub + " of '" + source.name + "' is not made of triangles, " +
                        "so geometry cannot be removed from it safely.";
                return null;
            }

            var report = new Report { sourceVertexCount = sourceVertexCount };
            var referencedBefore = new bool[sourceVertexCount];
            var referencedAfter = new bool[sourceVertexCount];
            var keptIndices = new List<int[]>(source.subMeshCount);

            for (int sub = 0; sub < source.subMeshCount; sub++)
            {
                int[] indices = source.GetIndices(sub);
                var kept = new List<int>(indices.Length);

                for (int i = 0; i + 2 < indices.Length; i += 3)
                {
                    int a = indices[i];
                    int b = indices[i + 1];
                    int c = indices[i + 2];
                    referencedBefore[a] = true;
                    referencedBefore[b] = true;
                    referencedBefore[c] = true;
                    report.sourceTriangleCount++;

                    // One removed corner takes the whole triangle. Keeping triangles
                    // that still have two corners would leave a ragged fringe of
                    // stretched geometry along the cut instead of a clean edge.
                    if (remove[a] || remove[b] || remove[c]) continue;

                    kept.Add(a);
                    kept.Add(b);
                    kept.Add(c);
                    referencedAfter[a] = true;
                    referencedAfter[b] = true;
                    referencedAfter[c] = true;
                    report.keptTriangleCount++;
                }

                keptIndices.Add(kept.ToArray());
                if (kept.Count == 0 && indices.Length > 0) report.emptiedSubMeshes.Add(sub);
            }

            var remap = new int[sourceVertexCount];
            var keptVertices = new List<int>(sourceVertexCount);
            for (int vertex = 0; vertex < sourceVertexCount; vertex++)
            {
                if (!referencedAfter[vertex])
                {
                    remap[vertex] = -1;
                    if (!referencedBefore[vertex]) report.preExistingOrphans++;
                    else if (!remove[vertex]) report.orphanedByRemoval++;
                    continue;
                }
                remap[vertex] = keptVertices.Count;
                keptVertices.Add(vertex);
            }

            report.keptVertexCount = keptVertices.Count;
            report.removedVertexCount = sourceVertexCount - keptVertices.Count;

            if (report.removedVertexCount == 0)
            {
                error = "The region does not cover any of this mesh's geometry, so nothing would " +
                        "be removed.";
                return null;
            }
            if (keptVertices.Count == 0)
            {
                error = "The region covers the whole mesh. Removing it would leave nothing behind.";
                return null;
            }

            return new Plan
            {
                report = report,
                source = source,
                remap = remap,
                keptVertices = keptVertices,
                keptIndices = keptIndices
            };
        }

        /// <summary>
        /// Returns a copy of <paramref name="source"/> with every vertex flagged in
        /// <paramref name="remove"/> deleted, along with the triangles that used it
        /// and any vertex those triangles leave behind. Returns null with a reason
        /// on failure. The source mesh is never touched.
        /// </summary>
        public static Mesh CreateWithRemovedRegion(Mesh source, bool[] remove, string meshNameSuffix,
            Options options, out Report report, out string error)
        {
            report = null;

            var plan = TryPlan(source, remove, out error);
            if (plan == null) return null;

            report = plan.report;
            return Build(plan, meshNameSuffix, options, out error);
        }

        /// <summary>Builds the mesh a <see cref="Plan"/> describes.</summary>
        public static Mesh Build(Plan plan, string meshNameSuffix, Options options, out string error)
        {
            error = null;

            if (plan == null)
            {
                error = "There is no removal plan to build.";
                return null;
            }
            if (options == null) options = new Options();

            Mesh source = plan.source;
            if (!source.isReadable)
            {
                error = "The mesh '" + source.name + "' cannot be modified because Read/Write is " +
                        "disabled in its model import settings.";
                return null;
            }

            Report report = plan.report;
            int[] remap = plan.remap;
            List<int> keptVertices = plan.keptVertices;
            List<int[]> keptIndices = plan.keptIndices;
            int keptCount = keptVertices.Count;

            var clone = new Mesh { name = source.name + meshNameSuffix };
            clone.indexFormat = keptCount > 65534 ? IndexFormat.UInt32 : source.indexFormat;

            try
            {
                CopyVertexChannels(source, clone, keptVertices);
                CopySkinning(source, clone, keptVertices);

                int subMeshCount = 0;
                var writtenIndices = new List<int[]>(keptIndices.Count);
                for (int sub = 0; sub < keptIndices.Count; sub++)
                {
                    if (keptIndices[sub].Length == 0 && !options.keepEmptySubMeshes)
                    {
                        report.droppedSubMeshes.Add(sub);
                        continue;
                    }
                    writtenIndices.Add(keptIndices[sub]);
                    subMeshCount++;
                }

                clone.subMeshCount = subMeshCount;
                for (int sub = 0; sub < writtenIndices.Count; sub++)
                {
                    int[] indices = writtenIndices[sub];
                    var remapped = new int[indices.Length];
                    for (int i = 0; i < indices.Length; i++) remapped[i] = remap[indices[i]];
                    clone.SetIndices(remapped, MeshTopology.Triangles, sub, false);
                }

                CopyBlendShapes(source, clone, keptVertices, report);

                clone.bindposes = source.bindposes;

                // Preserved rather than recalculated, matching the add-a-shape path.
                // A skinned renderer culls against its own localBounds, so a tighter
                // mesh bounds would buy nothing and would be one more difference
                // between the uploaded mesh and the one the customer authored with.
                clone.bounds = source.bounds;
            }
            catch (Exception e)
            {
                UnityEngine.Object.DestroyImmediate(clone);
                error = "The mesh '" + source.name + "' could not be rebuilt without the region (" +
                        e.Message + ").";
                return null;
            }

            if (clone.vertexCount != keptCount)
            {
                UnityEngine.Object.DestroyImmediate(clone);
                error = "The rebuilt mesh has " + clone.vertexCount + " vertices where " + keptCount +
                        " were expected.";
                return null;
            }
            if (clone.blendShapeCount != source.blendShapeCount)
            {
                UnityEngine.Object.DestroyImmediate(clone);
                error = "The rebuilt mesh kept " + clone.blendShapeCount + " of the original " +
                        source.blendShapeCount + " blendshapes. Aborting rather than breaking the " +
                        "animations that drive them.";
                return null;
            }

            return clone;
        }

        // --------------------------------------------------------
        //  Channels
        // --------------------------------------------------------

        static void CopyVertexChannels(Mesh source, Mesh clone, List<int> kept)
        {
            int keptCount = kept.Count;

            var positions = new List<Vector3>(source.vertexCount);
            source.GetVertices(positions);
            clone.SetVertices(Gather(positions, kept));

            if (source.HasVertexAttribute(VertexAttribute.Normal))
            {
                var normals = new List<Vector3>(source.vertexCount);
                source.GetNormals(normals);
                if (normals.Count == source.vertexCount) clone.SetNormals(Gather(normals, kept));
            }

            if (source.HasVertexAttribute(VertexAttribute.Tangent))
            {
                var tangents = new List<Vector4>(source.vertexCount);
                source.GetTangents(tangents);
                if (tangents.Count == source.vertexCount) clone.SetTangents(Gather(tangents, kept));
            }

            if (source.HasVertexAttribute(VertexAttribute.Color))
            {
                // Colours authored as bytes are carried as bytes, so a mesh that
                // round-trips through this tool is bit-identical in that channel.
                if (source.GetVertexAttributeFormat(VertexAttribute.Color) == VertexAttributeFormat.UNorm8)
                {
                    var colors = new List<Color32>(source.vertexCount);
                    source.GetColors(colors);
                    if (colors.Count == source.vertexCount) clone.SetColors(Gather(colors, kept));
                }
                else
                {
                    var colors = new List<Color>(source.vertexCount);
                    source.GetColors(colors);
                    if (colors.Count == source.vertexCount) clone.SetColors(Gather(colors, kept));
                }
            }

            for (int channel = 0; channel < 8; channel++)
            {
                var attribute = VertexAttribute.TexCoord0 + channel;
                if (!source.HasVertexAttribute(attribute)) continue;

                var uvs = new List<Vector4>(source.vertexCount);
                source.GetUVs(channel, uvs);
                if (uvs.Count != source.vertexCount) continue;

                var gathered = Gather(uvs, kept);

                // Written back at the dimension it was stored at. Promoting a 2D
                // channel to 4D would silently double its size in the upload.
                switch (source.GetVertexAttributeDimension(attribute))
                {
                    case 2:
                        var uv2 = new List<Vector2>(keptCount);
                        for (int i = 0; i < gathered.Count; i++)
                            uv2.Add(new Vector2(gathered[i].x, gathered[i].y));
                        clone.SetUVs(channel, uv2);
                        break;
                    case 3:
                        var uv3 = new List<Vector3>(keptCount);
                        for (int i = 0; i < gathered.Count; i++)
                            uv3.Add(new Vector3(gathered[i].x, gathered[i].y, gathered[i].z));
                        clone.SetUVs(channel, uv3);
                        break;
                    default:
                        clone.SetUVs(channel, gathered);
                        break;
                }
            }
        }

        static void CopySkinning(Mesh source, Mesh clone, List<int> kept)
        {
            var bonesPerVertex = source.GetBonesPerVertex();
            if (!bonesPerVertex.IsCreated || bonesPerVertex.Length != source.vertexCount) return;

            var allWeights = source.GetAllBoneWeights();
            if (!allWeights.IsCreated) return;

            // Influences are stored back to back, so a vertex's slice has to be
            // found by walking the counts rather than indexed directly.
            var offsets = new int[source.vertexCount];
            int running = 0;
            for (int vertex = 0; vertex < source.vertexCount; vertex++)
            {
                offsets[vertex] = running;
                running += bonesPerVertex[vertex];
            }

            int keptInfluences = 0;
            for (int i = 0; i < kept.Count; i++) keptInfluences += bonesPerVertex[kept[i]];

            var newBonesPerVertex = new NativeArray<byte>(kept.Count, Allocator.Temp);
            var newWeights = new NativeArray<BoneWeight1>(keptInfluences, Allocator.Temp);
            try
            {
                int write = 0;
                for (int i = 0; i < kept.Count; i++)
                {
                    int vertex = kept[i];
                    byte count = bonesPerVertex[vertex];
                    newBonesPerVertex[i] = count;
                    int start = offsets[vertex];
                    for (int influence = 0; influence < count; influence++)
                        newWeights[write++] = allWeights[start + influence];
                }
                clone.SetBoneWeights(newBonesPerVertex, newWeights);
            }
            finally
            {
                newBonesPerVertex.Dispose();
                newWeights.Dispose();
            }
        }

        static void CopyBlendShapes(Mesh source, Mesh clone, List<int> kept, Report report)
        {
            int shapeCount = source.blendShapeCount;
            if (shapeCount == 0) return;

            int sourceVertexCount = source.vertexCount;
            int keptCount = kept.Count;

            var deltaVertices = new Vector3[sourceVertexCount];
            var deltaNormals = new Vector3[sourceVertexCount];
            var deltaTangents = new Vector3[sourceVertexCount];

            for (int shape = 0; shape < shapeCount; shape++)
            {
                string name = source.GetBlendShapeName(shape);
                int frameCount = source.GetBlendShapeFrameCount(shape);
                bool movesAnythingKept = false;

                for (int frame = 0; frame < frameCount; frame++)
                {
                    source.GetBlendShapeFrameVertices(shape, frame, deltaVertices, deltaNormals,
                        deltaTangents);

                    var vertices = new Vector3[keptCount];
                    var normals = new Vector3[keptCount];
                    var tangents = new Vector3[keptCount];
                    bool anyNormal = false;
                    bool anyTangent = false;

                    for (int i = 0; i < keptCount; i++)
                    {
                        int vertex = kept[i];
                        vertices[i] = deltaVertices[vertex];
                        normals[i] = deltaNormals[vertex];
                        tangents[i] = deltaTangents[vertex];

                        if (vertices[i].sqrMagnitude > 0f) movesAnythingKept = true;
                        if (normals[i].sqrMagnitude > 0f) anyNormal = true;
                        if (tangents[i].sqrMagnitude > 0f) anyTangent = true;
                    }

                    // An all-zero channel means the same as an absent one, and
                    // omitting it keeps the uploaded mesh smaller.
                    clone.AddBlendShapeFrame(name, source.GetBlendShapeFrameWeight(shape, frame),
                        vertices, anyNormal ? normals : null, anyTangent ? tangents : null);
                }

                // Recorded, not removed: an FX layer still drives this shape by name.
                if (!movesAnythingKept) report.emptiedBlendShapes.Add(name);
            }
        }

        static List<T> Gather<T>(List<T> source, List<int> kept)
        {
            var output = new List<T>(kept.Count);
            for (int i = 0; i < kept.Count; i++) output.Add(source[kept[i]]);
            return output;
        }
    }
}
#endif
