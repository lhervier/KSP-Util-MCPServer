using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
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
                "Captures the screen as it is drawn, user interface included. Returns the image, and saves it " +
                "as a PNG when a path is given (relative to the KSP folder, or absolute).",
                Schema.Object(
                    Schema.P("path", "string", "where to save the PNG"),
                    Schema.P("return_image", "boolean", "whether to return the image (default true)")),
                Screenshot) { Concurrent = true };
            yield return new Tool("set_camera",
                "Sets the flight camera: distance from the vessel in metres, heading (the direction it looks in, " +
                "from north towards east) and pitch in degrees, and its field of view, which Alt and the mouse " +
                "wheel narrow (20 to 160 degrees, 60 by default), and where it aims away from the vessel, as " +
                "dragging with the middle mouse button does (at most half the field of view either way). Values " +
                "left out are kept.",
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
                "Enters or leaves the map view of a flight, as the M key does, and answers once the game shows " +
                "the view asked for; centres the map's camera on a body or a vessel, as double-clicking it or " +
                "Focus View of its menu does, and sets the camera's distance. Refuses when the game would not take " +
                "the key: the map toggle locked, or the map view not allowed in this game. Answers with what the " +
                "camera is centred on and its distance.",
                Schema.Object(
                    Schema.P("enabled", "boolean",
                        "true to enter the map view, false to leave it (default: true with focus or distance, the " +
                        "view kept otherwise)"),
                    Schema.P("focus", "string",
                        "the body's name, or else the vessel's id, or its name when no other vessel bears it"),
                    Schema.P("distance", "number",
                        "the camera's distance from what it is centred on, in units of the map (6 km each), within " +
                        "the limits the game sets for it")),
                MapViewTool);
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
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                File.WriteAllBytes(path, png);
                call.Text("Saved " + Path.GetFullPath(path) + " (" + png.Length + " bytes)");
            }
            if (call.Bool("return_image", true))
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
