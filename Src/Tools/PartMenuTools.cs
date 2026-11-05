using System;
using System.Collections;
using System.Collections.Generic;
using KSP.UI;
using UnityEngine;
using UnityEngine.UI;

namespace com.github.lhervier.ksp.util.mcpserver
{
    /// <summary>
    /// Tools that work the part action windows (PAWs) of the loaded vessels' parts as the player does with the
    /// mouse: right click on a part or on empty space, the pin, the buttons of a window.
    /// </summary>
    internal static class PartMenuTools
    {
        /// <summary>The input lock FlightDriver sets while the window of KSP does not have the focus.</summary>
        private const string FocusLock = "flightDriver_ApplicationFocus";

        /// <summary>How far from the screen's edges a drag keeps a part action window (its screenEdgeOffset).</summary>
        private const float ScreenEdgeOffset = 20f;

        public static IEnumerable<Tool> All()
        {
            yield return new Tool("list_parts",
                "Lists the parts of the active vessel, or of another loaded vessel: id (its persistent id, which the " +
                "other part tools take), name, title, the resources it holds with their amount and capacity, and " +
                "whether its part action window is open and pinned, and for a wheel, whether it touches the ground. On " +
                "EVA, the active vessel is the kerbal: name the " +
                "vessel to list the parts of the one being built.",
                Schema.Object(
                    Schema.P("vessel", "string", "the vessel's id or name, as list_vessels gives them (default: the active vessel)")),
                ListParts) { Concurrent = true };
            yield return new Tool("part_menu",
                "Works the part action windows of the loaded vessels' parts as the mouse does, then reads them as " +
                "read_part_menus does. open: a right click on the part, without the modifier key, so the other " +
                "windows close unless pinned. pin, unpin: the pin of the part's open window. move: drags the part's " +
                "open window by its title bar until its top left corner is at x, y (pixels from the top left corner " +
                "of the game's window), kept on screen as a drag keeps it. close: a right click on empty space, " +
                "without the modifier key: every window not pinned closes.",
                Schema.Object(
                    Schema.P("action", "string", "open, pin, unpin, move or close", true),
                    Schema.P("part", "number", "the part's id, as list_parts gives it (not for close)"),
                    Schema.P("x", "number", "move: pixels from the left edge of the game's window"),
                    Schema.P("y", "number", "move: pixels from the top edge of the game's window")),
                PartMenu);
            yield return new Tool("read_part_menus",
                "Reads the open part action windows: for each, the part's id and title, whether it is pinned, where " +
                "it is on screen (x, y of its top left corner and its size, in pixels from the top left corner of the " +
                "game's window), and the items it shows: buttons (their label and the name of their event), fields " +
                "(their label), resources " +
                "(amount and capacity), and " +
                "the resource transfer controls: which of their In, Out and Stop buttons show, their state (None, " +
                "In, Out) and how many controls of other parts they are linked with.",
                Schema.Object(), ReadPartMenus) { Concurrent = true };
            yield return new Tool("press_part_button",
                "Presses a button of a part's open part action window, as a click does, then reads the windows as " +
                "read_part_menus does: a button by its label or by the name of its event (the same in every language " +
                "of the game), or the In, Out or Stop button of a resource's transfer " +
                "control. Refuses a button the window does not show, and any input lock that makes the game ignore " +
                "the click but the one KSP sets when its window loses the focus.",
                Schema.Object(
                    Schema.P("part", "number", "the part's id, as list_parts gives it", true),
                    Schema.P("button", "string",
                        "the label of a button, or the name of its event, as read_part_menus gives them (case ignored)"),
                    Schema.P("resource", "string", "the resource of a transfer control, by name (LiquidFuel, Oxidizer...)"),
                    Schema.P("transfer", "string", "in, out or stop: the transfer control's button to press")),
                PressPartButton);
            yield return new Tool("set_part_menu_value",
                "Sets a slider of a part's open part action window to a number, as typing it in the slider's numeric " +
                "box does: the game moves the slider there and handles the change as when the player drags it, " +
                "rounded to the slider's step and kept within its range, on the part's symmetry counterparts too " +
                "when the game does so. The slider by its label or by the name of its field (the same in every " +
                "language of the game), as read_part_menus gives them. Then reads the windows as read_part_menus " +
                "does. Refuses a slider the window does not show, and any input lock that makes the game ignore the " +
                "slider but the one KSP sets when its window loses the focus.",
                Schema.Object(
                    Schema.P("part", "number", "the part's id, as list_parts gives it", true),
                    Schema.P("field", "string",
                        "the label of the slider, or the name of its field, as read_part_menus gives them (case ignored)",
                        true),
                    Schema.P("value", "number", "the number to set", true)),
                SetPartMenuValue);
            yield return new Tool("list_docking_ports",
                "Lists the docking ports of the active vessel, or of another loaded vessel: the part's id, as the " +
                "other part tools take it, its title, the state of the port as the game names it, the id of the port " +
                "it is docked to, and whether its part action window offers to undock: of two ports docked " +
                "together, the game offers it on one only, which press_part_button then undocks with its Undock " +
                "button.",
                Schema.Object(
                    Schema.P("vessel", "string", "the vessel's id or name, as list_vessels gives them (default: the active vessel)")),
                ListDockingPorts) { Concurrent = true };
        }

