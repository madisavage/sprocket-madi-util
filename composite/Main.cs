extern alias UnityCoreModule;

using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MadiLib;
using Operations;
using Properties;
using Sprocket.MeshEditing;
using Sprocket.PlateMesh;
using Sprocket.UI;
using Sprocket.Vehicles.PlateStructures.Design;
using UnityAction = UnityCoreModule::UnityEngine.Events.UnityAction;
using Vector3 = System.Numerics.Vector3;

namespace Composite
{
    /// <summary>
    /// BepInEx entry point. Adds a Composite section to the freeform
    /// compartment editor that turns the selected faces into a stack of thin
    /// plates.
    /// </summary>
    [BepInPlugin("madi.composite", "composite", ModInfo.Version)]
    [BepInDependency("madi.madilib", ">=" + ModInfo.Version)]
    public sealed class Plugin : BasePlugin
    {
        internal static ManualLogSource Logger = null!;

        public override void Load()
        {
            Logger = Log;
            new Harmony("madi.composite").PatchAll();
        }
    }

    /// <summary>
    /// Adds the Composite section to the freeform editor panel: layer count,
    /// layer thickness, gap, direction, and a button that stacks the selected
    /// faces as one undoable edit.
    /// </summary>
    [HarmonyPatch(typeof(FreeformPlateStructureEditor), nameof(FreeformPlateStructureEditor.OnEditGUI))]
    internal static class FreeformEditGuiPatch
    {
        // Settings last the session, like the game's own editor toggles.
        private static float layers = 5f;
        private static float thickness = 10f;
        private static float gap = 5f;
        private static bool inward = true;
        // Index into axisNames; 0 stacks along the face normals.
        private static int axis = 2;
        private static readonly string[] axisNames = { "Face normals", "X", "Y", "Z" };

        private static FloatProperty? layersProperty, thicknessProperty, gapProperty;

        // Keeps the button's native callback alive between redraws.
        private static UnityAction? onStack;
        private static FreeformPlateStructureEditor? editor;

        /// <summary>Runs after the game draws the freeform panel and appends the section.</summary>
        /// <param name="__instance">The freeform editor being drawn.</param>
        /// <param name="__0">The layout the game is drawing the panel into.</param>
        private static void Postfix(FreeformPlateStructureEditor __instance, IGUILayout __0)
        {
            IGUIElementDrawer? drawer = __0?.TryCast<IGUIElementDrawer>();
            if (drawer == null)
                return;
            editor = __instance;
            if (!EditorGui.Section(drawer, "composite.freeform", "Composite", __instance.Sprocket_Vehicles_PlateStructures_Design_IFreeformEditor_RequestRedraw))
                return;

            layersProperty ??= new FloatProperty(
                "Layers",
                (Func<float>)(() => layers),
                (Action<float>)(value => layers = value),
                "Plates in the stack, counting the selected faces.")
            { Min = 2f, Max = 50f, Step = 1f };
            thicknessProperty ??= new FloatProperty(
                "Layer thickness",
                (Func<float>)(() => thickness),
                (Action<float>)(value => thickness = value),
                "Thickness of every plate in the stack, in mm. The selected faces are set to it too.")
            { Min = 1f, Max = 500f, Step = 1f };
            gapProperty ??= new FloatProperty(
                "Gap",
                (Func<float>)(() => gap),
                (Action<float>)(value => gap = value),
                "Air gap between plates, in mm.")
            { Min = 0f, Max = 500f, Step = 1f };
            onStack ??= DelegateSupport.ConvertDelegate<UnityAction>(new Action(() => { if (editor != null) Stack(editor); }))!;

            drawer.Float(layersProperty);
            drawer.Float(thicknessProperty);
            drawer.Float(gapProperty);
            EditorGui.Dropdown(drawer, "Axis", axisNames, axis, i => axis = i,
                "Direction the plates stack in. Along X, Y or Z every plate is the same shape, moved along that world axis; the gap is still measured square to the plates.");
            drawer.ToggleField("Stack inward", inward, (Action<bool>)(value => inward = value),
                "Off: new plates go out, the side the faces face. On: they go in.");
            var tooltip = new UITooltip("Stack selected faces", "Copies the selected faces into a stack of plates. Undo reverts it.");
            drawer.Button("Stack selected faces", onStack, ref tooltip);
        }

