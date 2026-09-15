// ============================================================
//  Portable BlendShape Injector  (v1.2.1) - deformation transfer
//
//  Regenerates a baked blendshape on a mesh that does not share
//  the source's vertex count, vertex order, or topology.
//
//  Three tiers, cheapest and most exact first:
//    0 Identity   - same vertex count and matching positions,
//                   deltas copied by index. Bit exact.
//    1 Positional - every control point lands on a target vertex,
//                   deltas copied by position. Bit exact, handles
//                   reordered and seam-split vertices.
//    2 Surface    - closest point on the baked surface patch with
//                   barycentric interpolation, normal filtering,
//                   and a smooth distance falloff. Handles
//                   subdivided, decimated, and sculpted bodies.
// ============================================================

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Justper.PortableBlendShapes
{
    public enum TransferTier
    {
        None = 0,
        Identity = 1,
        Positional = 2,
        Surface = 3
    }

    public sealed class TransferSettings
    {
        public float maximumMatchDistance = 0.01f;
        public float minimumNormalAlignment = 0.3f;

        /// <summary>Tier 0 acceptance distance, in metres.</summary>
        public float identityTolerance = 0.0001f;

        /// <summary>Tier 1 acceptance distance, in metres.</summary>
        public float positionalTolerance = 0.0002f;

        /// <summary>Tier 2 fade start, as a fraction of the maximum match distance.</summary>
        public float fadeStart = 0.25f;

        public bool allowIdentityTier = true;
        public bool allowPositionalTier = true;
    }

    public sealed class TransferResult
    {
        public bool success;
        public string error;
        public TransferTier tier = TransferTier.None;

        /// <summary>[frame][target vertex], in target mesh local space.</summary>
        public Vector3[][] framePositions;
        public Vector3[][] frameNormals;
        public Vector3[][] frameTangents;

        public int affectedVertexCount;
        public float maximumDisplacement;
        public float meanSurfaceDistance;
        public float controlCoverage;

        /// <summary>
        /// Per target vertex: true when the vertex belongs to the region the shape
        /// was baked over, meaning it resolved to a moving (non-boundary) control
        /// point. This is membership by association, not by how far the vertex
        /// travels, so a vertex the shape barely nudges still counts and the rim
        /// the boundary ring pins down still does not. Region removal needs that
        /// distinction; a displacement threshold cannot express it.
        /// </summary>
        public bool[] regionMembership;

        /// <summary>How many entries of <see cref="regionMembership"/> are true.</summary>
        public int regionVertexCount;

        /// <summary>Vertices dropped for sitting on a surface disconnected from the deformed one.</summary>
        public int prunedVertexCount;

        /// <summary>Hits where the vertex sat behind the surface it matched.</summary>
        public int behindSurfaceHits;

        /// <summary>Why an exact copy was not possible, when it was not.</summary>
        public string exactPathRejection;

        public static TransferResult Failed(string message)
        {
            return new TransferResult { success = false, error = message };
        }
    }

    // ------------------------------------------------------------
    //  Neutral mesh sampled into reference space
    // ------------------------------------------------------------

    public sealed class ReferenceMeshSample
    {
        public Vector3[] positions;
        public Vector3[] normals;

        /// <summary>
        /// The target's own triangles, when it can be read. Used to keep
        /// deformation from jumping onto a surface that only happens to be close,
        /// such as teeth behind a lip.
        /// </summary>
        public int[] triangles;
        public Matrix4x4 referenceFromMesh = Matrix4x4.identity;
        public Matrix4x4 meshFromReference = Matrix4x4.identity;
        public Matrix4x4 normalFromReference = Matrix4x4.identity;
        public PortableBlendShapeData.ReferenceSpaceKind referenceSpace =
            PortableBlendShapeData.ReferenceSpaceKind.MeshLocal;
        public string spaceNote = "";
        public Bounds bounds;

        public int VertexCount { get { return positions != null ? positions.Length : 0; } }

        /// <summary>
        /// Reads a renderer's neutral mesh into reference-bone space.
        /// Falls back through bind pose, live transforms, and mesh space.
        /// </summary>
        public static ReferenceMeshSample Build(SkinnedMeshRenderer renderer, Transform referenceBone,
            out string error)
        {
            error = null;
            if (renderer == null)
            {
                error = "Renderer is missing.";
                return null;
            }
            var mesh = renderer.sharedMesh;
            if (mesh == null || mesh.vertexCount == 0)
            {
                error = "Renderer '" + renderer.name + "' has no mesh.";
                return null;
            }

            Vector3[] vertices;
            Vector3[] meshNormals;
            if (!PortableMeshReader.TryReadPositionsAndNormals(mesh, out vertices, out meshNormals, out error))
                return null;

            var sample = new ReferenceMeshSample();
            if (mesh.isReadable)
            {
                try { sample.triangles = mesh.triangles; }
                catch (Exception) { sample.triangles = null; }
            }
            sample.referenceFromMesh = BuildReferenceMatrix(renderer, mesh, referenceBone,
                out sample.referenceSpace, out sample.spaceNote);
            sample.meshFromReference = sample.referenceFromMesh.inverse;
            // Normal-like vectors map with the inverse transpose of the mesh-space map.
            sample.normalFromReference = sample.referenceFromMesh.transpose;

            Matrix4x4 normalToReference = sample.referenceFromMesh.inverse.transpose;

            int count = vertices.Length;
            sample.positions = new Vector3[count];
            sample.normals = new Vector3[count];
            bool hasNormals = meshNormals != null && meshNormals.Length == count;

            for (int i = 0; i < count; i++)
            {
                sample.positions[i] = sample.referenceFromMesh.MultiplyPoint3x4(vertices[i]);
                sample.normals[i] = hasNormals
                    ? Vector3.Normalize(normalToReference.MultiplyVector(meshNormals[i]))
                    : Vector3.zero;
            }

            sample.bounds = new Bounds(sample.positions.Length > 0 ? sample.positions[0] : Vector3.zero,
                Vector3.zero);
            for (int i = 1; i < count; i++) sample.bounds.Encapsulate(sample.positions[i]);
            return sample;
        }

        /// <summary>
        /// Bind pose first: it is stored in the mesh, so it does not change when
        /// the customer poses, rotates, or scales the avatar in the scene.
        /// </summary>
        public static Matrix4x4 BuildReferenceMatrix(SkinnedMeshRenderer renderer, Mesh mesh,
            Transform referenceBone, out PortableBlendShapeData.ReferenceSpaceKind kind, out string note)
        {
            note = "";
            if (referenceBone != null && renderer != null)
            {
                var bones = renderer.bones;
                if (bones != null)
                {
                    int index = -1;
                    for (int i = 0; i < bones.Length; i++)
                    {
                        if (bones[i] != referenceBone) continue;
                        index = i;
                        break;
                    }
                    if (index >= 0)
                    {
                        Matrix4x4[] bindposes = null;
                        try { bindposes = mesh.bindposes; }
                        catch (Exception) { bindposes = null; }
                        if (bindposes != null && index < bindposes.Length)
                        {
                            kind = PortableBlendShapeData.ReferenceSpaceKind.BindPose;
                            Matrix4x4 bindPose = bindposes[index];
                            float unitCorrection;
                            bindPose = NormalizeBindPoseUnits(bindPose,
                                ReferenceUnitScale(renderer.transform, referenceBone), out unitCorrection);
                            if (Mathf.Abs(unitCorrection - 1f) > 0.001f)
                                note = "The mesh's bind pose uses different units from its avatar " +
                                       "transforms, so its scale was normalized by " +
                                       unitCorrection.ToString("0.###") + "x.";
                            return bindPose;
                        }
                    }
                }
            }

            if (referenceBone != null && renderer != null)
            {
                kind = PortableBlendShapeData.ReferenceSpaceKind.CurrentTransforms;
                note = "The reference bone does not skin this mesh, so the current scene pose was used. " +
                       "Keep the avatar in its rest pose.";
                return referenceBone.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
            }

            kind = PortableBlendShapeData.ReferenceSpaceKind.MeshLocal;
            note = "No reference bone was found, so raw mesh space was used.";
            return Matrix4x4.identity;
        }

        /// <summary>
        /// Some FBX versions encode bind poses in centimetres while their Unity
        /// transforms use metres. Preserve the pose, but bring its output units
        /// in line with the live renderer-to-bone matrix.
        /// </summary>
        public static Matrix4x4 NormalizeBindPoseUnits(Matrix4x4 bindPose, float referenceUnitScale,
            out float correction)
        {
            correction = 1f;
            if (referenceUnitScale <= 1e-8f || float.IsNaN(referenceUnitScale) ||
                float.IsInfinity(referenceUnitScale))
                return bindPose;

            // Ordinary importer and floating-point differences are left alone.
            // Unit conventions such as centimetres versus metres are not.
            if (referenceUnitScale >= 0.25f && referenceUnitScale <= 4f) return bindPose;

            correction = referenceUnitScale;
            for (int row = 0; row < 3; row++)
                for (int column = 0; column < 4; column++)
                    bindPose[row, column] *= referenceUnitScale;
            return bindPose;
        }

        static float ReferenceUnitScale(Transform renderer, Transform referenceBone)
        {
            if (renderer == null || referenceBone == null) return 1f;
            Transform common = CommonAncestor(renderer, referenceBone);
            if (common == null) return 1f;
            return AverageBasisScale(common.worldToLocalMatrix * referenceBone.localToWorldMatrix);
        }

        static Transform CommonAncestor(Transform first, Transform second)
        {
            var ancestors = new HashSet<Transform>();
            for (var current = first; current != null; current = current.parent)
                ancestors.Add(current);
            for (var current = second; current != null; current = current.parent)
                if (ancestors.Contains(current)) return current;
            return null;
        }

        static float AverageBasisScale(Matrix4x4 matrix)
        {
            float x = new Vector3(matrix.m00, matrix.m10, matrix.m20).magnitude;
            float y = new Vector3(matrix.m01, matrix.m11, matrix.m21).magnitude;
            float z = new Vector3(matrix.m02, matrix.m12, matrix.m22).magnitude;
            return (x + y + z) / 3f;
        }
    }

    // ------------------------------------------------------------
    //  Mesh reading
    // ------------------------------------------------------------

    public static class PortableMeshReader
    {
        /// <summary>
        /// Reads positions and normals even when the model's Read/Write option is
        /// off, so meshes can at least be inspected and scored. Writing a mesh
        /// still requires Read/Write.
        /// </summary>
        public static bool TryReadPositionsAndNormals(Mesh mesh, out Vector3[] positions,
            out Vector3[] normals, out string error)
        {
            positions = null;
            normals = null;
            error = null;

            if (mesh == null)
            {
                error = "Mesh is missing.";
                return false;
            }

            if (mesh.isReadable)
            {
                positions = mesh.vertices;
                normals = mesh.normals;
                return true;
            }

            try
            {
                using (var meshDataArray = MeshUtility.AcquireReadOnlyMeshData(mesh))
                {
                    var data = meshDataArray[0];
                    int count = data.vertexCount;

                    var vertexBuffer = new NativeArray<Vector3>(count, Allocator.Temp);
                    data.GetVertices(vertexBuffer);
                    positions = vertexBuffer.ToArray();
                    vertexBuffer.Dispose();

                    if (data.HasVertexAttribute(VertexAttribute.Normal))
                    {
                        var normalBuffer = new NativeArray<Vector3>(count, Allocator.Temp);
                        data.GetNormals(normalBuffer);
                        normals = normalBuffer.ToArray();
                        normalBuffer.Dispose();
                    }
                    else
                    {
                        normals = new Vector3[count];
                    }
                }
                return true;
            }
            catch (Exception e)
            {
                error = "Mesh '" + mesh.name + "' could not be read (" + e.Message + "). " +
                        "Enable Read/Write in the model's import settings.";
                return false;
            }
        }
    }

    // ------------------------------------------------------------
    //  Uniform grid for nearest-point queries
    // ------------------------------------------------------------

    public sealed class PointHash
    {
        readonly Vector3[] points;
        readonly Vector3 origin;
        readonly float cellSize;
        readonly int sizeX, sizeY, sizeZ;
        readonly int[] cellStart;
        readonly int[] entries;

        public PointHash(Vector3[] points, float preferredCellSize)
        {
            this.points = points ?? new Vector3[0];
            int count = this.points.Length;

            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int i = 0; i < count; i++)
            {
                min = Vector3.Min(min, this.points[i]);
                max = Vector3.Max(max, this.points[i]);
            }
            if (count == 0)
            {
                min = Vector3.zero;
                max = Vector3.zero;
            }

            Vector3 extent = max - min;
            float largest = Mathf.Max(extent.x, Mathf.Max(extent.y, extent.z));
            float cell = Mathf.Max(preferredCellSize, 1e-5f);
            // Keep the grid well under a few million cells for large bodies.
            float minimumCell = largest / 128f;
            if (cell < minimumCell) cell = minimumCell;
            if (cell <= 0f) cell = 1f;

            origin = min;
            cellSize = cell;
            sizeX = Mathf.Max(1, Mathf.CeilToInt(extent.x / cell) + 1);
            sizeY = Mathf.Max(1, Mathf.CeilToInt(extent.y / cell) + 1);
            sizeZ = Mathf.Max(1, Mathf.CeilToInt(extent.z / cell) + 1);

            int cellCount = sizeX * sizeY * sizeZ;
            var counts = new int[cellCount + 1];
            var cellOf = new int[count];
            for (int i = 0; i < count; i++)
            {
                int index = CellIndex(this.points[i]);
                cellOf[i] = index;
                counts[index + 1]++;
            }
            for (int i = 1; i <= cellCount; i++) counts[i] += counts[i - 1];

            cellStart = counts;
            entries = new int[count];
            var cursor = new int[cellCount];
            for (int i = 0; i < count; i++)
            {
                int index = cellOf[i];
                entries[cellStart[index] + cursor[index]] = i;
                cursor[index]++;
            }
        }

        int Clamp(int value, int size)
        {
            return value < 0 ? 0 : (value >= size ? size - 1 : value);
        }

        int CellIndex(Vector3 point)
        {
            int x = Clamp(Mathf.FloorToInt((point.x - origin.x) / cellSize), sizeX);
            int y = Clamp(Mathf.FloorToInt((point.y - origin.y) / cellSize), sizeY);
            int z = Clamp(Mathf.FloorToInt((point.z - origin.z) / cellSize), sizeZ);
            return (z * sizeY + y) * sizeX + x;
        }

        /// <summary>Nearest stored point within maximumDistance, or -1.</summary>
        public int FindNearest(Vector3 point, float maximumDistance, out float distanceSquared)
        {
            distanceSquared = maximumDistance * maximumDistance;
            int best = -1;
            if (points.Length == 0) return -1;

            int rings = Mathf.Max(1, Mathf.CeilToInt(maximumDistance / cellSize));
            int cx = Clamp(Mathf.FloorToInt((point.x - origin.x) / cellSize), sizeX);
            int cy = Clamp(Mathf.FloorToInt((point.y - origin.y) / cellSize), sizeY);
            int cz = Clamp(Mathf.FloorToInt((point.z - origin.z) / cellSize), sizeZ);

            for (int z = cz - rings; z <= cz + rings; z++)
            {
                if (z < 0 || z >= sizeZ) continue;
                for (int y = cy - rings; y <= cy + rings; y++)
                {
                    if (y < 0 || y >= sizeY) continue;
                    for (int x = cx - rings; x <= cx + rings; x++)
                    {
                        if (x < 0 || x >= sizeX) continue;
                        int cell = (z * sizeY + y) * sizeX + x;
                        int start = cellStart[cell];
                        int end = cellStart[cell + 1];
                        for (int i = start; i < end; i++)
                        {
                            int candidate = entries[i];
                            float d = (points[candidate] - point).sqrMagnitude;
                            if (d >= distanceSquared) continue;
                            distanceSquared = d;
                            best = candidate;
                        }
                    }
                }
            }
            return best;
        }
    }

    // ------------------------------------------------------------
    //  Surface patch acceleration structure
    // ------------------------------------------------------------

    public sealed class PatchBvh
    {
        public struct Hit
        {
            public int triangle;
            public Vector3 barycentric;
            public Vector3 surfaceNormal;
            public float distanceSquared;
        }

        sealed class Node
        {
            public Bounds bounds;
            public int start;
            public int count;
            public Node left;
            public Node right;
        }

        readonly Vector3[] vertices;
        readonly Vector3[] normals;
        readonly int[] triangles;
        readonly int[] order;
        readonly Vector3[] centroids;
        readonly Bounds[] triangleBounds;
        readonly Node root;

        public int TriangleCount { get { return triangles.Length / 3; } }

        public PatchBvh(Vector3[] vertices, Vector3[] normals, int[] triangles)
        {
            this.vertices = vertices;
            this.normals = normals;
            this.triangles = triangles;

            int count = triangles.Length / 3;
            order = new int[count];
            centroids = new Vector3[count];
            triangleBounds = new Bounds[count];
            for (int triangle = 0; triangle < count; triangle++)
            {
                order[triangle] = triangle;
                int offset = triangle * 3;
                Vector3 a = vertices[triangles[offset]];
                Vector3 b = vertices[triangles[offset + 1]];
                Vector3 c = vertices[triangles[offset + 2]];
                var bounds = new Bounds(a, Vector3.zero);
                bounds.Encapsulate(b);
                bounds.Encapsulate(c);
                triangleBounds[triangle] = bounds;
                centroids[triangle] = (a + b + c) / 3f;
            }
            root = count > 0 ? Build(0, count) : null;
        }

        Node Build(int start, int count)
        {
            var node = new Node { start = start, count = count };
            Bounds bounds = triangleBounds[order[start]];
            var centroidBounds = new Bounds(centroids[order[start]], Vector3.zero);
            for (int i = 1; i < count; i++)
            {
                int triangle = order[start + i];
                bounds.Encapsulate(triangleBounds[triangle]);
                centroidBounds.Encapsulate(centroids[triangle]);
            }
            node.bounds = bounds;
            if (count <= 8) return node;

            Vector3 size = centroidBounds.size;
            int axis = size.x >= size.y && size.x >= size.z ? 0 : (size.y >= size.z ? 1 : 2);
            Array.Sort(order, start, count, new CentroidComparer(centroids, axis));
            int leftCount = count / 2;
            node.left = Build(start, leftCount);
            node.right = Build(start + leftCount, count - leftCount);
            return node;
        }

        sealed class CentroidComparer : IComparer<int>
        {
            readonly Vector3[] centroids;
            readonly int axis;

            public CentroidComparer(Vector3[] centroids, int axis)
            {
                this.centroids = centroids;
                this.axis = axis;
            }

            public int Compare(int a, int b)
            {
                float left = centroids[a][axis];
                float right = centroids[b][axis];
                return left.CompareTo(right);
            }
        }

        /// <summary>
        /// Closest point on the patch whose interpolated surface normal agrees
        /// with the query normal. The normal test runs inside the search so a
        /// rejected surface never wins over a valid one further away.
        /// </summary>
        public bool FindClosest(Vector3 point, Vector3 pointNormal, float maximumDistanceSquared,
            float minimumNormalAlignment, out Hit hit)
        {
            hit = new Hit { triangle = -1, distanceSquared = maximumDistanceSquared };
            if (root == null) return false;
            Search(root, point, pointNormal, minimumNormalAlignment, ref hit);
            return hit.triangle >= 0;
        }

        void Search(Node node, Vector3 point, Vector3 pointNormal, float minimumAlignment, ref Hit best)
        {
            if (node == null || node.bounds.SqrDistance(point) >= best.distanceSquared) return;

            if (node.left == null)
            {
                for (int i = 0; i < node.count; i++)
                {
                    int triangle = order[node.start + i];
                    int offset = triangle * 3;
                    int ia = triangles[offset];
                    int ib = triangles[offset + 1];
                    int ic = triangles[offset + 2];
                    Vector3 a = vertices[ia];
                    Vector3 b = vertices[ib];
                    Vector3 c = vertices[ic];

                    Vector3 barycentric;
                    Vector3 closest = ClosestPointOnTriangle(point, a, b, c, out barycentric);
                    float distance = (closest - point).sqrMagnitude;
                    if (distance >= best.distanceSquared) continue;

                    Vector3 interpolated = normals[ia] * barycentric.x +
                                           normals[ib] * barycentric.y +
                                           normals[ic] * barycentric.z;
                    if (interpolated.sqrMagnitude < 1e-12f)
                    {
                        interpolated = Vector3.Cross(b - a, c - a);
                        if (interpolated.sqrMagnitude < 1e-14f) continue;
                    }
                    interpolated = interpolated.normalized;

                    if (minimumAlignment > -1f && pointNormal.sqrMagnitude > 1e-12f &&
                        Vector3.Dot(pointNormal.normalized, interpolated) < minimumAlignment)
                        continue;

                    best.triangle = triangle;
                    best.barycentric = barycentric;
                    best.surfaceNormal = interpolated;
                    best.distanceSquared = distance;
                }
                return;
            }

            float leftDistance = node.left.bounds.SqrDistance(point);
            float rightDistance = node.right.bounds.SqrDistance(point);
            if (leftDistance <= rightDistance)
            {
                Search(node.left, point, pointNormal, minimumAlignment, ref best);
                Search(node.right, point, pointNormal, minimumAlignment, ref best);
            }
            else
            {
                Search(node.right, point, pointNormal, minimumAlignment, ref best);
                Search(node.left, point, pointNormal, minimumAlignment, ref best);
            }
        }

        public static Vector3 ClosestPointOnTriangle(Vector3 point, Vector3 a, Vector3 b, Vector3 c,
            out Vector3 barycentric)
        {
            Vector3 ab = b - a;
            Vector3 ac = c - a;
            Vector3 ap = point - a;

            float d1 = Vector3.Dot(ab, ap);
            float d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f)
            {
                barycentric = new Vector3(1f, 0f, 0f);
                return a;
            }

            Vector3 bp = point - b;
            float d3 = Vector3.Dot(ab, bp);
            float d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3)
            {
                barycentric = new Vector3(0f, 1f, 0f);
                return b;
            }

            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f)
            {
                float v = d1 / (d1 - d3);
                barycentric = new Vector3(1f - v, v, 0f);
                return a + ab * v;
            }

            Vector3 cp = point - c;
            float d5 = Vector3.Dot(ab, cp);
            float d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6)
            {
                barycentric = new Vector3(0f, 0f, 1f);
                return c;
            }

            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f)
            {
                float w = d2 / (d2 - d6);
                barycentric = new Vector3(1f - w, 0f, w);
                return a + ac * w;
            }

            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f)
            {
                float w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
                barycentric = new Vector3(0f, 1f - w, w);
                return b + (c - b) * w;
            }

            float denominator = va + vb + vc;
            if (Mathf.Abs(denominator) < 1e-20f)
            {
                barycentric = new Vector3(1f, 0f, 0f);
                return a;
            }
            float inverse = 1f / denominator;
            float vv = vb * inverse;
            float ww = vc * inverse;
            barycentric = new Vector3(1f - vv - ww, vv, ww);
            return a + ab * vv + ac * ww;
        }
    }

    // ------------------------------------------------------------
    //  Transfer
    // ------------------------------------------------------------

    public static class DeformationFieldEvaluator
    {
        public static TransferResult Evaluate(PortableBlendShapeData data, ReferenceMeshSample target,
            TransferSettings settings)
        {
            if (data == null) return TransferResult.Failed("No deformation asset assigned.");
            if (target == null || target.VertexCount == 0)
                return TransferResult.Failed("The target mesh could not be read.");

            string invalid;
            if (!data.IsValid(out invalid)) return TransferResult.Failed(invalid);
            if (settings == null) settings = new TransferSettings();

            int frameCount = data.frames.Count;
            var result = new TransferResult
            {
                framePositions = new Vector3[frameCount][],
                frameNormals = new Vector3[frameCount][],
                frameTangents = new Vector3[frameCount][]
            };

            if (settings.allowIdentityTier && TryIdentity(data, target, settings, result)) { }
            else if (settings.allowPositionalTier && TryPositional(data, target, settings, result)) { }
            else if (!TrySurface(data, target, settings, result)) return result;

            Summarize(result, target.VertexCount);
            result.success = true;
            return result;
        }

        /// <summary>
        /// Last safety net for a mesh that scored well but is not the body.
        /// Detection compares surfaces; this compares the result against what the
        /// shape did on the mesh it was baked from. Returns null when the result
        /// looks sane, otherwise the reason it does not.
        /// </summary>
        public static string CheckPlausible(PortableBlendShapeData data, TransferResult result, string path)
        {
            if (result == null || !result.success) return "The shape could not be generated.";

            if (result.affectedVertexCount == 0)
                return "The generated '" + data.blendShapeName + "' on '" + path +
                       "' would not move a single vertex.";

            if (data.maximumSourceDisplacement > 0f &&
                result.maximumDisplacement > data.maximumSourceDisplacement * 2f + 0.001f)
                return string.Format(
                    "The generated '{0}' on '{1}' moves vertices up to {2:0.##} mm, far more than the " +
                    "{3:0.##} mm in the original shape. This mesh is probably not the right body, or it " +
                    "uses a different scale.",
                    data.blendShapeName, path, result.maximumDisplacement * 1000f,
                    data.maximumSourceDisplacement * 1000f);

            if (result.tier == TransferTier.Surface && result.controlCoverage < 0.5f)
                return string.Format(
                    "Only {0:0.#}% of the baked shape reached '{1}'. The body mesh is too different " +
                    "from the one this accessory was made for.",
                    result.controlCoverage * 100f, path);

            return null;
        }

        // --- Tier 0 ------------------------------------------------

        static bool TryIdentity(PortableBlendShapeData data, ReferenceMeshSample target,
            TransferSettings settings, TransferResult result)
        {
            if (data.sourceVertexCount != target.VertexCount)
            {
                Reject(result, string.Format(
                    "this mesh has {0} vertices, the model the shape was baked from had {1}",
                    target.VertexCount, data.sourceVertexCount));
                return false;
            }

            int[] indices = data.GetControlSourceIndices();
            if (indices == null || indices.Length != data.controlCount) return false;

            Vector3[] controls = data.GetControlPositions();
            float tolerance = settings.identityTolerance * settings.identityTolerance;
            for (int i = 0; i < indices.Length; i++)
            {
                int index = indices[i];
                if (index < 0 || index >= target.VertexCount) return false;
                if ((target.positions[index] - controls[i]).sqrMagnitude <= tolerance) continue;
                Reject(result, "the vertices are in a different order than the baked model's");
                return false;
            }

            for (int frame = 0; frame < data.frames.Count; frame++)
            {
                result.framePositions[frame] = ScatterByIndex(data.GetFrameDeltaPositions(frame), indices,
                    target.VertexCount, target.meshFromReference);
                result.frameNormals[frame] = ScatterByIndex(data.GetFrameDeltaNormals(frame), indices,
                    target.VertexCount, target.normalFromReference);
                result.frameTangents[frame] = ScatterByIndex(data.GetFrameDeltaTangents(frame), indices,
                    target.VertexCount, target.normalFromReference);
            }

            var membership = new bool[target.VertexCount];
            for (int i = 0; i < indices.Length; i++)
                if (!data.IsBoundary(i)) membership[indices[i]] = true;
            result.regionMembership = membership;

            result.tier = TransferTier.Identity;
            result.controlCoverage = 1f;
            result.meanSurfaceDistance = 0f;
            return true;
        }

        static Vector3[] ScatterByIndex(Vector3[] deltas, int[] indices, int vertexCount, Matrix4x4 toMesh)
        {
            if (deltas == null) return null;
            var output = new Vector3[vertexCount];
            for (int i = 0; i < indices.Length; i++)
                output[indices[i]] = toMesh.MultiplyVector(deltas[i]);
            return output;
        }

        // --- Tier 1 ------------------------------------------------

        static bool TryPositional(PortableBlendShapeData data, ReferenceMeshSample target,
            TransferSettings settings, TransferResult result)
        {
            Vector3[] controls = data.GetControlPositions();
            float tolerance = Mathf.Max(settings.positionalTolerance, 1e-6f);
            float neighbourhood = Mathf.Max(data.medianEdgeLength * 0.9f, tolerance * 4f);

            var controlHash = new PointHash(controls, Mathf.Max(tolerance * 4f, neighbourhood));
            var matchedControl = new int[target.VertexCount];
            var unmatchedNearby = new List<int>();

            Bounds nearPatch = data.referenceBounds;
            nearPatch.Expand(Mathf.Max(neighbourhood, 0.001f) * 2f);

            for (int vertex = 0; vertex < target.VertexCount; vertex++)
            {
                float distanceSquared;
                int control = controlHash.FindNearest(target.positions[vertex], tolerance,
                    out distanceSquared);
                matchedControl[vertex] = control;
                if (control < 0 && nearPatch.Contains(target.positions[vertex]))
                    unmatchedNearby.Add(vertex);
            }

            // Every moving control needs somewhere to put its movement. Asked from
            // the control side, because a UV seam splits one surface point into
            // several controls and a single vertex there satisfies all of them.
            var targetHash = new PointHash(target.positions,
                Mathf.Max(data.medianEdgeLength * 0.5f, tolerance * 4f));
            float search = Mathf.Max(data.medianEdgeLength * 4f, 0.01f);
            int missing = 0;
            float worstGap = 0f;

            for (int control = 0; control < data.controlCount; control++)
            {
                if (data.IsBoundary(control)) continue;
                float distanceSquared;
                if (targetHash.FindNearest(controls[control], tolerance, out distanceSquared) >= 0)
                    continue;

                missing++;
                int nearest = targetHash.FindNearest(controls[control], search, out distanceSquared);
                float gap = nearest >= 0 ? Mathf.Sqrt(distanceSquared) : search;
                if (gap > worstGap) worstGap = gap;
            }

            if (missing > 0)
            {
                // How far off they are decides whether this is a near miss worth
                // loosening the limit for, or a genuinely different mesh.
                Reject(result, string.Format(
                    "{0} of the shape's {1} moving sample points have no vertex within {2:0.##} mm on " +
                    "this mesh (the furthest is {3:0.##} mm away), so its surface is not the same one " +
                    "the shape was baked on",
                    missing, data.controlCount, tolerance * 1000f, worstGap * 1000f));
                return false;
            }

            int split;
            if (!CoincidentControlsAgree(data, controls, tolerance, out split))
            {
                Reject(result, string.Format(
                    "the baked shape pulls one surface point apart at a seam (sample point {0}), which " +
                    "cannot be copied onto a mesh split differently", split));
                return false;
            }

            int intruder;
            int intruderCount;
            if (!AllUnmatchedVerticesAreOffPatch(data, target, controls, unmatchedNearby, matchedControl,
                    tolerance, neighbourhood, out intruder, out intruderCount))
            {
                Reject(result, string.Format(
                    "{0} of this mesh's vertices sit in the moving area with no matching point on the " +
                    "baked shape (for example vertex {1} at {2}), so this mesh is built differently there",
                    intruderCount, intruder, target.positions[intruder].ToString("0.####")));
                return false;
            }

            for (int frame = 0; frame < data.frames.Count; frame++)
            {
                result.framePositions[frame] = GatherByMatch(data.GetFrameDeltaPositions(frame),
                    matchedControl, target.meshFromReference);
                result.frameNormals[frame] = GatherByMatch(data.GetFrameDeltaNormals(frame),
                    matchedControl, target.normalFromReference);
                result.frameTangents[frame] = GatherByMatch(data.GetFrameDeltaTangents(frame),
                    matchedControl, target.normalFromReference);
            }

            var membership = new bool[target.VertexCount];
            for (int vertex = 0; vertex < matchedControl.Length; vertex++)
                membership[vertex] = MatchesMovingControl(data, matchedControl, vertex);
            result.regionMembership = membership;

            result.tier = TransferTier.Positional;
            result.controlCoverage = 1f;
            result.meanSurfaceDistance = 0f;
            return true;
        }

        /// <summary>
        /// Controls sharing a position are one surface point the source mesh split
        /// at a seam. Copying by position picks one of them per vertex, so they have
        /// to move identically. They practically always do; if they do not, the
        /// shape tears that seam open and only surface matching can express it.
        /// </summary>
        static bool CoincidentControlsAgree(PortableBlendShapeData data, Vector3[] controls,
            float tolerance, out int disagreeing)
        {
            disagreeing = -1;
            var first = new Dictionary<long, int>(data.controlCount);
            const float allowed = 1e-5f;        // 0.01 mm

            for (int control = 0; control < data.controlCount; control++)
            {
                long key = PositionKey(controls[control]);
                int other;
                if (!first.TryGetValue(key, out other))
                {
                    first[key] = control;
                    continue;
                }

                for (int frame = 0; frame < data.frames.Count; frame++)
                {
                    var deltas = data.GetFrameDeltaPositions(frame);
                    if ((deltas[control] - deltas[other]).sqrMagnitude <= allowed * allowed) continue;
                    disagreeing = control;
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// A vertex lying on the moving part of the baked surface without a control
        /// of its own would be left behind and tear the mesh - a subdivided edge
        /// midpoint, for example. One sitting a few millimetres off that surface is
        /// simply other geometry, such as a tooth behind a lip, and is no reason to
        /// give up the exact path.
        /// </summary>
        static bool AllUnmatchedVerticesAreOffPatch(PortableBlendShapeData data,
            ReferenceMeshSample target, Vector3[] controls, List<int> unmatched, int[] matchedControl,
            float tolerance, float neighbourhood, out int intruder, out int intruderCount)
        {
            intruder = -1;
            intruderCount = 0;
            if (unmatched.Count == 0) return true;

            if (!data.HasSurfacePatch())
            {
                // Without a patch there is nothing to measure against, so fall back
                // to the blunt test: anything near a moving control blocks the path.
                var hash = new PointHash(controls, neighbourhood);
                foreach (int vertex in unmatched)
                {
                    float distanceSquared;
                    int near = hash.FindNearest(target.positions[vertex], neighbourhood,
                        out distanceSquared);
                    if (near < 0 || data.IsBoundary(near)) continue;
                    intruder = vertex;
                    intruderCount++;
                }
                return intruderCount == 0;
            }

            int[] patch = data.GetPatchTriangles();
            var bvh = new PatchBvh(controls, data.GetControlNormals(), patch);
            float onSurface = Mathf.Max(tolerance * 2f, 0.0003f);
            float onSurfaceSquared = onSurface * onSurface;

            var suspects = new HashSet<int>();
            foreach (int vertex in unmatched)
            {
                PatchBvh.Hit hit;
                if (!bvh.FindClosest(target.positions[vertex], target.normals[vertex], onSurfaceSquared,
                        -1f, out hit))
                    continue;

                int offset = hit.triangle * 3;
                if (data.IsBoundary(patch[offset]) && data.IsBoundary(patch[offset + 1]) &&
                    data.IsBoundary(patch[offset + 2]))
                    continue;       // sitting on the still rim, nothing to lose
                suspects.Add(vertex);
            }
            if (suspects.Count == 0) return true;

            var triangles = target.triangles;
            if (triangles == null || triangles.Length < 3)
            {
                foreach (int vertex in suspects) intruder = vertex;
                intruderCount = suspects.Count;
                return false;       // cannot verify without topology, so stay safe
            }

            // Lying close to the surface is not enough. Only a vertex joined to the
            // moving area can be left behind and tear it. A separate surface running
            // alongside - the inside of a mouth just past a lip fold - cannot,
            // because everything it connects to stays still as well.
            var woven = new HashSet<int>();
            for (int i = 0; i + 2 < triangles.Length; i += 3)
            {
                int a = triangles[i];
                int b = triangles[i + 1];
                int c = triangles[i + 2];

                bool suspectA = suspects.Contains(a);
                bool suspectB = suspects.Contains(b);
                bool suspectC = suspects.Contains(c);
                if (!suspectA && !suspectB && !suspectC) continue;

                bool movingA = MatchesMovingControl(data, matchedControl, a);
                bool movingB = MatchesMovingControl(data, matchedControl, b);
                bool movingC = MatchesMovingControl(data, matchedControl, c);

                if (suspectA && (movingB || movingC)) woven.Add(a);
                if (suspectB && (movingA || movingC)) woven.Add(b);
                if (suspectC && (movingA || movingB)) woven.Add(c);
            }

            if (woven.Count == 0) return true;
            intruderCount = woven.Count;
            foreach (int vertex in woven)
            {
                intruder = vertex;
                break;
            }
            return false;
        }

        static bool MatchesMovingControl(PortableBlendShapeData data, int[] matchedControl, int vertex)
        {
            int control = matchedControl[vertex];
            return control >= 0 && !data.IsBoundary(control);
        }

        /// <summary>
        /// Each tier records one reason and returns, so the last writer is the
        /// furthest the transfer got. Keeping the first would hide the useful
        /// reason behind "the vertex count differs", which is true of any variant
        /// of a model and says nothing about the region that matters.
        /// </summary>
        static void Reject(TransferResult result, string reason)
        {
            result.exactPathRejection = reason;
        }

        static Vector3[] GatherByMatch(Vector3[] deltas, int[] matchedControl, Matrix4x4 toMesh)
        {
            if (deltas == null) return null;
            var output = new Vector3[matchedControl.Length];
            for (int vertex = 0; vertex < matchedControl.Length; vertex++)
            {
                int control = matchedControl[vertex];
                if (control < 0) continue;
                output[vertex] = toMesh.MultiplyVector(deltas[control]);
            }
            return output;
        }

        // --- Tier 2 ------------------------------------------------

        static bool TrySurface(PortableBlendShapeData data, ReferenceMeshSample target,
            TransferSettings settings, TransferResult result)
        {
            if (!data.HasSurfacePatch())
            {
                result.error = "The deformation asset has no surface patch, so it can only be applied to " +
                               "an unmodified body. Re-bake it with a current version of the tool.";
                return false;
            }

            Vector3[] controls = data.GetControlPositions();
            Vector3[] controlNormals = data.GetControlNormals();
            int[] patch = data.GetPatchTriangles();
            var bvh = new PatchBvh(controls, controlNormals, patch);

            float maximumDistance = Mathf.Max(settings.maximumMatchDistance, 1e-5f);
            float maximumDistanceSquared = maximumDistance * maximumDistance;
            float fadeStart = Mathf.Clamp01(settings.fadeStart) * maximumDistance;

            Bounds searchBounds = data.referenceBounds;
            searchBounds.Expand(maximumDistance * 2f);

            int frameCount = data.frames.Count;
            var framePositions = new Vector3[frameCount][];
            var frameNormals = new Vector3[frameCount][];
            var frameTangents = new Vector3[frameCount][];
            var sourcePositions = new Vector3[frameCount][];
            var sourceNormals = new Vector3[frameCount][];
            var sourceTangents = new Vector3[frameCount][];

            for (int frame = 0; frame < frameCount; frame++)
            {
                sourcePositions[frame] = data.GetFrameDeltaPositions(frame);
                sourceNormals[frame] = data.GetFrameDeltaNormals(frame);
                sourceTangents[frame] = data.GetFrameDeltaTangents(frame);
                framePositions[frame] = new Vector3[target.VertexCount];
                frameNormals[frame] = sourceNormals[frame] != null ? new Vector3[target.VertexCount] : null;
                frameTangents[frame] = sourceTangents[frame] != null ? new Vector3[target.VertexCount] : null;
            }

            int candidates = 0;
            int hits = 0;
            int behindHits = 0;
            double distanceSum = 0.0;
            var touchedControls = new bool[data.controlCount];

            // The control each vertex leans on most, used to tell islands apart.
            var hitControl = new int[target.VertexCount];
            var hitDistance = new float[target.VertexCount];
            for (int vertex = 0; vertex < hitControl.Length; vertex++)
            {
                hitControl[vertex] = -1;
                hitDistance[vertex] = -1f;
            }

            for (int vertex = 0; vertex < target.VertexCount; vertex++)
            {
                Vector3 position = target.positions[vertex];
                if (!searchBounds.Contains(position)) continue;
                candidates++;

                PatchBvh.Hit hit;
                if (!bvh.FindClosest(position, target.normals[vertex], maximumDistanceSquared,
                        settings.minimumNormalAlignment, out hit))
                    continue;

                float distance = Mathf.Sqrt(hit.distanceSquared);
                float weight = distance <= fadeStart
                    ? 1f
                    : Mathf.SmoothStep(1f, 0f, (distance - fadeStart) / Mathf.Max(1e-6f, maximumDistance - fadeStart));
                if (weight <= 0f) continue;

                hits++;
                distanceSum += distance;
                hitDistance[vertex] = distance;

                int offset = hit.triangle * 3;
                int ia = patch[offset];
                int ib = patch[offset + 1];
                int ic = patch[offset + 2];
                touchedControls[ia] = true;
                touchedControls[ib] = true;
                touchedControls[ic] = true;

                Vector3 closestPoint = controls[ia] * hit.barycentric.x +
                                       controls[ib] * hit.barycentric.y +
                                       controls[ic] * hit.barycentric.z;
                if (Vector3.Dot(position - closestPoint, hit.surfaceNormal) < 0f) behindHits++;

                hitControl[vertex] = hit.barycentric.x >= hit.barycentric.y
                    ? (hit.barycentric.x >= hit.barycentric.z ? ia : ic)
                    : (hit.barycentric.y >= hit.barycentric.z ? ib : ic);

                for (int frame = 0; frame < frameCount; frame++)
                {
                    var deltas = sourcePositions[frame];
                    Vector3 blended = deltas[ia] * hit.barycentric.x +
                                      deltas[ib] * hit.barycentric.y +
                                      deltas[ic] * hit.barycentric.z;
                    framePositions[frame][vertex] = target.meshFromReference.MultiplyVector(blended * weight);

                    if (frameNormals[frame] != null)
                    {
                        var normalDeltas = sourceNormals[frame];
                        Vector3 blendedNormal = normalDeltas[ia] * hit.barycentric.x +
                                                normalDeltas[ib] * hit.barycentric.y +
                                                normalDeltas[ic] * hit.barycentric.z;
                        frameNormals[frame][vertex] =
                            target.normalFromReference.MultiplyVector(blendedNormal * weight);
                    }

                    if (frameTangents[frame] != null)
                    {
                        var tangentDeltas = sourceTangents[frame];
                        Vector3 blendedTangent = tangentDeltas[ia] * hit.barycentric.x +
                                                 tangentDeltas[ib] * hit.barycentric.y +
                                                 tangentDeltas[ic] * hit.barycentric.z;
                        frameTangents[frame][vertex] =
                            target.normalFromReference.MultiplyVector(blendedTangent * weight);
                    }
                }
            }

            int moving = 0;
            int movingTouched = 0;
            for (int control = 0; control < data.controlCount; control++)
            {
                if (data.IsBoundary(control)) continue;
                moving++;
                if (touchedControls[control]) movingTouched++;
            }

            // Distance and normals alone cannot tell a tooth 2 mm behind a lip from
            // a lip a customer sculpted 2 mm inwards. Connectivity can: deformation
            // that is not joined to the main deformed area over the target's own
            // surface is not deformation, it is a nearby object.
            // A vertex belongs to the region when the patch corner it leans on most
            // is one of the moving controls. Taking the dominant corner rather than
            // any corner puts the edge of the region exactly where the boundary ring
            // sits, so removal stops at the rim the shape was baked to hold still.
            var membership = new bool[target.VertexCount];
            for (int vertex = 0; vertex < hitControl.Length; vertex++)
            {
                int control = hitControl[vertex];
                membership[vertex] = control >= 0 && !data.IsBoundary(control);
            }
            result.regionMembership = membership;

            result.behindSurfaceHits = behindHits;
            result.prunedVertexCount = PruneDisconnectedIslands(target, framePositions, frameNormals,
                frameTangents, hitControl, membership);

            result.framePositions = framePositions;
            result.frameNormals = frameNormals;
            result.frameTangents = frameTangents;
            result.tier = TransferTier.Surface;
            result.controlCoverage = moving > 0 ? (float)movingTouched / moving : 0f;

            // Averaged over the vertices that actually end up deformed. Including
            // the ones pruned away would report the distance to whatever geometry
            // was rejected, not how far this body is from the reference surface.
            double keptSum = 0.0;
            int keptCount = 0;
            for (int vertex = 0; vertex < target.VertexCount; vertex++)
            {
                if (hitDistance[vertex] < 0f) continue;
                bool stillMoves = false;
                for (int frame = 0; frame < frameCount; frame++)
                {
                    if (framePositions[frame][vertex].sqrMagnitude <= 1e-18f) continue;
                    stillMoves = true;
                    break;
                }
                if (!stillMoves) continue;
                keptSum += hitDistance[vertex];
                keptCount++;
            }
            result.meanSurfaceDistance = keptCount > 0
                ? (float)(keptSum / keptCount)
                : (hits > 0 ? (float)(distanceSum / hits) : 0f);

            if (hits == 0)
            {
                result.error = "No vertex of the target mesh lies within " +
                               (maximumDistance * 100f).ToString("0.##") +
                               " cm of the baked surface. This does not look like the right body mesh.";
                return false;
            }
            return true;
        }

        /// <summary>
        /// Groups every moved vertex into islands connected over the target's own
        /// surface, keeps the island carrying the most movement plus any other
        /// island that moves nearly as much - a shape may legitimately deform an
        /// upper and a lower jaw - and clears the rest. Returns how many vertices
        /// were cleared.
        /// </summary>
        static int PruneDisconnectedIslands(ReferenceMeshSample target, Vector3[][] framePositions,
            Vector3[][] frameNormals, Vector3[][] frameTangents, int[] hitControl, bool[] membership)
        {
            var triangles = target.triangles;
            if (triangles == null || triangles.Length < 3) return 0;

            int vertexCount = target.VertexCount;
            var magnitude = new float[vertexCount];
            float globalMaximum = 0f;
            int moved = 0;

            for (int frame = 0; frame < framePositions.Length; frame++)
            {
                var deltas = framePositions[frame];
                if (deltas == null) continue;
                for (int vertex = 0; vertex < vertexCount; vertex++)
                {
                    float value = deltas[vertex].magnitude;
                    if (value <= magnitude[vertex]) continue;
                    if (magnitude[vertex] <= 0f) moved++;
                    magnitude[vertex] = value;
                    if (value > globalMaximum) globalMaximum = value;
                }
            }
            if (moved == 0 || globalMaximum <= 0f) return 0;

            var sets = new DisjointSet(vertexCount);

            // Vertices split by a UV seam or a hard edge are one surface point.
            var coincident = new Dictionary<long, int>(moved);
            for (int vertex = 0; vertex < vertexCount; vertex++)
            {
                if (magnitude[vertex] <= 0f) continue;
                long key = PositionKey(target.positions[vertex]);
                int first;
                if (coincident.TryGetValue(key, out first)) sets.Union(first, vertex);
                else coincident[key] = vertex;
            }

            for (int i = 0; i + 2 < triangles.Length; i += 3)
            {
                int a = triangles[i];
                int b = triangles[i + 1];
                int c = triangles[i + 2];
                bool movedA = magnitude[a] > 0f;
                bool movedB = magnitude[b] > 0f;
                bool movedC = magnitude[c] > 0f;
                if (movedA && movedB) sets.Union(a, b);
                if (movedB && movedC) sets.Union(b, c);
                if (movedA && movedC) sets.Union(a, c);
            }

            var islandTotal = new Dictionary<int, float>();
            var islandClaims = new Dictionary<int, HashSet<int>>();
            for (int vertex = 0; vertex < vertexCount; vertex++)
            {
                if (magnitude[vertex] <= 0f) continue;
                int root = sets.Find(vertex);

                float total;
                islandTotal.TryGetValue(root, out total);
                islandTotal[root] = total + magnitude[vertex];

                if (hitControl[vertex] < 0) continue;
                HashSet<int> claimed;
                if (!islandClaims.TryGetValue(root, out claimed))
                    islandClaims[root] = claimed = new HashSet<int>();
                claimed.Add(hitControl[vertex]);
            }
            if (islandTotal.Count <= 1) return 0;

            var order = new List<int>(islandTotal.Keys);
            order.Sort((a, b) => islandTotal[b].CompareTo(islandTotal[a]));

            // The island carrying the most movement is the deformed surface. Another
            // island is kept only if it claims a part of the baked shape that the
            // kept ones do not: a tongue the shape genuinely moves has its own area,
            // while teeth behind a lip claim the very same area as the lip.
            var keep = new HashSet<int>();
            var claimedUnion = new HashSet<int>();
            foreach (int root in order)
            {
                HashSet<int> claimed;
                if (!islandClaims.TryGetValue(root, out claimed)) claimed = new HashSet<int>();

                if (keep.Count == 0)
                {
                    keep.Add(root);
                    foreach (int control in claimed) claimedUnion.Add(control);
                    continue;
                }

                int shared = 0;
                foreach (int control in claimed)
                    if (claimedUnion.Contains(control)) shared++;
                float overlap = claimed.Count > 0 ? (float)shared / claimed.Count : 1f;
                if (overlap > 0.15f) continue;

                keep.Add(root);
                foreach (int control in claimed) claimedUnion.Add(control);
            }

            int cleared = 0;
            for (int vertex = 0; vertex < vertexCount; vertex++)
            {
                if (magnitude[vertex] <= 0f) continue;
                if (keep.Contains(sets.Find(vertex))) continue;
                cleared++;

                // Geometry rejected as "not the deformed surface" is equally not part
                // of the region, so removal must not take it either.
                if (membership != null) membership[vertex] = false;

                for (int frame = 0; frame < framePositions.Length; frame++)
                {
                    if (framePositions[frame] != null) framePositions[frame][vertex] = Vector3.zero;
                    if (frameNormals[frame] != null) frameNormals[frame][vertex] = Vector3.zero;
                    if (frameTangents[frame] != null) frameTangents[frame][vertex] = Vector3.zero;
                }
            }
            return cleared;
        }

        static long PositionKey(Vector3 position)
        {
            const float scale = 100000f;      // 0.01 mm buckets
            long x = (long)Mathf.Round(position.x * scale);
            long y = (long)Mathf.Round(position.y * scale);
            long z = (long)Mathf.Round(position.z * scale);
            return (x * 73856093L) ^ (y * 19349663L) ^ (z * 83492791L);
        }

        sealed class DisjointSet
        {
            readonly int[] parent;
            readonly int[] rank;

            public DisjointSet(int count)
            {
                parent = new int[count];
                rank = new int[count];
                for (int i = 0; i < count; i++) parent[i] = i;
            }

            public int Find(int item)
            {
                while (parent[item] != item)
                {
                    parent[item] = parent[parent[item]];
                    item = parent[item];
                }
                return item;
            }

            public void Union(int a, int b)
            {
                int rootA = Find(a);
                int rootB = Find(b);
                if (rootA == rootB) return;
                if (rank[rootA] < rank[rootB])
                {
                    parent[rootA] = rootB;
                    return;
                }
                parent[rootB] = rootA;
                if (rank[rootA] == rank[rootB]) rank[rootA]++;
            }
        }

        static void Summarize(TransferResult result, int vertexCount)
        {
            int affected = 0;
            float maximum = 0f;
            var counted = new bool[vertexCount];

            for (int frame = 0; frame < result.framePositions.Length; frame++)
            {
                var deltas = result.framePositions[frame];
                if (deltas == null) continue;
                for (int vertex = 0; vertex < deltas.Length; vertex++)
                {
                    float magnitude = deltas[vertex].magnitude;
                    if (magnitude > maximum) maximum = magnitude;
                    if (magnitude <= 1e-6f || counted[vertex]) continue;
                    counted[vertex] = true;
                    affected++;
                }
            }

            result.affectedVertexCount = affected;
            result.maximumDisplacement = maximum;

            if (result.regionMembership == null) result.regionMembership = new bool[vertexCount];
            int region = 0;
            for (int vertex = 0; vertex < result.regionMembership.Length; vertex++)
                if (result.regionMembership[vertex]) region++;
            result.regionVertexCount = region;
        }
    }
}
#endif