        private static IEnumerator ListParts(ToolCall call)
        {
            if (!Require.Flight(call))
            {
                yield break;
            }
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (call.Has("vessel"))
            {
                string error;
                vessel = Lookup.FindVessel(call.String("vessel"), out error);
                if (vessel == null)
                {
                    call.Fail(error);
                    yield break;
                }
                if (!vessel.loaded)
                {
                    call.Fail(vessel.vesselName + " is not loaded: its parts are not in the scene");
                    yield break;
                }
            }
            List<object> parts = new List<object>();
            foreach (Part p in vessel.parts)
            {
                List<object> resources = new List<object>();
                foreach (PartResource r in p.Resources)
                {
                    resources.Add(new Dictionary<string, object>
                    {
                        { "name", r.resourceName },
                        { "amount", r.amount },
                        { "max", r.maxAmount }
                    });
                }
                UIPartActionWindow window = OpenWindow(p);
                Dictionary<string, object> part = new Dictionary<string, object>
                {
                    { "id", (double)p.persistentId },
                    { "name", p.partInfo.name },
                    { "title", p.partInfo.title },
                    { "resources", resources },
                    { "menuOpen", window != null },
                    { "menuPinned", window != null && window.Pinned }
                };
                ModuleWheelBase wheel = p.FindModuleImplementing<ModuleWheelBase>();
                if (wheel != null)
                {
                    part["wheel"] = new Dictionary<string, object> { { "grounded", wheel.isGrounded } };
                }
                parts.Add(part);
            }
            call.Text(parts);
        }

        private static IEnumerator PartMenu(ToolCall call)
        {
            if (!Require.Flight(call))
            {
                yield break;
            }
            UIPartActionController controller = UIPartActionController.Instance;
            string action = call.String("action", "").ToLowerInvariant();
            if (action == "close")
            {
                // What a right click on empty space does without the modifier key.
                controller.Deselect(false);
            }
            else if (action == "move")
            {
                Part part = FindPart(call);
                if (part == null)
                {
                    yield break;
                }
                UIPartActionWindow window = OpenWindow(part);
                if (window == null)
                {
                    call.Fail("The part action window of " + part.partInfo.title + " is not open");
                    yield break;
                }
                if (!call.Has("x") || !call.Has("y"))
                {
                    call.Fail("Give x and y, the pixels from the top left corner of the game's window");
                    yield break;
                }
                Move(window, (float)call.Number("x"), (float)call.Number("y"));
            }
            else if (action == "open" || action == "pin" || action == "unpin")
            {
                Part part = FindPart(call);
                if (part == null)
                {
                    yield break;
                }
                if (action == "open")
                {
                    // What a right click on the part does (HandleMouseClick, then SelectPart).
                    controller.SpawnPartActionWindow(part);
                }
                else
                {
                    UIPartActionWindow window = OpenWindow(part);
                    if (window == null)
                    {
                        call.Fail("The part action window of " + part.partInfo.title + " is not open");
                        yield break;
                    }
                    // The toggle's listener is the window's OnPin, as when the player clicks it.
                    window.togglePinned.isOn = action == "pin";
                }
            }
            else
            {
                call.Fail("Unknown action " + call.String("action") + ": open, pin, unpin, move or close");
                yield break;
            }
            // Let the windows update once before reading them.
            yield return null;
            call.Text(Menus());
        }

