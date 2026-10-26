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
        private const uint WM_KEYDOWN = 0x0100;
        private const uint WM_KEYUP = 0x0101;
        private const uint WM_SYSKEYDOWN = 0x0104;
        private const uint WM_SYSKEYUP = 0x0105;
        private const int MK_LBUTTON = 0x0001;
        private const int MK_RBUTTON = 0x0002;
        private const int MK_SHIFT = 0x0004;
        private const int MK_CONTROL = 0x0008;
        private const int VK_SHIFT = 0x10;
        private const int VK_CONTROL = 0x11;
        private const int VK_MENU = 0x12;

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

        /// <summary>The modifier keys a click can be made with, by Unity's key code.</summary>
        private static readonly Dictionary<KeyCode, Key> Keys = new Dictionary<KeyCode, Key>
        {
            // Unity reads a posted Shift by the key of its side only, not by VK_SHIFT as a keyboard sends it; Ctrl
            // and Alt by the key of either side, the extended flag telling the right one.
            { KeyCode.LeftShift, new Key(KeyCode.LeftShift, VK_SHIFT, 0xA0, 0xA0, 0x2A, false, MK_SHIFT) },
            { KeyCode.RightShift, new Key(KeyCode.RightShift, VK_SHIFT, 0xA1, 0xA1, 0x36, false, MK_SHIFT) },
            { KeyCode.LeftControl, new Key(KeyCode.LeftControl, VK_CONTROL, 0xA2, VK_CONTROL, 0x1D, false, MK_CONTROL) },
            { KeyCode.RightControl, new Key(KeyCode.RightControl, VK_CONTROL, 0xA3, VK_CONTROL, 0x1D, true, MK_CONTROL) },
            { KeyCode.LeftAlt, new Key(KeyCode.LeftAlt, VK_MENU, 0xA4, VK_MENU, 0x38, false, 0) },
            { KeyCode.RightAlt, new Key(KeyCode.RightAlt, VK_MENU, 0xA5, VK_MENU, 0x38, true, 0) }
        };

        /// <summary>The names the modifiers argument takes, but mod, which names the game's modifier key.</summary>
        private static readonly Dictionary<string, KeyCode> KeyNames = new Dictionary<string, KeyCode>
        {
            { "shift", KeyCode.LeftShift },
            { "right_shift", KeyCode.RightShift },
            { "ctrl", KeyCode.LeftControl },
            { "right_ctrl", KeyCode.RightControl },
            { "alt", KeyCode.LeftAlt },
            { "right_alt", KeyCode.RightAlt }
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
                "count 2, a double click: a second click right after the first, as a player's double click. With " +
                "modifiers, the click is made with those keys held, as a keyboard holds them: pressed once the " +
                "pointer is there, each seen held by Input.GetKey before the button goes down, released once it " +
                "went up. They satisfy Input.GetKey of their KeyCode (LeftShift, RightShift, LeftControl, " +
                "RightControl, LeftAlt, RightAlt), and so the key bindings of the game bound to them: " +
                "GameSettings.MODIFIER_KEY (Left Alt or Right Alt by default) among them, which the inventories of " +
                "the part action windows read to take a whole stack; mod names the key MODIFIER_KEY is bound to. " +
                "Refused with modifiers while the game's window does not have the focus: the game then reads no key.",
                Schema.Object(
                    Schema.P("x", "number", "pixels from the left edge of the game's window", true),
                    Schema.P("y", "number", "pixels from the top edge of the game's window", true),
                    Schema.P("button", "string", "left or right (default left)"),
                    Schema.P("count", "number", "1 for a click, 2 for a double click (default 1)"),
                    Schema.P("modifiers", "array",
                        "keys held during the click: shift, ctrl, alt (the left ones), right_shift, right_ctrl, " +
                        "right_alt, or mod, the game's modifier key (default none)")),
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
            List<Key> keys;
            if (!Modifiers(call, out keys))
            {
                yield break;
            }
            // Unity drops the key messages its window gets while it does not have the focus.
            if (keys.Count > 0 && !Application.isFocused)
            {
                call.Fail("The game reads no key while its window does not have the focus: a click with modifiers " +
                    "needs it");
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
            // The keys pressed so far, released whatever ends the click: a failure, or the call cancelled.
            List<Key> held = new List<Key>();
            try
            {
                yield return HoldKeys(call, target, keys, held);
                if (!call.IsError)
                {
                    yield return Press(call, target, button, count, ModifierFlags(keys));
                }
            }
            finally
            {
                ReleaseKeys(target, held);
                if (focusLocked)
                {
                    InputLockManager.SetControlLock(FocusLock);
                }
            }
            if (call.IsError)
            {
                yield break;
            }
            yield return Waits.Until(call, () => keys.TrueForAll(k => !Input.GetKey(k.Code)), ButtonSeconds,
                "The game did not see the modifier keys go up");
            if (call.IsError)
            {
                yield break;
            }
            Dictionary<string, object> report = Report(target, call.String("button", "left").ToLowerInvariant());
            if (keys.Count > 0)
            {
                report["modifiers"] = keys.ConvertAll(k => (object)k.Code.ToString());
            }
            call.Text(report);
        }

        /// <summary>
        /// Reads the modifier keys the call names, each once, in its order. Returns false after failing the call
        /// when one is unknown, or when mod names a key other than Shift, Ctrl or Alt.
        /// </summary>
        private static bool Modifiers(ToolCall call, out List<Key> keys)
        {
            keys = new List<Key>();
            if (!call.Has("modifiers"))
            {
                return true;
            }
            // A single name is taken as a list of one.
            object given = call.Arguments["modifiers"];
            List<object> names = given as List<object> ?? new List<object> { given };
            foreach (object item in names)
            {
                string name = Convert.ToString(item).ToLowerInvariant();
                KeyCode code;
                if (name == "mod")
                {
                    code = GameSettings.MODIFIER_KEY.primary.code;
                    if (!Keys.ContainsKey(code))
                    {
                        call.Fail("mod: the game's modifier key is bound to " + code + ", not to Shift, Ctrl or Alt");
                        return false;
                    }
                }
                else if (!KeyNames.TryGetValue(name, out code))
                {
                    call.Fail("modifiers: shift, ctrl, alt, right_shift, right_ctrl, right_alt or mod, not " + name);
                    return false;
                }
                if (!keys.Exists(k => k.Code == code))
                {
                    keys.Add(Keys[code]);
                }
            }
            return true;
        }

        /// <summary>The flags of the mouse messages for the keys held: Shift and Ctrl only, as Windows has no other.</summary>
        private static int ModifierFlags(List<Key> keys)
        {
            int flags = 0;
            foreach (Key key in keys)
            {
                flags |= key.MouseFlag;
            }
            return flags;
        }

        /// <summary>
        /// Presses the keys at the target's window, in order, adding each one to held as it goes down, and returns
        /// once the game reads them all held. Fails the call when it does not after a while.
        /// </summary>
        private static IEnumerator HoldKeys(ToolCall call, Target target, List<Key> keys, List<Key> held)
        {
            foreach (Key key in keys)
            {
                held.Add(key);
                PostKey(target, key, true, held);
                yield return Waits.Until(call, () => Input.GetKey(key.Code), ButtonSeconds,
                    "The game did not see " + key.Code + " go down");
                if (call.IsError)
                {
                    yield break;
                }
            }
        }

        /// <summary>Releases the keys held, the last pressed first, and empties held; does not wait for the game.</summary>
        private static void ReleaseKeys(Target target, List<Key> held)
        {
            for (int i = held.Count - 1; i >= 0; i--)
            {
                PostKey(target, held[i], false, held);
                held.RemoveAt(i);
            }
        }

        /// <summary>
        /// Posts to the target's window the message of a key going down or up, as Windows sends a keyboard's,
        /// and sets the key state of this thread to match. held: the keys held, this one included.
        /// </summary>
        private static void PostKey(Target target, Key key, bool down, List<Key> held)
        {
            // Windows sends the keys pressed and released while Alt is held, Alt itself included, as system keys,
            // unless Ctrl is held too.
            bool system = held.Exists(k => k.Vk == VK_MENU) && !held.Exists(k => k.Vk == VK_CONTROL);
            uint message = down ? (system ? WM_SYSKEYDOWN : WM_KEYDOWN) : (system ? WM_SYSKEYUP : WM_KEYUP);
            // lParam: a repeat count of 1, the scan code, the extended key flag, the context code (Alt held), then
            // the previous state and the transition, both set for a key going up.
            long flags = 1L | ((long)key.Scan << 16) | (key.Extended ? 1L << 24 : 0L) | (system ? 1L << 29 : 0L)
                | (down ? 0L : (1L << 30) | (1L << 31));
            // The key state of a thread, which GetKeyState reads, follows the keyboard's messages only, not posted
            // ones: set by hand. The key of its side, then the key of either side, held while one of the two is.
            byte[] state = new byte[256];
            if (GetKeyboardState(state))
            {
                state[key.SideVk] = (byte)(down ? 0x80 : 0);
                bool either = false;
                foreach (Key other in Keys.Values)
                {
                    either |= other.Vk == key.Vk && (state[other.SideVk] & 0x80) != 0;
                }
                state[key.Vk] = (byte)(either ? 0x80 : 0);
                SetKeyboardState(state);
            }
            PostMessage(target.Window, message, (IntPtr)key.MessageVk, (IntPtr)unchecked((int)flags));
        }

        /// <summary>
        /// Presses and releases the button count times at the target, each time once the game has seen it.
        /// modifierFlags: the flags of the modifier keys held, for the button's messages.
        /// </summary>
        private static IEnumerator Press(ToolCall call, Target target, Button button, int count, int modifierFlags)
        {
            for (int i = 0; i < count; i++)
            {
                // The button goes down, then up once the game has seen it down for a frame: a press as short as a
                // player's, which every handler of the game sees, whether it acts on the press or on the release.
                Post(target, button.Down, (IntPtr)(button.Flag | modifierFlags));
                yield return Waits.Until(call, () => Input.GetMouseButton(button.Index), ButtonSeconds,
                    "The game did not see the button go down");
                if (call.IsError)
                {
                    yield break;
                }
                yield return null;
                Post(target, button.Up, (IntPtr)modifierFlags);
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

        /// <summary>
        /// A modifier key: Unity's key code for it, the virtual key of either side and the one of its side, the
        /// virtual key its messages carry, its scan code, whether it is an extended key, and its flag in the mouse
        /// messages (0 if none).
        /// </summary>
        private struct Key
        {
            public readonly KeyCode Code;
            public readonly int Vk;
            public readonly int SideVk;
            public readonly int MessageVk;
            public readonly int Scan;
            public readonly bool Extended;
            public readonly int MouseFlag;

            public Key(KeyCode code, int vk, int sideVk, int messageVk, int scan, bool extended, int mouseFlag)
            {
                Code = code;
                Vk = vk;
                SideVk = sideVk;
                MessageVk = messageVk;
                Scan = scan;
                Extended = extended;
                MouseFlag = mouseFlag;
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
        private static extern bool GetKeyboardState(byte[] state);

        [DllImport("user32.dll")]
        private static extern bool SetKeyboardState(byte[] state);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    }
}
