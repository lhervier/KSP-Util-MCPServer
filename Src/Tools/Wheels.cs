namespace com.github.lhervier.ksp.util.mcpserver
{
    /// <summary>
    /// The wheel throttle and steering, and the pitch, yaw and roll, held for the active vessel, given through
    /// its fly-by-wire the way a player's keys are, and its brakes. One vessel at a time: holding the controls of
    /// another lets go of the first one's.
    /// </summary>
    internal static class Wheels
    {
        // The commands given to the wheels; NaN leaves the player's own input alone.
        private static float _throttle = float.NaN;
        private static float _steer = float.NaN;
        private static float _pitch = float.NaN;
        private static float _yaw = float.NaN;
        private static float _roll = float.NaN;
        private static Vessel _wired;

        /// <summary>
        /// Holds the wheel throttle and steering of the active vessel at these values, NaN leaving either to the
        /// player, until changed or released.
        /// </summary>
        public static void Hold(float throttle, float steer)
        {
            Wire(FlightGlobals.ActiveVessel);
            _throttle = throttle;
            _steer = steer;
        }

        /// <summary>
        /// Holds the pitch, yaw and roll of the active vessel at these values (−1 to 1, as the keys give them),
        /// NaN leaving any of them to the player, until changed or released.
        /// </summary>
        public static void HoldAttitude(float pitch, float yaw, float roll)
        {
            Wire(FlightGlobals.ActiveVessel);
            _pitch = pitch;
            _yaw = yaw;
            _roll = roll;
        }

        /// <summary>Gives the wheel throttle and steering, and the pitch, yaw and roll, back to the player.</summary>
        public static void Release()
        {
            _throttle = float.NaN;
            _steer = float.NaN;
            _pitch = float.NaN;
            _yaw = float.NaN;
            _roll = float.NaN;
        }

        /// <summary>Sets the brakes of a vessel on or off, as the brakes action group does.</summary>
        public static void Brakes(Vessel v, bool on)
        {
            v.ActionGroups.SetGroup(KSPActionGroup.Brakes, on);
        }

        private static void Wire(Vessel v)
        {
            if (ReferenceEquals(_wired, v))
            {
                return;
            }
            if (_wired != null)
            {
                _wired.OnFlyByWire -= FlyByWire;
            }
            _wired = v;
            if (v != null)
            {
                v.OnFlyByWire += FlyByWire;
            }
        }

        private static void FlyByWire(FlightCtrlState s)
        {
            if (!float.IsNaN(_throttle))
            {
                s.wheelThrottle = _throttle;
            }
            if (!float.IsNaN(_steer))
            {
                s.wheelSteer = _steer;
            }
            if (!float.IsNaN(_pitch))
            {
                s.pitch = _pitch;
            }
            if (!float.IsNaN(_yaw))
            {
                s.yaw = _yaw;
            }
            if (!float.IsNaN(_roll))
            {
                s.roll = _roll;
            }
        }
    }
}