        private static IEnumerator ReadPartMenus(ToolCall call)
        {
            if (!Require.Flight(call))
            {
                yield break;
            }
            call.Text(Menus());
        }

        private static IEnumerator PressPartButton(ToolCall call)
        {
            if (!Require.Flight(call))
            {
                yield break;
            }
            Part part = FindPart(call);
            if (part == null)
            {
                yield break;
            }
            // The lock the game sets whenever its window loses the focus is lifted for the click, as set_target
            // ignores it: the player's click would have given the focus back. While it holds, the windows do
            // not even show the buttons that need control of the vessel: they get two frames to build them.
            bool focusLocked = InputLockManager.lockStack.ContainsKey(FocusLock);
            if (focusLocked)
            {
                InputLockManager.RemoveControlLock(FocusLock);
                yield return null;
                yield return null;
            }
            string failure = Press(call, part);
            if (focusLocked)
            {
                InputLockManager.SetControlLock(FocusLock);
            }
            if (failure != null)
            {
                call.Fail(failure);
                yield break;
            }
            yield return null;
            call.Text(Menus());
        }

        private static IEnumerator SetPartMenuValue(ToolCall call)
        {
            if (!Require.Flight(call))
            {
                yield break;
            }
            Part part = FindPart(call);
            if (part == null)
            {
                yield break;
            }
            // The focus lock is lifted as press_part_button lifts it: the player's click would have given the focus
            // back.
            bool focusLocked = InputLockManager.lockStack.ContainsKey(FocusLock);
            if (focusLocked)
            {
                InputLockManager.RemoveControlLock(FocusLock);
                yield return null;
                yield return null;
            }
            string failure = Slide(call, part);
            if (focusLocked)
            {
                InputLockManager.SetControlLock(FocusLock);
            }
            if (failure != null)
            {
                call.Fail(failure);
                yield break;
            }
            yield return null;
            call.Text(Menus());
        }

