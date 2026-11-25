using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace com.github.lhervier.ksp.util.mcpserver
{
    /// <summary>
    /// Tools that move the active kerbal on EVA as the player's movement keys do (see <see cref="Walker"/>): hold
    /// a direction, walk along a heading, walk to a point. A walk ends early, the kerbal let go, when the active
    /// vessel changes, the flight scene closes, the kerbal falls over (ragdoll), grabs a ladder, or its call is
    /// cancelled; stop_drive stops it.
    /// </summary>
    internal static class WalkTools
    {
        /// <summary>The name of the tool that walks along a heading.</summary>
        public const string WalkTool = "walk";

        /// <summary>The name of the tool that walks to a point.</summary>
        public const string WalkToTool = "walk_to";

        // Seconds without getting closer before a walk gives up (stalled).
        private const float StallSeconds = 5f;

        // Beyond this distance from its target, walk_to lets the kerbal run when asked to; closer, it walks.
        private const double RunUntil = 3.0;

        public static IEnumerable<Tool> All()
        {
            yield return new Tool("set_eva_controls",
                "Holds the active EVA kerbal's movement keys until changed: forward/back and right/left (signs " +
                "only), relative to where it faces when called; on a ladder forward climbs. run holds the run " +
                "key. Both 0 or omitted let go. Answers with the state.",
                Schema.Object(
                    Schema.P("forward", "number", "positive forward, negative back"),
                    Schema.P("right", "number", "positive right, negative left"),
                    Schema.P("run", "boolean", "run instead of walking (default false)")),
                SetEvaControls);
            yield return new Tool(WalkTool,
                "Walks the active EVA kerbal along a heading for a distance, then lets go and waits until it " +
                "stands. Stops when stalled for five seconds; fails when it falls, grabs a ladder or the active " +
                "vessel changes. stop_drive stops it.",
                Schema.Object(
                    Schema.P("heading", "number", "degrees from north towards east", true),
                    Schema.P("distance", "number", "metres to walk, straight from the start (default 10)"),
                    Schema.P("run", "boolean", "run instead of walking (default false)")),
                Walk);
            yield return new Tool(WalkToTool,
                "Walks the active EVA kerbal to a latitude and longitude (running if asked, until 3 m away), lets " +
                "go within the tolerance and waits until it stands. Stops when stalled; fails when it falls, " +
                "grabs a ladder or the active vessel changes.",
                Schema.Object(
                    Schema.P("latitude", "number", "degrees", true),
                    Schema.P("longitude", "number", "degrees", true),
                    Schema.P("tolerance", "number", "metres from the target to stop at (default 0.3)"),
                    Schema.P("run", "boolean", "run while far from the target (default false)")),
                WalkTo);
        }

        // The active kerbal on EVA, or null after failing the call.
        private static KerbalEVA ActiveKerbal(ToolCall call)
        {
            if (!Require.LiveVessel(call))
            {
                return null;
            }
            Vessel v = FlightGlobals.ActiveVessel;
            KerbalEVA eva = v.isEVA ? v.evaController : null;
            if (eva == null)
            {
                call.Fail(v.vesselName + " is not a kerbal on EVA");
                return null;
            }
            if (v.packed)
            {
                call.Fail(v.vesselName + " is on rails: wait for it to be loaded");
                return null;
            }
            return eva;
        }

        // The active kerbal on EVA standing on the ground or in water, ready to walk, or null after failing the call.
        private static KerbalEVA WalkingKerbal(ToolCall call)
        {
            KerbalEVA eva = ActiveKerbal(call);
            if (eva == null)
            {
                return null;
            }
            string name = eva.vessel.vesselName;
            if (eva.OnALadder)
            {
                call.Fail(name + " is on a ladder: let_go_ladder first, or set_eva_controls to climb");
                return null;
            }
            if (eva.isRagdoll)
            {
                call.Fail(name + " has fallen over: wait for them to stand up");
                return null;
            }
            if (!eva.vessel.LandedOrSplashed)
            {
                call.Fail(name + " is not on the ground nor in water (" + eva.vessel.situation + ")");
                return null;
            }
            return eva;
        }

        private static IEnumerator SetEvaControls(ToolCall call)
        {
            KerbalEVA eva = ActiveKerbal(call);
            if (eva == null)
            {
                yield break;
            }
            float forward = call.Has("forward") ? (float)call.Number("forward") : 0f;
            float right = call.Has("right") ? (float)call.Number("right") : 0f;
            if (forward == 0f && right == 0f)
            {
                Walker.Release();
            }
            else
            {
                Walker.HoldKeys(eva, forward, right, call.Bool("run"));
            }
            // A few physics steps, for the state to show the kerbal set off or stopping.
            for (int i = 0; i < 5; i++)
            {
                yield return new WaitForFixedUpdate();
            }
            call.Text(GameState.State());
        }

        private static IEnumerator Walk(ToolCall call)
        {
            KerbalEVA eva = WalkingKerbal(call);
            if (eva == null)
            {
                yield break;
            }
            Vessel v = eva.vessel;
            double heading = call.Number("heading");
            double distance = call.Number("distance", 10.0);
            bool run = call.Bool("run");
            double startLat = v.latitude, startLon = v.longitude;
            WalkWatch watch = new WalkWatch(eva);
            Progress progress = new Progress();
            string reason = "distance";
            string trouble = null;
            double walked = 0.0;
            try
            {
                Walker.HoldHeading(eva, heading, run);
                while (true)
                {
                    trouble = watch.Trouble();
                    if (trouble != null)
                    {
                        break;
                    }
                    double n, e;
                    Geo.Offset(v, startLat, startLon, out n, out e);
                    walked = Math.Sqrt(n * n + e * e);
                    if (walked >= distance)
                    {
                        break;
                    }
                    if (progress.Stalled(distance - walked))
                    {
                        reason = "stalled";
                        break;
                    }
                    yield return new WaitForFixedUpdate();
                }
            }
            finally
            {
                Walker.Release();
            }
            if (trouble != null)
            {
                call.Fail("Stopped: " + trouble + ". " + Json.Write(GameState.State()));
                yield break;
            }
            yield return Settle(v);
            double fn, fe;
            Geo.Offset(v, startLat, startLon, out fn, out fe);
            Dictionary<string, object> state = GameState.State();
            state["stoppedBecause"] = reason;
            state["walked"] = Math.Sqrt(fn * fn + fe * fe);
            state["walkedAtRelease"] = walked;
            call.Text(state);
        }

        private static IEnumerator WalkTo(ToolCall call)
        {
            KerbalEVA eva = WalkingKerbal(call);
            if (eva == null)
            {
                yield break;
            }
            Vessel v = eva.vessel;
            double lat = call.Number("latitude");
            double lon = call.Number("longitude");
            double tolerance = call.Number("tolerance", 0.3);
            bool run = call.Bool("run");
            WalkWatch watch = new WalkWatch(eva);
            Progress progress = new Progress();
            string reason = "arrived";
            string trouble = null;
            double distanceAtRelease = 0.0;
            try
            {
                bool holding = false;
                while (true)
                {
                    trouble = watch.Trouble();
                    if (trouble != null)
                    {
                        break;
                    }
                    double n, e;
                    Geo.Offset(v, lat, lon, out n, out e);
                    distanceAtRelease = Math.Sqrt(n * n + e * e);
                    if (distanceAtRelease <= tolerance)
                    {
                        break;
                    }
                    if (progress.Stalled(distanceAtRelease))
                    {
                        reason = "stalled";
                        break;
                    }
                    double bearing = Math.Atan2(e, n) * 180.0 / Math.PI;
                    bool running = run && distanceAtRelease > RunUntil;
                    if (!holding)
                    {
                        Walker.HoldHeading(eva, bearing, running);
                        holding = true;
                    }
                    else
                    {
                        Walker.SetHeading(bearing);
                        Walker.SetRun(running);
                    }
                    yield return new WaitForFixedUpdate();
                }
            }
            finally
            {
                Walker.Release();
            }
            if (trouble != null)
            {
                call.Fail("Stopped: " + trouble + ". " + Json.Write(GameState.State()));
                yield break;
            }
            yield return Settle(v);
            double fn, fe;
            Geo.Offset(v, lat, lon, out fn, out fe);
            Dictionary<string, object> state = GameState.State();
            state["missedBy"] = new Dictionary<string, object>
            {
                { "metres", Math.Sqrt(fn * fn + fe * fe) },
                { "north", -fn },
                { "east", -fe }
            };
            state["missedByAtRelease"] = distanceAtRelease;
            state["stoppedBecause"] = reason;
            call.Text(state);
        }

        /// <summary>
        /// Waits for a kerbal let go to stand still: under 5 cm/s over the ground for half a second, 10 seconds
        /// at most; ends early when it is no longer the active vessel.
        /// </summary>
        public static IEnumerator Settle(Vessel v)
        {
            float stillSince = -1f;
            float start = Time.time;
            while (Time.time - start < 10f && v != null && v == FlightGlobals.ActiveVessel)
            {
                if (v.srfSpeed < 0.05)
                {
                    if (stillSince < 0f)
                    {
                        stillSince = Time.time;
                    }
                    else if (Time.time - stillSince > 0.5f)
                    {
                        break;
                    }
                }
                else
                {
                    stillSince = -1f;
                }
                yield return new WaitForFixedUpdate();
            }
        }

        /// <summary>Tells when a walk has got no closer to its end for <see cref="StallSeconds"/>.</summary>
        private sealed class Progress
        {
            private double _best = double.MaxValue;
            private float _lastProgress = Time.time;

            /// <summary>Takes the distance left now, and returns whether the walk is stalled.</summary>
            public bool Stalled(double remaining)
            {
                if (remaining < _best - 0.05)
                {
                    _best = remaining;
                    _lastProgress = Time.time;
                    return false;
                }
                return Time.time - _lastProgress > StallSeconds;
            }
        }

        /// <summary>
        /// Tells why a walk must end before its end: the flight scene closed, the active vessel changed, the
        /// kerbal was destroyed, fell over or grabbed a ladder.
        /// </summary>
        private sealed class WalkWatch
        {
            private readonly KerbalEVA _eva;
            private readonly Vessel _vessel;

            /// <summary>A watch over the walk of <paramref name="eva"/>.</summary>
            public WalkWatch(KerbalEVA eva)
            {
                _eva = eva;
                _vessel = eva.vessel;
            }

            /// <summary>Why the walk must end, or null when it may go on.</summary>
            public string Trouble()
            {
                if (!HighLogic.LoadedSceneIsFlight)
                {
                    return "the flight scene closed";
                }
                if (_eva == null || _vessel == null || _vessel.state == Vessel.State.DEAD)
                {
                    return "the kerbal was destroyed";
                }
                if (FlightGlobals.ActiveVessel != _vessel)
                {
                    return "the active vessel changed";
                }
                if (_eva.isRagdoll)
                {
                    return "the kerbal fell over";
                }
                if (_eva.OnALadder)
                {
                    return "the kerbal grabbed a ladder";
                }
                return null;
            }
        }
    }
}
