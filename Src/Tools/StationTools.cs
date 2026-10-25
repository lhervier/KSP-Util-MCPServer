using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using KSP.UI.Screens;
using UnityEngine;
using UnityEngine.UI;

namespace com.github.lhervier.ksp.util.mcpserver
{
    /// <summary>
    /// Tools that read what the player sees of the tracking station and of the dialogs open over a scene, with
    /// where each thing lies on screen, for mouse_click.
    /// </summary>
    internal static class StationTools
    {
        // Private in SpaceTracking: the rows of the list of tracked objects, in the order they are shown.
        private static readonly FieldInfo VesselWidgets = typeof(SpaceTracking).GetField("vesselWidgets",
            BindingFlags.Instance | BindingFlags.NonPublic);

        public static IEnumerable<Tool> All()
        {
            yield return new Tool("read_tracking_station",
                "Reads the tracking station as the player sees it: each row of its list of tracked objects, from " +
                "the top, with its vessel's name and type, whether the row is lit (shown as selected), where it " +
                "lies on screen, and whether it lies within the visible part of the scrolled list (inView); the " +
                "vessel the station has selected, which its Fly, Recover and Terminate buttons act on (none: " +
                "null); and those buttons and the Track button, whether each shows and responds, and where. When " +
                "another mod hides the station's own list and shows its own rows instead, listReplaced is true, " +
                "rows are that mod's rows on screen (each a toggle of a toggle group, labelled with a tracked " +
                "vessel's name), and " +
                "stationRows are the station's own rows, hidden, with their vessel and whether each is lit. Points " +
                "are pixels of the game's window from its top left corner, as mouse_click takes them. Only in the " +
                "tracking station.",
                Schema.Object(), ReadTrackingStation) { Concurrent = true };
            yield return new Tool("read_dialogs",
                "Reads the dialogs open over the scene, in any scene: each one's name, title and message, and its " +
                "buttons with their label, whether each responds, and the middle of each on screen, in pixels of " +
                "the game's window from its top left corner, as mouse_click takes them. An empty list when none is " +
                "open.",
                Schema.Object(), ReadDialogs) { Concurrent = true };
            yield return new Tool("press_dialog_button",
                "Presses a button of a dialog open over the scene, as a click does, then reads the dialogs as " +
                "read_dialogs does: the button by its label, in the dialog whose title or name is given, or in any " +
                "dialog. Refuses a button that does not show or does not respond, and a label shared by buttons of " +
                "several dialogs when no dialog is named.",
                Schema.Object(
                    Schema.P("button", "string", "the button's label, as read_dialogs gives it (case ignored)", true),
                    Schema.P("dialog", "string", "the dialog's title or name, as read_dialogs gives them (case ignored)")),
                PressDialogButton);
        }

        private static IEnumerator ReadTrackingStation(ToolCall call)
        {
            SpaceTracking station = SpaceTracking.Instance;
            if (HighLogic.LoadedScene != GameScenes.TRACKSTATION || station == null)
            {
                call.Fail("Not in the tracking station");
                yield break;
            }
            List<TrackingStationWidget> widgets = new List<TrackingStationWidget>();
            if (VesselWidgets != null && VesselWidgets.GetValue(station) is List<TrackingStationWidget> list)
            {
                widgets.AddRange(list.FindAll(w => w != null));
            }
            // The station's own rows are drawn only under a canvas: a mod that takes the list out of it shows its
            // own rows instead.
            bool replaced = widgets.Count > 0 && widgets.TrueForAll(w => w.GetComponentInParent<Canvas>() == null);
            List<object> rows = replaced ? ReplacementRows(station, widgets) : StationRows(widgets, true);
            Vessel selected = station.SelectedVessel;
            Dictionary<string, object> answer = new Dictionary<string, object>
            {
                { "rows", rows },
                { "listReplaced", replaced },
                { "selected", selected != null ? selected.GetDisplayName() : null },
                { "buttons", new Dictionary<string, object>
                    {
                        { "Fly", ButtonState(station.FlyButton) },
                        { "Recover", ButtonState(station.RecoverButton) },
                        { "Terminate", ButtonState(station.DeleteButton) },
                        { "Track", ButtonState(station.TrackButton) }
                    }
                }
            };
            if (replaced)
            {
                answer["stationRows"] = StationRows(widgets, false);
            }
            call.Text(answer);
        }

        /// <summary>
        /// The station's own rows, in their order: each one's vessel, type and whether it is lit, and, when
        /// <paramref name="withPlace"/> is true, where it lies on screen.
        /// </summary>
        private static List<object> StationRows(List<TrackingStationWidget> widgets, bool withPlace)
        {
            List<object> rows = new List<object>();
            foreach (TrackingStationWidget widget in widgets)
            {
                Dictionary<string, object> row = withPlace
                    ? RowPlace((RectTransform)widget.transform)
                    : new Dictionary<string, object>();
                row["vessel"] = widget.vessel != null ? widget.vessel.GetDisplayName() : null;
                row["type"] = widget.vessel != null ? widget.vessel.vesselType.ToString() : null;
                row["lit"] = widget.toggle != null && widget.toggle.isOn;
                rows.Add(row);
            }
            return rows;
        }

