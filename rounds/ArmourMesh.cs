extern alias UnityCoreModule;

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Sprocket.DamageModelling;
using UnityMatrix = UnityCoreModule::UnityEngine.Matrix4x4;

namespace Rounds
{
    /// <summary>
    /// The armour a penetration simulation runs on, read at native offsets:
    /// <c>MeshStructure</c> is a struct Il2CppInterop wraps as a class, so its
    /// arrays read garbage.
    /// </summary>
    public static class ArmourMesh
    {
        private static bool ready;
        private static int meshes, meshSize, materialSize, vertices, triangles, materialMap, objectIndex;

        private static void Init()
        {
            meshes = MadiLib.Native.FieldOffset<PenetrationSimulation>("meshes");
            meshSize = MadiLib.Native.ValueSize<MeshStructure>();
            materialSize = MadiLib.Native.ValueSize<TriangleMaterialInfo>();
            vertices = MadiLib.Native.StructOffset<MeshStructure>("vertices");
            triangles = MadiLib.Native.StructOffset<MeshStructure>("triangles");
            materialMap = MadiLib.Native.StructOffset<MeshStructure>("materialMap");
            objectIndex = MadiLib.Native.StructOffset<TriangleMaterialInfo>("objectIndex");
            ready = true;
        }

        private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

        /// <summary>
        /// Every armour mesh of the structure <paramref name="sim"/> ran on, moved into sim space by its space frame, merged,
        /// with each triangle's index into the sim's objects (-1 if unknown), and how many vertices weren't finite (left out).
        /// </summary>
        public static (Vector3[] Points, int[] Triangles, int[] Objects, int Bad) Read(PenetrationSimulation sim)
        {
            if (!ready)
                Init();
            int header = MadiLib.Native.ArrayHeader;
            var frames = sim.spaceFrames;
            IntPtr array = Marshal.ReadIntPtr(sim.Pointer, meshes);
            if (array == IntPtr.Zero || frames == null)
                return (Array.Empty<Vector3>(), Array.Empty<int>(), Array.Empty<int>(), 0);
            int count = Math.Min(MadiLib.Native.Length(array), frames.Length);
            var points = new List<Vector3>();
            var tris = new List<int>();
            var objects = new List<int>();
            int bad = 0;
            for (int m = 0; m < count; m++)
            {
                IntPtr mesh = MadiLib.Native.Element(array, m, meshSize);
                IntPtr verts = Marshal.ReadIntPtr(mesh, vertices), indices = Marshal.ReadIntPtr(mesh, triangles), map = Marshal.ReadIntPtr(mesh, materialMap);
                if (verts == IntPtr.Zero || indices == IntPtr.Zero)
                    continue;
                int vertexCount = MadiLib.Native.Length(verts), indexCount = MadiLib.Native.Length(indices);
                int mapCount = map == IntPtr.Zero ? 0 : MadiLib.Native.Length(map);
                var xyz = new float[vertexCount * 3];
                var index = new int[indexCount];
                Marshal.Copy(verts + header, xyz, 0, xyz.Length);
                Marshal.Copy(indices + header, index, 0, index.Length);
                UnityMatrix f = frames[m].localToSimSpace;
                int offset = points.Count;
                for (int v = 0; v < vertexCount; v++)
                {
                    float x = xyz[3 * v], y = xyz[3 * v + 1], z = xyz[3 * v + 2];
                    points.Add(new Vector3(
                        f.m00 * x + f.m01 * y + f.m02 * z + f.m03,
                        f.m10 * x + f.m11 * y + f.m12 * z + f.m13,
                        f.m20 * x + f.m21 * y + f.m22 * z + f.m23));
                }
                for (int v = offset; v < points.Count; v++)
                    if (!Finite(points[v]))
                        bad++;
                bool Good(int i) => (uint)index[i] < vertexCount && Finite(points[offset + index[i]]);
                for (int i = 0; i + 2 < indexCount; i += 3)
                    if (Good(i) && Good(i + 1) && Good(i + 2))
                    {
                        tris.Add(offset + index[i]);
                        tris.Add(offset + index[i + 1]);
                        tris.Add(offset + index[i + 2]);
                        // The map has an entry per triangle, as the game's own hit lookup reads it
                        objects.Add(i / 3 < mapCount ? Marshal.ReadInt16(map, header + i / 3 * materialSize + objectIndex) : -1);
                    }
            }
            return (points.ToArray(), tris.ToArray(), objects.ToArray(), bad);
        }

        /// <summary>How far a ray from <paramref name="origin"/> along <paramref name="direction"/> goes before it crosses a triangle, past 0.1 mm; infinity if it never does.</summary>
        public static float Distance(Vector3[] points, int[] triangles, Vector3 origin, Vector3 direction)
        {
            Vector3 d = Vector3.Normalize(direction);
            float nearest = float.PositiveInfinity;
            for (int i = 0; i + 2 < triangles.Length; i += 3)
                if (Cross(points, triangles, i, origin, d) is { } t && t > 0.0001f && t < nearest)
                    nearest = t;
            return nearest;
        }

        // Möller–Trumbore, both sides: how far along unit d the ray crosses triangle i, or null
        private static float? Cross(Vector3[] points, int[] triangles, int i, Vector3 origin, Vector3 d)
        {
            Vector3 a = points[triangles[i]], e1 = points[triangles[i + 1]] - a, e2 = points[triangles[i + 2]] - a;
            Vector3 p = Vector3.Cross(d, e2);
            float det = Vector3.Dot(e1, p);
            if (MathF.Abs(det) < 1e-12f)
                return null;
            Vector3 s = origin - a;
            float u = Vector3.Dot(s, p) / det;
            if (u < 0f || u > 1f)
                return null;
            Vector3 q = Vector3.Cross(s, e1);
            float v = Vector3.Dot(d, q) / det;
            if (v < 0f || u + v > 1f)
                return null;
            return Vector3.Dot(e2, q) / det;
        }

        /// <summary>
        /// The unit normal of the triangle a ray from <paramref name="origin"/> along <paramref name="direction"/> crosses
        /// nearest <paramref name="distance"/> along it (within 1 mm or 2%), e.g. the face a sim fragment ended on; null if none.
        /// The game doesn't fill in a fragment's hit triangle or normal, so this finds it again.
        /// </summary>
        public static Vector3? Face(Vector3[] points, int[] triangles, Vector3 origin, Vector3 direction, float distance)
        {
            Vector3 d = Vector3.Normalize(direction);
            float best = Math.Max(0.001f, 0.02f * distance);
            Vector3? normal = null;
            for (int i = 0; i + 2 < triangles.Length; i += 3)
                if (Cross(points, triangles, i, origin, d) is { } t && MathF.Abs(t - distance) <= best)
                {
                    best = MathF.Abs(t - distance);
                    Vector3 a = points[triangles[i]];
                    normal = Vector3.Normalize(Vector3.Cross(points[triangles[i + 1]] - a, points[triangles[i + 2]] - a));
                }
            return normal;
        }
    }
}
