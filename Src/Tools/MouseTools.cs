using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.EventSystems;

namespace com.github.lhervier.ksp.util.mcpserver
{
    /// <summary>
    /// Tools that work the mouse over the game's window as a player's mouse does: the game reads them through
    /// its own input, whether its window has the focus or not, and whether the Windows session is locked or
    /// not. The cursor of Windows moves with the game's pointer. Windows only.
    /// </summary>
    internal static class MouseTools
    {
        private const uint WM_MOUSEMOVE = 0x0200;
        private const uint WM_LBUTTONDOWN = 0x0201;
        private const uint WM_LBUTTONUP = 0x0202;
        private const uint WM_RBUTTONDOWN = 0x0204;
        private const uint WM_RBUTTONUP = 0x0205;
        private const int MK_LBUTTON = 0x0001;
        private const int MK_RBUTTON = 0x0002;

        /// <summary>The window class of the Unity player's window.</summary>
        private const string UnityWindowClass = "UnityWndClass";

        /// <summary>How long the game may take to see the pointer move, in seconds of real time.</summary>
        private const float MoveSeconds = 2f;

        /// <summary>How long the game may take to see a button go down or up, in seconds of real time.</summary>
        private const float ButtonSeconds = 2f;

        /// <summary>How many of the interface objects under the pointer an answer lists, the topmost first.</summary>
        private const int MaxObjects = 10;

        private static readonly Dictionary<string, Button> Buttons = new Dictionary<string, Button>
        {
            { "left", new Button(0, WM_LBUTTONDOWN, WM_LBUTTONUP, MK_LBUTTON) },
            { "right", new Button(1, WM_RBUTTONDOWN, WM_RBUTTONUP, MK_RBUTTON) }
        };

        /// <summary>The input lock FlightDriver sets while the window of KSP does not have the focus.</summary>
        private const string FocusLock = "flightDriver_ApplicationFocus";

        /// <summary>The field behind EventSystem.isFocused, which has no setter.</summary>
        private static readonly FieldInfo HasFocus =
            typeof(EventSystem).GetField("m_HasFocus", BindingFlags.Instance | BindingFlags.NonPublic);

        public static IEnumerable<Tool> All()
        {
            yield return new Tool("mouse_move",
                "Moves the mouse pointer to x, y of the game's window, as a mouse moved over the window does, in any " +
                "scene, whether the window has the focus or not, the Windows session locked or not: the cursor of " +
                "Windows moves there too, since the game reads its position. x and y are pixels of the " +
                "game's window, counted from its top left corner (the frame and title bar left out): x to the right, " +
                "y down. Unity's Input.mousePosition counts y up from the bottom edge instead. Refuses a point out of " +
                "the window. Answers with the pointer's position as Unity reads it and the interface objects under it.",
                Schema.Object(
                    Schema.P("x", "number", "pixels from the left edge of the game's window", true),
                    Schema.P("y", "number", "pixels from the top edge of the game's window", true)),
                MouseMove);
            yield return new Tool("mouse_click",
                "Clicks at x, y as a mouse does: moves the pointer there as mouse_move does, then presses and releases " +
                "the button, and answers once the game has seen both, with the pointer's position as Unity reads it " +
                "and the interface objects under it. x and y as mouse_move takes them. The pointer stays there. With " +
                "count 2, a double click: a second click right after the first, as a player's double click.",
                Schema.Object(
                    Schema.P("x", "number", "pixels from the left edge of the game's window", true),
                    Schema.P("y", "number", "pixels from the top edge of the game's window", true),
                    Schema.P("button", "string", "left or right (default left)"),
                    Schema.P("count", "number", "1 for a click, 2 for a double click (default 1)")),
                MouseClick);
        }

        private static IEnumerator MouseMove(ToolCall call)
        {
            Target target;
            if (!Find(call, out target))
            {
                yield break;
            }
            yield return Move(call, target);
            if (call.IsError)
            {
                yield break;
            }
            call.Text(Report(target, null));
        }

        private static IEnumerator MouseClick(ToolCall call)
        {
            Button button;
            if (!call.Choice("button", "left", Buttons, out button))
            {
                yield break;
            }
            Target target;
            if (!Find(call, out target))
            {
                yield break;
            }
            yield return Move(call, target);
            if (call.IsError)
            {
                yield break;
            }

            int count = (int)call.Number("count", 1);
            if (count != 1 && count != 2)
            {
                call.Fail("count: 1 for a click, 2 for a double click");
                yield break;
            }
            // The lock the game sets on every control whenever its window loses the focus is lifted for the click,
            // as press_part_button does: the player's click would have given the focus back. While it holds, the
            // EVA construction tools, among others, refuse to change.
            bool focusLocked = InputLockManager.lockStack.ContainsKey(FocusLock);
            if (focusLocked)
            {
                InputLockManager.RemoveControlLock(FocusLock);
            }
            try
            {
                yield return Press(call, target, button, count);
            }
            finally
            {
                if (focusLocked)
                {
                    InputLockManager.SetControlLock(FocusLock);
                }
            }
            if (call.IsError)
            {
                yield break;
            }
            call.Text(Report(target, call.String("button", "left").ToLowerInvariant()));
        }

