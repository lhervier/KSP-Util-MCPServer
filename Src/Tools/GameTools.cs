using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace com.github.lhervier.ksp.util.mcpserver
{
    /// <summary>Tools that open, start, load, save and quit a game.</summary>
    internal static class GameTools
    {
        private static readonly Dictionary<string, Game.Modes> Modes = new Dictionary<string, Game.Modes>
        {
            { "CAREER", Game.Modes.CAREER },
            { "SCIENCE_SANDBOX", Game.Modes.SCIENCE_SANDBOX },
            { "SANDBOX", Game.Modes.SANDBOX }
        };

        // What load_save waits for, by the name its argument gives: true when a vessel on rails will do.
        private static readonly Dictionary<string, bool> LoadWaits = new Dictionary<string, bool>
        {
            { "physics", false },
            { "loaded", true }
        };

        public static IEnumerable<Tool> All()
        {
            yield return new Tool("load_save",
                "Loads a save into flight, as the quickload does, from the space centre or from flight only (the " +
                "scenarios of the mods installed since are added), and waits until the active vessel is unpacked and " +
                "physics runs on it, or, with wait set to loaded, only until it is loaded, on rails or not: for a " +
                "vessel that something keeps on rails. Refused anywhere else, the main menu included: open the game " +
                "first with open_game, which goes to the space centre, as a player does.",
                Schema.Object(
                    Schema.P("folder", "string", "the game's folder under saves/ (default: the game being played)"),
                    Schema.P("save", "string", "the save's name, without .sfs", true),
                    Schema.P("wait", "string", "physics (the default): until physics runs on the active vessel; " +
                        "loaded: until it is loaded, on rails or not")),
                LoadSave);
            yield return new Tool("open_game",
                "Opens a game's persistent save, from any scene, the main menu included, as Resume Game does, and " +
                "always at the space centre, whatever scene the save names. Like the game, it writes the save back as " +
                "persistent, with the scenarios of the mods installed since. Waits until the space centre is up and " +
                "its buildings are set up.",
                Schema.Object(
                    Schema.P("folder", "string", "the game's folder under saves/", true)),
                OpenGame);
            yield return new Tool("new_game",
                "Starts a new game from the main menu, as New Game does with the Normal difficulty and the " +
                "default flag: in saves/<folder>, opening at the space centre. Waits until the space centre is " +
                "up. Refuses a folder that already holds a game. Some settings of the Custom difficulty can be " +
                "changed, as its sliders and switches do, within their range.",
                Schema.Object(
                    Schema.P("folder", "string", "the new game's folder under saves/, also its name", true),
                    Schema.P("mode", "string", "CAREER, SCIENCE_SANDBOX or SANDBOX (default CAREER)"),
                    Schema.P("starting_funds", "number",
                        "a career's starting funds, 0 to 500000 in steps of 1000 (default 25000, Normal's)"),
                    Schema.P("funds_penalties", "number",
                        "a career's funds penalties in percent, which the costs of building upgrades and repairs " +
                        "are multiplied by: 10 to 1000 in steps of 10 (default 100, Normal's)"),
                    Schema.P("starting_science", "number",
                        "the starting science of a career or a science game, 0 to 5000 in steps of 10 (default 0)"),
                    Schema.P("bypass_entry_purchase", "boolean",
                        "whether the parts of a technology are bought as soon as it is researched, as the switch " +
                        "Bypass Entry Purchase After Research does (default false, Normal's)"),
                    Schema.P("building_damage", "number",
                        "the building impact damage multiplier, which the damage a crash does to a building of " +
                        "the space centre is multiplied by: 0.01 to 1 (default 0.05, Normal's)")),
                NewGame);
            yield return new Tool("play_mission",
                "Starts a mission of Making History from the main menu, from its start, as Restart then Play of " +
                "Play Missions do: the game the mission saved is deleted, the mission's own game is set up, its launch sites placed, and the game opens in the scene the " +
                "mission starts in. Waits until that scene is up. Needs the expansion, and the main menu. The only " +
                "tool that can go from the main menu straight into flight, as the expansion does, without the space " +
                "centre: a mod may then behave otherwise than in a flight reached through it, as Parallax does with " +
                "the texture of the ground.",
                Schema.Object(
                    Schema.P("mission", "string",
                        "the mission's folder: absolute, or its name under the user's Missions folder or the " +
                        "expansion's stock missions", true)),
                PlayMission);
            yield return new Tool("save_game",
                "Saves the game as it is now, as a quicksave does, to saves/<folder>/<save>.sfs, overwriting it. " +
                "Saved from flight, the save opens in flight.",
                Schema.Object(
                    Schema.P("save", "string", "the save's name, without .sfs", true),
                    Schema.P("folder", "string", "the game's folder under saves/ (default: the game being played)")),
                SaveGame);
            yield return new Tool("quit_game",
                "Quits KSP, a second after answering, so that the answer gets back first.",
                Schema.Object(), QuitGame) { Concurrent = true };
        }

        /// <summary>The path of a save of the game in <paramref name="folder"/>, under the saves folder of KSP.</summary>
        public static string SavePath(string folder, string fileName)
        {
            return Path.Combine(Path.Combine(Path.Combine(KSPUtil.ApplicationRootPath, "saves"), folder), fileName);
        }

        private static IEnumerator LoadSave(ToolCall call)
        {
            // A flight reached straight from the main menu, the space centre never loaded since KSP started, is not
            // what a player gets: Parallax, for one, then paints the ground of Kerbin with the wrong texture.
            if (HighLogic.LoadedScene != GameScenes.SPACECENTER && !HighLogic.LoadedSceneIsFlight)
            {
                call.Fail("Saves are loaded from the space centre or in flight, as the quickload does, and the game is in " +
                    HighLogic.LoadedScene + (HighLogic.LoadedScene == GameScenes.MAINMENU
                        ? ": open the game first with open_game, which goes to the space centre"
                        : ": go to the space centre first with go_to_scene"));
                yield break;
            }
            string folder = call.String("folder", HighLogic.SaveFolder);
            string save = call.String("save");
            bool onRailsToo;
            if (!call.Choice("wait", "physics", LoadWaits, out onRailsToo))
            {
                yield break;
            }
            // LoadGame also clears the dictionaries of persistent ids, which are internal; the file is read again
            // for onGameStatePostLoad, which wants its node.
            Game game = GamePersistence.LoadGame(save, folder, true, false);
            ConfigNode node = GamePersistence.LoadSFSFile(save, folder);
            if (game == null || game.flightState == null)
            {
                call.Fail("Could not load saves/" + folder + "/" + save + ".sfs");
                yield break;
            }

            // What the quickload does (QuickSaveLoad.onQuickloadPipelineFinished): without UpdateScenarioModules,
            // the scenario of a mod the save does not hold yet (Principia's, added to all games) never runs.
            Vessel previous = FlightGlobals.ActiveVessel;
            GamePersistence.UpdateScenarioModules(game);
            HighLogic.SaveFolder = folder;
            HighLogic.CurrentGame = game;
            GameEvents.onGameStatePostLoad.Fire(node);
            FlightDriver.StartAndFocusVessel(game, game.flightState.activeVesselIdx);
            yield return Waits.ForNewActiveVessel(call, previous, null, onRailsToo);
        }

        private static IEnumerator OpenGame(ToolCall call)
        {
            string folder = call.String("folder");
            // Read as GamePersistence.LoadGame reads a save.
            ConfigNode node = GamePersistence.LoadSFSFile("persistent", folder);
            Game game = node != null ? GamePersistence.LoadGameCfg(node, "persistent", true, false) : null;
            if (game == null)
            {
                call.Fail("Could not open saves/" + folder + "/persistent.sfs");
                yield break;
            }

            // What the main menu's Resume Game does. KSP writes every persistent save to open at the space centre;
            // one copied from a save made in flight would open in flight, which no player gets, hence the scene
            // forced. The game is saved back as persistent once the scenarios of the installed mods are added: the
            // space centre reads persistent from disk (SpaceCenterMain.Start), and would open the game without them.
            game.startScene = GameScenes.SPACECENTER;
            GamePersistence.UpdateScenarioModules(game);
            GamePersistence.SaveGame(game, "persistent", folder, SaveMode.OVERWRITE);
            HighLogic.CurrentGame = game;
            HighLogic.SaveFolder = folder;
            GameEvents.onGameStatePostLoad.Fire(node);

            // Watched from before the start: the space centre may be the scene being left, so the scene loaded now
            // does not tell.
            SceneWatch watch = SceneWatch.Start(GameScenes.SPACECENTER);
            game.Start();
            yield return Waits.ForScene(call, watch);
            if (call.IsError)
            {
                yield break;
            }
            call.Text(GameState.State());
        }

        private static IEnumerator NewGame(ToolCall call)
        {
            if (!Require.Scene(call, GameScenes.MAINMENU, "New games start from the main menu"))
            {
                yield break;
            }
            string folder = call.String("folder");
            Game.Modes mode;
            if (!call.Choice("mode", "CAREER", Modes, out mode))
            {
                yield break;
            }
            if (File.Exists(SavePath(folder, "persistent.sfs")))
            {
                call.Fail("saves/" + folder + " already holds a game");
                yield break;
            }

            // What the main menu does once New Game is confirmed: the flag it proposes, the parameters of the
            // difficulty it selects first.
            MainMenu menu = UnityEngine.Object.FindObjectOfType<MainMenu>();
            string flag = menu != null && !string.IsNullOrEmpty(menu.DefaultFlagURL) ? menu.DefaultFlagURL : "Squad/Flags/default";
            GameParameters parameters = GameParameters.GetDefaultParameters(mode, GameParameters.Preset.Normal);
            if (!CustomDifficulty(call, mode, parameters))
            {
                yield break;
            }

            SceneWatch watch = SceneWatch.Start(GameScenes.SPACECENTER);
            HighLogic.CurrentGame = GamePersistence.CreateNewGame(folder, mode, parameters, flag,
                GameScenes.SPACECENTER, EditorFacility.None);
            GameEvents.onGameNewStart.Fire();
            HighLogic.CurrentGame.Start();
            yield return Waits.ForScene(call, watch);
            if (!call.IsError)
            {
                call.Text(GameState.State());
            }
        }

        /// <summary>
        /// Writes into <paramref name="parameters"/> the settings of the Custom difficulty the call gives, as its
        /// sliders and switches write them; returns false after failing the call when one is out of its range or
        /// steps, or means nothing in that mode of game.
        /// </summary>
        private static bool CustomDifficulty(ToolCall call, Game.Modes mode, GameParameters parameters)
        {
            if (call.Has("building_damage"))
            {
                double damage = call.Number("building_damage");
                if (!(damage >= 0.01 && damage <= 1.0))
                {
                    call.Fail("building_damage: 0.01 to 1");
                    return false;
                }
                parameters.preset = GameParameters.Preset.Custom;
                parameters.CustomParams<GameParameters.AdvancedParams>().BuildingImpactDamageMult = (float)damage;
            }
            if (call.Has("starting_science") || call.Has("bypass_entry_purchase"))
            {
                if (mode == Game.Modes.SANDBOX)
                {
                    call.Fail("A sandbox has neither science nor parts to buy");
                    return false;
                }
                double science = call.Number("starting_science", parameters.Career.StartingScience);
                if (!InSteps(science, 0.0, 5000.0, 10.0))
                {
                    call.Fail("starting_science: 0 to 5000, in steps of 10");
                    return false;
                }
                parameters.preset = GameParameters.Preset.Custom;
                parameters.Career.StartingScience = (float)science;
                parameters.Difficulty.BypassEntryPurchaseAfterResearch = call.Bool("bypass_entry_purchase",
                    parameters.Difficulty.BypassEntryPurchaseAfterResearch);
            }
            if (call.Has("starting_funds") || call.Has("funds_penalties"))
            {
                if (mode != Game.Modes.CAREER)
                {
                    call.Fail("Only a career has funds");
                    return false;
                }
                double funds = call.Number("starting_funds", parameters.Career.StartingFunds);
                double penalties = call.Number("funds_penalties", parameters.Career.FundsLossMultiplier * 100.0);
                if (!InSteps(funds, 0.0, 500000.0, 1000.0))
                {
                    call.Fail("starting_funds: 0 to 500000, in steps of 1000");
                    return false;
                }
                if (!InSteps(penalties, 10.0, 1000.0, 10.0))
                {
                    call.Fail("funds_penalties: 10 to 1000, in steps of 10");
                    return false;
                }
                parameters.preset = GameParameters.Preset.Custom;
                parameters.Career.StartingFunds = (float)funds;
                parameters.Career.FundsLossMultiplier = (float)(penalties / 100.0);
            }
            return true;
        }

        /// <summary>Whether a value lies between two bounds and is a whole number of steps.</summary>
        private static bool InSteps(double value, double min, double max, double step)
        {
            return value >= min && value <= max && Math.Abs(value / step - Math.Round(value / step)) <= 1e-9;
        }

        private static IEnumerator PlayMission(ToolCall call)
        {
            if (!Expansions.ExpansionsLoader.IsExpansionInstalled("MakingHistory"))
            {
                call.Fail("Making History is not installed");
                yield break;
            }
            if (HighLogic.LoadedScene != GameScenes.MAINMENU || Expansions.Missions.Runtime.MissionSystem.Instance == null)
            {
                call.Fail("Missions start from the main menu, and the game is in " + HighLogic.LoadedScene);
                yield break;
            }
            string file = MissionFile(call.String("mission"));
            if (file == null)
            {
                call.Fail("No mission " + call.String("mission") + " (persistent.mission not found)");
                yield break;
            }
            Expansions.Missions.MissionFileInfo info = Expansions.Missions.MissionFileInfo.CreateFromPath(file);

            // What Restart does (MissionPlayDialog.ConfirmRestart): the files of the game the mission saved are
            // deleted, its Ships folder kept. Without that, the mission resumes from that game.
            if (Directory.Exists(info.SaveFolderPath))
            {
                foreach (FileInfo saved in new DirectoryInfo(info.SaveFolderPath).GetFiles())
                {
                    saved.Delete();
                }
            }

            // What Play Missions does once a mission is picked: clear what an earlier mission left, set the
            // mission's game up, then start that game and clear the objects the setup used.
            MethodInfo removeObjects = typeof(Expansions.Missions.Runtime.MissionSystem).GetMethod("RemoveMissionObjects",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (removeObjects == null)
            {
                call.Fail("MissionSystem.RemoveMissionObjects not found in this version of KSP");
                yield break;
            }
            removeObjects.Invoke(null, new object[] { true });
            MissionStart start = new MissionStart();
            Expansions.Missions.Runtime.MissionSystem.Instance.StartCoroutine(
                Expansions.Missions.Runtime.MissionSystem.Instance.SetupMissionGame(info, true, false,
                    () => start.Succeeded = true, () => start.Failed = true));
            yield return Waits.Until(call, () => start.Succeeded || start.Failed, Waits.SceneSeconds,
                "The mission was not set up after 3 minutes");
            if (call.IsError)
            {
                yield break;
            }
            if (start.Failed || HighLogic.CurrentGame == null)
            {
                removeObjects.Invoke(null, new object[] { false });
                call.Fail("KSP could not set the mission up: see KSP.log");
                yield break;
            }

            Game game = HighLogic.CurrentGame;
            Vessel previous = FlightGlobals.ActiveVessel;
            if (game.startScene == GameScenes.FLIGHT)
            {
                game.Start();
                removeObjects.Invoke(null, new object[] { false });
                yield return Waits.ForNewActiveVessel(call, previous);
                yield break;
            }
            SceneWatch watch = SceneWatch.Start(game.startScene);
            game.Start();
            removeObjects.Invoke(null, new object[] { false });
            yield return Waits.ForScene(call, watch);
            if (!call.IsError)
            {
                call.Text(GameState.State());
            }
        }

        /// <summary>
        /// The <c>persistent.mission</c> file of a mission given by its folder: absolute, or a folder name
        /// looked up among the user's missions, then the expansion's stock missions. Null if there is none.
        /// </summary>
        private static string MissionFile(string mission)
        {
            List<string> folders = new List<string>();
            if (Path.IsPathRooted(mission))
            {
                folders.Add(mission);
            }
            else
            {
                folders.Add(Path.Combine(Expansions.Missions.MissionsUtils.UsersMissionsPath, mission));
                folders.Add(Path.Combine(Expansions.Missions.MissionsUtils.StockMissionsPath, mission));
            }
            foreach (string folder in folders)
            {
                string file = Path.GetFullPath(Path.Combine(folder, "persistent.mission"));
                if (File.Exists(file))
                {
                    return file;
                }
            }
            return null;
        }

        /// <summary>How the setup of a mission ended, filled by its callbacks.</summary>
        private sealed class MissionStart
        {
            public bool Succeeded;
            public bool Failed;
        }

        private static IEnumerator SaveGame(ToolCall call)
        {
            if (!Require.Game(call))
            {
                yield break;
            }
            string save = call.String("save");
            string folder = call.String("folder", HighLogic.SaveFolder);

            // What a quicksave does: the game brought up to date, set to open in the scene it was saved from.
            Game game = HighLogic.CurrentGame.Updated();
            if (HighLogic.LoadedSceneIsFlight)
            {
                game.startScene = GameScenes.FLIGHT;
            }
            GamePersistence.SaveGame(game, save, folder, SaveMode.OVERWRITE);
            string path = SavePath(folder, save + ".sfs");
            if (!File.Exists(path))
            {
                call.Fail("Could not save " + path);
                yield break;
            }
            call.Text("Saved " + Path.GetFullPath(path));
        }

        private static IEnumerator QuitGame(ToolCall call)
        {
            // The answer is only sent once this tool ends: quitting happens later, on its own.
            MainThread.Host.StartCoroutine(QuitSoon());
            call.Text("quitting");
            yield break;
        }

        private static IEnumerator QuitSoon()
        {
            yield return new WaitForSecondsRealtime(1f);
            Log.Info("Quitting KSP, as asked");
            Application.Quit();
        }
    }
}