        /// <summary>
        /// Sets the slider the call names in the open window of a part to the call's value, as typing it in the
        /// slider's numeric box does. Returns null once set, or why it could not be: no open window, no such slider
        /// shown, or an input lock that makes the game ignore it.
        /// </summary>
        private static string Slide(ToolCall call, Part part)
        {
            UIPartActionWindow window = OpenWindow(part);
            if (window == null)
            {
                return "The part action window of " + part.partInfo.title + " is not open";
            }
            string label = call.String("field");
            UIPartActionFloatRange item = null;
            foreach (UIPartActionItem i in window.ListItems)
            {
                UIPartActionFloatRange range = i as UIPartActionFloatRange;
                if (range != null && range.gameObject.activeInHierarchy && range.Field != null
                    && (string.Equals(range.Field.guiName, label, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(range.Field.name, label, StringComparison.OrdinalIgnoreCase)))
                {
                    item = range;
                    break;
                }
            }
            if (item == null)
            {
                return "The window of " + part.partInfo.title + " shows no slider " + label;
            }
            if (!call.Has("value"))
            {
                return "Give the value to set";
            }
            UI_FloatRange control = (UI_FloatRange)item.Control;
            // The slider's listener ignores a change while the tweakables are locked (OnValueChanged), the lock of
            // full control for a slider that requires it.
            ControlTypes locks = control.requireFullControl ? ControlTypes.TWEAKABLES_FULLONLY
                : ControlTypes.TWEAKABLES_ANYCONTROL;
            if (!InputLockManager.IsUnlocked(locks))
            {
                return "The game ignores the sliders of part action windows now: input locked by " +
                    string.Join(", ", new List<string>(InputLockManager.lockStack.Keys).ToArray());
            }
            // What the numeric box does with the number typed (OnFieldInput): it moves the slider, whose listener
            // rounds to the step, writes the field and calls the game's handlers, as for a drag.
            float value = (float)call.Number("value");
            item.slider.value = Mathf.Clamp01((value - control.minValue) / (control.maxValue - control.minValue));
            return null;
        }

        private static IEnumerator ListDockingPorts(ToolCall call)
        {
            if (!Require.Flight(call))
            {
                yield break;
            }
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (call.Has("vessel"))
            {
                string error;
                vessel = Lookup.FindVessel(call.String("vessel"), out error);
                if (vessel == null)
                {
                    call.Fail(error);
                    yield break;
                }
                if (!vessel.loaded)
                {
                    call.Fail(vessel.vesselName + " is not loaded: its parts are not in the scene");
                    yield break;
                }
            }
            List<object> ports = new List<object>();
            foreach (Part p in vessel.parts)
            {
                foreach (ModuleDockingNode node in p.FindModulesImplementing<ModuleDockingNode>())
                {
                    BaseEvent undock = node.Events["Undock"];
                    ports.Add(new Dictionary<string, object>
                    {
                        { "id", (double)p.persistentId },
                        { "title", p.partInfo.title },
                        { "state", node.fsm != null ? node.fsm.currentStateName : null },
                        { "dockedTo", node.otherNode != null && node.otherNode.part != null
                            ? (object)(double)node.otherNode.part.persistentId : null },
                        // The window shows an event's button while the event is active and shown in flight.
                        { "offersUndock", undock != null && undock.active && undock.guiActive }
                    });
                }
            }
            call.Text(ports);
        }

        /// <summary>
        /// Clicks the button the call names in the open window of a part. Returns null once clicked, or why it
        /// could not be: no open window, no such button shown, or an input lock that makes the game ignore it.
        /// </summary>
        private static string Press(ToolCall call, Part part)
        {
            UIPartActionWindow window = OpenWindow(part);
            if (window == null)
            {
                return "The part action window of " + part.partInfo.title + " is not open";
            }
            Button button;
            string what;
            if (call.Has("transfer"))
            {
                string resource = call.String("resource");
                UIPartActionResourceTransfer control = null;
                foreach (UIPartActionItem item in window.ListItems)
                {
                    UIPartActionResourceTransfer t = item as UIPartActionResourceTransfer;
                    if (t != null && t.Resource != null && string.Equals(t.Resource.resourceName, resource, StringComparison.OrdinalIgnoreCase))
                    {
                        control = t;
                        break;
                    }
                }
                if (control == null)
                {
                    return "The window of " + part.partInfo.title + " shows no transfer control for " + resource;
                }
                string transfer = call.String("transfer").ToLowerInvariant();
                if (transfer == "in")
                {
                    button = control.flowInBtn;
                }
                else if (transfer == "out")
                {
                    button = control.flowOutBtn;
                }
                else if (transfer == "stop")
                {
                    button = control.flowStopBtn;
                }
                else
                {
                    return "Unknown transfer button " + call.String("transfer") + ": in, out or stop";
                }
                what = transfer + " of " + resource;
            }
            else if (call.Has("button"))
            {
                string label = call.String("button");
                button = null;
                foreach (UIPartActionItem item in window.ListItems)
                {
                    UIPartActionButton b = item as UIPartActionButton;
                    if (b != null && b.gameObject.activeInHierarchy && b.Evt != null
                        && (string.Equals(b.Evt.guiName, label, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(b.Evt.name, label, StringComparison.OrdinalIgnoreCase)))
                    {
                        button = b.button;
                        break;
                    }
                }
                if (button == null)
                {
                    return "The window of " + part.partInfo.title + " shows no button " + label;
                }
                what = label;
            }
            else
            {
                return "Give a button, or a resource and a transfer button";
            }
            // A click reaches onClick only on a button that shows and is interactable (Button.Press).
            if (!button.gameObject.activeInHierarchy || !button.IsInteractable())
            {
                return "The button " + what + " does not show in the window of " + part.partInfo.title;
            }
            // The window's buttons ignore a click while the tweakables are locked.
            if (!InputLockManager.IsUnlocked(ControlTypes.TWEAKABLES_ANYCONTROL))
            {
                return "The game ignores the buttons of part action windows now: input locked by " +
                    string.Join(", ", new List<string>(InputLockManager.lockStack.Keys).ToArray());
            }
            button.onClick.Invoke();
            return null;
        }

        /// <summary>
        /// Returns the part of a loaded vessel whose persistent id the call's "part" argument gives, or null
        /// after failing the call.
        /// </summary>
        internal static Part FindPart(ToolCall call)
        {
            if (!call.Has("part"))
            {
                call.Fail("Give the part's id, as list_parts gives it");
                return null;
            }
            uint id = (uint)call.Number("part");
            foreach (Vessel v in FlightGlobals.VesselsLoaded)
            {
                foreach (Part p in v.parts)
                {
                    if (p.persistentId == id)
                    {
                        return p;
                    }
                }
            }
            call.Fail("No loaded vessel has a part of id " + id);
            return null;
        }

        /// <summary>
        /// Drags a part action window until its top left corner is at (x, y), in pixels from the top left corner
        /// of the game's window, as one drag of its title bar would: kept on screen the way a drag keeps it.
        /// </summary>
        private static void Move(UIPartActionWindow window, float x, float y)
        {
            RectTransform rect = (RectTransform)window.transform;
            Camera camera = CanvasCamera(rect);
            Vector3[] corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            // corners[1] is the top left corner; screen points count y from the bottom.
            Vector3 target;
            RectTransformUtility.ScreenPointToWorldPointInRectangle(rect, new Vector2(x, Screen.height - y), camera,
                out target);
            Vector3 delta = target - corners[1];
            // What OnDrag of the window does with the mouse's move.
            UIMasterController.DragTooltip(rect, new Vector2(delta.x, delta.y), Vector2.one * ScreenEdgeOffset);
        }

        /// <summary>
        /// Returns where a part action window is on screen: its top left corner and its size, in pixels from the
        /// top left corner of the game's window.
        /// </summary>
        private static Dictionary<string, object> ScreenRect(UIPartActionWindow window)
        {
            RectTransform rect = (RectTransform)window.transform;
            Camera camera = CanvasCamera(rect);
            Vector3[] corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            // corners[0] is the bottom left corner, corners[2] the top right one.
            Vector2 bottomLeft = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
            Vector2 topRight = RectTransformUtility.WorldToScreenPoint(camera, corners[2]);
            return new Dictionary<string, object>
            {
                { "x", (double)bottomLeft.x },
                { "y", (double)(Screen.height - topRight.y) },
                { "width", (double)(topRight.x - bottomLeft.x) },
                { "height", (double)(topRight.y - bottomLeft.y) }
            };
        }

        /// <summary>The camera that draws the canvas of a user interface element, null for an overlay canvas.</summary>
        private static Camera CanvasCamera(RectTransform rect)
        {
            Canvas canvas = rect.GetComponentInParent<Canvas>().rootCanvas;
            return canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        }

        /// <summary>Returns the open part action window of a part, or null when it has none open.</summary>
        private static UIPartActionWindow OpenWindow(Part part)
        {
            UIPartActionController controller = UIPartActionController.Instance;
            if (controller == null)
            {
                return null;
            }
            foreach (UIPartActionWindow w in controller.windows)
            {
                if (w != null && w.part == part && w.gameObject.activeInHierarchy)
                {
                    return w;
                }
            }
            return null;
        }

        /// <summary>Describes the open part action windows and the items each one shows.</summary>
        private static List<object> Menus()
        {
            List<object> menus = new List<object>();
            UIPartActionController controller = UIPartActionController.Instance;
            if (controller == null)
            {
                return menus;
            }
            foreach (UIPartActionWindow w in controller.windows)
            {
                if (w == null || w.part == null || !w.gameObject.activeInHierarchy)
                {
                    continue;
                }
                List<object> items = new List<object>();
                foreach (UIPartActionItem item in w.ListItems)
                {
                    if (item == null || !item.gameObject.activeInHierarchy)
                    {
                        continue;
                    }
                    items.Add(Describe(item));
                }
                menus.Add(new Dictionary<string, object>
                {
                    { "part", (double)w.part.persistentId },
                    { "title", w.part.partInfo.title },
                    { "pinned", w.Pinned },
                    { "rect", ScreenRect(w) },
                    { "display", w.Display.ToString() },
                    { "items", items }
                });
            }
            return menus;
        }

        /// <summary>Describes one item of a part action window.</summary>
        private static Dictionary<string, object> Describe(UIPartActionItem item)
        {
            // The transfer control first: it is a resource item too.
            UIPartActionResourceTransfer transfer = item as UIPartActionResourceTransfer;
            if (transfer != null)
            {
                List<object> buttons = new List<object>();
                if (transfer.flowInBtn.gameObject.activeInHierarchy)
                {
                    buttons.Add("in");
                }
                if (transfer.flowOutBtn.gameObject.activeInHierarchy)
                {
                    buttons.Add("out");
                }
                if (transfer.flowStopBtn.gameObject.activeInHierarchy)
                {
                    buttons.Add("stop");
                }
                return new Dictionary<string, object>
                {
                    { "kind", "transfer" },
                    { "resource", transfer.Resource != null ? transfer.Resource.resourceName : null },
                    { "buttons", buttons },
                    { "state", transfer.State.ToString() },
                    { "targets", transfer.Targets != null ? transfer.Targets.Count : 0 }
                };
            }
            UIPartActionResourceItem resource = item as UIPartActionResourceItem;
            if (resource != null)
            {
                PartResource r = resource.Resource;
                return new Dictionary<string, object>
                {
                    { "kind", "resource" },
                    { "resource", r != null ? r.resourceName : null },
                    { "amount", r != null ? r.amount : double.NaN },
                    { "max", r != null ? r.maxAmount : double.NaN }
                };
            }
            UIPartActionButton button = item as UIPartActionButton;
            if (button != null)
            {
                return new Dictionary<string, object>
                {
                    { "kind", "button" },
                    { "label", button.Evt != null ? button.Evt.guiName : null },
                    { "event", button.Evt != null ? button.Evt.name : null }
                };
            }
            UIPartActionFloatRange range = item as UIPartActionFloatRange;
            UI_FloatRange rangeControl = range != null ? range.Control as UI_FloatRange : null;
            if (rangeControl != null && range.Field != null)
            {
                object value = range.Field.GetValue(range.Field.host);
                return new Dictionary<string, object>
                {
                    { "kind", "slider" },
                    { "label", range.Field.guiName },
                    { "name", range.Field.name },
                    { "value", value is float ? (object)(double)(float)value : null },
                    { "min", (double)rangeControl.minValue },
                    { "max", (double)rangeControl.maxValue },
                    { "step", (double)rangeControl.stepIncrement }
                };
            }
            UIPartActionFieldItem field = item as UIPartActionFieldItem;
            if (field != null)
            {
                return new Dictionary<string, object>
                {
                    { "kind", "field" },
                    { "label", field.Field != null ? field.Field.guiName : null },
                    { "name", field.Field != null ? field.Field.name : null }
                };
            }
            return new Dictionary<string, object> { { "kind", item.GetType().Name } };
        }
    }
}
