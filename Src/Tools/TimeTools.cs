using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace com.github.lhervier.ksp.util.mcpserver
{
    /// <summary>Tools that set the game's time: the universal time, the time warp, the pause.</summary>
    internal static class TimeTools
    {
        // Private in TimeWarp: what Alt with the warp keys calls, and whether a Warp To is running.
        private static readonly MethodInfo WarpSetMode = typeof(TimeWarp).GetMethod("setMode",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo WarpAutoEngaged = typeof(TimeWarp).GetField("autoWarpEngaged",
            BindingFlags.Instance | BindingFlags.NonPublic);

        public static IEnumerable<Tool> All()
        {
            yield return new Tool("set_time",
                "Sets the universal time of the game, in seconds, in any scene but the main menu: the time of " +
                "day at a spot, for one. A flight reverted to its launch goes back to the time of the launch.",
                Schema.Object(Schema.P("ut", "number", "seconds", true)),
                SetTime);
            yield return new Tool("set_warp",
                "Sets the time warp in flight, as the . and , keys (physics warp with Alt): rate is an index into " +
                "KSP's rates, 0 normal. Waits until reached; fails when KSP only allows a lower rate, which the " +
                "answer gives.",
                Schema.Object(
                    Schema.P("rate_index", "number",
                        "0 for normal time; on rails, up to 7 (100000x with KSP's rates), in physics warp up to 3 (4x)", true),
                    Schema.P("physics", "boolean", "physics warp, as Alt with the keys (default false: on rails)")),
                SetWarp);
            yield return new Tool("warp_to",
                "Warps to a universal time, as Warp To of a manoeuvre node or of the map view does: KSP picks the " +
                "rates on rails, slows down and stops at that time. Waits until it is back to normal time.",
                Schema.Object(
                    Schema.P("ut", "number", "the universal time to warp to, in seconds"),
                    Schema.P("seconds", "number", "instead of ut: how long to warp for, in seconds of game time")),
                WarpTo);
            yield return new Tool("warp_to_morning",
                "Warps to the next local morning of the active vessel (KSP's dawn plus a minute), or to " +
                "local_time of the solar day (0.5 noon), and waits for normal time. Gives the local time before " +
                "and after.",
                Schema.Object(Schema.P("local_time", "number",
                    "the time of the local solar day to warp to, 0 to 1 (default: KSP's dawn, 0.3, plus a minute)")),
                WarpToMorning);
            yield return new Tool("set_pause",
                "Pauses or resumes the flight, as Escape does, without the menu.",
                Schema.Object(Schema.P("paused", "boolean", "true to pause", true)),
                SetPause) { Concurrent = true };
        }

        private static IEnumerator SetTime(ToolCall call)
        {
            if (Planetarium.fetch == null)
            {
                call.Fail("No game loaded");
                yield break;
            }
            Planetarium.SetUniversalTime(call.Number("ut"));
            yield return null;
            call.Text(GameState.State());
        }

        private static IEnumerator SetWarp(ToolCall call)
        {
            TimeWarp warp = TimeWarp.fetch;
            if (!HighLogic.LoadedSceneIsFlight || warp == null || !FlightGlobals.ready)
            {
                call.Fail("Not in flight");
                yield break;
            }
            bool physics = call.Bool("physics");
            float[] rates = physics ? warp.physicsWarpRates : warp.warpRates;
            double asked = call.Number("rate_index");
            int index = (int)asked;
            if (index != asked || index < 0 || index >= rates.Length)
            {
                call.Fail("rate_index: 0 to " + (rates.Length - 1));
                yield break;
            }

            // The keys switch between rails and physics warp at the lowest rates only.
            TimeWarp.Modes mode = physics ? TimeWarp.Modes.LOW : TimeWarp.Modes.HIGH;
            if (warp.Mode != mode)
            {
                if (TimeWarp.CurrentRateIndex != 0)
                {
                    TimeWarp.SetRate(0, true);
                    yield return null;
                }
                if (!(bool)WarpSetMode.Invoke(warp, new object[] { mode }))
                {
                    call.Fail("KSP did not switch to " + (physics ? "physics" : "rails") + " warp");
                    yield break;
                }
            }

            // KSP lowers the rate asked for to the one it allows, then reaches it gradually.
            TimeWarp.SetRate(index, false);
            float start = Time.realtimeSinceStartup;
            do
            {
                yield return null;
            }
            while (TimeWarp.CurrentRate != warp.tgt_rate && Time.realtimeSinceStartup - start < 10f);

            Dictionary<string, object> answer = new Dictionary<string, object>
            {
                { "physics", warp.Mode == TimeWarp.Modes.LOW },
                { "rateIndex", TimeWarp.CurrentRateIndex },
                { "rate", (double)TimeWarp.CurrentRate },
                { "ut", Planetarium.GetUniversalTime() }
            };
            if (TimeWarp.CurrentRateIndex != index)
            {
                call.Fail("KSP allows a lower rate only here: " + Json.Write(answer));
                yield break;
            }
            call.Text(answer);
        }

        private static IEnumerator WarpTo(ToolCall call)
        {
            TimeWarp warp = TimeWarp.fetch;
            if (warp == null || Planetarium.fetch == null)
            {
                call.Fail("No time warp in this scene");
                yield break;
            }
            double now = Planetarium.GetUniversalTime();
            double ut = call.Has("ut") ? call.Number("ut") : now + call.Number("seconds");
            if (double.IsNaN(ut) || ut <= now)
            {
                call.Fail("ut or seconds: a time ahead of the game's, which is " + now);
                yield break;
            }
            IEnumerator run = RunWarp(call, warp, ut);
            while (run.MoveNext())
            {
                yield return run.Current;
            }
            if (!call.IsError)
            {
                call.Text(GameState.State());
            }
        }

        private static IEnumerator WarpToMorning(ToolCall call)
        {
            TimeWarp warp = TimeWarp.fetch;
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (!HighLogic.LoadedSceneIsFlight || warp == null || vessel == null || Sun.Instance == null)
            {
                call.Fail("Not in flight, or no sun");
                yield break;
            }
            CelestialBody body = vessel.mainBody;
            double before = Sun.Instance.GetLocalTimeAtPosition(vessel.latitude, vessel.longitude, body);
            // The length of the solar day, as UIWarpToNextMorning computes it: the body's turn, lengthened by its
            // motion along its orbit
            double day = body.rotationPeriod;
            if (body.orbit != null && body.orbit.period - day > 0.01)
            {
                day = body.orbit.period * day / (body.orbit.period - day);
            }
            double seconds = call.Has("local_time")
                ? day * UtilMath.WrapAround(call.Number("local_time") - before, 0.0, 1.0)
                : day * UtilMath.WrapAround(KSP.UI.UIWarpToNextMorning.timeOfDawn - before, 0.0, 1.0) + 60.0;
            if (seconds < 1.0)
            {
                call.Text(new Dictionary<string, object> { { "localTimeBefore", before }, { "localTimeAfter", before } });
                yield break;
            }
            IEnumerator run = RunWarp(call, warp, Planetarium.GetUniversalTime() + seconds);
            while (run.MoveNext())
            {
                yield return run.Current;
            }
            if (!call.IsError)
            {
                Dictionary<string, object> answer = GameState.State();
                answer["localTimeBefore"] = before;
                answer["localTimeAfter"] = Sun.Instance.GetLocalTimeAtPosition(vessel.latitude, vessel.longitude, body);
                call.Text(answer);
            }
        }

        /// <summary>
        /// Warps to <paramref name="ut"/> as Warp To does, and waits until KSP is back to normal time. Fails
        /// <paramref name="call"/> when the warp is still running after 10 minutes, after stopping it.
        /// </summary>
        private static IEnumerator RunWarp(ToolCall call, TimeWarp warp, double ut)
        {
            warp.WarpTo(ut);

            // KSP starts the warp at its next frame, and ends it once back to normal time; it gives up on its
            // own when it cannot warp at all. A warp cut short, by the time limit or by a cancellation, is
            // stopped.
            bool over = false;
            try
            {
                float start = Time.realtimeSinceStartup;
                do
                {
                    yield return null;
                    if (Time.realtimeSinceStartup - start > 600f)
                    {
                        call.Fail("Still warping after 10 minutes: stopped");
                        yield break;
                    }
                }
                while (warp.setAutoWarp || (bool)WarpAutoEngaged.GetValue(warp));
                over = true;
            }
            finally
            {
                if (!over && warp != null)
                {
                    warp.CancelAutoWarp();
                }
            }
        }

        private static IEnumerator SetPause(ToolCall call)
        {
            FlightDriver.SetPause(call.Bool("paused"), false);
            yield return null;
            call.Text(new Dictionary<string, object> { { "paused", FlightDriver.Pause } });
        }
    }
}