        /// <summary>
        /// The rows a mod shows in place of the station's own list, from the top of the screen: each toggle of a
        /// toggle group of the station's interface, shown and not flat, that holds a text naming the vessel of one
        /// of the station's rows, with that vessel's name and type, whether the toggle is on, and where it lies.
        /// </summary>
        private static List<object> ReplacementRows(SpaceTracking station, List<TrackingStationWidget> widgets)
        {
            List<KeyValuePair<double, object>> found = new List<KeyValuePair<double, object>>();
            Canvas canvas = station.FlyButton != null ? station.FlyButton.GetComponentInParent<Canvas>() : null;
            if (canvas == null)
            {
                return new List<object>();
            }
            HashSet<Toggle> seen = new HashSet<Toggle>();
            // Only components of active objects: what a collapsed group or a closed panel hides is left out.
            foreach (Component component in canvas.rootCanvas.GetComponentsInChildren<Component>())
            {
                string text = TextOf(component);
                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }
                TrackingStationWidget widget = widgets.Find(w => w.vessel != null && Names(w, text.Trim()));
                if (widget == null)
                {
                    continue;
                }
                // The row is the nearest toggle around the text, one of a toggle group, as the rows of a list one
                // of which is selected are: a header that opens a group of rows, labelled with the name of a type
                // or a body that a vessel may also bear, is not one of them.
                Toggle toggle = component.GetComponentInParent<Toggle>();
                if (toggle == null || toggle.group == null || !seen.Add(toggle))
                {
                    continue;
                }
                Dictionary<string, object> row = RowPlace((RectTransform)toggle.transform);
                if ((double)row["width"] < 1 || (double)row["height"] < 1)
                {
                    continue;
                }
                row["vessel"] = widget.vessel.GetDisplayName();
                row["type"] = widget.vessel.vesselType.ToString();
                row["lit"] = toggle.isOn;
                found.Add(new KeyValuePair<double, object>((double)row["top"], row));
            }
            found.Sort((a, b) => a.Key.CompareTo(b.Key));
            return found.ConvertAll(pair => pair.Value);
        }

        /// <summary>Whether <paramref name="text"/> is the name a row of the station shows for its vessel.</summary>
        private static bool Names(TrackingStationWidget widget, string text)
        {
            return text == widget.vessel.GetDisplayName() || text == widget.vessel.vesselName
                || (widget.textName != null && text == widget.textName.text);
        }

        // The text property of each type of component met, null for the types that have none.
        private static readonly Dictionary<System.Type, PropertyInfo> TextProperties =
            new Dictionary<System.Type, PropertyInfo>();

        /// <summary>
        /// The text a component shows, when it is a text of Unity's interface or of TextMeshPro (read by its text
        /// property, so that no reference to TextMeshPro is needed); null for any other component.
        /// </summary>
        private static string TextOf(Component component)
        {
            if (component == null)
            {
                return null;
            }
            System.Type type = component.GetType();
            if (!TextProperties.TryGetValue(type, out PropertyInfo property))
            {
                property = null;
                for (System.Type t = type; t != null; t = t.BaseType)
                {
                    if (t.FullName == "UnityEngine.UI.Text" || t.FullName == "TMPro.TMP_Text")
                    {
                        property = type.GetProperty("text", typeof(string));
                        break;
                    }
                }
                TextProperties[type] = property;
            }
            return property != null ? property.GetValue(component, null) as string : null;
        }

        /// <summary>
        /// Where a row lies on screen, as <see cref="ScreenRect"/> gives it, and whether its middle lies within the
        /// visible part of the scrolled list that holds it (always true outside a scrolled list).
        /// </summary>
        private static Dictionary<string, object> RowPlace(RectTransform rect)
        {
            Dictionary<string, object> place = ScreenRect(rect);
            bool inView = true;
            ScrollRect scroll = rect.GetComponentInParent<ScrollRect>();
            if (scroll != null)
            {
                RectTransform viewport = scroll.viewport != null ? scroll.viewport : (RectTransform)scroll.transform;
                Dictionary<string, object> view = ScreenRect(viewport);
                double y = (double)place["y"];
                double top = (double)view["top"];
                inView = y >= top && y <= top + (double)view["height"];
            }
            place["inView"] = inView;
            return place;
        }