        /// <summary>Presses and releases the button count times at the target, each time once the game has seen it.</summary>
        private static IEnumerator Press(ToolCall call, Target target, Button button, int count)
        {
            for (int i = 0; i < count; i++)
            {
                // The button goes down, then up once the game has seen it down for a frame: a press as short as a
                // player's, which every handler of the game sees, whether it acts on the press or on the release.
                Post(target, button.Down, (IntPtr)button.Flag);
                yield return Waits.Until(call, () => Input.GetMouseButton(button.Index), ButtonSeconds,
                    "The game did not see the button go down");
                if (call.IsError)
                {
                    yield break;
                }
                yield return null;
                Post(target, button.Up, IntPtr.Zero);
                yield return Waits.Until(call, () => !Input.GetMouseButton(button.Index), ButtonSeconds,
                    "The game did not see the button go up");
                if (call.IsError)
                {
                    yield break;
                }
                // One more frame for the event system to send the click; the second click of a double click
                // follows at once, well within the time the game allows between the two.
                yield return null;
            }
        }

        /// <summary>
        /// Reads the point the call names and the game's window. Returns false after failing the call when
        /// either is missing, or when the point lies out of the window.
        /// </summary>
        private static bool Find(ToolCall call, out Target target)
        {
            target = new Target();
            if (!call.Has("x") || !call.Has("y"))
            {
                call.Fail("Give x and y, the pixels from the top left corner of the game's window");
                return false;
            }
            target.X = (int)Math.Round(call.Number("x"));
            target.Y = (int)Math.Round(call.Number("y"));
            target.Window = GameWindow();
            if (target.Window == IntPtr.Zero)
            {
                call.Fail("The window of the game was not found (Windows only)");
                return false;
            }
            // The size of the window's client area, in the pixels window messages count in.
            ClientRect client;
            GetClientRect(target.Window, out client);
            target.Width = client.Right - client.Left;
            target.Height = client.Bottom - client.Top;
            if (target.X < 0 || target.Y < 0 || target.X >= target.Width || target.Y >= target.Height)
            {
                call.Fail("x, y = " + target.X + ", " + target.Y + " lies out of the game's window, which is "
                    + target.Width + " x " + target.Height + " pixels: x from 0 to " + (target.Width - 1)
                    + ", y from 0 to " + (target.Height - 1));
                return false;
            }
            return true;
        }

        /// <summary>
        /// Moves the game's pointer to the target, and returns once the game and its interface have seen it there.
        /// Fails the call when the game still reads it elsewhere after a while.
        /// </summary>
        private static IEnumerator Move(ToolCall call, Target target)
        {
            // Unity reads the pointer's position from the cursor of Windows, but only when its window gets a mouse
            // message, which Windows no longer sends once the session is locked, nor to a window the cursor is not
            // over: the cursor is put there, then the move posted. A click posted without it lands where the
            // pointer was.
            FocusEventSystem();
            ScreenPoint point = new ScreenPoint { X = target.X, Y = target.Y };
            ClientToScreen(target.Window, ref point);
            SetCursorPos(point.X, point.Y);
            Post(target, WM_MOUSEMOVE, IntPtr.Zero);
            // Input.mousePosition counts y up from the bottom edge.
            Vector2 expected = new Vector2(target.X, target.Height - 1 - target.Y);
            yield return Waits.Until(call, () => (Vector2)Input.mousePosition == expected, MoveSeconds,
                "The game did not see the pointer move to " + target.X + ", " + target.Y);
            if (call.IsError)
            {
                call.Text("Input.mousePosition reads " + Input.mousePosition.x + ", " + Input.mousePosition.y);
                yield break;
            }
            // The event system updates the objects under the pointer in the frame the game reads it; one more
            // frame leaves room for the order of the scripts.
            yield return null;
        }

        /// <summary>
        /// Makes the event system take the pointer as it does in a window with the focus. Once the game's window
        /// has lost the focus (the player clicked another window), the event system says so, and the input module
        /// then ignores the pointer: no hover, no click on the interface. It stays so until the game's window gets
        /// the focus back, or the game loses it again.
        /// </summary>
        private static void FocusEventSystem()
        {
            EventSystem events = EventSystem.current;
            if (events == null || events.isFocused || HasFocus == null)
            {
                return;
            }
            // EventSystem.OnApplicationFocus is the only writer of isFocused.
            HasFocus.SetValue(events, true);
        }

