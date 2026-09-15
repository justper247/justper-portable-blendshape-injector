// ============================================================
//  Portable BlendShape Injector  (v1.2.1) - mesh copy
//
//  Copies the target mesh and appends the generated shape.
//
//  The copy is made with Object.Instantiate rather than by
//  rebuilding the mesh field by field: that keeps every UV
//  channel and its dimension, variable bone influences, extra
//  material slots, bind poses, bounds, and the index and frame
//  data of every existing blendshape exactly as they were.
// ============================================================

#if UNITY_EDITOR
using System;
using UnityEngine;

namespace Justper.PortableBlendShapes
{
    public static class GeneratedMeshBuilder
    {
        /// <summary>
        /// Returns a copy of <paramref name="source"/> with the transferred shape
        /// appended, or null with a reason. The source mesh is never touched.
        /// </summary>
        public static Mesh CreateWithAddedShape(Mesh source, PortableBlendShapeData data,
            TransferResult result, out string error)
        {
            error = null;

            if (source == null)
            {
                error = "The target mesh is missing.";
                return null;
            }
            if (!source.isReadable)
            {
                error = "The mesh '" + source.name + "' cannot be modified because Read/Write is " +
                        "disabled in its model import settings.";
                return null;
            }
            if (data == null || result == null || !result.success)
            {
                error = "There is no generated shape to add.";
                return null;
            }
            if (source.GetBlendShapeIndex(data.blendShapeName) >= 0)
            {
                error = "The mesh already has a blendshape called '" + data.blendShapeName + "'.";
                return null;
            }

            int vertexCount = source.vertexCount;
            for (int frame = 0; frame < data.frames.Count; frame++)
            {
                var positions = result.framePositions[frame];
                if (positions == null || positions.Length != vertexCount)
                {
                    error = "Generated frame " + (frame + 1) + " does not match the mesh's vertex count.";
                    return null;
                }
                for (int vertex = 0; vertex < vertexCount; vertex++)
                {
                    Vector3 delta = positions[vertex];
                    if (!float.IsNaN(delta.x) && !float.IsNaN(delta.y) && !float.IsNaN(delta.z) &&
                        !float.IsInfinity(delta.x) && !float.IsInfinity(delta.y) &&
                        !float.IsInfinity(delta.z))
                        continue;
                    error = "Generated frame " + (frame + 1) + " contains invalid numbers.";
                    return null;
                }
            }

            Mesh clone;
            try
            {
                clone = UnityEngine.Object.Instantiate(source);
            }
            catch (Exception e)
            {
                error = "The mesh '" + source.name + "' could not be copied (" + e.Message + ").";
                return null;
            }
            clone.name = source.name + "_" + data.blendShapeName;

            try
            {
                AppendFrames(clone, data, result, false);
            }
            catch (Exception first)
            {
                // Omitting the normal and tangent channels keeps the uploaded mesh
                // smaller. If this Unity version rejects the omission, fall back to
                // explicit zeroes, which mean the same thing.
                bool omitted = false;
                for (int frame = 0; frame < data.frames.Count; frame++)
                    omitted |= result.frameNormals[frame] == null || result.frameTangents[frame] == null;

                bool recovered = false;
                if (omitted)
                {
                    UnityEngine.Object.DestroyImmediate(clone);
                    clone = UnityEngine.Object.Instantiate(source);
                    clone.name = source.name + "_" + data.blendShapeName;
                    try
                    {
                        AppendFrames(clone, data, result, true);
                        recovered = true;
                    }
                    catch (Exception)
                    {
                        recovered = false;
                    }
                }

                if (!recovered)
                {
                    UnityEngine.Object.DestroyImmediate(clone);
                    error = "'" + data.blendShapeName + "' could not be added to the mesh (" +
                            first.Message + ").";
                    return null;
                }
            }

            if (clone.GetBlendShapeIndex(data.blendShapeName) < 0)
            {
                UnityEngine.Object.DestroyImmediate(clone);
                error = "'" + data.blendShapeName + "' was not present after it was added.";
                return null;
            }

            return clone;
        }

        static void AppendFrames(Mesh clone, PortableBlendShapeData data, TransferResult result,
            bool fillMissingChannels)
        {
            int vertexCount = clone.vertexCount;
            for (int frame = 0; frame < data.frames.Count; frame++)
            {
                var normals = result.frameNormals[frame];
                var tangents = result.frameTangents[frame];
                if (fillMissingChannels)
                {
                    if (normals == null) normals = new Vector3[vertexCount];
                    if (tangents == null) tangents = new Vector3[vertexCount];
                }
                clone.AddBlendShapeFrame(data.blendShapeName, data.frames[frame].weight,
                    result.framePositions[frame], normals, tangents);
            }
        }
    }
}
#endif
