using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace com.github.lhervier.ksp.util.mcpserver
{
    /// <summary>
    /// The direction held for the active kerbal on EVA, given to it the way the player's movement keys are, and
    /// whether it runs. One kerbal at a time: holding a direction for another lets go of the first one.
    /// </summary>
    /// <remarks>
    /// KerbalEVA.FixedUpdate clears the direction asked for (<c>tgtRpos</c>), lets HandleMovementInput read the
    /// keys into it, then runs its state machine, whose states walk, run, bound or swim along that direction and
    /// turn the kerbal towards <c>tgtFwd</c>. The direction held is written in between: each state's
    /// OnFixedUpdate is wrapped, on that kerbal only, so as to write it first; the events that start and stop
    /// running, which read the run key, are wrapped to read it as held too.
    /// </remarks>
    internal static class Walker
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private static readonly FieldInfo TgtRpos = typeof(KerbalEVA).GetField("tgtRpos", Instance);
        private static readonly FieldInfo TgtFwd = typeof(KerbalEVA).GetField("tgtFwd", Instance);
        private static readonly FieldInfo TgtUp = typeof(KerbalEVA).GetField("tgtUp", Instance);
        private static readonly FieldInfo LadderTgtRPos = typeof(KerbalEVA).GetField("ladderTgtRPos", Instance);
        private static readonly FieldInfo ManualAxisControl = typeof(KerbalEVA).GetField("manualAxisControl", Instance);
        private static readonly FieldInfo FsmStates = typeof(KerbalFSM).GetField("States", Instance);

        // The kerbals whose state machine is wrapped, by instance id: never reused while the game runs.
        private static readonly HashSet<int> Hooked = new HashSet<int>();

        private static KerbalEVA _eva;
        private static double _heading = double.NaN;
        private static float _forward;
        private static float _right;
        private static bool _keys;
        private static bool _run;

        /// <summary>The kerbal whose movement is held, or null.</summary>
        public static KerbalEVA Held
        {
            get { return _eva != null && !double.IsNaN(_heading) ? _eva : null; }
        }

        /// <summary>
        /// Holds the kerbal walking, or running, towards <paramref name="heading"/> (degrees from north towards
        /// east), turning to face it, until changed or released; on a ladder, it does not move.
        /// </summary>
        public static void HoldHeading(KerbalEVA eva, double heading, bool run)
        {
            Hook(eva);
            _eva = eva;
            _heading = Geo.Normalize(heading);
            _keys = false;
            _forward = 0f;
            _right = 0f;
            _run = run;
        }

        /// <summary>
        /// Holds the movement keys of the kerbal as forward (positive) or back, and right (positive) or left, of
        /// where it faces now: on the ground or in water, it turns to that direction and keeps walking along it, as
        /// the keys do with the camera behind it; on a ladder, forward climbs and back goes down, as the keys do.
        /// </summary>
        public static void HoldKeys(KerbalEVA eva, float forward, float right, bool run)
        {
            Vessel v = eva.vessel;
            double facing = Geo.Heading(v, eva.transform.forward);
            HoldHeading(eva, facing + Math.Atan2(right, forward) * 180.0 / Math.PI, run);
            _keys = true;
            _forward = Math.Sign(forward);
            _right = Math.Sign(right);
        }

        /// <summary>Changes the heading held, the run kept as it was; without effect when nothing is held.</summary>
        public static void SetHeading(double heading)
        {
            if (Held != null)
            {
                _heading = Geo.Normalize(heading);
            }
        }

        /// <summary>Changes whether the kerbal held runs; without effect when nothing is held.</summary>
        public static void SetRun(bool run)
        {
            _run = run;
        }

        /// <summary>Gives the movement of the kerbal back to the player.</summary>
        public static void Release()
        {
            _eva = null;
            _heading = double.NaN;
            _keys = false;
            _forward = 0f;
            _right = 0f;
            _run = false;
        }

        /// <summary>What is held, for the state of the vessel: null when nothing is.</summary>
        public static Dictionary<string, object> State(KerbalEVA eva)
        {
            if (eva == null || Held != eva)
            {
                return null;
            }
            return new Dictionary<string, object> { { "heading", _heading }, { "run", _run } };
        }

        // Whether the movement held applies to this kerbal now: the one held, the active vessel, under control.
        private static bool Applies(KerbalEVA eva)
        {
            if (Held != eva)
            {
                return false;
            }
            Vessel v = eva.vessel;
            if (v == null || v != FlightGlobals.ActiveVessel || v.packed || v.state != Vessel.State.ACTIVE)
            {
                // Another vessel took over: the keys of the player would no longer move this kerbal either.
                Release();
                return false;
            }
            return true;
        }

        // Writes the direction held, as HandleMovementInput writes the keys' with the camera behind the kerbal.
        private static void Steer(KerbalEVA eva)
        {
            if (!Applies(eva))
            {
                return;
            }
            Vessel v = eva.vessel;
            Vector3 up = FlightGlobals.getUpAxis(v.mainBody, eva.transform.position);
            Vector3d north, east;
            Geo.NorthEast(v, out north, out east);
            double radians = _heading * Math.PI / 180.0;
            Vector3 direction = Vector3.ProjectOnPlane(
                (Vector3)(north * Math.Cos(radians) + east * Math.Sin(radians)), up).normalized;
            TgtRpos.SetValue(eva, direction);
            TgtFwd.SetValue(eva, direction);
            TgtUp.SetValue(eva, up);
            ManualAxisControl.SetValue(eva, false);
            if (_keys)
            {
                LadderTgtRPos.SetValue(eva, eva.transform.up * _forward + eva.transform.right * _right);
            }
        }

        // Whether the kerbal is held running.
        private static bool Runs(KerbalEVA eva)
        {
            return _run && Applies(eva) && !eva.JetpackDeployed && !EVAConstructionModeController.MovementRestricted;
        }

        // Wraps the states and run events of a kerbal's state machine, once per kerbal.
        private static void Hook(KerbalEVA eva)
        {
            if (!Hooked.Add(eva.GetInstanceID()))
            {
                return;
            }
            List<KFSMState> states = (List<KFSMState>)FsmStates.GetValue(eva.fsm);
            foreach (KFSMState state in states)
            {
                KFSMCallback original = state.OnFixedUpdate;
                state.OnFixedUpdate = () =>
                {
                    Steer(eva);
                    original();
                };
            }
            KFSMEventCondition startRun = eva.On_startRun.OnCheckCondition;
            eva.On_startRun.OnCheckCondition = st => startRun(st) || Runs(eva);
            KFSMEventCondition endRun = eva.On_endRun.OnCheckCondition;
            eva.On_endRun.OnCheckCondition = st => endRun(st) && !Runs(eva);
        }
    }
}
