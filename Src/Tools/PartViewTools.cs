using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using EditorGizmos;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace com.github.lhervier.ksp.util.mcpserver
{
    /// <summary>
    /// Tools that find a part on screen and turn the camera to it, and that pick the tool of EVA construction
    /// as its buttons do: what a protocol needs to click a part as the player does.
    /// </summary>
    internal static class PartViewTools
    {
        /// <summary>How many rays a side of the grid cast over a part's rectangle on screen.</summary>
        private const int GridSide = 16;

        /// <summary>The headings find_view tries, in degrees from the current one.</summary>
        private static readonly float[] ViewHeadings = { 0f, 45f, 90f, 135f, 180f, 225f, 270f, 315f };

        /// <summary>The pitches find_view tries, in degrees.</summary>
        private static readonly float[] ViewPitches = { 10f, 35f, 60f };

        /// <summary>How many frames the flight camera gets to settle on a new heading and pitch.</summary>
        private const int SettleFrames = 3;

        private static readonly Dictionary<string, ConstructionMode> Modes = new Dictionary<string, ConstructionMode>
        {
            { "place", ConstructionMode.Place },
            { "move", ConstructionMode.Move },
            { "rotate", ConstructionMode.Rotate }
        };

        private static readonly Dictionary<string, bool> Panels = new Dictionary<string, bool>
        {
            { "open", true },
            { "close", false }
        };

        /// <summary>How long the construction panel and its buttons may take to show or hide, in seconds of real time.</summary>
        private const float PanelSeconds = 5f;

        /// <summary>The toggles of the tools' buttons in EVAConstructionToolsUI, internal to the game.</summary>
        private static readonly Dictionary<ConstructionMode, FieldInfo> ModeButtons = new Dictionary<ConstructionMode, FieldInfo>
        {
            { ConstructionMode.Place, typeof(EVAConstructionToolsUI).GetField("placeButton", BindingFlags.Instance | BindingFlags.NonPublic) },
            { ConstructionMode.Move, typeof(EVAConstructionToolsUI).GetField("moveButton", BindingFlags.Instance | BindingFlags.NonPublic) },
            { ConstructionMode.Rotate, typeof(EVAConstructionToolsUI).GetField("rotateButton", BindingFlags.Instance | BindingFlags.NonPublic) }
        };

        /// <summary>The axes of a gizmo, in its own frame.</summary>
        private static readonly Dictionary<string, Vector3> GizmoAxes = new Dictionary<string, Vector3>
        {
            { "x", Vector3.right },
            { "y", Vector3.up },
            { "z", Vector3.forward }
        };

        /// <summary>Over how many frames a gizmo is dragged when the call does not say.</summary>
        private const int DefaultGizmoSteps = 20;

        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        // The gizmos of EVAConstructionModeEditor, their handles, and the callbacks the handles call when the mouse
        // grabs, drags and releases them, all internal to the game.
        private static readonly FieldInfo EditorGizmoOffset = typeof(EVAConstructionModeEditor).GetField("gizmoOffset", Private);
        private static readonly FieldInfo EditorGizmoRotate = typeof(EVAConstructionModeEditor).GetField("gizmoRotate", Private);
        private static readonly FieldInfo OffsetHandles = typeof(GizmoOffset).GetField("handles", Private);
        private static readonly FieldInfo RotateHandles = typeof(GizmoRotate).GetField("handles", Private);
        private static readonly FieldInfo RotateHandleAxis = typeof(GizmoRotateHandle).GetField("a0", Private);
        private static readonly MethodInfo OffsetMoveStart = typeof(GizmoOffset).GetMethod("OnHandleMoveStart", Private);
        private static readonly MethodInfo OffsetMove = typeof(GizmoOffset).GetMethod("OnHandleMove", Private);
        private static readonly MethodInfo OffsetMoveEnd = typeof(GizmoOffset).GetMethod("OnHandleMoveEnd", Private);
        private static readonly MethodInfo RotateStart = typeof(GizmoRotate).GetMethod("OnHandleRotateStart", Private);
        private static readonly MethodInfo RotateTurn = typeof(GizmoRotate).GetMethod("OnHandleRotate", Private);
        private static readonly MethodInfo RotateEnd = typeof(GizmoRotate).GetMethod("OnHandleRotateEnd", Private);

        public static IEnumerable<Tool> All()
        {
            yield return new Tool("locate_part",
                "Finds a part of a loaded vessel on screen: the rectangle its colliders cover (x, y of its top left " +
                "corner, width and height, in pixels from the top left corner of the game's window, as mouse_move " +
                "takes them), and, casting rays from the camera over that rectangle as the game finds the part under " +
                "the mouse, how much of the part shows (the share of the rays that meet the part and meet it first) " +
                "and a point where the mouse would be over this part, with the interface objects drawn over that " +
                "point. Also the colliders of the part, how many are triggers (a trigger collides with nothing), and " +
                "whether it is the part selected in EVA construction, and how far it leans: the angle between its up " +
                "axis and the vertical of the body below it. With find_view, first turns the flight camera " +
                "around what it aims at (aim_camera) to the heading and pitch where the most of the part shows clear of the " +
                "interface.",
                Schema.Object(
                    Schema.P("part", "number", "the part's id, as list_parts gives it", true),
                    Schema.P("find_view", "boolean",
                        "turn the camera to the heading and pitch where the most of the part shows clear of the interface (default false)")),
                LocatePart);
            yield return new Tool("aim_camera",
                "Aims the flight camera at a part of a loaded vessel, as the Aim Camera button of its part action " +
                "window does: the camera then turns around that part. Without a part, aims it back at the active " +
                "vessel, as the Reset Camera button does.",
                Schema.Object(
                    Schema.P("part", "number", "the part's id, as list_parts gives it (default: back to the active vessel)")),
                AimCamera) { Concurrent = true };
            yield return new Tool("construction_tool",
                "Opens or closes the construction panel of EVA construction, as its button in the toolbar does, " +
                "and picks its Place, Move or Rotate tool, as its buttons do; answers with whether the panel is open, " +
                "the tool in use and the part selected. The game opens the panel only for an engineer on EVA, and " +
                "refuses Move and Rotate while the gizmos are locked and while a part is held from the Place tool: " +
                "the answer then names the tool kept. Without panel or tool, only reads.",
                Schema.Object(
                    Schema.P("panel", "string", "open or close (default: left as it is)"),
                    Schema.P("tool", "string", "place, move or rotate (default: left as it is)")),
                ConstructionTool);
            yield return new Tool("construction_gizmo",
                "Drags the gizmo of the part selected in EVA construction, as a player drags one of its handles with " +
                "the mouse: with the Move tool, along the x, y or z axis of the gizmo by distance metres; with the " +
                "Rotate tool, around it by angle degrees. The drag is spread over steps frames, one move per frame, " +
                "with the game's snapping applied as the handle applies it, then released, so that the game and the " +
                "mods see every step and the release. Answers with the axis in world space, what was asked, what the " +
                "snapping left of it and how far the part really went (along the axis, or around it), and the part's " +
                "position at the end.",
                Schema.Object(
                    Schema.P("axis", "string", "x, y or z: an axis of the gizmo, in the frame it shows (Absolute or Local)", true),
                    Schema.P("distance", "number", "Move tool: metres along the axis, negative the other way"),
                    Schema.P("angle", "number", "Rotate tool: degrees around the axis, negative the other way"),
                    Schema.P("steps", "number", "how many frames the drag is spread over (default " + DefaultGizmoSteps + ")")),
                ConstructionGizmo);
        }

        private static IEnumerator LocatePart(ToolCall call)
        {
            if (!Require.Flight(call))
            {
                yield break;
            }
            Part part = PartMenuTools.FindPart(call);
            if (part == null)
            {
                yield break;
            }
            if (call.Bool("find_view"))
            {
                FlightCamera camera = FlightCamera.fetch;
                if (camera == null || CameraManager.Instance.currentCameraMode != CameraManager.CameraMode.Flight)
                {
                    call.Fail("find_view turns the flight camera: not in the flight view");
                    yield break;
                }
                float heading0 = camera.camHdg;
                float bestHeading = camera.camHdg;
                float bestPitch = camera.camPitch;
                double bestShown = -1.0;
                foreach (float pitch in ViewPitches)
                {
                    foreach (float heading in ViewHeadings)
                    {
                        camera.camHdg = heading0 + heading * Mathf.Deg2Rad;
                        camera.camPitch = pitch * Mathf.Deg2Rad;
                        for (int i = 0; i < SettleFrames; i++)
                        {
                            yield return null;
                        }
                        if (part == null)
                        {
                            call.Fail("The part is gone");
                            yield break;
                        }
                        Sight sight = Look(part);
                        // The share of the grid where the part shows clear of the interface, so that a nearer view
                        // wins over a farther one where the part shows whole but small.
                        double shown = sight.Samples > 0 ? (double)sight.Clear / sight.Samples : 0.0;
                        if (shown > bestShown)
                        {
                            bestShown = shown;
                            bestHeading = camera.camHdg;
                            bestPitch = camera.camPitch;
                        }
                    }
                }
                camera.camHdg = bestHeading;
                camera.camPitch = bestPitch;
                for (int i = 0; i < SettleFrames; i++)
                {
                    yield return null;
                }
                if (part == null)
                {
                    call.Fail("The part is gone");
                    yield break;
                }
            }
            call.Text(Describe(part, Look(part)));
        }

        private static IEnumerator AimCamera(ToolCall call)
        {
            if (!Require.Flight(call))
            {
                yield break;
            }
            if (call.Has("part"))
            {
                Part part = PartMenuTools.FindPart(call);
                if (part == null)
                {
                    yield break;
                }
                // What Part.AimCamera does, the event behind the button.
                if (!part.canAimCamera)
                {
                    call.Fail("The camera cannot aim at " + part.partInfo.title + ": its window shows no Aim Camera button");
                    yield break;
                }
                FlightCamera.SetTarget(part);
            }
            else
            {
                // What Part.ResetCamera does.
                FlightCamera.SetTarget(FlightGlobals.ActiveVessel);
            }
            yield return null;
            Part target = FlightCamera.fetch.targetMode == FlightCamera.TargetMode.Part
                ? FlightCamera.fetch.partTarget : null;
            call.Text(new Dictionary<string, object>
            {
                { "targetMode", FlightCamera.fetch.targetMode.ToString() },
                { "part", target != null ? (object)(double)target.persistentId : null },
                { "title", target != null ? target.partInfo.title : null }
            });
        }

        private static IEnumerator ConstructionTool(ToolCall call)
        {
            if (!Require.Flight(call))
            {
                yield break;
            }
            EVAConstructionModeController controller = EVAConstructionModeController.Instance;
            if (controller == null)
            {
                call.Fail("No EVA construction in this scene");
                yield break;
            }
            if (call.Has("panel"))
            {
                bool open;
                if (!call.Choice("panel", null, Panels, out open))
                {
                    yield break;
                }
                if (open && !ConstructionOpen(controller))
                {
                    // What the toolbar's button does; the game checks itself that an engineer is on EVA.
                    controller.OpenConstructionPanel();
                    yield return Waits.Until(call, () => ConstructionOpen(controller), PanelSeconds,
                        "The game did not open the construction panel: it needs an engineer on EVA");
                    if (call.IsError)
                    {
                        yield break;
                    }
                }
                else if (!open && controller.IsOpen)
                {
                    controller.ClosePanel();
                    yield return Waits.Until(call, () => !controller.IsOpen, PanelSeconds,
                        "The game did not close the construction panel");
                    if (call.IsError)
                    {
                        yield break;
                    }
                }
            }
            if (call.Has("tool"))
            {
                if (!ConstructionOpen(controller))
                {
                    call.Fail("The construction panel of EVA construction is not open");
                    yield break;
                }
                ConstructionMode mode;
                if (!call.Choice("tool", null, Modes, out mode))
                {
                    yield break;
                }
                Toggle button = null;
                // The buttons show a few frames after the panel opens.
                yield return Waits.Until(call, () =>
                {
                    button = (Toggle)ModeButtons[mode].GetValue(controller.evaToolsUI);
                    // A click reaches the toggle only when it shows and is interactable.
                    return button != null && button.gameObject.activeInHierarchy && button.IsInteractable();
                }, PanelSeconds, "The button of the " + call.String("tool") + " tool does not show");
                if (call.IsError)
                {
                    yield break;
                }
                // The toggle's listener is onXxxButtonInput, as when the player clicks it; the game puts the
                // toggles back on the tool kept when it refuses the change.
                button.isOn = true;
                yield return null;
            }
            Part selected = controller.evaEditor != null ? controller.evaEditor.SelectedPart : null;
            call.Text(new Dictionary<string, object>
            {
                { "panelOpen", ConstructionOpen(controller) },
                { "tool", controller.evaEditor != null ? controller.evaEditor.EVAConstructionMode.ToString() : null },
                { "selectedPart", selected != null ? (object)(double)selected.persistentId : null },
                { "selectedTitle", selected != null ? selected.partInfo.title : null }
            });
        }

        private static IEnumerator ConstructionGizmo(ToolCall call)
        {
            if (!Require.Flight(call))
            {
                yield break;
            }
            EVAConstructionModeController controller = EVAConstructionModeController.Instance;
            if (controller == null || !ConstructionOpen(controller))
            {
                call.Fail("The construction panel of EVA construction is not open");
                yield break;
            }
            EVAConstructionModeEditor editor = controller.evaEditor;
            if (editor.SelectedPart == null)
            {
                call.Fail("No part is selected in EVA construction");
                yield break;
            }
            Vector3 localAxis;
            if (!call.Choice("axis", null, GizmoAxes, out localAxis))
            {
                yield break;
            }
            int steps = (int)call.Number("steps", DefaultGizmoSteps);
            if (steps < 1)
            {
                call.Fail("steps: at least 1");
                yield break;
            }
            switch (editor.EVAConstructionMode)
            {
                case ConstructionMode.Move:
                    yield return DragOffsetGizmo(call, editor, localAxis, steps);
                    break;
                case ConstructionMode.Rotate:
                    yield return DragRotateGizmo(call, editor, localAxis, steps);
                    break;
                default:
                    call.Fail("The Place tool has no gizmo: pick the Move or Rotate tool");
                    break;
            }
        }

        // Drags the Move gizmo along an axis of its own, as GizmoOffsetHandle does from the mouse.
        private static IEnumerator DragOffsetGizmo(ToolCall call, EVAConstructionModeEditor editor, Vector3 localAxis, int steps)
        {
            if (!call.Has("distance"))
            {
                call.Fail("distance: the metres to drag the Move gizmo by");
                yield break;
            }
            float distance = (float)call.Number("distance");
            GizmoOffset gizmo = (GizmoOffset)EditorGizmoOffset.GetValue(editor);
            if (gizmo == null)
            {
                call.Fail("The Move gizmo is not up: the game does not let this part move");
                yield break;
            }
            if (gizmo.IsDragging)
            {
                call.Fail("The Move gizmo is being dragged already");
                yield break;
            }

            // The handle a player would grab: the one that points the most along the axis. A handle reports the
            // drag along its own direction, from the gizmo's centre to itself, as On_MouseDown measures it.
            Vector3 worldAxis = gizmo.transform.rotation * localAxis;
            GizmoOffsetHandle handle = null;
            Vector3 handleAxis = Vector3.zero;
            float best = -1f;
            foreach (GizmoOffsetHandle h in (GizmoOffsetHandle[])OffsetHandles.GetValue(gizmo))
            {
                Vector3 direction = (h.transform.position - gizmo.transform.position).normalized;
                float alignment = Mathf.Abs(Vector3.Dot(direction, worldAxis));
                if (alignment > best)
                {
                    best = alignment;
                    handle = h;
                    handleAxis = direction;
                }
            }
            if (handle == null)
            {
                call.Fail("The Move gizmo has no handle");
                yield break;
            }
            float sign = Mathf.Sign(Vector3.Dot(handleAxis, worldAxis));

            Part part = editor.SelectedPart;
            Vector3 start = part.transform.position;
            float dragged = 0f;
            OffsetMoveStart.Invoke(gizmo, new object[] { handle, handleAxis });
            try
            {
                for (int i = 1; i <= steps; i++)
                {
                    yield return null;
                    if (part == null || gizmo == null)
                    {
                        call.Fail("The part or its gizmo went away during the drag");
                        yield break;
                    }
                    // The distance is counted from where the drag started, as the mouse's is.
                    dragged = sign * distance * i / steps;
                    if (gizmo.useGrid)
                    {
                        dragged -= dragged % gizmo.SnapInterval;
                    }
                    OffsetMove.Invoke(gizmo, new object[] { handle, handleAxis, dragged });
                }
            }
            finally
            {
                // Released whatever happened, or the gizmo would keep its other handles locked.
                if (gizmo != null)
                {
                    OffsetMoveEnd.Invoke(gizmo, new object[] { handle, handleAxis, dragged });
                }
            }
            yield return null;
            if (part == null)
            {
                call.Fail("The part went away after the drag");
                yield break;
            }
            Vector3 moved = part.transform.position - start;
            call.Text(new Dictionary<string, object>
            {
                { "part", (double)part.persistentId },
                { "tool", "Move" },
                { "coordSpace", gizmo.CoordSpace.ToString() },
                { "axis", Vector(worldAxis) },
                { "snap", gizmo.useGrid },
                { "asked", (double)distance },
                { "dragged", (double)(sign * dragged) },
                { "moved", (double)Vector3.Dot(moved, worldAxis) },
                { "movedOffAxis", (double)Vector3.ProjectOnPlane(moved, worldAxis).magnitude },
                { "position", Vector(part.transform.position) }
            });
        }

        // Turns the Rotate gizmo around an axis of its own, as GizmoRotateHandle does from the mouse.
        private static IEnumerator DragRotateGizmo(ToolCall call, EVAConstructionModeEditor editor, Vector3 localAxis, int steps)
        {
            if (!call.Has("angle"))
            {
                call.Fail("angle: the degrees to turn the Rotate gizmo by");
                yield break;
            }
            float angle = (float)call.Number("angle");
            GizmoRotate gizmo = (GizmoRotate)EditorGizmoRotate.GetValue(editor);
            if (gizmo == null)
            {
                call.Fail("The Rotate gizmo is not up: the game does not let this part turn");
                yield break;
            }
            if (gizmo.IsDragging)
            {
                call.Fail("The Rotate gizmo is being dragged already");
                yield break;
            }

            // The ring a player would grab: the one whose axis, in the gizmo's frame, is the axis asked for. A ring
            // reports the turn around its axis in world space, as On_MouseDown measures it.
            GizmoRotateHandle handle = null;
            Vector3 ringAxis = Vector3.zero;
            float best = -1f;
            foreach (GizmoRotateHandle h in (GizmoRotateHandle[])RotateHandles.GetValue(gizmo))
            {
                Vector3 axis = (Vector3)RotateHandleAxis.GetValue(h);
                float alignment = Mathf.Abs(Vector3.Dot(axis.normalized, localAxis));
                if (alignment > best)
                {
                    best = alignment;
                    handle = h;
                    ringAxis = axis;
                }
            }
            if (handle == null)
            {
                call.Fail("The Rotate gizmo has no ring");
                yield break;
            }
            float sign = Mathf.Sign(Vector3.Dot(ringAxis, localAxis));
            Vector3 worldAxis = gizmo.transform.rotation * ringAxis;

            Part part = editor.SelectedPart;
            Quaternion start = part.transform.rotation;
            float turned = 0f;
            RotateStart.Invoke(gizmo, new object[] { handle, worldAxis });
            try
            {
                for (int i = 1; i <= steps; i++)
                {
                    yield return null;
                    if (part == null || gizmo == null)
                    {
                        call.Fail("The part or its gizmo went away during the drag");
                        yield break;
                    }
                    // The angle is counted from where the drag started, as the mouse's is.
                    turned = sign * angle * i / steps;
                    if (gizmo.useAngleSnap)
                    {
                        turned -= turned % gizmo.SnapDegrees;
                    }
                    RotateTurn.Invoke(gizmo, new object[] { handle, worldAxis, turned });
                }
            }
            finally
            {
                // Released whatever happened, or the gizmo would keep its other rings locked.
                if (gizmo != null)
                {
                    RotateEnd.Invoke(gizmo, new object[] { handle, worldAxis, turned });
                }
            }
            yield return null;
            if (part == null)
            {
                call.Fail("The part went away after the drag");
                yield break;
            }
            // The turn the part really made, signed around the axis asked for.
            float rotated;
            Vector3 rotationAxis;
            (part.transform.rotation * Quaternion.Inverse(start)).ToAngleAxis(out rotated, out rotationAxis);
            if (rotated > 180f)
            {
                rotated -= 360f;
            }
            Vector3 askedAxis = sign * worldAxis;
            call.Text(new Dictionary<string, object>
            {
                { "part", (double)part.persistentId },
                { "tool", "Rotate" },
                { "coordSpace", gizmo.CoordSpace.ToString() },
                { "axis", Vector(askedAxis) },
                { "snap", gizmo.useAngleSnap },
                { "asked", (double)angle },
                { "turned", (double)(sign * turned) },
                { "rotated", (double)(rotated * Mathf.Sign(Vector3.Dot(rotationAxis, askedAxis))) },
                { "position", Vector(part.transform.position) }
            });
        }

        private static Dictionary<string, object> Vector(Vector3 v)
        {
            return new Dictionary<string, object> { { "x", (double)v.x }, { "y", (double)v.y }, { "z", (double)v.z } };
        }

        /// <summary>Whether the construction panel of EVA construction is open, its tools ready.</summary>
        private static bool ConstructionOpen(EVAConstructionModeController controller)
        {
            return controller.IsOpen && controller.panelMode == EVAConstructionModeController.PanelMode.Construction
                && controller.evaEditor != null && controller.evaToolsUI != null;
        }

        /// <summary>What the camera sees of a part: its rectangle on screen and what the grid of rays met.</summary>
        private sealed class Sight
        {
            /// <summary>Whether the camera that draws the scene exists and the part is in front of it.</summary>
            public bool InFront;

            /// <summary>The rectangle the part's colliders cover, in pixels from the bottom left corner.</summary>
            public Rect Rect;

            /// <summary>How many rays of the grid fell on the screen, how many met the part, how many met it first.</summary>
            public int Samples;
            public int OnPart;
            public int Visible;

            /// <summary>How many of the rays that met the part first fall where no interface object is drawn.</summary>
            public int Clear;

            /// <summary>Where the mouse would be over the part, from the bottom left corner; null when nowhere.</summary>
            public Vector2? Point;

            /// <summary>The interface objects drawn over that point, the topmost first.</summary>
            public List<string> Interface = new List<string>();
        }

        /// <summary>
        /// Casts a grid of rays from the camera over the rectangle a part covers on screen, and finds a point where
        /// the game would see the mouse over that part, away from the interface when it can.
        /// </summary>
        private static Sight Look(Part part)
        {
            Sight sight = new Sight();
            Camera camera = CameraManager.GetCurrentCamera();
            List<Collider> colliders = Colliders(part);
            if (camera == null || !camera.enabled || colliders.Count == 0)
            {
                return sight;
            }
            // The rectangle of the eight corners of each collider's bounds; a corner behind the camera makes the
            // projection meaningless.
            float xMin = float.MaxValue, yMin = float.MaxValue, xMax = float.MinValue, yMax = float.MinValue;
            foreach (Collider c in colliders)
            {
                Bounds b = c.bounds;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = new Vector3(
                        (i & 1) == 0 ? b.min.x : b.max.x,
                        (i & 2) == 0 ? b.min.y : b.max.y,
                        (i & 4) == 0 ? b.min.z : b.max.z);
                    Vector3 s = camera.WorldToScreenPoint(corner);
                    if (s.z <= 0f)
                    {
                        return sight;
                    }
                    xMin = Mathf.Min(xMin, s.x);
                    yMin = Mathf.Min(yMin, s.y);
                    xMax = Mathf.Max(xMax, s.x);
                    yMax = Mathf.Max(yMax, s.y);
                }
            }
            sight.InFront = true;
            sight.Rect = Rect.MinMaxRect(xMin, yMin, xMax, yMax);

            // The grid covers the part of the rectangle on screen, its rays through the middle of each cell.
            Rect screen = Rect.MinMaxRect(Mathf.Max(xMin, 0f), Mathf.Max(yMin, 0f),
                Mathf.Min(xMax, Screen.width - 1), Mathf.Min(yMax, Screen.height - 1));
            if (screen.width <= 0f || screen.height <= 0f)
            {
                return sight;
            }
            List<Vector2> visible = new List<Vector2>();
            for (int i = 0; i < GridSide; i++)
            {
                for (int j = 0; j < GridSide; j++)
                {
                    Vector2 point = new Vector2(
                        screen.xMin + (i + 0.5f) * screen.width / GridSide,
                        screen.yMin + (j + 0.5f) * screen.height / GridSide);
                    sight.Samples++;
                    Ray ray = camera.ScreenPointToRay(point);
                    RaycastHit hit;
                    bool onPart = false;
                    foreach (Collider c in colliders)
                    {
                        if (c.Raycast(ray, out hit, float.MaxValue))
                        {
                            onPart = true;
                            break;
                        }
                    }
                    if (!onPart)
                    {
                        continue;
                    }
                    sight.OnPart++;
                    if (HoveredPart(camera, point) == part)
                    {
                        sight.Visible++;
                        visible.Add(point);
                        if (InterfaceAt(point).Count == 0)
                        {
                            sight.Clear++;
                        }
                    }
                }
            }
            if (visible.Count == 0)
            {
                return sight;
            }
            // The visible point nearest the middle of the visible ones, the first one free of the interface.
            Vector2 middle = Vector2.zero;
            foreach (Vector2 p in visible)
            {
                middle += p;
            }
            middle /= visible.Count;
            visible.Sort((a, b) => (a - middle).sqrMagnitude.CompareTo((b - middle).sqrMagnitude));
            foreach (Vector2 p in visible)
            {
                List<string> over = InterfaceAt(p);
                if (over.Count == 0)
                {
                    sight.Point = p;
                    return sight;
                }
            }
            sight.Point = visible[0];
            sight.Interface = InterfaceAt(visible[0]);
            return sight;
        }

        /// <summary>
        /// The part the game would see under the mouse at a screen point (from the bottom left corner): what
        /// Mouse.CheckHoveredPart does, without writing the game's statics.
        /// </summary>
        private static Part HoveredPart(Camera camera, Vector2 point)
        {
            Ray ray = camera.ScreenPointToRay(point);
            EVAConstructionModeController controller = EVAConstructionModeController.Instance;
            if (controller != null && controller.evaEditor != null && controller.evaEditor.SelectedPart != null)
            {
                // A part selected in EVA construction: the nearest part met, whatever lies in front, that is not
                // the held part (in the CARGO state).
                RaycastHit[] hits = Physics.RaycastAll(ray, float.MaxValue, Part.layerMask.value);
                Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
                foreach (RaycastHit h in hits)
                {
                    Part p = PartOf(h);
                    if (p != null && p.State != PartStates.CARGO)
                    {
                        return p;
                    }
                }
                return null;
            }
            // Otherwise the first thing met, part or not.
            RaycastHit first;
            if (!Physics.Raycast(ray, out first, float.MaxValue, Part.layerMask.value))
            {
                return null;
            }
            return PartOf(first);
        }

        /// <summary>The part a hit collider belongs to, as Mouse.GetValidHoverPart finds it.</summary>
        private static Part PartOf(RaycastHit hit)
        {
            PartPointer pointer = FlightGlobals.GetPartPointerUpwardsCached(hit.collider.gameObject);
            Part part = pointer != null && pointer.PartReference != null
                ? pointer.PartReference
                : FlightGlobals.GetPartUpwardsCached(hit.collider.gameObject);
            if (part != null && part.State == PartStates.PLACEMENT)
            {
                ModuleCargoPart cargo = part.FindModuleImplementing<ModuleCargoPart>();
                if ((cargo != null && cargo.IsDeployedSciencePart()) || EditorLogic.SelectedPart == part)
                {
                    return null;
                }
            }
            return part;
        }

        /// <summary>The names of the interface objects drawn at a screen point (from the bottom left corner).</summary>
        private static List<string> InterfaceAt(Vector2 point)
        {
            List<string> names = new List<string>();
            EventSystem events = EventSystem.current;
            if (events == null)
            {
                return names;
            }
            List<RaycastResult> hits = new List<RaycastResult>();
            events.RaycastAll(new PointerEventData(events) { position = point }, hits);
            foreach (RaycastResult r in hits)
            {
                if (r.gameObject != null)
                {
                    names.Add(r.gameObject.name);
                }
            }
            return names;
        }

        /// <summary>The colliders of a part that take part in physics: enabled, on an active object.</summary>
        private static List<Collider> Colliders(Part part)
        {
            List<Collider> colliders = new List<Collider>();
            foreach (Collider c in part.GetComponentsInChildren<Collider>(false))
            {
                if (c.enabled)
                {
                    colliders.Add(c);
                }
            }
            return colliders;
        }

        /// <summary>The answer of locate_part.</summary>
        private static Dictionary<string, object> Describe(Part part, Sight sight)
        {
            List<Collider> colliders = Colliders(part);
            int triggers = 0;
            foreach (Collider c in colliders)
            {
                if (c.isTrigger)
                {
                    triggers++;
                }
            }
            EVAConstructionModeController controller = EVAConstructionModeController.Instance;
            bool selected = controller != null && controller.evaEditor != null && controller.evaEditor.SelectedPart == part;
            FlightCamera camera = FlightCamera.fetch;
            Dictionary<string, object> answer = new Dictionary<string, object>
            {
                { "part", (double)part.persistentId },
                { "title", part.partInfo.title },
                { "vessel", part.vessel != null ? part.vessel.vesselName : null },
                { "colliders", colliders.Count },
                { "triggers", triggers },
                { "selectedInConstruction", selected },
                // The part's up axis against the vertical where it is: 0 for a part standing as on the launch pad.
                { "tilt", (double)Vector3.Angle(part.transform.up, (Vector3)FlightGlobals.getUpAxis(part.vessel.mainBody, part.transform.position)) },
                { "inFront", sight.InFront }
            };
            if (sight.InFront)
            {
                // Screen points count y up from the bottom edge; the answer counts it down from the top one.
                answer["rect"] = new Dictionary<string, object>
                {
                    { "x", (double)sight.Rect.xMin },
                    { "y", (double)(Screen.height - sight.Rect.yMax) },
                    { "width", (double)sight.Rect.width },
                    { "height", (double)sight.Rect.height }
                };
            }
            answer["rays"] = sight.Samples;
            answer["raysOnPart"] = sight.OnPart;
            answer["raysShowingPart"] = sight.Visible;
            answer["raysShowingPartClearOfInterface"] = sight.Clear;
            answer["shown"] = sight.OnPart > 0 ? (double)sight.Visible / sight.OnPart : 0.0;
            answer["point"] = sight.Point.HasValue
                ? new Dictionary<string, object>
                {
                    { "x", Math.Round((double)sight.Point.Value.x) },
                    { "y", Math.Round((double)(Screen.height - sight.Point.Value.y)) }
                }
                : null;
            answer["interfaceOverPoint"] = sight.Interface;
            if (camera != null)
            {
                answer["camera"] = new Dictionary<string, object>
                {
                    { "heading", camera.camHdg * 180.0 / Math.PI },
                    { "pitch", camera.camPitch * 180.0 / Math.PI },
                    { "distance", (double)camera.Distance },
                    { "targetMode", camera.targetMode.ToString() }
                };
            }
            return answer;
        }
    }
}
