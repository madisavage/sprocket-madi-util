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
using Sprocket.VehicleDesigner.Access;
using Sprocket.Vehicles;
using Sprocket.Vehicles.Operations;
using Sprocket.Vehicles.PlateStructures;
using Sprocket.Vehicles.PlateStructures.Design;
using UnityAction = UnityCoreModule::UnityEngine.Events.UnityAction;
using Vector3 = System.Numerics.Vector3;

namespace Composite
{
    /// <summary>
    /// BepInEx entry point. Adds a Composite section to the freeform
    /// compartment editor that stacks thin plates behind the selected faces,
    /// in new add-ons.
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
    /// layer thickness, gap, add-on count, direction, and a button that stacks
    /// copies of the selected faces in new add-ons as one undoable edit.
    /// </summary>
    [HarmonyPatch(typeof(FreeformPlateStructureEditor), nameof(FreeformPlateStructureEditor.OnEditGUI))]
    internal static class FreeformEditGuiPatch
    {
        // Settings last the session, like the game's own editor toggles.
        private static float layers = 5f;
        private static float thickness = 10f;
        private static float gap = 5f;
        private static float addons = 1f;
        private static bool inward = true;
        // Index into axisNames; 0 stacks along the face normals.
        private static int axis = 2;
        private static readonly string[] axisNames = { "Face normals", "X", "Y", "Z" };

        private static FloatProperty? layersProperty, thicknessProperty, gapProperty, addonsProperty;

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
                "Plates added behind the selected faces.")
            { Min = 1f, Max = 500f, SoftMin = 1f, SoftMax = 50f, UseSoftLimits = true, Step = 1f };
            thicknessProperty ??= new FloatProperty(
                "Layer thickness",
                (Func<float>)(() => thickness),
                (Action<float>)(value => thickness = value),
                "Thickness of every added plate, in mm. The selected faces keep theirs.")
            { Min = 1f, Max = 500f, Step = 1f };
            gapProperty ??= new FloatProperty(
                "Gap",
                (Func<float>)(() => gap),
                (Action<float>)(value => gap = value),
                "Air gap between plates, in mm.")
            { Min = 0f, Max = 5000f, SoftMin = 0f, SoftMax = 500f, UseSoftLimits = true, Step = 1f };
            addonsProperty ??= new FloatProperty(
                "Add-ons",
                (Func<float>)(() => addons),
                (Action<float>)(value => addons = value),
                "Add-ons the plates alternate between, nearest first: with 3, plates 1 and 4 go in the first, 2 and 5 in the second, 3 and 6 in the third, so each can have its own material.")
            { Min = 1f, Max = 100f, SoftMin = 1f, SoftMax = 10f, UseSoftLimits = true, Step = 1f };
            onStack ??= DelegateSupport.ConvertDelegate<UnityAction>(new Action(() => { if (editor != null) Stack(editor); }))!;

