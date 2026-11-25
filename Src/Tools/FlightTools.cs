using System;
using System.Collections;
using System.Collections.Generic;
using KSP.UI.Screens;

namespace com.github.lhervier.ksp.util.mcpserver
{
    /// <summary>Tools that fly the active vessel as its pilot does: throttle, SAS, staging.</summary>
    internal static class FlightTools
    {
        public static IEnumerable<Tool> All()
        {
            yield return new Tool("set_flight",
                "Sets the main throttle of the active vessel (0 to 1, as Shift, Ctrl, Z and X do), turns SAS on or " +
                "off, and chooses its mode; values left out are kept. Answers with the state of the game, the " +
                "throttle and the SAS mode included.",
                Schema.Object(
                    Schema.P("throttle", "number", "0 (cut) to 1 (full)"),
                    Schema.P("sas", "boolean", "SAS on or off"),
                    Schema.P("sas_mode", "string",
                        "StabilityAssist, Prograde, Retrograde, Normal, Antinormal, RadialIn, RadialOut, Target, " +
                        "AntiTarget, Maneuver")),
                SetFlight);
            yield return new Tool("stage",
                "Activates the next stage of the active vessel, as the space bar does. Answers with the stage now " +
                "current.",
                Schema.Object(), Stage);
            yield return new Tool("action_group",
                "Fires a custom action group (1 to 10) of the active vessel, as its number key does. Refused " +
                "while the game locks the groups. Answers with the group's state after the press and the screen " +
                "messages posted meanwhile.",
                Schema.Object(
                    Schema.P("group", "number", "the custom group, 1 to 10, as its key", true)),
                ActionGroup);
        }

        // The custom action groups, by the number of their key.
        private static readonly KSPActionGroup[] CustomGroups =
        {
            KSPActionGroup.Custom01, KSPActionGroup.Custom02, KSPActionGroup.Custom03, KSPActionGroup.Custom04,
            KSPActionGroup.Custom05, KSPActionGroup.Custom06, KSPActionGroup.Custom07, KSPActionGroup.Custom08,
            KSPActionGroup.Custom09, KSPActionGroup.Custom10
        };

        private static IEnumerator ActionGroup(ToolCall call)
        {
            if (!Require.ActiveVessel(call))
            {
                yield break;
            }
            double number = call.Number("group");
            if (double.IsNaN(number) || number != Math.Floor(number) || number < 1 || number > CustomGroups.Length)
            {
                call.Fail("group must be a whole number from 1 to " + CustomGroups.Length);
                yield break;
            }
            // What a number key does (FlightInputHandler): the group toggled, unless the game locks them.
            if (!InputLockManager.IsUnlocked(ControlTypes.CUSTOM_ACTION_GROUPS))
            {
                call.Fail("the game locks the custom action groups");
                yield break;
            }
            KSPActionGroup group = CustomGroups[(int)number - 1];
            Vessel v = FlightGlobals.ActiveVessel;

            // The messages on the screen before the press, with the time each was shown, to tell apart those the
            // press brings: a message posted again while its text is still shown is not a new one, the game only
            // shows it again from the start.
            var before = new Dictionary<ScreenMessage, float>();
            if (ScreenMessages.Instance != null)
            {
                foreach (ScreenMessage message in ScreenMessages.Instance.ActiveMessages)
                {
                    if (message != null)
                    {
                        before[message] = message.startTime;
                    }
                }
            }
            v.ActionGroups.ToggleGroup(group);
            yield return null;

            var messages = new List<object>();
            if (ScreenMessages.Instance != null)
            {
                foreach (ScreenMessage message in ScreenMessages.Instance.ActiveMessages)
                {
                    float shown;
                    if (message != null && (!before.TryGetValue(message, out shown) || message.startTime != shown))
                    {
                        messages.Add(message.message);
                    }
                }
            }
            call.Text(new Dictionary<string, object>
            {
                { "group", (int)number },
                { "on", v.ActionGroups[group] },
                { "messages", messages }
            });
        }

        private static IEnumerator SetFlight(ToolCall call)
        {
            if (!Require.ActiveVessel(call))
            {
                yield break;
            }
            Vessel v = FlightGlobals.ActiveVessel;
            if (call.Has("throttle"))
            {
                // What the throttle keys write: the state the game copies into the vessel's controls every frame.
                FlightInputHandler.state.mainThrottle = UnityEngine.Mathf.Clamp01((float)call.Number("throttle"));
            }
            if (call.Has("sas"))
            {
                v.ActionGroups.SetGroup(KSPActionGroup.SAS, call.Bool("sas"));
            }
            // The SAS takes its mode only once it is on: wait a frame after turning it on.
            yield return null;
            if (call.Has("sas_mode"))
            {
                VesselAutopilot.AutopilotMode mode;
                try
                {
                    mode = (VesselAutopilot.AutopilotMode)Enum.Parse(typeof(VesselAutopilot.AutopilotMode),
                        call.String("sas_mode"), true);
                }
                catch (ArgumentException)
                {
                    call.Fail("Unknown SAS mode " + call.String("sas_mode"));
                    yield break;
                }
                if (!v.Autopilot.CanSetMode(mode))
                {
                    call.Fail("The SAS of this vessel cannot hold " + mode + " now");
                    yield break;
                }
                v.Autopilot.SetMode(mode);
                yield return null;
            }
            Dictionary<string, object> state = GameState.State();
            state["throttle"] = (double)FlightInputHandler.state.mainThrottle;
            state["sasMode"] = v.Autopilot.Mode.ToString();
            call.Text(state);
        }

        private static IEnumerator Stage(ToolCall call)
        {
            if (!Require.ActiveVessel(call))
            {
                yield break;
            }
            StageManager.ActivateNextStage();
            yield return null;
            call.Text(new Dictionary<string, object> { { "currentStage", StageManager.CurrentStage } });
        }
    }
}
