using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace com.github.lhervier.ksp.util.mcpserver
{
    /// <summary>
    /// Tools that drive a rover with its wheel throttle, steering and brakes (see <see cref="Wheels"/>), and
    /// stop it. A drive ends early, the rover braked, when the active vessel changes, the flight scene
    /// closes, the rover tips over, or its call is cancelled.
    /// </summary>
    internal static class DriveTools
    {
        private const string DriveTool = "drive";
        private const string DriveToTool = "drive_to";

        // A target behind the rover counts as reached within this distance: further, drive_to stops and turns
        // round.
        private const double ReachedWithin = 3.0;

        // What drive and drive_to tell of their steering.
        private const string LearnsTurning = "How sharply the rover turns at each speed it learns as it drives, " +
            "and remembers for that vessel until the game is closed.";

        // Times drive_to stops to choose its direction again before giving up.
        private const int MaxTurnRounds = 4;

        public static IEnumerable<Tool> All()
        {
            yield return new Tool("set_controls",
                "Holds the wheel throttle and steering, and the pitch, yaw and roll, of the active vessel at the " +
                "given values (−1 to 1) until changed, as the player's keys give them (D gives a yaw of 1); null " +
                "or left out gives them back to the player. Steering: positive turns left. Also sets the brakes " +
                "when given.",
                Schema.Object(
                    Schema.P("wheel_throttle", "number", "−1 (reverse) to 1 (forward)"),
                    Schema.P("wheel_steer", "number", "−1 to 1, positive to the left"),
                    Schema.P("pitch", "number", "−1 to 1, as S (1) and W (−1) give it"),
                    Schema.P("yaw", "number", "−1 to 1, as D (1) and A (−1) give it"),
                    Schema.P("roll", "number", "−1 to 1, as E (1) and Q (−1) give it"),
                    Schema.P("brakes", "boolean", "brakes on or off")),
                SetControls);
            yield return new Tool(DriveTool,
                "Drives the active rover along a heading at a given speed, steering to hold it, then stops with " +
                "the brakes on and waits for it to be still. In reverse, the rover backs away, its front still " +
                "facing the heading. Stops after a distance, or when the floating origin " +
                "shifts if until_shift is true, whichever comes first. The rover's front is where its wheels " +
                "drive it with a positive throttle. Far off the heading, it slows down to turn; it never turns " +
                "harder than keeps it on its wheels. " + LearnsTurning + " Stalled against a small step, it asks " +
                "for more speed every three seconds until the rover gets over; stuck with the most it asks, it " +
                "stops (stoppedBecause stalled). Fails, the rover braked, when it tips over, runs away at " +
                "twice the speed asked, or when the active vessel changes; stop_drive stops it.",
                Schema.Object(
                    Schema.P("heading", "number", "degrees from north towards east", true),
                    Schema.P("speed", "number", "metres per second (default 3)"),
                    Schema.P("distance", "number", "metres to drive (default 1000)"),
                    Schema.P("until_shift", "boolean", "stop as soon as the floating origin shifts"),
                    Schema.P("reverse", "boolean", "drive backwards, the front facing the heading (default false)")),
                Drive);
            yield return new Tool(DriveToTool,
                "Drives the active rover to a latitude and longitude and stops there with the brakes on, still. " +
                "Drives forward or in reverse, whichever faces the target; when the target falls behind the " +
                "rover further than 3 m away, stops and chooses again. Follows the straight line from where it " +
                "chose to the target. Far off that line's direction, it slows down to turn; it never turns harder " +
                "than keeps it on its wheels. " + LearnsTurning + " Stops after 300 s. Stalled against a small step, it asks for more speed every three seconds until the " +
                "rover gets over; stuck with the most it asks, it stops (stalled). For a precise stop, start from a few metres away, roughly lined up with the target. " +
                "Fails, the rover braked, when it tips over, runs away at twice the speed asked, or when the " +
                "active vessel changes; stop_drive stops it.",
                Schema.Object(
                    Schema.P("latitude", "number", "degrees", true),
                    Schema.P("longitude", "number", "degrees", true),
                    Schema.P("speed", "number", "top speed in metres per second (default 2)"),
                    Schema.P("tolerance", "number", "metres from the target to stop at (default 0.2)")),
                DriveTo);
            yield return new Tool("stop_drive",
                "Stops the rover: ends a drive or drive_to still running, whose call then fails, or else lets go " +
                "of the wheels held by set_controls; puts the brakes on and waits for the rover to be still, " +
                "unless the game is paused. Can be called while another tool runs.",
                Schema.Object(), StopDrive) { Concurrent = true };
        }

        private static IEnumerator SetControls(ToolCall call)
        {
            if (!Require.ActiveVessel(call))
            {
                yield break;
            }
            Wheels.Hold(Axis(call, "wheel_throttle"), Axis(call, "wheel_steer"));
            Wheels.HoldAttitude(Axis(call, "pitch"), Axis(call, "yaw"), Axis(call, "roll"));
            if (call.Has("brakes"))
            {
                Wheels.Brakes(FlightGlobals.ActiveVessel, call.Bool("brakes"));
            }
            yield return null;
            call.Text(GameState.State());
        }

        // A control axis given to set_controls, NaN when left out or null.
        private static float Axis(ToolCall call, string name)
        {
            return call.Has(name) ? (float)call.Number(name) : float.NaN;
        }

        private static IEnumerator StopDrive(ToolCall call)
        {
            List<MainThread.Job> drives = MainThread.Cancel(j => j.Name == DriveTool || j.Name == DriveToTool,
                "stopped by stop_drive");
            // The drives let go of the wheels as they end; at once, in case they do not end now.
            Wheels.Release();
            Vessel v = FlightGlobals.ActiveVessel;
            if (v != null)
            {
                Wheels.Brakes(v, true);
            }
            List<object> stopped = drives.ConvertAll(j => (object)j.Name);

            // Paused, the drives end and the rover stops as the game resumes.
            if (!FlightDriver.Pause)
            {
                yield return Waits.Until(call, () => drives.TrueForAll(j => j.IsDone), 10f,
                    "The drive had not stopped after 10 seconds");
                if (call.IsError)
                {
                    yield break;
                }
                if (v != null && v == FlightGlobals.ActiveVessel)
                {
                    yield return StopAndSettle(v);
                }
            }
            Dictionary<string, object> state = GameState.State();
            state["stopped"] = stopped;
            call.Text(state);
        }

        // The highest speed at which to take a heading error of this many degrees: a rover far off its
        // direction slows down, to turn on a tighter circle and stay on its wheels.
        private static double SpeedForTurn(double errorDegrees, double speed)
        {
            double cosine = Math.Cos(Math.Min(90.0, Math.Abs(errorDegrees)) * Math.PI / 180.0);
            return Math.Max(Math.Min(speed, 1.5), speed * Math.Max(0.2, cosine));
        }

        // The direction a positive wheel throttle drives the rover in, in world space.
        private static Vector3d Forward(Vessel v)
        {
            return Geo.RoverForward(v);
        }

        // Speed along the rover's forward direction, positive forward.
        private static double ForwardSpeed(Vessel v)
        {
            return Vector3d.Dot(v.srf_velocity, Forward(v));
        }

        // The direction the rover faces, in degrees from north towards east.
        private static double Heading(Vessel v)
        {
            return Geo.Heading(v, Forward(v));
        }

        // Brakes until the rover is still for two seconds, 30 seconds at most, then gives the wheels back. Ends
        // early when the rover is no longer the active vessel.
        private static IEnumerator StopAndSettle(Vessel v)
        {
            Wheels.Hold(0f, 0f);
            Wheels.Brakes(v, true);
            float stillSince = -1f;
            float start = Time.time;
            while (Time.time - start < 30f && v != null && v == FlightGlobals.ActiveVessel)
            {
                if (v.srfSpeed < 0.02)
                {
                    if (stillSince < 0f)
                    {
                        stillSince = Time.time;
                    }
                    else if (Time.time - stillSince > 2f)
                    {
                        break;
                    }
                }
                else
                {
                    stillSince = -1f;
                }
                yield return null;
            }
            Wheels.Release();
        }

        // Brakes until the rover is nearly still, 10 seconds at most, then takes the brakes off again.
        private static IEnumerator Halt(Vessel v)
        {
            Wheels.Hold(0f, 0f);
            Wheels.Brakes(v, true);
            float start = Time.time;
            while (Time.time - start < 10f && v.srfSpeed > 0.3)
            {
                yield return new WaitForFixedUpdate();
            }
            Wheels.Brakes(v, false);
        }

        /// <summary>
        /// The speed added to the lowest speed a drive asks for, while the rover is stalled against a small step,
        /// the lip of a runway say: it grows for every three seconds without progress, until the rover gets over,
        /// or until it is at its largest and the rover is stuck.
        /// </summary>
        private sealed class StallBoost
        {
            private double _bestRemaining = double.MaxValue;
            private float _lastProgress = Time.time;

            /// <summary>The speed added, in metres per second.</summary>
            public double Value { get; private set; }

            /// <summary>Whether the rover made no progress for three seconds with the largest speed added: what
            /// stops it is more than the wheels can climb.</summary>
            public bool Stuck { get; private set; }

            /// <summary>Takes the distance left to drive now, and returns the speed to add.</summary>
            public double Update(double remaining)
            {
                if (remaining < _bestRemaining - 0.05)
                {
                    _bestRemaining = remaining;
                    _lastProgress = Time.time;
                }
                else if (Time.time - _lastProgress > 3f)
                {
                    Stuck = Value >= MaxBoost;
                    Value = Math.Min(MaxBoost, Value + 0.3);
                    _lastProgress = Time.time;
                }
                return Value;
            }

            /// <summary>Starts again from no boost, as after a halt.</summary>
            public void Reset()
            {
                _bestRemaining = double.MaxValue;
                _lastProgress = Time.time;
                Value = 0.0;
                Stuck = false;
            }

            private const double MaxBoost = 1.5;
        }

        // Ends a drive that did not reach its end: the wheels given back and the brakes on, whatever stopped it.
        private static void Abandon(Vessel v)
        {
            Wheels.Release();
            if (v != null)
            {
                Wheels.Brakes(v, true);
            }
        }

        private static IEnumerator Drive(ToolCall call)
        {
            if (!Require.ActiveVessel(call))
            {
                yield break;
            }
            Vessel v = FlightGlobals.ActiveVessel;
            double heading = Geo.Normalize(call.Number("heading"));
            double speed = call.Number("speed", 3.0);
            double distance = call.Number("distance", 1000.0);
            bool untilShift = call.Bool("until_shift");
            bool reverse = call.Bool("reverse");

            double startLat = v.latitude, startLon = v.longitude;
            int startShifts = GameState.Shifts;
            DriveWatch watch = new DriveWatch(v, speed);
            RoverSteering steering = new RoverSteering(v, reverse);
            SpeedHold speedHold = new SpeedHold();
            StallBoost stall = new StallBoost();
            string reason = "distance";
            string trouble = null;
            bool ended = false;
            try
            {
                Wheels.Brakes(v, false);
                while (true)
                {
                    trouble = watch.Trouble();
                    if (trouble != null)
                    {
                        break;
                    }
                    double n, e;
                    Geo.Offset(v, startLat, startLon, out n, out e);
                    double remaining = distance - Math.Sqrt(n * n + e * e);
                    if (remaining <= 0.0)
                    {
                        break;
                    }
                    if (untilShift && GameState.Shifts > startShifts)
                    {
                        reason = "shift";
                        break;
                    }

                    // Slow down as the end comes, so that the brakes stop the rover there rather than tens of
                    // metres on; brake when faster than that.
                    double along = reverse ? -ForwardSpeed(v) : ForwardSpeed(v);
                    // The front faces the heading: in reverse, the direction of travel is the opposite one.
                    double error = Geo.Delta(Heading(v), heading);
                    double wanted = Math.Max(1.0 + stall.Update(remaining),
                        Math.Min(SpeedForTurn(error, speed), remaining * 0.35));
                    if (stall.Stuck)
                    {
                        reason = "stalled";
                        break;
                    }
                    bool tooFast = along > wanted * 1.1 + 0.15;
                    Wheels.Brakes(v, tooFast);
                    float steer = steering.Steer(error, along, Time.fixedDeltaTime);
                    float throttle = speedHold.Throttle(wanted, along, Time.fixedDeltaTime, tooFast);
                    Wheels.Hold(reverse ? -throttle : throttle, steer);
                    yield return new WaitForFixedUpdate();
                }
                if (trouble == null || watch.StillDriving())
                {
                    yield return StopAndSettle(v);
                }
                ended = true;
            }
            finally
            {
                if (!ended)
                {
                    Abandon(v);
                }
            }
            if (trouble != null)
            {
                Abandon(v);
                call.Fail("Stopped: " + trouble + ". " + Json.Write(GameState.State()));
                yield break;
            }
            Dictionary<string, object> state = GameState.State();
            state["stoppedBecause"] = reason;
            state["floatingOrigin"] = GameState.FloatingOriginState();
            call.Text(state);
        }

        private static IEnumerator DriveTo(ToolCall call)
        {
            if (!Require.ActiveVessel(call))
            {
                yield break;
            }
            Vessel v = FlightGlobals.ActiveVessel;
            double lat = call.Number("latitude");
            double lon = call.Number("longitude");
            double topSpeed = call.Number("speed", 2.0);
            double tolerance = call.Number("tolerance", 0.2);
            double reachedWithin = Math.Max(ReachedWithin, tolerance);

            DriveWatch watch = new DriveWatch(v, topSpeed);
            float start = Time.time;
            bool reverse = false;
            bool decided = false;
            double fromLat = 0.0, fromLon = 0.0;
            RoverSteering steering = null;
            SpeedHold speedHold = null;
            int turnRounds = 0;
            double maxSpeed = 0.0;
            StallBoost stall = new StallBoost();
            string reason = "time limit";
            string trouble = null;
            double speedAtStop = 0.0;
            bool ended = false;
            try
            {
                Wheels.Brakes(v, false);
                while (Time.time - start < 300f)
                {
                    trouble = watch.Trouble();
                    if (trouble != null)
                    {
                        break;
                    }
                    double n, e;
                    Geo.Offset(v, lat, lon, out n, out e);
                    double distance = Math.Sqrt(n * n + e * e);
                    double bearing = Geo.Normalize(Math.Atan2(e, n) * 180.0 / Math.PI);
                    double heading = Heading(v);

                    // Forward or reverse, chosen from where the target lies: at the start, and again whenever
                    // the target falls behind. The rover then follows the line from where it chose to the
                    // target.
                    if (!decided)
                    {
                        reverse = Math.Abs(Geo.Delta(heading, bearing)) > 90.0;
                        decided = true;
                        fromLat = v.latitude;
                        fromLon = v.longitude;
                        steering = new RoverSteering(v, reverse);
                        speedHold = new SpeedHold();
                    }
                    double travel = reverse ? Geo.Normalize(heading + 180.0) : heading;

                    // The line, as a unit vector (north, east), and where the rover stands from it: how far the
                    // target lies along it, and how far the rover is to its right.
                    double on, oe;
                    Geo.Offset(v, fromLat, fromLon, out on, out oe);
                    double ln = n - on, le = e - oe;
                    double length = Math.Sqrt(ln * ln + le * le);
                    if (length < 1.0)
                    {
                        // Chosen within a metre of the target: the line is the bearing.
                        ln = n;
                        le = e;
                        length = Math.Max(distance, 1e-6);
                    }
                    ln /= length;
                    le /= length;
                    double remaining = n * ln + e * le;
                    double right = n * le - e * ln;

                    // Aim at a point of the line ahead of the rover, further the faster it goes, beyond the target
                    // if need be: the heading asked then turns smoothly however close the target is, where aiming
                    // at the target itself swings it more and more.
                    double lookAhead = Math.Max(4.0, 1.5 * Math.Abs(ForwardSpeed(v)));
                    double aim = Math.Atan2(le, ln) * 180.0 / Math.PI - Math.Atan2(right, lookAhead) * 180.0 / Math.PI;
                    double error = Geo.Delta(travel, aim);

                    if (remaining <= tolerance && distance <= reachedWithin)
                    {
                        reason = "arrived";
                        break;
                    }
                    if (remaining < 0.0)
                    {
                        // Behind further away: the rover turned round, or went past it. Stop, then choose again.
                        if (++turnRounds > MaxTurnRounds)
                        {
                            reason = "target behind " + MaxTurnRounds + " times";
                            break;
                        }
                        yield return Halt(v);
                        decided = false;
                        stall.Reset();
                        continue;
                    }

                    double along = reverse ? -ForwardSpeed(v) : ForwardSpeed(v);
                    maxSpeed = Math.Max(maxSpeed, along);
                    double wanted = Math.Max(0.15 + stall.Update(remaining),
                        Math.Min(SpeedForTurn(error, topSpeed), remaining * 0.3));
                    if (stall.Stuck)
                    {
                        reason = "stalled";
                        break;
                    }

                    // Too fast for what is left: brake rather than only cut the throttle, the wheels alone
                    // roll on for metres.
                    bool tooFast = along > wanted * 1.1 + 0.15;
                    Wheels.Brakes(v, tooFast);
                    float steer = steering.Steer(error, along, Time.fixedDeltaTime);
                    float throttle = speedHold.Throttle(wanted, along, Time.fixedDeltaTime, tooFast);
                    Wheels.Hold(reverse ? -throttle : throttle, steer);
                    yield return new WaitForFixedUpdate();
                }
                if (trouble == null || watch.StillDriving())
                {
                    speedAtStop = reverse ? -ForwardSpeed(v) : ForwardSpeed(v);
                    yield return StopAndSettle(v);
                }
                ended = true;
            }
            finally
            {
                if (!ended)
                {
                    Abandon(v);
                }
            }
            if (trouble != null)
            {
                Abandon(v);
                call.Fail("Stopped: " + trouble + ". " + Json.Write(GameState.State()));
                yield break;
            }

            double fn, fe;
            Geo.Offset(v, lat, lon, out fn, out fe);
            Dictionary<string, object> state = GameState.State();
            state["missedBy"] = new Dictionary<string, object>
            {
                { "metres", Math.Sqrt(fn * fn + fe * fe) },
                { "north", -fn },
                { "east", -fe }
            };
            state["stoppedBecause"] = reason;
            state["reverse"] = reverse;
            state["turnRounds"] = Math.Min(turnRounds, MaxTurnRounds);
            state["maxSpeed"] = maxSpeed;
            state["speedAtStop"] = speedAtStop;
            call.Text(state);
        }

        /// <summary>
        /// Tells why a drive must end before its end: the flight scene closed, the active vessel changed, the
        /// rover was destroyed, tipped over, or runs away at more than twice the speed asked for. Tipped over is
        /// counted from how the rover stood when the drive started.
        /// </summary>
        private sealed class DriveWatch
        {
            // Beyond this angle between the rover's up, as it stood at the start, and the local vertical, it no
            // longer stands on its wheels.
            private const float TippedDegrees = 60f;

            private readonly Vessel _vessel;
            private readonly Vector3 _upInVessel;
            private readonly double _speedLimit;

            /// <summary>A watch over the drive of <paramref name="v"/>, asked to go at <paramref name="speed"/> m/s at most.</summary>
            public DriveWatch(Vessel v, double speed)
            {
                _vessel = v;
                _upInVessel = v.ReferenceTransform.InverseTransformDirection(Up(v));
                _speedLimit = Math.Max(2.0 * speed, speed + 3.0);
            }

            /// <summary>Whether the rover is still the active vessel of the flight, not destroyed.</summary>
            public bool StillDriving()
            {
                return HighLogic.LoadedSceneIsFlight && _vessel != null && _vessel.state != Vessel.State.DEAD
                    && FlightGlobals.ActiveVessel == _vessel;
            }

            /// <summary>Why the drive must end, or null when it may go on.</summary>
            public string Trouble()
            {
                if (!HighLogic.LoadedSceneIsFlight)
                {
                    return "the flight scene closed";
                }
                if (_vessel == null || _vessel.state == Vessel.State.DEAD)
                {
                    return "the rover was destroyed";
                }
                if (FlightGlobals.ActiveVessel != _vessel)
                {
                    return "the active vessel changed";
                }
                float tilt = Vector3.Angle(_vessel.ReferenceTransform.TransformDirection(_upInVessel), Up(_vessel));
                if (tilt > TippedDegrees)
                {
                    return "the rover tipped over (" + tilt.ToString("F0") + "° from how it stood)";
                }
                if (_vessel.srfSpeed > _speedLimit)
                {
                    return "the rover ran away (" + _vessel.srfSpeed.ToString("F1") + " m/s over the ground, " +
                        ForwardSpeed(_vessel).ToString("F1") + " m/s of it forward)";
                }
                return null;
            }

            // The local vertical at the vessel, in world space.
            private static Vector3 Up(Vessel v)
            {
                return (v.CoMD - v.mainBody.position).normalized;
            }
        }
    }
}
