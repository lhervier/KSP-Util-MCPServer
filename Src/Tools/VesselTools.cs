using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace com.github.lhervier.ksp.util.mcpserver
{
    /// <summary>
    /// Tools that choose the active vessel and its target, and move it as the Alt+F12 menu does: set it on
    /// the ground or on an orbit, with its cheats.
    /// </summary>
    internal static class VesselTools
    {
        public static IEnumerable<Tool> All()
        {
            yield return new Tool("switch_vessel",
                "Makes a vessel the active one, and waits until physics runs on it: a loaded vessel as the switch " +
                "vessel keys [ and ] do; one that is not loaded, of the player's, as Switch To of the map view " +
                "does: KSP saves the game as persistent and opens the flight again on it. Fails when KSP refuses " +
                "the switch (the active vessel under acceleration, moving over the surface, in the atmosphere...).",
                Schema.Object(
                    Schema.P("vessel", "string", "the vessel's id, or its name when no other vessel bears it", true)),
                SwitchVessel);
            yield return new Tool("set_target",
                "Sets the target of the active vessel to another vessel, as Set as Target does, or clears it.",
                Schema.Object(
                    Schema.P("vessel", "string",
                        "the vessel's id, or its name when no other vessel bears it; left out, clears the target")),
                SetTarget);
            yield return new Tool("go_eva",
                "Sends a crew member of the active vessel out on EVA, as the EVA button of the crew portrait does, " +
                "and waits until the kerbal is the active vessel and physics runs on it. Fails when KSP lets no one " +
                "out (no hatch, the hatch obstructed...).",
                Schema.Object(
                    Schema.P("kerbal", "string", "the crew member's name (default: the first one aboard)")),
                GoEva);
            yield return new Tool("set_cheats",
                "Turns on or off the cheats of the Cheats tab of the Alt+F12 menu given; those left out are kept. " +
                "Answers with the state of them all.",
                Schema.Object(Cheats.ConvertAll(c => Schema.P(c.Name, "boolean", c.Label)).ToArray()),
                SetCheats) { Concurrent = true };
            yield return new Tool("set_position",
                "Moves the active vessel above a point of a body, as Set Position of the Alt+F12 menu does, in the " +
                "attitude it had against its local horizon before the move. It first leaves the map view, where the " +
                "game builds no terrain, and stops the time warp. It holds the vessel a few metres up, " +
                "still, until the ground and what stands on it are built under it, lowers it to the given height " +
                "above whatever lies under it (the ground, a rock, a building), lets it go, and answers once it is " +
                "landed (or splashed) and still, or after the time limit. Refuses a point where it would reach " +
                "another vessel (50 m from one that is not loaded).",
                Schema.Object(
                    Schema.P("body", "string", "the body's name (default: the vessel's)"),
                    Schema.P("latitude", "number", "degrees", true),
                    Schema.P("longitude", "number", "degrees", true),
                    Schema.P("altitude", "number",
                        "metres between the lowest point of the vessel and what lies under it when it is let go " +
                        "(default 0.05)"),
                    Schema.P("heading", "number",
                        "degrees from north, towards east, the direction the control point faces once moved: the vessel " +
                        "is turned around the vertical (default: the heading it had)"),
                    Schema.P("timeout", "number", "seconds to wait for the vessel to settle once let go (default 60)")),
                SetPosition);
            yield return new Tool("set_orbit",
                "Puts the active vessel on an orbit, as Set Orbit of the Alt+F12 menu does, and waits until " +
                "physics runs on it again. Stops the time warp first: a landed vessel the warp holds on rails " +
                "would stay where it is set, turning with the body.",
                Schema.Object(
                    Schema.P("body", "string", "the body's name (default: the vessel's)"),
                    Schema.P("altitude", "number", "metres above the body's radius, for a circular orbit"),
                    Schema.P("sma", "number", "semi-major axis in metres, instead of altitude"),
                    Schema.P("eccentricity", "number", "default 0"),
                    Schema.P("inclination", "number", "degrees, default 0"),
                    Schema.P("lan", "number", "longitude of the ascending node, degrees, default 0"),
                    Schema.P("argument_of_periapsis", "number", "degrees, default 0"),
                    Schema.P("mean_anomaly", "number", "radians at the current time, default 0")),
                SetOrbit);
        }

        private static IEnumerator SwitchVessel(ToolCall call)
        {
            string error;
            Vessel vessel = Lookup.FindVessel(call.String("vessel"), out error);
            if (vessel == null)
            {
                call.Fail(error);
                yield break;
            }
            if (vessel == FlightGlobals.ActiveVessel)
            {
                call.Text(GameState.State());
                yield break;
            }
            if (!vessel.loaded)
            {
                // What Switch To of the map view does (MapContextMenuOptions.FocusObject): only for a vessel of
                // the player's, and only if the game lets the player switch to a vessel far away.
                if (vessel.DiscoveryInfo.Level != DiscoveryLevels.Owned)
                {
                    call.Fail(vessel.vesselName + " is not the player's: the map view does not switch to it");
                    yield break;
                }
                if (!HighLogic.CurrentGame.Parameters.Flight.CanSwitchVesselsFar)
                {
                    call.Fail("This game does not let the player switch to a vessel far away");
                    yield break;
                }
            }
            // Read before the switch: KSP refuses it for these reasons only, with a message on the screen.
            ClearToSaveStatus clear = FlightGlobals.ClearToSave();
            if (!FlightGlobals.SetActiveVessel(vessel))
            {
                call.Fail("KSP refused to switch to " + vessel.vesselName + ": " + clear);
                yield break;
            }
            if (!vessel.loaded)
            {
                // KSP saves the game as persistent and opens the flight again on that vessel: a new scene, with
                // a new Vessel object, found by its id.
                yield return Waits.ForNewActiveVessel(call, null, vessel.id);
                yield break;
            }
            yield return Waits.Until(call, () => FlightGlobals.ActiveVessel == vessel && !vessel.packed, 60f,
                "Physics was not running on " + vessel.vesselName + " after a minute");
            if (!call.IsError)
            {
                call.Text(GameState.State());
            }
        }

        private static IEnumerator SetTarget(ToolCall call)
        {
            if (!Require.FlightScene(call))
            {
                yield break;
            }
            if (!call.Has("vessel"))
            {
                FlightGlobals.fetch.SetVesselTarget(null);
                yield return null;
                call.Text("target cleared");
                yield break;
            }
            string error;
            Vessel vessel = Lookup.FindVessel(call.String("vessel"), out error);
            if (vessel == null)
            {
                call.Fail(error);
                yield break;
            }
            if (vessel == FlightGlobals.ActiveVessel)
            {
                call.Fail("The active vessel cannot be its own target");
                yield break;
            }
            // While targeting is locked, EVA construction mode for one, the game ignores the request and only
            // tells the player so on screen. The lock KSP sets on every control while its window is not in focus
            // is left aside: a player clicking a target never meets it, a client of this server always may.
            foreach (KeyValuePair<string, ulong> entry in InputLockManager.lockStack)
            {
                if (entry.Key != "flightDriver_ApplicationFocus" && (entry.Value & (ulong)ControlTypes.TARGETING) != 0)
                {
                    call.Fail("Targeting is locked (" + entry.Key + "), as it is in EVA construction mode: leave it first");
                    yield break;
                }
            }
            FlightGlobals.fetch.SetVesselTarget(vessel, true);
            yield return null;
            call.Text("target: " + vessel.vesselName + " (" + vessel.id + ")");
        }

        private static IEnumerator GoEva(ToolCall call)
        {
            if (!Require.Flight(call))
            {
                yield break;
            }
            Vessel vessel = FlightGlobals.ActiveVessel;
            ProtoCrewMember crew = null;
            foreach (ProtoCrewMember member in vessel.GetVesselCrew())
            {
                if (!call.Has("kerbal") || member.name == call.String("kerbal"))
                {
                    crew = member;
                    break;
                }
            }
            if (crew == null)
            {
                call.Fail(call.Has("kerbal") ? call.String("kerbal") + " is not aboard " + vessel.vesselName
                    : "No one is aboard " + vessel.vesselName);
                yield break;
            }
            if (crew.KerbalRef == null)
            {
                call.Fail(crew.name + " has no seat model loaded: KSP cannot send them out");
                yield break;
            }
            // What the EVA button of the portrait does (KerbalPortrait.ClickEVA).
            CameraManager.Instance.SetCameraFlight();
            if (FlightEVA.SpawnEVA(crew.KerbalRef) == null)
            {
                call.Fail("KSP let " + crew.name + " out of no hatch");
                yield break;
            }
            yield return Waits.Until(call, () => FlightGlobals.ActiveVessel != null
                    && FlightGlobals.ActiveVessel.isEVA && FlightGlobals.ActiveVessel.vesselName == crew.name
                    && !FlightGlobals.ActiveVessel.packed,
                60f, crew.name + " was not the active vessel a minute after leaving");
            if (!call.IsError)
            {
                call.Text(GameState.State());
            }
        }

        /// <summary>One cheat of the Alt+F12 menu: the argument that sets it, its label in the menu, its field.</summary>
        private sealed class Cheat
        {
            public readonly string Name;
            public readonly string Label;
            public readonly FieldInfo Field;

            public Cheat(string name, string label, string field)
            {
                Name = name;
                Label = label;
                Field = typeof(CheatOptions).GetField(field, BindingFlags.Public | BindingFlags.Static);
            }
        }

        /// <summary>The cheats of the Cheats tab of the Alt+F12 menu, in its order.</summary>
        private static readonly List<Cheat> Cheats = new List<Cheat>
        {
            new Cheat("unbreakable_joints", "Unbreakable Joints", "UnbreakableJoints"),
            new Cheat("no_crash_damage", "No Crash Damage", "NoCrashDamage"),
            new Cheat("ignore_max_temperature", "Ignore Max Temperature", "IgnoreMaxTemperature"),
            new Cheat("infinite_fuel", "Infinite Propellant", "InfinitePropellant"),
            new Cheat("infinite_electricity", "Infinite Electricity", "InfiniteElectricity"),
            new Cheat("biomes_visible", "Biomes Visible in Map View", "BiomesVisible"),
            new Cheat("allow_part_clipping", "Allow Part Clipping", "AllowPartClipping"),
            new Cheat("non_strict_attachment_orientation", "Non Strict Part Attachment Orientation Checks",
                "NonStrictAttachmentOrientation"),
            new Cheat("ignore_agency_mindset", "Ignore Agency Mindset on Contracts", "IgnoreAgencyMindsetOnContracts"),
            new Cheat("ignore_eva_construction_mass_limit", "Ignore EVA Construction Mass Limit",
                "IgnoreEVAConstructionMassLimit"),
            new Cheat("ignore_kerbal_inventory_limits", "Ignore Kerbal Inventory Limits", "IgnoreKerbalInventoryLimits")
        };

        private static IEnumerator SetCheats(ToolCall call)
        {
            foreach (Cheat cheat in Cheats)
            {
                if (call.Has(cheat.Name))
                {
                    cheat.Field.SetValue(null, call.Bool(cheat.Name));
                }
            }
            yield return null;
            Dictionary<string, object> state = new Dictionary<string, object>();
            foreach (Cheat cheat in Cheats)
            {
                state[cheat.Name] = (bool)cheat.Field.GetValue(null);
            }
            call.Text(state);
        }

        private static IEnumerator SetPosition(ToolCall call)
        {
            if (!Require.LiveVessel(call))
            {
                yield break;
            }
            int body = Lookup.BodyIndex(call);
            if (body < 0)
            {
                yield break;
            }
            Vessel placed = FlightGlobals.ActiveVessel;
            double reach = Reach(placed);

            // A vessel set where another one lies lands on it, and both break: the two may not reach each other, a
            // metre to spare. The reach of a vessel not loaded is not known: 50 m stands for it.
            CelestialBody target = FlightGlobals.Bodies[body];
            double lat = call.Number("latitude");
            double lon = call.Number("longitude");
            Vector3d spot = target.GetWorldSurfacePosition(lat, lon, Lookup.TerrainHeight(target, lat, lon));
            foreach (Vessel other in FlightGlobals.Vessels)
            {
                if (other == null || other == placed || other.mainBody != target)
                {
                    continue;
                }
                double distance = Vector3d.Distance(other.GetWorldPos3D(), spot);
                double apart = other.loaded ? reach + Reach(other) + 1.0 : 50.0;
                if (distance < apart)
                {
                    call.Fail(other.vesselName + " (" + other.id + ") lies " + distance.ToString("F1") +
                        " m from that point, " + apart.ToString("F1") + " m at least are needed");
                    yield break;
                }
            }

            double release = call.Number("altitude", 0.05);
            if (release < 0.0)
            {
                call.Fail("altitude: a height above what lies under the vessel, not below it");
                yield break;
            }

            // The map view deactivates the terrain of the vessel's body: nothing would be built under it. A vessel
            // packed by the time warp would not go off rails to be held.
            yield return Waits.ForFlightView(call);
            if (call.IsError)
            {
                yield break;
            }
            yield return Waits.ForNormalTime(call);
            if (call.IsError)
            {
                yield break;
            }

            // The attitude against the local horizon, taken where the vessel is now.
            Quaternion attitude = Quaternion.Inverse(
                Geo.LocalFrame(placed.mainBody, placed.latitude, placed.longitude, placed.altitude))
                * placed.vesselTransform.rotation;
            if (call.Has("heading"))
            {
                // Turned around the local vertical by the difference between the heading asked and the one it has.
                if (Math.Abs(Geo.Pitch(placed)) > 89.0)
                {
                    call.Fail("The control point of " + placed.vesselName + " faces straight up or down: it has no " +
                        "heading to turn to " + call.Number("heading") + " degrees");
                    yield break;
                }
                float turn = (float)(call.Number("heading") - Geo.Heading(placed));
                attitude = Quaternion.AngleAxis(turn, Vector3.up) * attitude;
            }

            // Held with its lowest point a few metres above the terrain, or at the height asked if higher: the
            // ground under it is not built yet, nor what stands on it.
            double ground = Lookup.TerrainHeight(target, lat, lon);
            bool overSea = target.ocean && ground < 0.0;
            double terrain = overSea ? 0.0 : ground;
            double rootAltitude = terrain + reach + Math.Max(HoldClearance, release);
            // What lies under the held vessel is nearer than this, or the ground is not built yet under it.
            double farthest = reach + Math.Max(HoldClearance, release) + 10.0;
            // As the menu does it, but without its ease to the ground: the game loses the lighter gravity of that
            // ease when the vessel goes off rails while keeping it flagged.
            FlightGlobals.fetch.SetVesselPosition(body, lat, lon, rootAltitude - terrain, 0.0, 0.0, true, false);
            FloatingOrigin.ResetTerrainShaderOffset();
            Hold(placed, target, lat, lon, rootAltitude, attitude);

            // Held still until what lies under it reads the same for a second, the vessel off rails.
            float holdStart = Time.realtimeSinceStartup;
            double clearance = double.PositiveInfinity;
            int steady = 0;
            while (true)
            {
                yield return new WaitForFixedUpdate();
                if (FlightGlobals.ActiveVessel != placed || placed.state == Vessel.State.DEAD)
                {
                    call.Fail("The vessel was destroyed while it was held above the ground");
                    yield break;
                }
                if (Time.realtimeSinceStartup - holdStart > HoldLimit)
                {
                    call.Fail("Nothing under the vessel read the same for a second after " + HoldLimit + " s (" +
                        (double.IsInfinity(clearance) ? "nothing found under it" : clearance.ToString("F3") + " m under it") +
                        "): it is let go where it is held, and falls");
                    yield break;
                }
                if (placed.packed)
                {
                    // On rails, the game keeps it where it was set.
                    continue;
                }
                Hold(placed, target, lat, lon, rootAltitude, attitude);
                Collider under;
                double reading = Clearance(placed, target, overSea, rootAltitude + reach + 100.0, out under);
                // A quad of the terrain below the finest level is still to be split, and its finer quads will not
                // stand at the same height.
                PQ quad = under != null ? under.GetComponent<PQ>() : null;
                if (reading > farthest || (quad != null && quad.subdivision < quad.sphereRoot.maxLevel))
                {
                    reading = double.PositiveInfinity;
                }
                steady = !double.IsInfinity(reading) && Math.Abs(reading - clearance) < 0.001 ? steady + 1 : 0;
                if (steady == 0 && !double.IsInfinity(reading))
                {
                    Log.Info("set_position: " + reading.ToString("F4") + " m above " + Describe(under, quad));
                }
                clearance = reading;
                if (steady >= SteadySteps)
                {
                    Log.Info("set_position: held " + (Time.realtimeSinceStartup - holdStart).ToString("F1") + " s, " +
                        clearance.ToString("F4") + " m above " + Describe(under, quad));
                    break;
                }
            }

            // Lowered to the height asked above what was found, held there a few steps, read again, then let go.
            for (int pass = 0; pass < 3 && Math.Abs(clearance - release) > 0.002; pass++)
            {
                rootAltitude -= clearance - release;
                for (int i = 0; i < 5; i++)
                {
                    Hold(placed, target, lat, lon, rootAltitude, attitude);
                    yield return new WaitForFixedUpdate();
                    if (FlightGlobals.ActiveVessel != placed || placed.state == Vessel.State.DEAD)
                    {
                        call.Fail("The vessel was destroyed while it was lowered");
                        yield break;
                    }
                }
                Hold(placed, target, lat, lon, rootAltitude, attitude);
                Collider under;
                clearance = Clearance(placed, target, overSea, rootAltitude + reach + 100.0, out under);
                Log.Info("set_position: lowered, " + clearance.ToString("F4") + " m above " +
                    Describe(under, under != null ? under.GetComponent<PQ>() : null));
                if (double.IsInfinity(clearance))
                {
                    call.Fail("Nothing was found under the vessel once lowered: it is let go where it is held");
                    yield break;
                }
            }
            Hold(placed, target, lat, lon, rootAltitude, attitude);
            Log.Info("set_position: let go");

            // Settled: on the ground, and still for two seconds in a row.
            float start = Time.realtimeSinceStartup;
            float stillSince = -1f;
            float limit = (float)call.Number("timeout", 60.0);
            float logged = -1f;
            while (Time.realtimeSinceStartup - start < limit)
            {
                yield return null;
                Vessel vessel = FlightGlobals.ActiveVessel;
                if (vessel == placed && Time.realtimeSinceStartup - logged >= 0.5f)
                {
                    logged = Time.realtimeSinceStartup;
                    Log.Info("set_position: " + vessel.situation + ", " + vessel.srfSpeed.ToString("F3") + " m/s, " +
                        vessel.heightFromTerrain.ToString("F3") + " m above the terrain");
                }
                // A vessel that broke on landing is no longer the active one, or is dead.
                if (vessel != placed || vessel.state == Vessel.State.DEAD)
                {
                    call.Fail("The vessel was destroyed while it was put down");
                    yield break;
                }
                bool down = !vessel.packed
                    && (vessel.situation == Vessel.Situations.LANDED || vessel.situation == Vessel.Situations.SPLASHED
                        || vessel.situation == Vessel.Situations.PRELAUNCH);
                if (down && vessel.srfSpeed < 0.01)
                {
                    if (stillSince < 0f)
                    {
                        stillSince = Time.realtimeSinceStartup;
                    }
                    else if (Time.realtimeSinceStartup - stillSince >= 2f)
                    {
                        call.Text(GameState.State());
                        yield break;
                    }
                }
                else
                {
                    stillSince = -1f;
                }
            }
            call.Fail("The vessel had not settled after " + limit + " s: " + Json.Write(GameState.State()));
        }

        // set_position: metres between the lowest point of the vessel and the terrain while it is held, how long it
        // may be held at most, and how many physics steps what lies under it must read the same (a second).
        private const double HoldClearance = 2.0;
        private const float HoldLimit = 30f;
        private const int SteadySteps = 50;

        /// <summary>
        /// Puts the vessel at a root altitude above a point of a body, in an attitude against the local horizon,
        /// still against the surface. To be called at every physics step for as long as it is held.
        /// </summary>
        private static void Hold(Vessel vessel, CelestialBody body, double latitude, double longitude, double altitude,
            Quaternion attitude)
        {
            // Recomputed from the body at every step: the floating origin and the frame may move meanwhile.
            Vector3d position = body.GetWorldSurfacePosition(latitude, longitude, altitude);
            vessel.SetRotation(Geo.LocalFrame(body, latitude, longitude, altitude) * attitude, false);
            // The parts at their places in the craft as built, around the root part.
            vessel.SetPosition(position, true);
            if (!vessel.packed)
            {
                // Still against the surface, Krakensbane's frame velocity taken out as every velocity of the game.
                // While the scene turns with the body (inverseRotation, low down), the surface does not move in it.
                Vector3d surface = body.inverseRotation ? Vector3d.zero : body.getRFrmVel(position);
                Vector3 velocity = surface - Krakensbane.GetFrameVelocity();
                foreach (Part part in vessel.parts)
                {
                    if (part.rb != null)
                    {
                        part.rb.velocity = velocity;
                        part.rb.angularVelocity = Vector3.zero;
                    }
                }
            }
            // Being moved at every step is not a crash.
            vessel.IgnoreGForces(2);
            vessel.IgnoreSpeed(2);
        }

        /// <summary>What lies under a vessel, for the log: the sea, a quad of the terrain and its level, or another
        /// collider.</summary>
        private static string Describe(Collider under, PQ quad)
        {
            if (under == null)
            {
                return "the sea";
            }
            if (quad != null)
            {
                return "quad " + quad.name + ", level " + quad.subdivision + " of " + quad.sphereRoot.maxLevel;
            }
            return under.name + " (layer " + under.gameObject.layer + ")";
        }

        /// <summary>The colliders of a vessel's parts that touch the world.</summary>
        private static IEnumerable<Collider> SolidColliders(Vessel vessel)
        {
            foreach (Part part in vessel.parts)
            {
                foreach (Collider collider in part.GetComponentsInChildren<Collider>())
                {
                    // Layer 21: the part triggers, as ShipConstruction.PutShipToGround leaves them out.
                    if (collider.enabled && !collider.isTrigger && collider.gameObject.layer != 21)
                    {
                        yield return collider;
                    }
                }
            }
        }

        /// <summary>The corners of a collider's world box.</summary>
        private static IEnumerable<Vector3> Corners(Bounds bounds)
        {
            for (int i = 0; i < 8; i++)
            {
                yield return new Vector3((i & 1) == 0 ? bounds.min.x : bounds.max.x,
                    (i & 2) == 0 ? bounds.min.y : bounds.max.y, (i & 4) == 0 ? bounds.min.z : bounds.max.z);
            }
        }

        /// <summary>How far the vessel reaches from its root part, whatever its attitude, in metres.</summary>
        private static double Reach(Vessel vessel)
        {
            Vector3 root = vessel.vesselTransform.position;
            double reach = 0.0;
            foreach (Collider collider in SolidColliders(vessel))
            {
                foreach (Vector3 corner in Corners(collider.bounds))
                {
                    reach = Math.Max(reach, (corner - root).magnitude);
                }
            }
            return reach;
        }

        /// <summary>
        /// Metres between the vessel and what lies under it, along the local vertical: the first collider its parts
        /// would hit falling straight down, or the sea when <paramref name="overSea"/>, whichever is nearer;
        /// infinite when there is neither within <paramref name="range"/> metres.
        /// </summary>
        private static double Clearance(Vessel vessel, CelestialBody body, bool overSea, double range, out Collider under)
        {
            Vector3 down = -(vessel.CoMD - body.position).normalized;
            double nearest = double.PositiveInfinity;
            under = null;
            foreach (Part part in vessel.parts)
            {
                if (part.rb == null)
                {
                    // A part without physics moves with its parent, whose sweep carries its colliders.
                    continue;
                }
                foreach (RaycastHit hit in part.rb.SweepTestAll(down, (float)range, QueryTriggerInteraction.Ignore))
                {
                    // The vessel's own parts, and what its parts do not collide with, are not under it.
                    Part other = hit.collider.GetComponentInParent<Part>();
                    if ((other != null && other.vessel == vessel)
                        || Physics.GetIgnoreLayerCollision(part.gameObject.layer, hit.collider.gameObject.layer))
                    {
                        continue;
                    }
                    if (hit.distance < nearest)
                    {
                        nearest = hit.distance;
                        under = hit.collider;
                    }
                }
            }
            if (overSea)
            {
                // The sea has no collider: the altitude of the lowest corner of the vessel's boxes stands for it.
                foreach (Collider collider in SolidColliders(vessel))
                {
                    foreach (Vector3 corner in Corners(collider.bounds))
                    {
                        double altitude = body.GetAltitude(corner);
                        if (altitude >= 0.0 && altitude < nearest)
                        {
                            nearest = altitude;
                            under = null;
                        }
                    }
                }
            }
            return nearest;
        }

        private static IEnumerator SetOrbit(ToolCall call)
        {
            if (!Require.LiveVessel(call))
            {
                yield break;
            }
            int body = Lookup.BodyIndex(call);
            if (body < 0)
            {
                yield break;
            }
            double sma = call.Has("sma") ? call.Number("sma")
                : call.Has("altitude") ? FlightGlobals.Bodies[body].Radius + call.Number("altitude") : double.NaN;
            if (double.IsNaN(sma))
            {
                call.Fail("Give either altitude or sma");
                yield break;
            }
            // A landed vessel packed by the time warp keeps its orbit driver tracking its transform, the game does
            // not pack it again for the move, and it would stay where it is set, turning with the body.
            yield return Waits.ForNormalTime(call);
            if (call.IsError)
            {
                yield break;
            }
            FlightGlobals.fetch.SetShipOrbit(body, call.Number("eccentricity", 0.0), sma, call.Number("inclination", 0.0),
                call.Number("lan", 0.0), call.Number("mean_anomaly", 0.0), call.Number("argument_of_periapsis", 0.0), 0.0);
            FloatingOrigin.ResetTerrainShaderOffset();

            // The vessel is packed for the move, and unpacked a few frames later.
            yield return null;
            yield return Waits.Until(call, () => FlightGlobals.ActiveVessel != null && !FlightGlobals.ActiveVessel.packed,
                60f, "Physics was not running on the vessel a minute after the orbit was set");
            if (call.IsError)
            {
                yield break;
            }
            if (FlightGlobals.ActiveVessel.state == Vessel.State.DEAD)
            {
                // An orbit that runs under the terrain: the vessel crashes as physics resumes.
                call.Fail("The vessel was destroyed once on its orbit: " + Json.Write(GameState.State()));
                yield break;
            }
            call.Text(GameState.State());
        }
    }
}