        private static IEnumerator PressDialogButton(ToolCall call)
        {
            string label = call.String("button", "");
            string dialogName = call.String("dialog");
            List<UnityEngine.UI.Button> matches = new List<UnityEngine.UI.Button>();
            foreach (PopupDialog dialog in Object.FindObjectsOfType<PopupDialog>())
            {
                MultiOptionDialog content = dialog.dialogToDisplay;
                if (content == null || content.Options == null)
                {
                    continue;
                }
                if (dialogName != null && !string.Equals(content.title, dialogName, System.StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(content.name, dialogName, System.StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                List<DialogGUIButton> found = new List<DialogGUIButton>();
                foreach (DialogGUIBase option in content.Options)
                {
                    SpaceCenterTools.CollectButtons(option, found);
                }
                foreach (DialogGUIButton button in found)
                {
                    if (button.uiItem != null && button.uiItem.activeInHierarchy
                        && string.Equals(button.OptionText, label, System.StringComparison.OrdinalIgnoreCase))
                    {
                        matches.Add(button.uiItem.GetComponent<UnityEngine.UI.Button>());
                    }
                }
            }
            if (matches.Count == 0)
            {
                call.Fail("No dialog open shows a button " + label);
                yield break;
            }
            if (matches.Count > 1)
            {
                call.Fail(matches.Count + " buttons read " + label + ": name the dialog");
                yield break;
            }
            UnityEngine.UI.Button target = matches[0];
            // A click reaches onClick only on a button that is interactable (Button.Press).
            if (target == null || !target.IsInteractable())
            {
                call.Fail("The button " + label + " does not respond");
                yield break;
            }
            target.onClick.Invoke();
            yield return null;
            yield return ReadDialogs(call);
        }

        private static IEnumerator ReadDialogs(ToolCall call)
        {
            List<object> dialogs = new List<object>();
            foreach (PopupDialog dialog in Object.FindObjectsOfType<PopupDialog>())
            {
                MultiOptionDialog content = dialog.dialogToDisplay;
                List<object> buttons = new List<object>();
                if (content != null && content.Options != null)
                {
                    List<DialogGUIButton> found = new List<DialogGUIButton>();
                    foreach (DialogGUIBase option in content.Options)
                    {
                        SpaceCenterTools.CollectButtons(option, found);
                    }
                    foreach (DialogGUIButton button in found)
                    {
                        if (button.uiItem == null || !button.uiItem.activeInHierarchy)
                        {
                            continue;
                        }
                        Dictionary<string, object> item = ScreenRect((RectTransform)button.uiItem.transform);
                        UnityEngine.UI.Button uiButton = button.uiItem.GetComponent<UnityEngine.UI.Button>();
                        item["label"] = button.OptionText;
                        item["responds"] = uiButton == null || uiButton.interactable;
                        buttons.Add(item);
                    }
                }
                dialogs.Add(new Dictionary<string, object>
                {
                    { "name", content != null ? content.name : "" },
                    { "title", content != null ? content.title : "" },
                    { "message", content != null ? content.message : "" },
                    { "buttons", buttons }
                });
            }
            call.Text(dialogs);
            yield break;
        }

        /// <summary>
        /// Whether a button of the tracking station shows and responds to a click, and the middle of it on
        /// screen; shown false alone when there is no such button.
        /// </summary>
        private static Dictionary<string, object> ButtonState(UnityEngine.UI.Button button)
        {
            if (button == null)
            {
                return new Dictionary<string, object> { { "shown", false } };
            }
            Dictionary<string, object> state = ScreenRect((RectTransform)button.transform);
            state["shown"] = button.gameObject.activeInHierarchy;
            state["responds"] = button.gameObject.activeInHierarchy && button.interactable;
            return state;
        }

        /// <summary>
        /// Where an interface element lies on screen, in pixels of the game's window from its top left corner, y
        /// down: its middle (x, y), its left and top edges, its width and height.
        /// </summary>
        private static Dictionary<string, object> ScreenRect(RectTransform rect)
        {
            Canvas canvas = rect.GetComponentInParent<Canvas>();
            Camera camera = null;
            if (canvas != null && canvas.rootCanvas.renderMode != RenderMode.ScreenSpaceOverlay)
            {
                camera = canvas.rootCanvas.worldCamera;
            }
            Vector3[] corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            // corners[0] is the bottom left corner, corners[2] the top right one; screen points count y up from the
            // bottom edge, the window's pixels down from the top one, as mouse_move does.
            Vector2 bottomLeft = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
            Vector2 topRight = RectTransformUtility.WorldToScreenPoint(camera, corners[2]);
            double left = bottomLeft.x;
            double top = Screen.height - 1 - topRight.y;
            double width = topRight.x - bottomLeft.x;
            double height = topRight.y - bottomLeft.y;
            return new Dictionary<string, object>
            {
                { "x", System.Math.Round(left + width / 2) },
                { "y", System.Math.Round(top + height / 2) },
                { "left", System.Math.Round(left) },
                { "top", System.Math.Round(top) },
                { "width", System.Math.Round(width) },
                { "height", System.Math.Round(height) }
            };
        }
    }
}