        /// <summary>
        /// The pointer as Unity reads it, the size of the window, and the interface objects under the pointer:
        /// the one the event system has it over, and every one a ray from it meets, the topmost first, each with
        /// the object a click on it reaches. Adds the button clicked when one is given.
        /// </summary>
        private static Dictionary<string, object> Report(Target target, string button)
        {
            Vector3 mouse = Input.mousePosition;
            Dictionary<string, object> report = new Dictionary<string, object>
            {
                { "x", target.X },
                { "y", target.Y },
                { "window", new Dictionary<string, object> { { "width", target.Width }, { "height", target.Height } } },
                { "mousePosition", new Dictionary<string, object> { { "x", (double)mouse.x }, { "y", (double)mouse.y } } }
            };
            if (button != null)
            {
                report["button"] = button;
            }
            EventSystem events = EventSystem.current;
            if (events == null)
            {
                report["ui"] = new List<object>();
                return report;
            }
            // KSP's input module exposes the pointer's last event: the object the pointer is over for it.
            StandaloneInputModuleCustom module = events.currentInputModule as StandaloneInputModuleCustom;
            PointerEventData last = module != null ? module.GetLastPointerEventDataPublic(PointerInputModule.kMouseLeftId) : null;
            if (last != null && last.pointerEnter != null)
            {
                report["pointerEnter"] = last.pointerEnter.name;
            }
            List<RaycastResult> hits = new List<RaycastResult>();
            events.RaycastAll(new PointerEventData(events) { position = mouse }, hits);
            List<object> objects = new List<object>();
            for (int i = 0; i < hits.Count && i < MaxObjects; i++)
            {
                GameObject hit = hits[i].gameObject;
                Dictionary<string, object> item = new Dictionary<string, object>
                {
                    { "name", hit.name },
                    { "path", PathOf(hit.transform) }
                };
                // The object a click reaches is the first one up the hierarchy that handles it, as the event
                // system finds it.
                GameObject handler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit)
                    ?? ExecuteEvents.GetEventHandler<IPointerDownHandler>(hit);
                if (handler != null)
                {
                    item["clickHandler"] = handler.name;
                }
                objects.Add(item);
            }
            report["ui"] = objects;
            return report;
        }

        // The names of an object and of its parents, the root first, its canvas included.
        private static string PathOf(Transform transform)
        {
            StringBuilder path = new StringBuilder(transform.name);
            for (Transform t = transform.parent; t != null; t = t.parent)
            {
                path.Insert(0, t.name + "/");
            }
            return path.ToString();
        }

        /// <summary>Posts a mouse message at the target to the game's window.</summary>
        private static void Post(Target target, uint message, IntPtr flags)
        {
            // lParam: x in the low word, y in the high word, both in client pixels from the top left corner.
            IntPtr point = (IntPtr)(((target.Y & 0xFFFF) << 16) | (target.X & 0xFFFF));
            PostMessage(target.Window, message, flags, point);
        }

        /// <summary>The handle of the game's window, or zero when there is none.</summary>
        private static IntPtr GameWindow()
        {
            // The visible top level window of this process that the Unity player made: the class tells it from a
            // console or a dialog of the same process.
            uint self = (uint)Process.GetCurrentProcess().Id;
            IntPtr found = IntPtr.Zero;
            StringBuilder className = new StringBuilder(64);
            EnumWindows((window, unused) =>
            {
                uint owner;
                GetWindowThreadProcessId(window, out owner);
                if (owner != self || !IsWindowVisible(window))
                {
                    return true;
                }
                className.Length = 0;
                GetClassName(window, className, className.Capacity);
                if (className.ToString() != UnityWindowClass)
                {
                    return true;
                }
                found = window;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>A mouse button: Unity's index for it, its window messages, and its flag while held.</summary>
        private struct Button
        {
            public readonly int Index;
            public readonly uint Down;
            public readonly uint Up;
            public readonly int Flag;

            public Button(int index, uint down, uint up, int flag)
            {
                Index = index;
                Down = down;
                Up = up;
                Flag = flag;
            }
        }

        /// <summary>A point of the game's window, with the window and its size.</summary>
        private struct Target
        {
            public IntPtr Window;
            public int X;
            public int Y;
            public int Width;
            public int Height;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ScreenPoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ClientRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr window, StringBuilder className, int maxCount);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr window, out ClientRect rect);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr window, ref ScreenPoint point);

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    }
}