        /// <summary>Stacks the selected faces of <paramref name="editor"/>'s mesh as one undoable edit.</summary>
        private static void Stack(FreeformPlateStructureEditor editor)
        {
            PlateStructureEditOperations operations = editor.operations;
            MeshEditor meshEditor = editor.meshEditor;
            EditMesh mesh = meshEditor.Mesh.EditMesh;
            mesh.ReIndex();

            Il2CppReferenceArray<Face> picked = ElementSelection.GetSelectedFaces(mesh, false);
            if (meshEditor.Symmetry)
                picked = ElementSelection.AppendMirrors(mesh, picked);
            var faces = new Dictionary<int, Vector3>();
            foreach (Face face in picked)
                faces[face.index] = new Vector3(face.normal.x, face.normal.y, face.normal.z);
            if (faces.Count == 0)
            {
                operations.NotifyError("Select the faces to stack first.");
                return;
            }

            MeshBlueprint before = new(mesh);
            Vector3? direction = null;
            if (axis > 0)
            {
                var world = new UnityCoreModule::UnityEngine.Vector3(axis == 1 ? 1f : 0f, axis == 2 ? 1f : 0f, axis == 3 ? 1f : 0f);
                var local = meshEditor.context.WorldToMesh.MultiplyVector(world);
                direction = Vector3.Normalize(new Vector3(local.x, local.y, local.z));
            }
            MeshBlueprint after;
            try
            {
                after = Stacked(before, faces, (int)layers, thickness, gap, inward, direction);
            }
            catch (InvalidOperationException e)
            {
                operations.NotifyError(e.Message);
                return;
            }
            StructureEdit edit = operations.CreateEdit("Stack composite", StructureEditOperationOptions.None);
            edit.execute = (Func<MeshOperatorContext, ExecutionResult>)(_ =>
            {
                try
                {
                    Load(mesh, after);
                    return ExecutionResult.Executed;
                }
                catch (Exception e)
                {
                    Plugin.Logger.LogError($"Couldn't stack the faces: {e}");
                    Load(mesh, before);
                    operations.NotifyError("Couldn't stack the faces; see the BepInEx log.");
                    return ExecutionResult.Cancelled;
                }
            });
            edit.revert = (Action<MeshOperatorContext>)(_ => Load(mesh, before));
            operations.Execute(edit, operations.GetNewGroupID());
        }

        private static void Load(EditMesh mesh, MeshBlueprint blueprint)
        {
            mesh.Clear();
            blueprint.ToMesh(mesh);
            mesh.MarkDirty(MeshDirtyFlags.All);
        }

        // A corner's thicken edge is an edge leaving the corner along a
        // neighbouring face; saved vehicles use 0 where there is none.
        private const ushort NoEdge = 0;

