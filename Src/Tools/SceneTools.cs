using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace com.github.lhervier.ksp.util.mcpserver
{
    /// <summary>
    /// Tools that go from one scene to another as the game's buttons do: launch a vessel, revert a flight,
    /// recover a vessel, fly one from the tracking station, leave for another scene.
    /// </summary>
    internal static class SceneTools
    {
        private static readonly Dictionary<string, GameScenes> Scenes = new Dictionary<string, GameScenes>
        {
            { "SPACECENTER", GameScenes.SPACECENTER },
            { "TRACKSTATION", GameScenes.TRACKSTATION },
            { "MAINMENU", GameScenes.MAINMENU }
        };

        private static readonly Dictionary<string, EditorFacility> Editors = new Dictionary<string, EditorFacility>
        {
            { "VAB", EditorFacility.VAB },
            { "SPH", EditorFacility.SPH }
        };

        public static IEnumerable<Tool> All()
        {
            yield return new Tool("launch_vessel",
                "Launches a .craft at a launch site, from the space centre or an editor only, as the launch " +
                "dialogs do, with the default crew or the kerbals named, and waits until physics runs. Saves " +
                "persistent first. Refuses parts not unlocked and sites the game does not offer.",
                Schema.Object(
                    Schema.P("craft", "string",
                        "the .craft file: absolute, or relative to the game's Ships folder (VAB/<name>.craft)", true),
                    Schema.P("site", "string",
                        "the launch site's name, as list_launch_sites gives it (default Runway; LaunchPad for the pad)"),
                    Schema.P("crew", "string",
                        "kerbals to board, comma-separated, in seat order (default: the editor's crew)"),
                    Schema.P("empty_inventories", "boolean",
                        "board the crew with empty inventories (default false)")),
                LaunchVessel);
            yield return new Tool("list_launch_sites",
                "Lists the launch sites the editors offer: the name launch_vessel takes, the shown name, the " +
                "editor, the body, what places it, and its first spawn point. Needs a game loaded.",
                Schema.Object(), ListLaunchSites) { Concurrent = true };
            yield return new Tool("revert_to_launch",
                "Reverts the flight to its launch, as Revert to Launch does, and waits until physics runs on " +
                "the vessel again.",
                Schema.Object(), RevertToLaunch);
            yield return new Tool("revert_to_editor",
                "Reverts the flight to the editor it was launched from, as Revert to Vehicle Assembly Building " +
                "or Revert to Space Plane Hangar does, and waits until the editor is up.",
                Schema.Object(
                    Schema.P("facility", "string", "VAB or SPH (default VAB)")),
                RevertToEditor);
            yield return new Tool("recover_vessel",
                "Recovers the active vessel, as the Recover button above the altimeter does: KSP saves the game, " +
                "goes to the space centre and recovers the vessel there. Waits until the recovery report is up, " +
                "and answers with the place and the share of the value it gives (location, factor).",
                Schema.Object(), RecoverVessel);
            yield return new Tool("go_to_scene",
                "Leaves the current scene for the space centre, the tracking station or the main menu, saving " +
                "persistent first as the game's buttons do, and waits until it is up. Refused at the main menu: " +
                "open_game or new_game first.",
                Schema.Object(
                    Schema.P("scene", "string", "SPACECENTER, TRACKSTATION or MAINMENU", true)),
                GoToScene);
            yield return new Tool("fly_vessel",
                "Flies a vessel from the tracking station, as its Fly button does: KSP saves the game as " +
                "persistent and opens the flight on that vessel. Waits until physics runs on it. Only a vessel " +
                "the player owns, not an asteroid.",
                Schema.Object(
                    Schema.P("vessel", "string", "the vessel's id, or its name when no other vessel bears it", true)),
                FlyVessel);
        }

        private static IEnumerator LaunchVessel(ToolCall call)
        {
            // A player launches from the space centre, or from an editor reached through it, never straight from
            // the main menu or from flight.
            if (HighLogic.LoadedScene != GameScenes.SPACECENTER && HighLogic.LoadedScene != GameScenes.EDITOR)
            {
                call.Fail("Vessels are launched from the space centre or its editors, and the game is in " +
                    HighLogic.LoadedScene + (HighLogic.LoadedScene == GameScenes.MAINMENU
                        ? ": open a game first with open_game or new_game, which go to the space centre"
                        : ": go to the space centre first with go_to_scene"));
                yield break;
            }
            string path = call.String("craft");
            if (!Path.IsPathRooted(path))
            {
                path = Path.Combine(GameTools.SavePath(HighLogic.SaveFolder, "Ships"), path);
            }
            path = Path.GetFullPath(path);
            if (!Sandbox.Allows(call, path))
            {
                yield break;
            }
            ConfigNode craft = File.Exists(path) ? ConfigNode.Load(path) : null;
            if (craft == null)
            {
                call.Fail("Could not read the craft " + path);
                yield break;
            }

            // What the launch dialog of the space centre reads of the craft. Where it asks for a confirmation
            // before launching a craft whose parts are not all unlocked, this refuses.
            KSP.UI.Screens.CraftProfileInfo profile = new KSP.UI.Screens.CraftProfileInfo().LoadDetailsFromCraftFile(craft, path);
            if (!profile.shipPartsUnlocked || !profile.shipPartModulesAvailable)
            {
                call.Fail("The craft cannot be launched as it is in this game: " + profile.GetErrorMessage());
                yield break;
            }
            string site = call.String("site", "Runway");
            List<string> sites = new List<string>();
            foreach (Dictionary<string, object> known in LaunchSites())
            {
                sites.Add((string)known["name"]);
            }
            if (!sites.Contains(site))
            {
                call.Fail("No launch site '" + site + "' in this game; the sites are: " + string.Join(", ", sites.ToArray()));
                yield break;
            }
            string limits = LaunchLimitsBroken(profile, site);
            if (limits != null)
            {
                call.Fail("The editor would not launch this craft: " + limits);
                yield break;
            }

            VesselCrewManifest crew;
            if (call.Has("crew"))
            {
                string error;
                crew = NamedCrew(craft, call.String("crew"), out error);
                if (crew == null)
                {
                    call.Fail(error);
                    yield break;
                }
            }
            else
            {
                // The crew the editor proposes for that craft, without hiring anyone.
                crew = HighLogic.CurrentGame.CrewRoster.DefaultCrewForVessel(craft, null, false);
            }
            if (call.Bool("empty_inventories"))
            {
                foreach (ProtoCrewMember kerbal in crew.GetAllCrew(false))
                {
                    EmptyInventory(kerbal);
                }
            }
            Vessel previous = FlightGlobals.ActiveVessel;
            FlightDriver.StartWithNewLaunch(path, HighLogic.CurrentGame.flagURL, site, crew);
            yield return Waits.ForNewActiveVessel(call, previous);
        }

        /// <summary>
        /// Empties a kerbal's inventory, as removing every part from it in the editor's crew panel does; the
        /// kerbal keeps it empty once on EVA.
        /// </summary>
        private static void EmptyInventory(ProtoCrewMember kerbal)
        {
            // The panel edits this module, then saves it to the kerbal's inventory node, which the kerbal on EVA
            // loads its inventory from.
            ModuleInventoryPart inventory = kerbal.KerbalInventoryModule;
            if (inventory == null)
            {
                kerbal.InventoryNode = new ConfigNode("INVENTORY");
                return;
            }
            for (int slot = 0; slot < inventory.InventorySlots; slot++)
            {
                inventory.ClearPartAtSlot(slot);
            }
            kerbal.SaveInventory(inventory);
        }

        private static IEnumerator ListLaunchSites(ToolCall call)
        {
            if (!Require.Game(call))
            {
                yield break;
            }
            call.Text(LaunchSites());
        }

        /// <summary>
        /// The launch sites of the game a vessel can be launched at, as the editors' launch site selector offers
        /// them: the space centre's launch facilities, then the other sites the game finds by name.
        /// </summary>
        private static List<Dictionary<string, object>> LaunchSites()
        {
            List<Dictionary<string, object>> sites = new List<Dictionary<string, object>>();
            PSystemSetup setup = PSystemSetup.Instance;
            if (setup == null)
            {
                return sites;
            }
            foreach (PSystemSetup.SpaceCenterFacility facility in setup.SpaceCenterFacilityLaunchSites)
            {
                PSystemSetup.SpaceCenterFacility.SpawnPoint spawn =
                    facility.spawnPoints != null && facility.spawnPoints.Length > 0 ? facility.spawnPoints[0] : null;
                sites.Add(Site(facility.name, KSP.Localization.Localizer.Format(facility.facilityDisplayName), facility.editorFacility, facility.hostBody,
                    "space centre", spawn != null, spawn != null ? spawn.latitude : 0, spawn != null ? spawn.longitude : 0,
                    spawn != null ? spawn.altitude : 0));
            }
            foreach (LaunchSite site in setup.LaunchSites)
            {
                // GetLaunchSite leaves out the sites of an expansion not installed and of a bundle it does not
                // know: the selector shows none of them, and a launch there fails.
                if (site == null || setup.GetLaunchSite(site.name) != site)
                {
                    continue;
                }
                LaunchSite.SpawnPoint spawn = site.spawnPoints != null && site.spawnPoints.Length > 0 ? site.spawnPoints[0] : null;
                sites.Add(Site(site.name, KSP.Localization.Localizer.Format(site.launchSiteName), site.editorFacility,
                    site.Body, site.isPQSCity2 ? "PQSCity2" : site.isPQSCity ? "PQSCity" : "other", spawn != null,
                    spawn != null ? spawn.latitude : 0, spawn != null ? spawn.longitude : 0, spawn != null ? spawn.altitude : 0));
            }
            return sites;
        }

        private static Dictionary<string, object> Site(string name, string displayName, EditorFacility editor,
            CelestialBody body, string placedBy, bool hasSpawn, double latitude, double longitude, double altitude)
        {
            return new Dictionary<string, object>
            {
                { "name", name },
                { "displayName", displayName },
                { "editor", editor.ToString() },
                { "body", body != null ? body.bodyName : null },
                { "placedBy", placedBy },
                { "latitude", hasSpawn ? (object)latitude : null },
                { "longitude", hasSpawn ? (object)longitude : null },
                { "altitude", hasSpawn ? (object)altitude : null }
            };
        }

        /// <summary>
        /// A crew manifest for the craft with the kerbals named, comma-separated, in the order of its crewable
        /// parts and seats, the other seats empty; or null, with the reason in error, when a kerbal is unknown or
        /// not available, or when the craft has too few seats.
        /// </summary>
        private static VesselCrewManifest NamedCrew(ConfigNode craft, string names, out string error)
        {
            error = null;
            KerbalRoster roster = HighLogic.CurrentGame.CrewRoster;
            Queue<ProtoCrewMember> kerbals = new Queue<ProtoCrewMember>();
            foreach (string raw in names.Split(','))
            {
                string name = raw.Trim();
                if (name.Length == 0)
                {
                    continue;
                }
                ProtoCrewMember kerbal = roster[name];
                if (kerbal == null)
                {
                    error = "No kerbal named '" + name + "' in this game";
                    return null;
                }
                if (kerbal.rosterStatus != ProtoCrewMember.RosterStatus.Available)
                {
                    error = name + " is not available: " + kerbal.rosterStatus;
                    return null;
                }
                kerbals.Enqueue(kerbal);
            }

            // Empty every seat first: the manifest read from the craft may hold the crew it was saved with.
            VesselCrewManifest manifest = VesselCrewManifest.FromConfigNode(craft);
            foreach (PartCrewManifest part in manifest.GetCrewableParts())
            {
                ProtoCrewMember[] seats = part.GetPartCrew();
                for (int seat = 0; seat < seats.Length; seat++)
                {
                    if (seats[seat] != null)
                    {
                        part.RemoveCrewFromSeat(seat);
                    }
                }
                for (int seat = 0; seat < seats.Length && kerbals.Count > 0; seat++)
                {
                    part.AddCrewToSeat(kerbals.Dequeue(), seat);
                }
            }
            if (kerbals.Count > 0)
            {
                error = "The craft has too few seats for the crew named: " + kerbals.Count + " left without one";
                return null;
            }
            return manifest;
        }

        /// <summary>
        /// Why the editor's Launch button would refuse a craft at a launch site of the space centre — too heavy or
        /// too large for the site's level, too many parts for its editor's — or null when it would not. Sites other
        /// than the launchpad and the runway are not checked.
        /// </summary>
        private static string LaunchLimitsBroken(KSP.UI.Screens.CraftProfileInfo profile, string site)
        {
            SpaceCenterFacility facility;
            if (string.Equals(site, "LaunchPad", StringComparison.OrdinalIgnoreCase))
            {
                facility = SpaceCenterFacility.LaunchPad;
            }
            else if (string.Equals(site, "Runway", StringComparison.OrdinalIgnoreCase))
            {
                facility = SpaceCenterFacility.Runway;
            }
            else
            {
                return null;
            }
            bool isPad = facility == SpaceCenterFacility.LaunchPad;
            bool isVAB = profile.shipFacility != EditorFacility.SPH;
            float siteLevel = ScenarioUpgradeableFacilities.GetFacilityLevel(facility);
            float editorLevel = ScenarioUpgradeableFacilities.GetFacilityLevel(
                isVAB ? SpaceCenterFacility.VehicleAssemblyBuilding : SpaceCenterFacility.SpaceplaneHangar);

            // The tests of the editor, with the limits it reads.
            PreFlightTests.IPreFlightTest[] tests =
            {
                new PreFlightTests.CraftWithinMassLimits(profile.totalMass, profile.shipName, facility,
                    GameVariables.Instance.GetCraftMassLimit(siteLevel, isPad)),
                new PreFlightTests.CraftWithinSizeLimits(profile.shipSize, profile.shipName, facility,
                    GameVariables.Instance.GetCraftSizeLimit(siteLevel, isPad)),
                new PreFlightTests.CraftWithinPartCountLimit(profile.partCount,
                    isVAB ? SpaceCenterFacility.VehicleAssemblyBuilding : SpaceCenterFacility.SpaceplaneHangar,
                    GameVariables.Instance.GetPartCountLimit(editorLevel, isVAB))
            };
            List<string> broken = new List<string>();
            foreach (PreFlightTests.IPreFlightTest test in tests)
            {
                if (!test.Test())
                {
                    broken.Add(test.GetWarningTitle() + ": " + test.GetWarningDescription());
                }
            }
            return broken.Count > 0 ? string.Join("; ", broken.ToArray()) : null;
        }

        private static IEnumerator RevertToLaunch(ToolCall call)
        {
            if (!Require.Flight(call))
            {
                yield break;
            }
            if (!FlightDriver.CanRevertToPostInit || FlightDriver.PostInitState == null)
            {
                call.Fail("This flight cannot be reverted to its launch");
                yield break;
            }
            Vessel previous = FlightGlobals.ActiveVessel;
            FlightDriver.RevertToLaunch();
            // What the buttons of the flight results dialog do after theirs, the dialog being up after a crash.
            KSP.UI.Dialogs.FlightResultsDialog.Close();
            yield return Waits.ForNewActiveVessel(call, previous);
        }

        private static IEnumerator RevertToEditor(ToolCall call)
        {
            if (!Require.Flight(call))
            {
                yield break;
            }
            EditorFacility facility;
            if (!call.Choice("facility", "VAB", Editors, out facility))
            {
                yield break;
            }
            if (!FlightDriver.CanRevertToPrelaunch || FlightDriver.PreLaunchState == null)
            {
                call.Fail("This flight cannot be reverted to an editor");
                yield break;
            }
            SceneWatch watch = SceneWatch.Start(GameScenes.EDITOR);
            FlightDriver.RevertToPrelaunch(facility);
            // What the buttons of the flight results dialog do after theirs, the dialog being up after a crash.
            KSP.UI.Dialogs.FlightResultsDialog.Close();
            yield return Waits.ForScene(call, watch);
            if (!call.IsError)
            {
                call.Text(GameState.State());
            }
        }

        private static IEnumerator RecoverVessel(ToolCall call)
        {
            if (!Require.Flight(call))
            {
                yield break;
            }
            Vessel v = FlightGlobals.ActiveVessel;
            if (!v.IsRecoverable)
            {
                call.Fail("The active vessel cannot be recovered");
                yield break;
            }
            // The button refuses for the same reasons: moving, on a ladder, about to crash...
            ClearToSaveStatus status = FlightGlobals.ClearToSave();
            if (status != ClearToSaveStatus.CLEAR)
            {
                call.Fail("KSP refuses to recover now: " + status);
                yield break;
            }
            SceneWatch watch = SceneWatch.Start(GameScenes.SPACECENTER);
            GameEvents.OnVesselRecoveryRequested.Fire(v);
            yield return Waits.ForScene(call, watch);
            if (call.IsError)
            {
                yield break;
            }

            // The vessel is recovered once the space centre is up, and the report follows.
            KSP.UI.Screens.MissionRecoveryDialog report = null;
            yield return Waits.Until(call,
                () => (report = UnityEngine.Object.FindObjectOfType<KSP.UI.Screens.MissionRecoveryDialog>()) != null,
                30f, "The space centre is up, but no recovery report after 30 seconds");
            if (call.IsError)
            {
                yield break;
            }
            Dictionary<string, object> state = GameState.State();
            state["recovery"] = new Dictionary<string, object>
            {
                { "location", report.recoveryLocation },
                { "factor", report.recoveryFactor }
            };
            call.Text(state);
        }

        private static IEnumerator GoToScene(ToolCall call)
        {
            GameScenes scene;
            if (!call.Choice("scene", null, Scenes, out scene))
            {
                yield break;
            }
            // Back at the main menu, KSP still holds the game it left, which a player cannot go back into from there.
            if (HighLogic.LoadedScene == GameScenes.MAINMENU)
            {
                call.Fail("The main menu has no game to leave: open one with open_game or new_game, which go to the " +
                    "space centre");
                yield break;
            }
            if (!Require.Game(call))
            {
                yield break;
            }
            // What the buttons above the altimeter do in flight, and what leaving any other scene does. Quit
            // to Main Menu, in flight, also forgets the vessels' persistent ids before saving (internal).
            if (scene == GameScenes.MAINMENU && HighLogic.LoadedSceneIsFlight)
            {
                MethodInfo clearIds = typeof(FlightGlobals).GetMethod("ClearpersistentIdDictionaries",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                if (clearIds == null)
                {
                    call.Fail("FlightGlobals.ClearpersistentIdDictionaries not found in this version of KSP");
                    yield break;
                }
                clearIds.Invoke(null, null);
            }
            SceneWatch watch = SceneWatch.Start(scene);
            GamePersistence.SaveGame("persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
            HighLogic.LoadScene(scene);
            // What the buttons of the flight results dialog do after theirs, the dialog being up after a crash.
            KSP.UI.Dialogs.FlightResultsDialog.Close();
            yield return Waits.ForScene(call, watch);
            if (!call.IsError)
            {
                call.Text(GameState.State());
            }
        }

        private static IEnumerator FlyVessel(ToolCall call)
        {
            if (!Require.Scene(call, GameScenes.TRACKSTATION, "Vessels are flown from the tracking station"))
            {
                yield break;
            }
            string error;
            Vessel vessel = Lookup.FindVessel(call.String("vessel"), out error, true);
            if (vessel == null)
            {
                call.Fail(error);
                yield break;
            }
            if (vessel.DiscoveryInfo.Level != DiscoveryLevels.Owned)
            {
                call.Fail(vessel.vesselName + " is not the player's: the tracking station does not fly it");
                yield break;
            }
            // What the Fly button does for a vessel of the player's.
            GamePersistence.SaveGame("persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
            FlightDriver.StartAndFocusVessel("persistent", FlightGlobals.Vessels.IndexOf(vessel));
            yield return Waits.ForNewActiveVessel(call, null);
        }
    }
}
