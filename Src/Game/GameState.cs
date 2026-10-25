using System.Collections.Generic;
using CommNet;

namespace com.github.lhervier.ksp.util.mcpserver
{
    /// <summary>
    /// The state of the game as the tools answer it, and the shifts of the floating origin counted since the
    /// flight scene opened.
    /// </summary>
    internal static class GameState
    {
        private static int _shifts;
        private static double _lastShift = double.NaN;

        /// <summary>The shifts of the floating origin counted since the flight scene opened.</summary>
        public static int Shifts
        {
            get { return _shifts; }
        }

        /// <summary>Counts a shift of the floating origin. Called by the host, which holds the subscription.</summary>
        public static void OnOriginShift(Vector3d offset, Vector3d nonFrame)
        {
            _shifts++;
            _lastShift = (offset + nonFrame).magnitude;
        }

        /// <summary>Starts counting again for a new flight scene. Called by the host.</summary>
        public static void OnFlightReady()
        {
            _shifts = 0;
            _lastShift = double.NaN;
        }

        /// <summary>The state of the game, as a dictionary ready to be written as JSON.</summary>
        public static Dictionary<string, object> State()
        {
            Dictionary<string, object> state = new Dictionary<string, object>
            {
                { "scene", HighLogic.LoadedScene.ToString() },
                { "paused", FlightDriver.Pause },
                // Without a planetarium, the time is the game's, and there is no game while KSP loads or on
                // the main menu.
                { "ut", Planetarium.fetch != null || HighLogic.CurrentGame != null
                    ? Planetarium.GetUniversalTime() : double.NaN },
                { "warpRate", (double)TimeWarp.CurrentRate }
            };
            // The row the alarm clock button, left of the time display, shows above it in flight.
            if (HighLogic.LoadedSceneIsFlight && FlightUIModeController.Instance != null)
            {
                state["nextAlarmShowing"] = FlightUIModeController.Instance.NextAlarmShowing;
            }
            Vessel v = HighLogic.LoadedSceneIsFlight ? FlightGlobals.ActiveVessel : null;
            if (v != null)
            {
                Dictionary<string, object> vessel = new Dictionary<string, object>
                {
                    { "id", v.id.ToString() },
                    { "name", v.vesselName },
                    { "situation", v.situation.ToString() },
                    { "packed", v.packed },
                    { "body", v.mainBody.bodyName },
                    { "latitude", v.latitude },
                    { "longitude", v.longitude },
                    { "altitude", v.altitude },
                    { "heightFromTerrain", (double)v.heightFromTerrain },
                    { "surfaceSpeed", v.srfSpeed },
                    { "heading", Geo.Heading(v) },
                    { "pitch", Geo.Pitch(v) },
                    { "brakes", v.ActionGroups[KSPActionGroup.Brakes] },
                    { "lights", v.ActionGroups[KSPActionGroup.Light] },
                    { "gear", v.ActionGroups[KSPActionGroup.Gear] },
                    { "sas", v.ActionGroups[KSPActionGroup.SAS] }
                };
                // As the day and night switches of the space centre read it, which turn its lights on before
                // 0.25 and after 0.7.
                if (Sun.Instance != null)
                {
                    vessel["localTime"] = Sun.Instance.GetLocalTimeAtPosition(v.latitude, v.longitude, v.mainBody);
                    vessel["solarDay"] = v.mainBody.solarDayLength;
                }
                vessel["commNet"] = CommNetState(v);
                state["vessel"] = vessel;
            }
            return state;
        }

        /// <summary>
        /// The vessel's link to the network, as the signal indicator of the flight view shows it: whether it is
        /// connected, and each hop of its control path with its length. Null when CommNet is off.
        /// </summary>
        private static Dictionary<string, object> CommNetState(Vessel v)
        {
            CommNetVessel connection = v.connection;
            if (connection == null || !CommNetScenario.CommNetEnabled)
            {
                return null;
            }
            List<object> hops = new List<object>();
            if (connection.ControlPath != null)
            {
                foreach (CommLink link in connection.ControlPath)
                {
                    hops.Add(new Dictionary<string, object>
                    {
                        { "from", link.a != null ? link.a.name : null },
                        { "to", link.b != null ? link.b.name : null },
                        { "distance", link.cost }
                    });
                }
            }
            return new Dictionary<string, object>
            {
                { "connected", connection.IsConnected },
                { "connectedHome", connection.IsConnectedHome },
                { "signalStrength", connection.ControlPath != null ? connection.SignalStrength : 0.0 },
                { "controlPath", hops }
            };
        }

        /// <summary>The floating origin seen from the active vessel, as a dictionary ready to be written as JSON.</summary>
        public static Dictionary<string, object> FloatingOriginState()
        {
            Dictionary<string, object> state = new Dictionary<string, object>
            {
                { "threshold", FloatingOrigin.fetch != null ? (double)FloatingOrigin.fetch.threshold : double.NaN },
                { "shiftsThisScene", _shifts },
                { "lastShift", _lastShift }
            };
            Vessel v = HighLogic.LoadedSceneIsFlight ? FlightGlobals.ActiveVessel : null;
            if (v != null)
            {
                // The origin of the world is the zero of world space: seen from the vessel, it lies at minus
                // the vessel's position.
                Vector3d toOrigin = -(Vector3d)v.transform.position;
                Vector3d north, east;
                Geo.NorthEast(v, out north, out east);
                Vector3d up = (v.CoMD - v.mainBody.position).normalized;
                state["distance"] = toOrigin.magnitude;
                state["north"] = Vector3d.Dot(toOrigin, north);
                state["east"] = Vector3d.Dot(toOrigin, east);
                state["up"] = Vector3d.Dot(toOrigin, up);
            }
            return state;
        }
    }
}
