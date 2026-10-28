using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace com.github.lhervier.ksp.util.mcpserver
{
    /// <summary>
    /// Coroutines that wait for the game to reach a state, failing the call when it does not in time. A tool
    /// yields them; the times are of real time, which neither the pause nor the time warp change.
    /// </summary>
    internal static class Waits
    {
        /// <summary>How long a scene may take to load: three minutes.</summary>
        public const float SceneSeconds = 180f;

        /// <summary>
        /// Waits until <paramref name="condition"/> holds, checked once a frame from now on; fails the call
        /// with <paramref name="failure"/> when it still does not after <paramref name="seconds"/>.
        /// </summary>
        public static IEnumerator Until(ToolCall call, Func<bool> condition, float seconds, string failure)
        {
            float start = Time.realtimeSinceStartup;
            while (!condition())
            {
                if (Time.realtimeSinceStartup - start > seconds)
                {
                    call.Fail(failure);
                    yield break;
                }
                yield return null;
            }
        }

        /// <summary>
        /// Brings the time warp, on rails or physics, back to normal time if it runs, and waits until physics runs
        /// on the active vessel at normal time; fails the call after 30 seconds.
        /// </summary>
        public static IEnumerator ForNormalTime(ToolCall call)
        {
            if (TimeWarp.CurrentRateIndex != 0)
            {
                TimeWarp.SetRate(0, true);
            }
            yield return Until(call, () => TimeWarp.CurrentRate == 1f && FlightGlobals.ActiveVessel != null
                    && !FlightGlobals.ActiveVessel.packed,
                30f, "Physics was not running on the active vessel 30 s after the time warp was stopped");
        }

        /// <summary>
        /// Leaves the map view for the flight view if it shows, and waits until the game shows the flight view;
        /// fails the call after 10 seconds.
        /// </summary>
        public static IEnumerator ForFlightView(ToolCall call)
        {
            if (!MapView.MapIsEnabled)
            {
                yield break;
            }
            MapView.ExitMapView();
            yield return Until(call, () => !MapView.MapIsEnabled, 10f, "The game did not leave the map view");
        }

        /// <summary>
        /// Waits until the scene of <paramref name="watch"/> has loaded, then stops the watch, also when the
        /// wait ends early; fails the call after three minutes. The space centre is waited for until it is
        /// ready to be left (see <see cref="ForSpaceCentre"/>).
        /// </summary>
        public static IEnumerator ForScene(ToolCall call, SceneWatch watch)
        {
            try
            {
                yield return Until(call, () => watch.Loaded, SceneSeconds,
                    "The scene " + watch.Scene + " was not up after 3 minutes");
            }
            finally
            {
                watch.Stop();
            }
            if (!call.IsError && watch.Scene == GameScenes.SPACECENTER)
            {
                yield return ForSpaceCentre();
            }
        }

        /// <summary>
        /// Waits until a flight scene is up with an active vessel other than <paramref name="previous"/>, the
        /// one whose id is <paramref name="id"/> when it is given, and physics runs on it, or, when
        /// <paramref name="onRailsToo"/>, as soon as it is loaded, on rails or not; then answers with the state of
        /// the game. Fails as soon as KSP gives up the flight for another scene, or after three minutes. To be
        /// yielded right after the flight is asked for.
        /// </summary>
        public static IEnumerator ForNewActiveVessel(ToolCall call, Vessel previous, Guid? id = null,
            bool onRailsToo = false)
        {
            // KSP asks for the flight scene when the tool asks it to, before this starts: any scene asked for
            // from now on means it gave the flight up, as it does when a launch site cannot be found.
            SceneRequest request = SceneRequest.Start();
            try
            {
                // The scene changes a few frames later: the vessel of the scene being left must not count. A
                // vessel destroyed with its scene compares equal to null, and so differs from the new one.
                float start = Time.realtimeSinceStartup;
                while (!(HighLogic.LoadedSceneIsFlight && FlightGlobals.ready && FlightGlobals.ActiveVessel != null
                         && FlightGlobals.ActiveVessel != previous
                         && (onRailsToo ? FlightGlobals.ActiveVessel.loaded : !FlightGlobals.ActiveVessel.packed)
                         && (id == null || FlightGlobals.ActiveVessel.id == id.Value)))
                {
                    if (request.Requested && request.Scene != GameScenes.FLIGHT)
                    {
                        call.Fail("KSP gave the flight up and went to " + request.Scene + ": see KSP.log");
                        yield break;
                    }
                    if (Time.realtimeSinceStartup - start > SceneSeconds)
                    {
                        call.Fail("The flight scene was not ready after 3 minutes");
                        yield break;
                    }
                    yield return null;
                }
            }
            finally
            {
                request.Stop();
            }
            call.Text(GameState.State());
        }

        /// <summary>The surface object that carries the space centre's camera, private to SpaceCenterCamera2.</summary>
        private static readonly FieldInfo CameraPivot =
            typeof(SpaceCenterCamera2).GetField("srfPivot", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>
        /// Waits until the space centre can be left without harm, or 10 seconds at most: every building has set
        /// up its colliders, and the camera has taken its place.
        /// </summary>
        public static IEnumerator ForSpaceCentre()
        {
            // SpaceCenterBuilding.Start sets up the colliders two frames after it starts, once its SetupFacility is
            // over. Leaving the space centre in that very frame leaves them in the next scene with no building,
            // where each frame of the mouse over one of them throws in SpaceCenterBuildingCollider.OnMouseOver.
            // SpaceCenterCamera2.Start hands its pivot to a SurfaceObject that moves it to the scene's root three
            // frames later. Left before, the pivot cannot go back to its parent, the next visits do not find it,
            // and the space centre's camera stays lost, far over the sea, until KSP is restarted.
            float start = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - start < 10f)
            {
                if (!CameraReady())
                {
                    yield return null;
                    continue;
                }
                var withColliders = new HashSet<SpaceCenterBuilding>();
                foreach (SpaceCenterBuildingCollider collider in UnityEngine.Object.FindObjectsOfType<SpaceCenterBuildingCollider>())
                {
                    if (collider.building != null)
                    {
                        withColliders.Add(collider.building);
                    }
                }
                SpaceCenterBuilding[] buildings = UnityEngine.Object.FindObjectsOfType<SpaceCenterBuilding>();
                if (buildings.Length > 0 && Array.TrueForAll(buildings, withColliders.Contains))
                {
                    yield break;
                }
                yield return null;
            }
        }

        /// <summary>Whether the space centre's camera has its pivot in place, or cannot get it any more.</summary>
        private static bool CameraReady()
        {
            SpaceCenterCamera2 camera = UnityEngine.Object.FindObjectOfType<SpaceCenterCamera2>();
            if (camera == null || CameraPivot == null)
            {
                return true;
            }
            // No pivot once Start is over: the camera was already lost on an earlier visit, nothing to wait for.
            SurfaceObject pivot = (SurfaceObject)CameraPivot.GetValue(camera);
            return pivot == null ? Time.timeSinceLevelLoad > 1f : pivot.IsPopped;
        }
    }

    /// <summary>
    /// Tells when a given scene has loaded, from the moment it starts. An instance, since an event refuses a
    /// static handler.
    /// </summary>
    internal sealed class SceneWatch
    {
        private readonly GameScenes _scene;

        /// <summary>Whether the scene has loaded since the watch started.</summary>
        public bool Loaded;

        private SceneWatch(GameScenes scene)
        {
            _scene = scene;
        }

        /// <summary>A watch for <paramref name="scene"/>, started: to be made before the scene is asked for.</summary>
        public static SceneWatch Start(GameScenes scene)
        {
            SceneWatch watch = new SceneWatch(scene);
            GameEvents.onLevelWasLoadedGUIReady.Add(watch.OnLoaded);
            return watch;
        }

        /// <summary>The scene watched for.</summary>
        public GameScenes Scene
        {
            get { return _scene; }
        }

        /// <summary>Stops watching. May be called more than once.</summary>
        public void Stop()
        {
            GameEvents.onLevelWasLoadedGUIReady.Remove(OnLoaded);
        }

        private void OnLoaded(GameScenes scene)
        {
            if (scene == _scene)
            {
                Loaded = true;
            }
        }
    }

    /// <summary>
    /// Records the last scene KSP is asked to load from the moment it starts, and whether that scene has
    /// loaded since. An instance, since an event refuses a static handler.
    /// </summary>
    internal sealed class SceneRequest
    {
        public bool Requested;
        public GameScenes Scene;
        public bool Loaded;

        private SceneRequest()
        {
        }

        /// <summary>A record of the scenes asked for, started.</summary>
        public static SceneRequest Start()
        {
            SceneRequest request = new SceneRequest();
            GameEvents.onGameSceneLoadRequested.Add(request.OnRequested);
            GameEvents.onLevelWasLoadedGUIReady.Add(request.OnLoaded);
            return request;
        }

        /// <summary>Stops recording. May be called more than once.</summary>
        public void Stop()
        {
            GameEvents.onGameSceneLoadRequested.Remove(OnRequested);
            GameEvents.onLevelWasLoadedGUIReady.Remove(OnLoaded);
        }

        private void OnRequested(GameScenes scene)
        {
            Requested = true;
            Scene = scene;
            Loaded = false;
        }

        private void OnLoaded(GameScenes scene)
        {
            if (Requested && scene == Scene)
            {
                Loaded = true;
            }
        }
    }
}