            drawer.Float(layersProperty);
            drawer.Float(thicknessProperty);
            drawer.Float(gapProperty);
            drawer.Float(addonsProperty);
            EditorGui.Dropdown(drawer, "Axis", axisNames, axis, i => axis = i,
                "Direction the plates stack in. Along X, Y or Z every plate is the same shape, moved along that world axis; the gap is still measured square to the plates.");
            drawer.ToggleField("Stack inward", inward, (Action<bool>)(value => inward = value),
                "On: new plates go behind the faces. Off: they go out, the side the faces face.");
            var tooltip = new UITooltip("Stack selected faces", "Stacks copies of the selected faces behind them in new add-ons on this compartment, split as Add-ons says, so each can have its own material. The selected faces are left as they are. Undo removes them.");
            drawer.Button("Stack selected faces", onStack, ref tooltip);
        }

        /// <summary>Stacks copies of the selected faces of <paramref name="editor"/>'s mesh in a new add-on.</summary>
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
            // Inward, the first copy clears the faces' own thickness (they thicken inward); outward, its own.
            ushort own = faces.Keys.Max(f => before.serializedFaces[f].thicknesses.Max());
            float start = (inward ? own : thickness) + gap;
            var plates = new List<MeshBlueprint>();
            try
            {
                MeshBlueprint stack = Stacked(before, faces, (int)layers + 1, thickness, gap, inward, direction, start);
                // Copy k's faces follow the source faces, faces.Count per copy; copy k goes in add-on k % count.
                int count = Math.Min((int)addons, (int)layers), n = before.serializedFaces.Length;
                for (int a = 0; a < count; a++)
                    plates.Add(Pick(stack, Enumerable.Range(0, (int)layers).Where(k => k % count == a)
                        .SelectMany(k => Enumerable.Range(n + k * faces.Count, faces.Count))));
            }
            catch (InvalidOperationException e)
            {
                operations.NotifyError(e.Message);
                return;
            }
            try
            {
                // One group, so one undo removes every add-on.
                int group = editor.Gateway.Editor.VehicleOperations.GetNewGroupID();
                foreach (MeshBlueprint addon in plates)
                    AddAddon(editor, addon, group);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"Couldn't add the stacked plates as an add-on: {e}");
                operations.NotifyError("Couldn't add the stacked plates as an add-on; see the BepInEx log.");
            }
        }

        // The game's "Addon Structures" part, from Parts/addonPlateStructurePart.json.
        private const string AddonGuid = "8f8a9d20-eb45-482e-b149-014c964c4e2c";

        /// <summary>
        /// Adds an add-on structure shaped as <paramref name="plates"/> on
        /// <paramref name="editor"/>'s compartment, at its origin so the mesh
        /// lines up, in vehicle-op group <paramref name="group"/>.
        /// </summary>
        private static void AddAddon(PlateStructureEditor editor, MeshBlueprint plates, int group)
        {
            IVehicleOperationCreator ops = editor.Gateway.Editor.VehicleOperations;
            VehicleObject compartment = editor.Component.vehicleObject;
            InstantiateObject made = ops.Instantiate(AddonGuid, group);
            Plugin.Logger.LogInfo($"Add-on instantiated: state {made.state}, {made.instances?.Length ?? 0} instance(s).");
            if (made.instances == null || made.instances.Length == 0 || made.instances[0]?.Object is not { } addon)
                throw new InvalidOperationException($"the game made no add-on (state {made.state})");

            ops.SetParent(compartment.GetReference(), new Il2CppReferenceArray<ISoftVehicleObject>(new[] { addon.GetReference() }), group);
            addon.VehicleTransform.ZeroLocalTransform();

            PlateStructure structure = addon.behaviours.Select(c => c?.TryCast<PlateStructure>()).FirstOrDefault(c => c != null)
                ?? throw new InvalidOperationException("the new add-on has no plate structure");
            int sharing = Each(compartment.Vehicle.ObjectReader.Items)
                .SelectMany(o => o?.behaviours ?? (IEnumerable<VehicleComponent>)Array.Empty<VehicleComponent>())
                .Count(c => c?.TryCast<PlateStructure>()?.Mesh?.Pointer == structure.Mesh.Pointer);
            Plugin.Logger.LogInfo($"Add-on mesh used by {sharing} structure(s).");
            if (sharing > 1)
                throw new InvalidOperationException("the new add-on shares its shape with other parts");
            Load(structure.Mesh.EditMesh, plates);
        }

        private static IEnumerable<T> Each<T>(Il2CppSystem.Collections.Generic.IReadOnlyList<T> list)
        {
            int count = list.Cast<Il2CppSystem.Collections.Generic.IReadOnlyCollection<T>>().Count;
            for (int i = 0; i < count; i++)
                yield return list[i];
        }

        /// <summary>
        /// The faces of <paramref name="mesh"/> at <paramref name="indices"/>,
        /// as a mesh of their own: only their vertices and edges, renumbered.
        /// </summary>
        internal static MeshBlueprint Pick(MeshBlueprint mesh, IEnumerable<int> indices)
        {
            var faces = indices.Select(f => mesh.serializedFaces[f]).ToList();
            var vertices = new Dictionary<int, int>();
            var positions = new List<float>();
            foreach (var face in faces)
                foreach (int v in face.vertices)
                    if (!vertices.ContainsKey(v))
                    {
                        vertices[v] = positions.Count / 3;
                        positions.AddRange(new[] { mesh.vertexPositions[3 * v], mesh.vertexPositions[3 * v + 1], mesh.vertexPositions[3 * v + 2] });
                    }
            // Edge 0 reads as "no thicken edge", so edges no corner thickens along go first.
            var thickening = faces.SelectMany(f => f.thickenEdgeIndicies).Where(e => e != NoEdge).Select(e => (int)e).ToHashSet();
            var kept = Enumerable.Range(0, mesh.serializedEdges.Length / 2)
                .Where(e => vertices.ContainsKey(mesh.serializedEdges[2 * e]) && vertices.ContainsKey(mesh.serializedEdges[2 * e + 1]))
                .OrderBy(e => thickening.Contains(e))
                .ToList();
            var edgeMap = new Dictionary<int, ushort>();
            var edges = new List<int>();
            var edgeFlags = new List<MeshBlueprint.SerializedEdgeFlags>();
            foreach (int e in kept)
            {
                edgeMap[e] = (ushort)(edges.Count / 2);
                edges.Add(vertices[mesh.serializedEdges[2 * e]]);
                edges.Add(vertices[mesh.serializedEdges[2 * e + 1]]);
                edgeFlags.Add(mesh.serializedEdgeFlags[e]);
            }
            var faceArray = new Il2CppReferenceArray<MeshBlueprint.SerializedFace>(faces.Count);
            for (int i = 0; i < faces.Count; i++)
            {
                var copy = Copy(faces[i], v => vertices[v], e => e == NoEdge ? NoEdge : edgeMap[e], faces[i].thicknesses[0], keepEdges: false);
                copy.thicknesses = faces[i].thicknesses.ToArray();
                faceArray[i] = copy;
            }
            return new MeshBlueprint(positions.ToArray(), edges.ToArray(), edgeFlags.ToArray(), faceArray);
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
        /// normals, <paramref name="gap"/> mm apart, the first <paramref name="start"/> mm
        /// from the faces (one thickness plus the gap if left out).
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
        internal static MeshBlueprint Stacked(MeshBlueprint source, IReadOnlyDictionary<int, Vector3> faces, int layers, float thickness, float gap, bool inward, Vector3? axis, float? start = null)
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
            float sign = inward ? -1f : 1f;
            float pitch = thickness + gap, first = start ?? pitch;
            for (int layer = 1; layer < layers; layer++)
            {
                var vertexMap = new Dictionary<int, int>();
                foreach (var (v, direction) in directions)
                {
                    vertexMap[v] = positions.Count / 3;
                    Vector3 p = new Vector3(positions[3 * v], positions[3 * v + 1], positions[3 * v + 2]) + direction * sign * (first + pitch * (layer - 1)) / 1000f;
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
