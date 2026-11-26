using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using KSP.UI.Screens.Mapview;
using UnityEngine;

namespace com.github.lhervier.ksp.util.mcpserver
{
    /// <summary>Tools that show the game: the screenshot, the flight camera, the interface.</summary>
    internal static class ViewTools
    {
        // Protected in FlightCamera: where it aims, away from the vessel, as the middle mouse button sets it.
        private static readonly FieldInfo AimHeading = typeof(FlightCamera).GetField("offsetHdg",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo AimPitch = typeof(FlightCamera).GetField("offsetPitch",
            BindingFlags.Instance | BindingFlags.NonPublic);

        public static IEnumerable<Tool> All()
        {
            yield return new Tool("screenshot",
                "Captures the screen as it is drawn, user interface included. Saves it as a PNG when a path is " +
                "given (relative to the KSP folder, or absolute); returns the image when no path is given, or when " +
                "return_image is true.",
                Schema.Object(
                    Schema.P("path", "string", "where to save the PNG"),
                    Schema.P("return_image", "boolean",
                        "whether to return the image (default: true without path, false with one)")),
                Screenshot) { Concurrent = true };
            yield return new Tool("set_camera",
                "Sets the flight camera: distance in metres, heading and pitch in degrees, field of view (20 to " +
                "160), and its aim offset from the vessel as a middle-button drag does. Values left out are kept.",
                Schema.Object(
                    Schema.P("distance", "number", "metres"),
                    Schema.P("heading", "number", "degrees"),
                    Schema.P("pitch", "number", "degrees"),
                    Schema.P("fov", "number", "field of view, degrees"),
                    Schema.P("aim_heading", "number", "degrees to the right of the vessel, middle mouse button"),
                    Schema.P("aim_pitch", "number", "degrees below the vessel, middle mouse button")),
                SetCamera) { Concurrent = true };
            yield return new Tool("set_ui",
                "Hides or shows the game's interface in flight, as F2 does: navball, staging, toolbars. Windows " +
                "of mods that do not follow it stay.",
                Schema.Object(Schema.P("visible", "boolean", "true to show it, false to hide it", true)),
                SetUi) { Concurrent = true };
            yield return new Tool("map_view",
                "Enters or leaves the map view, as the M key does, centres the map camera on a body or vessel " +
                "(focus) and sets its distance. Refused when the game would not take the key. Answers with the " +
                "focus and distance.",
                Schema.Object(
                    Schema.P("enabled", "boolean",
                        "true to enter the map view, false to leave it (default: true with focus or distance, the " +
                        "view kept otherwise)"),
                    Schema.P("focus", "string",
                        "the body's name, or else the vessel's id, or its name when no other vessel bears it"),
                    Schema.P("distance", "number",
                        "distance from the focus, in map units (6 km each)")),
                MapViewTool);
            yield return new Tool("read_map_icon",
                "In the map view, over a number of rendered frames, compares where a vessel's icon is drawn with " +
                "where its position lies on its body as drawn: frames showing the icon, mean and largest gap in " +
                "pixels, and optionally each frame.",
                Schema.Object(
                    Schema.P("vessel", "string", "the vessel's id, or its name when no other vessel bears it", true),
                    Schema.P("frames", "number", "how many rendered frames in a row to read, 1 to 1000 (default 1)"),
                    Schema.P("each_frame", "boolean", "whether to answer each frame too (default false)")),
                ReadMapIcon) { Concurrent = true };
            yield return new Tool("iva_view",
                "Enters the IVA view of the active vessel, or leaves it for the flight view, as the C key does, " +
                "and answers once the game shows the view asked for. Refuses when the game would not take the " +
                "key: camera modes locked, the game paused, IVA not allowed in this game, or no crew member to " +
                "look through.",
                Schema.Object(Schema.P("enabled", "boolean", "true to enter the IVA view, false to go back to the flight view", true)),
                IvaViewTool);
        }

        /// <summary>How long the game may take to switch to or from the IVA view, in seconds of real time.</summary>
        private const float IvaViewSeconds = 10f;

        private static IEnumerator IvaViewTool(ToolCall call)
        {
            CameraManager cameras = CameraManager.Instance;
            if (!HighLogic.LoadedSceneIsFlight || cameras == null)
            {
                call.Fail("No IVA view: not in flight");
                yield break;
            }
            bool enabled = call.Bool("enabled");
            bool inIva = cameras.currentCameraMode == CameraManager.CameraMode.IVA;
            if (inIva != enabled)
            {
                // The checks the C key goes through, in CameraManager.Update and NextCameraMode.
                if (!InputLockManager.IsUnlocked(ControlTypes.CAMERAMODES))
                {
                    call.Fail("Camera modes are locked: the C key would do nothing now");
                    yield break;
                }
                if (FlightDriver.Pause)
                {
                    call.Fail("The game is paused: the C key would do nothing now");
                    yield break;
                }
                CameraManager.CameraMode from = cameras.currentCameraMode;
                if (enabled)
                {
                    if (from != CameraManager.CameraMode.Flight && from != CameraManager.CameraMode.Internal)
                    {
                        call.Fail("The C key goes to the IVA view from the flight view only, not from " + from);
                        yield break;
                    }
                    if (!HighLogic.CurrentGame.Parameters.Flight.CanIVA)
                    {
                        call.Fail("This game does not allow the IVA view");
                        yield break;
                    }
                    if (!cameras.SetCameraIVA())
                    {
                        // As NextCameraMode does when no kerbal can take the view.
                        cameras.SetCameraFlight();
                        call.Fail("The active vessel has no crew member to look through");
                        yield break;
                    }
                }
                else
                {
                    cameras.SetCameraFlight();
                }
                CameraManager.CameraMode wanted = enabled ? CameraManager.CameraMode.IVA : CameraManager.CameraMode.Flight;
                yield return Waits.Until(call, () => cameras.currentCameraMode == wanted, IvaViewSeconds,
                    "The game did not " + (enabled ? "enter" : "leave") + " the IVA view");
                if (call.IsError)
                {
                    yield break;
                }
            }
            // The interface of the view follows a frame later.
            yield return null;
            yield return null;
            call.Text(new Dictionary<string, object>
            {
                { "enabled", cameras.currentCameraMode == CameraManager.CameraMode.IVA },
                { "cameraMode", cameras.currentCameraMode.ToString() }
            });
        }

        /// <summary>How long the game may take to switch to or from the map view, in seconds of real time.</summary>
        private const float MapViewSeconds = 10f;

        private static IEnumerator MapViewTool(ToolCall call)
        {
            if (!HighLogic.LoadedSceneIsFlight || MapView.fetch == null)
            {
                call.Fail("No map view: not in flight");
                yield break;
            }
            bool centring = call.Has("focus") || call.Has("distance");
            bool enabled = call.Bool("enabled", centring || MapView.MapIsEnabled);
            if (centring && !enabled)
            {
                call.Fail("focus and distance set the camera of the map view: they need enabled true");
                yield break;
            }
            MapObject focus = null;
            if (call.Has("focus"))
            {
                focus = MapTarget(call);
                if (focus == null)
                {
                    yield break;
                }
            }
            if (MapView.MapIsEnabled != enabled)
            {
                // The checks the M key goes through, in MapView.Update: a locked toggle, or a game whose map
                // view is off, ignores the key.
                if (!InputLockManager.IsUnlocked(ControlTypes.MAP_TOGGLE))
                {
                    call.Fail("The map toggle is locked: the M key would do nothing now");
                    yield break;
                }
                if (enabled && !HighLogic.CurrentGame.Parameters.Flight.CanUseMap)
                {
                    call.Fail("This game does not allow the map view");
                    yield break;
                }
                if (enabled)
                {
                    MapView.EnterMapView();
                }
                else
                {
                    MapView.ExitMapView();
                }
                yield return Waits.Until(call, () => MapView.MapIsEnabled == enabled, MapViewSeconds,
                    "The game did not " + (enabled ? "enter" : "leave") + " the map view");
                if (call.IsError)
                {
                    yield break;
                }
            }
            // The interface of the view follows a frame later.
            yield return null;
            yield return null;
            PlanetariumCamera camera = PlanetariumCamera.fetch;
            if (focus != null)
            {
                // The overload a double click ends in: it re-parents the camera's pivot and keeps the distance
                // within the limits of the new target.
                camera.SetTarget(focus);
            }
            if (call.Has("distance"))
            {
                camera.SetDistance((float)call.Number("distance"));
            }
            if (centring)
            {
                yield return null;
            }
            Dictionary<string, object> answer = new Dictionary<string, object> { { "enabled", MapView.MapIsEnabled } };
            if (camera != null && camera.target != null)
            {
                answer["focus"] = camera.target.GetName();
                answer["focusType"] = camera.target.type.ToString();
                answer["distance"] = (double)camera.Distance;
            }
            call.Text(answer);
        }

        /// <summary>
        /// The map object the <c>focus</c> argument names: a body's, by its name, or else a vessel's, by its id or
        /// name; null after failing the call when there is none.
        /// </summary>
        private static MapObject MapTarget(ToolCall call)
        {
            string name = call.String("focus");
            foreach (CelestialBody body in FlightGlobals.Bodies)
            {
                if (string.Equals(body.bodyName, name, StringComparison.OrdinalIgnoreCase))
                {
                    if (body.MapObject == null)
                    {
                        call.Fail(body.bodyName + " has no object in the map view");
                    }
                    return body.MapObject;
                }
            }
            string error;
            Vessel vessel = Lookup.FindVessel(name, out error);
            if (vessel == null)
            {
                call.Fail("No body named " + name + ", and " + char.ToLowerInvariant(error[0]) + error.Substring(1));
                return null;
            }
            if (vessel.mapObject == null)
            {
                call.Fail(vessel.vesselName + " has no object in the map view");
            }
            return vessel.mapObject;
        }

        // Protected internal in OrbitRendererBase: the icon of the vessel or body in the map view.
        private static readonly FieldInfo ObjectNode = typeof(OrbitRendererBase).GetField("objectNode",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        private const int MaxMapIconFrames = 1000;

        private static IEnumerator ReadMapIcon(ToolCall call)
        {
            if (!HighLogic.LoadedSceneIsFlight || !MapView.MapIsEnabled)
            {
                call.Fail("Not in the map view of a flight");
                yield break;
            }
            string error;
            Vessel vessel = Lookup.FindVessel(call.String("vessel"), out error);
            if (vessel == null)
            {
                call.Fail(error);
                yield break;
            }
            int frames = (int)call.Number("frames", 1);
            if (frames < 1 || frames > MaxMapIconFrames)
            {
                call.Fail("frames must be between 1 and " + MaxMapIconFrames);
                yield break;
            }
            bool eachFrame = call.Bool("each_frame", false);

            List<object> samples = new List<object>();
            int read = 0;
            int shown = 0;
            double gapSum = 0;
            double gapMax = 0;
            for (int i = 0; i < frames; i++)
            {
                // After every LateUpdate, the icons' included: what the frame shows.
                yield return new WaitForEndOfFrame();
                if (vessel == null || !MapView.MapIsEnabled)
                {
                    call.Fail("The vessel or the map view went away after " + read + " frames");
                    yield break;
                }
                MapNode node = vessel.orbitRenderer != null ? ObjectNode.GetValue(vessel.orbitRenderer) as MapNode : null;
                // The node is only placed while it shows: hidden, its place is stale.
                bool iconShown = node != null && node.gameObject.activeInHierarchy && node.VisualIconData.visible;
                Vector2? icon = iconShown ? IconScreenPoint(node) : (Vector2?)null;
                Vector2? place = PlaceScreenPoint(vessel);
                double? gap = null;
                if (icon.HasValue && place.HasValue)
                {
                    gap = (icon.Value - place.Value).magnitude;
                    gapSum += gap.Value;
                    gapMax = Math.Max(gapMax, gap.Value);
                    shown++;
                }
                read++;
                if (eachFrame)
                {
                    samples.Add(new Dictionary<string, object>
                    {
                        { "icon", Point(icon) },
                        { "place", Point(place) },
                        { "gap", gap.HasValue ? (object)Math.Round(gap.Value, 1) : null },
                        { "shown", iconShown },
                        { "warpRate", (double)TimeWarp.CurrentRate }
                    });
                }
            }
            Dictionary<string, object> answer = new Dictionary<string, object>
            {
                { "vessel", vessel.vesselName },
                { "frames", read },
                { "shownFrames", shown },
                { "gapMean", shown > 0 ? (object)Math.Round(gapSum / shown, 1) : null },
                { "gapMax", shown > 0 ? (object)Math.Round(gapMax, 1) : null },
                { "warpRate", (double)TimeWarp.CurrentRate }
            };
            if (eachFrame)
            {
                answer["each"] = samples;
            }
            call.Text(answer);
        }

        /// <summary>
        /// Where the map icon <paramref name="node"/> is drawn, in pixels of the game's window from its top left
        /// corner, y down.
        /// </summary>
        private static Vector2 IconScreenPoint(MapNode node)
        {
            Canvas canvas = node.GetComponentInParent<Canvas>();
            Camera camera = null;
            if (canvas != null && canvas.rootCanvas.renderMode != RenderMode.ScreenSpaceOverlay)
            {
                camera = canvas.rootCanvas.worldCamera;
            }
            Vector2 point = RectTransformUtility.WorldToScreenPoint(camera, node.transform.position);
            return new Vector2(point.x, Screen.height - 1 - point.y);
        }

        /// <summary>
        /// Where the latitude, longitude and altitude of <paramref name="vessel"/> lie on its body as the map view
        /// draws it, in pixels of the game's window from its top left corner, y down; null when that point is behind
        /// the map's camera.
        /// </summary>
        private static Vector2? PlaceScreenPoint(Vessel vessel)
        {
            CelestialBody body = vessel.mainBody;
            Camera camera = PlanetariumCamera.Camera;
            if (body == null || body.scaledBody == null || camera == null)
            {
                return null;
            }
            // The point in the frame of the body's transform, then through the drawn body's transform, scaled down.
            Vector3d world = body.GetWorldSurfacePosition(vessel.latitude, vessel.longitude, vessel.altitude);
            Vector3 local = Quaternion.Inverse(body.transform.rotation) * (Vector3)(world - body.position);
            Transform drawn = body.scaledBody.transform;
            Vector3d scaled = (Vector3d)drawn.position + (Vector3d)(drawn.rotation * (local * ScaledSpace.InverseScaleFactor));
            Vector3 point = camera.WorldToScreenPoint(scaled);
            if (point.z <= 0)
            {
                return null;
            }
            return new Vector2(point.x, Screen.height - 1 - point.y);
        }

        private static object Point(Vector2? point)
        {
            if (!point.HasValue)
            {
                return null;
            }
            return new Dictionary<string, object>
            {
                { "x", Math.Round(point.Value.x, 1) },
                { "y", Math.Round(point.Value.y, 1) }
            };
        }

        private static IEnumerator Screenshot(ToolCall call)
        {
            yield return new WaitForEndOfFrame();
            Texture2D texture = ScreenCapture.CaptureScreenshotAsTexture();
            // The screen's alpha is whatever the shaders wrote, and the terrain's writes zero: kept, the ground
            // shows as transparent, white or black depending on the viewer. Encode the colours alone.
            Texture2D opaque = new Texture2D(texture.width, texture.height, TextureFormat.RGB24, false);
            opaque.SetPixels32(texture.GetPixels32());
            opaque.Apply();
            byte[] png = opaque.EncodeToPNG();
            UnityEngine.Object.Destroy(texture);
            UnityEngine.Object.Destroy(opaque);

            string path = call.String("path");
            if (!string.IsNullOrEmpty(path))
            {
                if (!Path.IsPathRooted(path))
                {
                    path = Path.Combine(KSPUtil.ApplicationRootPath, path);
                }
                if (!Sandbox.Allows(call, path))
                {
                    yield break;
                }
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                File.WriteAllBytes(path, png);
                call.Text("Saved " + Path.GetFullPath(path) + " (" + png.Length + " bytes)");
            }
            // a saved capture is meant to be read later, reduced: returned only when asked
            if (call.Bool("return_image", string.IsNullOrEmpty(path)))
            {
                call.Png(png);
            }
        }

        private static IEnumerator SetCamera(ToolCall call)
        {
            FlightCamera camera = FlightCamera.fetch;
            if (camera == null)
            {
                call.Fail("No flight camera: not in flight");
                yield break;
            }
            if (call.Has("distance"))
            {
                camera.SetDistanceImmediate((float)call.Number("distance"));
            }
            if (call.Has("heading"))
            {
                camera.camHdg = Radians(call.Number("heading"));
            }
            if (call.Has("pitch"))
            {
                camera.camPitch = Radians(call.Number("pitch"));
            }
            if (call.Has("fov"))
            {
                // What Alt and the mouse wheel do: the field kept by the camera, then applied to its cameras.
                camera.FieldOfView = Mathf.Clamp((float)call.Number("fov"), camera.fovMin, camera.fovMax);
                camera.SetFoV(camera.FieldOfView);
            }
            // What dragging with the middle mouse button changes: the aim, not the place of the camera. The game
            // clamps it every frame to half the field of view.
            if (call.Has("aim_heading"))
            {
                AimHeading.SetValue(camera, Radians(call.Number("aim_heading")));
            }
            if (call.Has("aim_pitch"))
            {
                AimPitch.SetValue(camera, Radians(call.Number("aim_pitch")));
            }
            yield return null;
            call.Text(new Dictionary<string, object>
            {
                { "distance", (double)camera.Distance },
                { "heading", camera.camHdg * 180.0 / Math.PI },
                { "pitch", camera.camPitch * 180.0 / Math.PI },
                { "fov", (double)camera.FieldOfView },
                { "aimHeading", (float)AimHeading.GetValue(camera) * 180.0 / Math.PI },
                { "aimPitch", (float)AimPitch.GetValue(camera) * 180.0 / Math.PI }
            });
        }

        private static float Radians(double degrees)
        {
            return (float)(degrees * Math.PI / 180.0);
        }

        private static IEnumerator SetUi(ToolCall call)
        {
            // What F2 does: the game and the mods that follow it listen to these two events.
            if (call.Bool("visible"))
            {
                GameEvents.onShowUI.Fire();
            }
            else
            {
                GameEvents.onHideUI.Fire();
            }
            yield return null;
            call.Text(new Dictionary<string, object> { { "visible", call.Bool("visible") } });
        }
    }
}
