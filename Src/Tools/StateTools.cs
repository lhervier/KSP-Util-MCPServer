using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace com.github.lhervier.ksp.util.mcpserver
{
    /// <summary>Tools that read the game without changing it, and the tool that lets it run.</summary>
    internal static class StateTools
    {
        public static IEnumerable<Tool> All()
        {
            yield return new Tool("get_state",
                "Reads the game: the folder of the KSP that answers, scene, pause, universal time, time warp rate, and for the active vessel its id, " +
                "name, situation, latitude, longitude, altitude, height above the terrain, surface speed, heading " +
                "(degrees from north, towards east), pitch (degrees above the horizon, as the navball shows it), " +
                "brakes, lights, gear, SAS, the local time where it is (the fraction of " +
                "its body's solar day, 0.5 at noon) with the length of that day in seconds, and its link to the " +
                "CommNet network: connected or not, signal strength, and each hop of its control path with its " +
                "length in metres. In flight, also whether the next alarm shows above the time display, as the " +
                "alarm clock button left of it toggles it.",
                Schema.Object(), GetState) { Concurrent = true };
            yield return new Tool("get_floating_origin",
                "Reads KSP's floating origin, the point the world is kept centred on: the active vessel's " +
                "distance from it, where it lies from the vessel (metres north, east and up), the distance at " +
                "which KSP moves it back onto the vessel, and the shifts counted since the flight scene opened " +
                "with the length of the last one.",
                Schema.Object(), GetFloatingOrigin) { Concurrent = true };
            yield return new Tool("list_vessels",
                "Lists the vessels of the game: id, name, situation, body, whether each is loaded and packed, " +
                "whether it is the active vessel or its target, and its distance from the active vessel in metres.",
                Schema.Object(), ListVessels) { Concurrent = true };
            yield return new Tool("get_terrain",
                "Reads the terrain of a body at a point, as the game computes it: the body's radius, the height " +
                "of the terrain above it, whether the sea covers it, and its slope and roughness (the largest " +
                "difference of height) from the heights at four points around it.",
                Schema.Object(
                    Schema.P("body", "string", "the body's name (default: the active vessel's)"),
                    Schema.P("latitude", "number", "degrees", true),
                    Schema.P("longitude", "number", "degrees", true),
                    Schema.P("spacing", "number", "metres from the point to the four points of the slope (default 5)")),
                GetTerrain) { Concurrent = true };
            yield return new Tool("list_rocs",
                "Lists the surface features of Breaking Ground (ROCs) built near the active vessel, nearest first: " +
                "their id, type and name, their distance from the active vessel in metres, the latitude, longitude " +
                "and altitude of their pivot, the height of the terrain there and of the pivot above it, how many " +
                "colliders they have and how many are on, and whether a part of the active vessel touches them.",
                Schema.Object(Schema.P("radius", "number", "metres from the active vessel (default 300)")),
                ListRocs) { Concurrent = true };
            yield return new Tool("wait",
                "Waits a number of seconds of real time, letting the game run.",
                Schema.Object(Schema.P("seconds", "number", "how long", true)),
                Wait) { Concurrent = true };
        }

        private static IEnumerator GetState(ToolCall call)
        {
            Dictionary<string, object> state = GameState.State();
            state["kspFolder"] = Path.GetFullPath(KSPUtil.ApplicationRootPath);
            call.Text(state);
            yield break;
        }

        private static IEnumerator GetFloatingOrigin(ToolCall call)
        {
            call.Text(GameState.FloatingOriginState());
            yield break;
        }

        private static IEnumerator ListVessels(ToolCall call)
        {
            if (!Require.FlightScene(call))
            {
                yield break;
            }
            Vessel active = FlightGlobals.ActiveVessel;
            ITargetable target = FlightGlobals.fetch.VesselTarget;
            Vessel targetVessel = target != null ? target.GetVessel() : null;
            List<object> vessels = new List<object>();
            foreach (Vessel v in FlightGlobals.Vessels)
            {
                if (v == null)
                {
                    continue;
                }
                vessels.Add(new Dictionary<string, object>
                {
                    { "id", v.id.ToString() },
                    { "name", v.vesselName },
                    { "situation", v.situation.ToString() },
                    { "body", v.mainBody != null ? v.mainBody.bodyName : null },
                    { "loaded", v.loaded },
                    { "packed", v.packed },
                    { "active", v == active },
                    { "target", v == targetVessel },
                    { "distance", active != null ? Vector3d.Distance(v.GetWorldPos3D(), active.GetWorldPos3D()) : double.NaN }
                });
            }
            call.Text(vessels);
        }

        private static IEnumerator GetTerrain(ToolCall call)
        {
            if (FlightGlobals.Bodies == null || (!call.Has("body") && FlightGlobals.ActiveVessel == null))
            {
                call.Fail("Name a body, or be in flight");
                yield break;
            }
            int index = Lookup.BodyIndex(call);
            if (index < 0)
            {
                yield break;
            }
            CelestialBody body = FlightGlobals.Bodies[index];
            if (body.pqsController == null)
            {
                call.Fail(body.bodyName + " has no terrain");
                yield break;
            }
            double lat = call.Number("latitude");
            double lon = call.Number("longitude");
            double spacing = call.Number("spacing", 5.0);
            double height = Lookup.TerrainHeight(body, lat, lon);

            // The four points north, south, east and west of it, by the angle the spacing makes at the centre.
            double radius = body.Radius + height;
            double dLat = spacing / radius * 180.0 / Math.PI;
            double dLon = dLat / Math.Max(Math.Cos(lat * Math.PI / 180.0), 1e-6);
            double north = Lookup.TerrainHeight(body, lat + dLat, lon);
            double south = Lookup.TerrainHeight(body, lat - dLat, lon);
            double east = Lookup.TerrainHeight(body, lat, lon + dLon);
            double west = Lookup.TerrainHeight(body, lat, lon - dLon);
            double gradient = Math.Sqrt(Math.Pow((north - south) / (2 * spacing), 2) + Math.Pow((east - west) / (2 * spacing), 2));
            call.Text(new Dictionary<string, object>
            {
                { "body", body.bodyName },
                { "radius", body.Radius },
                { "height", height },
                { "underSea", body.ocean && height < 0.0 },
                { "slope", Math.Atan(gradient) * 180.0 / Math.PI },
                { "roughness", Math.Max(Math.Max(north, south), Math.Max(east, west))
                    - Math.Min(Math.Min(north, south), Math.Min(east, west)) }
            });
        }

        private static IEnumerator ListRocs(ToolCall call)
        {
            if (!Require.FlightScene(call))
            {
                yield break;
            }
            Vessel active = FlightGlobals.ActiveVessel;
            if (active == null)
            {
                call.Fail("No active vessel");
                yield break;
            }
            double radius = call.Number("radius", 300.0);
            CelestialBody body = active.mainBody;

            // The colliders the active vessel's parts touch, to tell which ROC it stands on.
            HashSet<Collider> touched = new HashSet<Collider>();
            foreach (Part p in active.parts)
            {
                if (p.currentCollisions == null)
                {
                    continue;
                }
                for (int i = 0; i < p.currentCollisions.Count; i++)
                {
                    touched.Add(p.currentCollisions.KeyAt(i));
                }
            }

            List<KeyValuePair<double, object>> found = new List<KeyValuePair<double, object>>();
            foreach (ROC roc in UnityEngine.Object.FindObjectsOfType<ROC>())
            {
                Vector3d position = roc.transform.position;
                double distance = Vector3d.Distance(position, active.CoMD);
                if (distance > radius)
                {
                    continue;
                }
                double lat = body.GetLatitude(position);
                double lon = body.GetLongitude(position);
                double alt = body.GetAltitude(position);
                double terrain = body.pqsController != null ? Lookup.TerrainHeight(body, lat, lon) : double.NaN;
                Collider[] colliders = roc.GetComponentsInChildren<Collider>(true);
                int on = 0;
                bool touching = false;
                foreach (Collider c in colliders)
                {
                    if (c.enabled && c.gameObject.activeInHierarchy)
                    {
                        on++;
                    }
                    touching |= touched.Contains(c);
                }
                found.Add(new KeyValuePair<double, object>(distance, new Dictionary<string, object>
                {
                    { "id", roc.rocID },
                    { "type", roc.type },
                    { "name", roc.displayName },
                    { "distance", distance },
                    { "latitude", lat },
                    { "longitude", lon },
                    { "altitude", alt },
                    { "terrainHeight", terrain },
                    { "heightFromTerrain", alt - terrain },
                    { "colliders", colliders.Length },
                    { "collidersOn", on },
                    { "touching", touching }
                }));
            }
            found.Sort((a, b) => a.Key.CompareTo(b.Key));
            List<object> rocs = new List<object>();
            foreach (KeyValuePair<double, object> entry in found)
            {
                rocs.Add(entry.Value);
            }
            call.Text(rocs);
        }

        private static IEnumerator Wait(ToolCall call)
        {
            yield return new WaitForSecondsRealtime((float)call.Number("seconds", 1.0));
            call.Text("waited");
        }
    }
}
