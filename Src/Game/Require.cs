namespace com.github.lhervier.ksp.util.mcpserver
{
    /// <summary>
    /// What a tool needs of the game before it acts. Each check returns whether it holds, after failing the
    /// call with the reason when it does not.
    /// </summary>
    internal static class Require
    {
        /// <summary>The flight scene ready, with an active vessel.</summary>
        public static bool Flight(ToolCall call)
        {
            if (HighLogic.LoadedSceneIsFlight && FlightGlobals.ready && FlightGlobals.ActiveVessel != null)
            {
                return true;
            }
            call.Fail("Not in flight");
            return false;
        }

        /// <summary>The flight scene ready, with an active vessel that has not been destroyed.</summary>
        public static bool LiveVessel(ToolCall call)
        {
            if (!Flight(call))
            {
                return false;
            }
            if (FlightGlobals.ActiveVessel.state == Vessel.State.DEAD)
            {
                call.Fail("The active vessel " + FlightGlobals.ActiveVessel.vesselName + " was destroyed: load a save");
                return false;
            }
            return true;
        }

        /// <summary>The flight scene, ready or not.</summary>
        public static bool FlightScene(ToolCall call)
        {
            if (HighLogic.LoadedSceneIsFlight && FlightGlobals.fetch != null)
            {
                return true;
            }
            call.Fail("Not in flight");
            return false;
        }

        /// <summary>An active vessel, in whatever state.</summary>
        public static bool ActiveVessel(ToolCall call)
        {
            if (FlightGlobals.ActiveVessel != null)
            {
                return true;
            }
            call.Fail("No active vessel");
            return false;
        }

        /// <summary>
        /// The game in <paramref name="scene"/>; the failure reads <paramref name="what"/>, followed by the
        /// scene the game is in.
        /// </summary>
        public static bool Scene(ToolCall call, GameScenes scene, string what)
        {
            if (HighLogic.LoadedScene == scene)
            {
                return true;
            }
            call.Fail(what + ", and the game is in " + HighLogic.LoadedScene);
            return false;
        }

        /// <summary>A game loaded.</summary>
        public static bool Game(ToolCall call)
        {
            if (HighLogic.CurrentGame != null)
            {
                return true;
            }
            call.Fail("No game loaded");
            return false;
        }
    }
}