        /// <summary>
        /// <paramref name="source"/> with the faces in <paramref name="faces"/>
        /// (index to normal) set to <paramref name="thickness"/> mm and
        /// <paramref name="layers"/> - 1 copies of them stacked along their
        /// normals, <paramref name="gap"/> mm apart.
        /// </summary>
        /// <remarks>
        /// Each copy keeps the faces' winding, so it thickens the same way as
        /// the original; spacing copies one thickness plus the gap apart then
        /// leaves the gap between plates whichever side they thicken to.
        /// Without <paramref name="axis"/>, vertices move along their selected
        /// faces' mean normal, lengthened so flat faces keep their spacing at
        /// shared corners. With it, every vertex moves along the axis, toward
        /// the side its faces face (so mirrored halves move apart), lengthened
        /// so the mean face keeps its spacing.
        /// </remarks>
        /// <exception cref="InvalidOperationException">The axis lies along the faces.</exception>
        internal static MeshBlueprint Stacked(MeshBlueprint source, IReadOnlyDictionary<int, Vector3> faces, int layers, float thickness, float gap, bool inward, Vector3? axis)
        {
            var positions = source.vertexPositions.ToList();
            var edges = source.serializedEdges.ToList();
            var edgeFlags = source.serializedEdgeFlags.ToList();
            var serialized = source.serializedFaces.ToList();
            ushort mm = (ushort)MathF.Round(thickness);

            var edgeIndex = new Dictionary<(int, int), int>();
            for (int e = 0; e < edges.Count / 2; e++)
                edgeIndex[Key(edges[2 * e], edges[2 * e + 1])] = e;

            // Vertex, offset direction per metre of plate spacing
            var normals = new Dictionary<int, List<Vector3>>();
            foreach (var (f, normal) in faces)
                foreach (int v in serialized[f].vertices)
                {
                    if (!normals.TryGetValue(v, out var list))
                        normals[v] = list = new();
                    list.Add(normal);
                }
            float along = axis is { } a0 ? faces.Values.Average(n => MathF.Abs(Vector3.Dot(a0, n))) : 1f;
            if (along < 0.05f)
                throw new InvalidOperationException("That axis runs along the selected faces; pick another.");
            var directions = normals.ToDictionary(p => p.Key, p =>
            {
                Vector3 mean = Vector3.Normalize(p.Value.Aggregate(Vector3.Zero, (a, b) => a + b));
                if (axis is { } a)
                    return a * MathF.Sign(Vector3.Dot(a, mean)) / along;
                // just a random cap maximum here, no rime or reason and this is evil
                return mean / MathF.Max(p.Value.Min(n => Vector3.Dot(mean, n)), 0.25f);
            });

            var result = serialized.Select((face, f) => faces.ContainsKey(f) ? Copy(face, v => v, _ => NoEdge, mm, keepEdges: true) : face).ToList();
            float pitch = (inward ? -1f : 1f) * (thickness + gap) / 1000f;
            for (int layer = 1; layer < layers; layer++)
            {
                var vertexMap = new Dictionary<int, int>();
                foreach (var (v, direction) in directions)
                {
                    vertexMap[v] = positions.Count / 3;
                    Vector3 p = new Vector3(positions[3 * v], positions[3 * v + 1], positions[3 * v + 2]) + direction * pitch * layer;
                    positions.AddRange(new[] { p.X, p.Y, p.Z });
                }
                var edgeMap = new Dictionary<int, int>();
                foreach (int f in faces.Keys)
                {
                    int[] vs = serialized[f].vertices;
                    for (int i = 0; i < vs.Length; i++)
                    {
                        if (!edgeIndex.TryGetValue(Key(vs[i], vs[(i + 1) % vs.Length]), out int e) || edgeMap.ContainsKey(e))
                            continue;
                        edgeMap[e] = edges.Count / 2;
                        edges.Add(vertexMap[edges[2 * e]]);
                        edges.Add(vertexMap[edges[2 * e + 1]]);
                        edgeFlags.Add(source.serializedEdgeFlags[e]);
                    }
                    result.Add(Copy(serialized[f], v => vertexMap[v], e => edgeMap.TryGetValue(e, out int n) ? (ushort)n : NoEdge, mm, keepEdges: false));
                }
            }

            var faceArray = new Il2CppReferenceArray<MeshBlueprint.SerializedFace>(result.Count);
            for (int i = 0; i < result.Count; i++)
                faceArray[i] = result[i];
            return new MeshBlueprint(positions.ToArray(), edges.ToArray(), edgeFlags.ToArray(), faceArray);
        }

        private static (int, int) Key(int a, int b) => a < b ? (a, b) : (b, a);

        /// <summary>
        /// A copy of <paramref name="face"/> at <paramref name="thickness"/> mm,
        /// its vertices and thicken edges renumbered (unless <paramref name="keepEdges"/>).
        /// </summary>
        private static MeshBlueprint.SerializedFace Copy(MeshBlueprint.SerializedFace face, Func<int, int> vertex, Func<int, ushort> edge, ushort thickness, bool keepEdges)
        {
            int n = face.vertices.Length;
            var copy = new MeshBlueprint.SerializedFace(n);
            copy.vertices = face.vertices.Select(vertex).ToArray();
            copy.thicknesses = Enumerable.Repeat(thickness, n).ToArray();
            copy.thickenModes = face.thickenModes.ToArray();
            copy.thickenEdgeIndicies = keepEdges
                ? face.thickenEdgeIndicies.ToArray()
                : face.thickenEdgeIndicies.Select(e => edge(e)).ToArray();
            return copy;
        }
    }
}
