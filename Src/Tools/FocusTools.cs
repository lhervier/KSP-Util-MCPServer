using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace com.github.lhervier.ksp.util.mcpserver
{
    /// <summary>
    /// The switch that keeps off the input locks the game sets on every control while its window does not have
    /// the focus, so that a script can play while the player works in another window. Off by default; it holds
    /// across scene changes, not across a restart of KSP, and turning the server off from the toolbar turns it
    /// off too.
    /// </summary>
    internal static class FocusTools
    {
        /// <summary>
        /// The lock each scene's handler of the focus sets on every control when the window loses it, and lifts
        /// when the window gets it back or the scene ends.
        /// </summary>
        private static readonly Dictionary<GameScenes, string> SceneLocks = new Dictionary<GameScenes, string>
        {
            { GameScenes.FLIGHT, "flightDriver_ApplicationFocus" },      // FlightDriver
            { GameScenes.EDITOR, "EditorDriver_AppFocus" },              // EditorDriver
            { GameScenes.SPACECENTER, "ksc_ApplicationFocus" },          // SpaceCenterMain and UISpaceCenter
            { GameScenes.MAINMENU, "applicationFocus" }                  // MainMenu
        };

        private static bool _ignored;

        /// <summary>Whether the focus locks are kept off.</summary>
        public static bool Ignored
        {
            get { return _ignored; }
        }

        public static IEnumerable<Tool> All()
        {
            yield return new Tool("set_focus_lock",
                "With ignore true, lifts and keeps off the lock the game sets on every control while its window " +
                "lacks the focus, in every scene, so a script plays as with the focus; false gives back the " +
                "game's behaviour. Off at start. Answers with the switch, the focus and the locks held.",
                Schema.Object(Schema.P("ignore", "boolean", "true to keep the lock off, false for the game's behaviour")),
                SetFocusLock) { Concurrent = true };
        }

        private static IEnumerator SetFocusLock(ToolCall call)
        {
            if (call.Has("ignore"))
            {
                SetIgnored(call.Bool("ignore"));
            }
            yield return null;
            List<object> held = new List<object>();
            foreach (string lockId in SceneLocks.Values)
            {
                if (InputLockManager.lockStack.ContainsKey(lockId))
                {
                    held.Add(lockId);
                }
            }
            call.Text(new Dictionary<string, object>
            {
                { "ignore", _ignored },
                { "windowFocused", Application.isFocused },
                { "focusLocks", held }
            });
        }

        /// <summary>
        /// Turns the switch on or off. On, the focus locks held are lifted at once; off, the lock of the scene is
        /// set again when the window does not have the focus, as the game would have set it.
        /// </summary>
        public static void SetIgnored(bool ignored)
        {
            if (ignored == _ignored)
            {
                return;
            }
            _ignored = ignored;
            if (ignored)
            {
                Hold();
                return;
            }
            string lockId;
            if (!Application.isFocused && SceneLocks.TryGetValue(HighLogic.LoadedScene, out lockId))
            {
                InputLockManager.SetControlLock(lockId);
            }
        }

        /// <summary>While the switch is on, lifts the focus locks the game has set since. Called every frame.</summary>
        public static void Hold()
        {
            if (!_ignored)
            {
                return;
            }
            // The game sets its lock from OnApplicationFocus, between two frames: lifted in the next frame, before
            // the tools step, it holds for the scripts that update before this one in that frame only.
            foreach (string lockId in SceneLocks.Values)
            {
                if (InputLockManager.lockStack.ContainsKey(lockId))
                {
                    InputLockManager.RemoveControlLock(lockId);
                }
            }
        }
    }
}
