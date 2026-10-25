# KSP-Util-MCPServer

**⚠️ Work in progress.** The tools, their names and their arguments can still change.

A mod for KSP 1.12 that lets a program drive the game: read where the active vessel is, load a save,
drive a rover to a point, take a screenshot, press a button of another mod. It answers requests sent over
HTTP, on the computer KSP runs on only, in the format of the
[Model Context Protocol](https://modelcontextprotocol.io) (MCP), so that an AI assistant can use it as a
set of tools — but a ten-line Python script, or `curl`, can use it just as well, and no AI is needed.

It was written to play measuring protocols that are too tedious, or too imprecise, to play by hand: one of
them asks to park a rover on the same two spots again and again, before and after the game moves its
world, and this mod parks it there to within a centimetre
([Terrain Precision Fix](https://github.com/lhervier/KSP-Fix-TerrainPrecision/blob/main/docs/statics/checking/driving-protocol.md)).

**How this was made.** Written with Claude, Anthropic's AI assistant, and reviewed by a human — me. I am
saying so up front, because contributions made with an AI deserve a closer look than others, and because
some people would rather stop reading here. What there is to check here is small: the source is thirty
short files, it opens no port beyond the loopback address, and it changes nothing in the game until a
request asks it to.

## What it offers

Every tool runs on the game's main thread and answers once it is done: `drive_to` answers when the rover
is parked, `load_save` when the vessel is in flight and physics runs on it. While it runs, a short message
at the top of the screen names it, with its arguments, so that whoever watches the game sees what drives
it: one message at a time, each tool's replacing the last, and never on a screenshot.

The tools that act on the game run one at a time: while one runs, another is refused at once, with the
name of the one running. The tools that only read the game, and a few whose action gets in no one's way —
`get_state`, `get_floating_origin`, `list_vessels`, `list_launch_sites`, `get_terrain`, `list_rocs`, `list_parts`, `read_part_menus`, `wait`, `screenshot`, `set_camera`, `aim_camera`,
`set_ui`, `set_pause`, `set_cheats`, `stop_drive`, `quit_game`, `get_member`, `set_member`, `call_method`
and the tools of other mods — run at any time: `get_state` answers while `drive_to` drives. A tool is
stopped when its client cancels the call (`notifications/cancelled` of MCP), or after ten minutes; it
then lets go of what it holds, the wheels of a rover say.

A request whose body or answer takes over 30 seconds to go through has its connection dropped, so that a
client gone silent cannot hold the server. The server also asks itself, every 15 seconds, whether it still
answers: after two checks in a row without an answer, it closes its port and opens it again, and says so in
`KSP.log`. Turned off from the toolbar, it gives its port back within 5 seconds, whatever its requests do.

Flight is reached as a player reaches it: through the space centre. From the main menu, `open_game` and
`new_game` go to the space centre; from there or one of its editors, `launch_vessel` launches a vessel and `load_save` loads a save,
which it also reloads in flight. A flight reached straight from the main menu, the space centre never loaded
since KSP started, is not one a player gets, and a mod may behave otherwise in it: Parallax Continued 1.0.4
paints the ground of Kerbin with the wrong texture until the space centre has been loaded once. Only
`play_mission` still goes from the main menu into flight, as Making History does.

| tool | what it does |
|---|---|
| `get_state` | the scene, the pause, the time, the time warp rate, and the active vessel: id, name, situation, latitude, longitude, altitude, height above the terrain, speed, heading, pitch, brakes, lights, gear, SAS, the local time where it is and the length of its body's day, its CommNet link (connected, signal strength, each hop of its control path with its length); in flight, whether the next alarm shows above the time display |
| `get_floating_origin` | the floating origin KSP keeps the world centred on: the vessel's distance from it, where it lies from the vessel (north, east, up), the distance at which KSP moves it, the moves since the scene opened |
| `open_game` | opens a game's `persistent` save from any scene, the main menu included, as *Resume Game* does: always at the space centre, written back as `persistent`; returns once the buildings are set up |
| `new_game` | starts a new career, science or sandbox game from the main menu, as *New Game* does at the Normal difficulty; a career's starting funds and funds penalties can be changed as the sliders of the Custom difficulty do |
| `play_mission` | starts a mission of Making History from the main menu, from its start, as *Restart* then *Play* of *Play Missions* do: in the scene the mission starts in, flight included, the one way into flight that skips the space centre |
| `load_save` | loads a save into flight, as the quickload does, from the space centre or from flight only |
| `save_game` | saves the game as it is now under a name, as a quicksave does |
| `launch_vessel` | launches a vessel from its `.craft` file at a launch site, from the space centre or one of its editors only, with the crew the editor would give it or the kerbals named, their inventories emptied or not, as the editor's Launch button does; refuses a launch site the game does not offer, and a craft with parts not yet unlocked, or too heavy, too large or of too many parts for the level of its site and its editor |
| `list_launch_sites` | the launch sites a vessel can be launched at, as the editors' launch site selector offers them: the name `launch_vessel` takes, the name shown, the editor, the body, what places the site (the space centre, a `PQSCity` or a `PQSCity2`), and where its first spawn point lies |
| `list_vessels` | the vessels of the game: id, name, situation, loaded or not, packed or not, active or target, distance from the active vessel |
| `switch_vessel` | makes a vessel the active one: a loaded one as the switch vessel keys `[` and `]` do, one far away as *Switch To* of the map view does |
| `set_target` | sets the active vessel's target to another vessel, or clears it; refuses while targeting is locked, as it is in EVA construction mode, but not because the window of KSP is out of focus |
| `go_eva` | sends a crew member of the active vessel out on EVA, as the EVA button of the crew portrait does, and answers once the kerbal is the active vessel |
| `screenshot` | captures the screen, interface included but not the message of this mod; returns the image and saves it as a PNG |
| `set_camera` | the flight camera's distance, heading and pitch, its field of view, and where it aims, as the middle mouse button does |
| `set_ui` | hides or shows the game's interface in flight, as F2 does |
| `map_view` | enters or leaves the map view of a flight, as the M key does, and answers once the game shows it; refuses when the game would ignore the key |
| `iva_view` | enters the IVA view of the active vessel or leaves it for the flight view, as the C key does, and answers once the game shows it; refuses when the game would ignore the key, and when the vessel has no crew to look through |
| `mouse_move` | moves the mouse pointer to a point of the game's window, as a mouse does, in any scene, with or without the focus, the Windows session locked or not; answers with the pointer as Unity reads it and the interface objects under it, each with the object a click on it reaches |
| `mouse_click` | clicks the left or right button at a point of the game's window: moves the pointer there, presses and releases the button, and answers once the game has seen both, as `mouse_move` answers; or double-clicks, two clicks in a row as a player's |
| `read_tracking_station` | the tracking station as the player sees it: each row of its list from the top, with its vessel, whether it looks selected, where it lies on screen, and whether it lies within the visible part of the scrolled list; when another mod hides the station's list and shows its own rows instead, those rows (toggles of a toggle group labelled with a tracked vessel's name), `listReplaced`, and the station's own rows, hidden, with whether each is lit; the vessel selected; whether *Fly*, *Recover*, *Terminate* and *Track* show and respond, and where; points as `mouse_click` takes them |
| `read_dialogs` | the dialogs open over the scene: name, title, message, and their buttons with their label, whether each responds, and the middle of each on screen, as `mouse_click` takes it |
| `press_dialog_button` | presses a button of a dialog open over the scene, by its label, as a click does, in the dialog named or in any |
| `set_time` | sets the universal time of the game: the time of day at a spot |
| `set_warp` | sets the time warp, on rails or in physics warp, as the `.` and `,` keys do, with Alt or without |
| `warp_to` | warps to a universal time, as *Warp To* of a manoeuvre node or of the map view does |
| `warp_to_morning` | warps to the next morning where the active vessel is, as the button of the time warp gauge does, or to another time of the local solar day |
| `set_pause` | pauses or resumes the flight |
| `wait` | lets the game run for a number of seconds |
| `set_cheats` | turns the cheats of the Cheats tab of the `Alt+F12` menu on or off: *Unbreakable Joints*, *No Crash Damage*, *Ignore Max Temperature*, *Infinite Propellant*, *Infinite Electricity* and the others |
| `set_position` | moves the active vessel to a point of a body, as *Set Position* of the `Alt+F12` menu does, in the attitude it had against its local horizon (or turned to a heading): holds it still a few metres up until the ground and what stands on it are built under it, lowers it to a given height above whatever lies under it, lets it go, and answers once it has settled; refuses a point where it would reach another vessel |
| `get_terrain` | the terrain of a body at a point: its height, whether the sea covers it, its slope and roughness |
| `list_rocs` | the surface features of Breaking Ground near the active vessel: id, type, distance, where their pivot lies and its height above the terrain, their colliders, whether the vessel touches them |
| `set_orbit` | puts the active vessel on an orbit, as *Set Orbit* of the `Alt+F12` menu does |
| `revert_to_launch` | reverts the flight to its launch, as *Revert to Launch* does |
| `revert_to_editor` | reverts the flight to the Vehicle Assembly Building or the Space Plane Hangar, as *Revert to* does |
| `recover_vessel` | recovers the active vessel, as the *Recover* button does, and waits for the recovery report |
| `go_to_scene` | leaves for the space centre, the tracking station or the main menu, saving the game first as the game's own buttons do; not from the main menu |
| `fly_vessel` | flies a vessel of the player's from the tracking station, as its *Fly* button does |
| `open_facility` | clicks a building of the space centre: its editor, its scene or its screen opens |
| `facility_menu` | opens a building's menu at the space centre, as a right click does, and reads it: level, damage, costs, funds; presses its *Upgrade* or *Repair* button and waits until KSP is done |
| `close_screen` | closes what is open over the scene, as its own button does: a dialog, the flight results after a crash, a building's menu, the recovery report (at the space centre or in the tracking station), a building's screen |
| `quit_game` | quits KSP, a second after answering |
| `set_controls` | holds the wheel throttle and steering, and sets the brakes |
| `set_flight` | sets the main throttle, turns SAS on or off and chooses its mode, as the pilot's keys do |
| `stage` | activates the next stage, as the space bar does |
| `list_parts` | the parts of the active vessel, or of another loaded vessel (on EVA, the one being built): the id the part tools take, name, title, resources, whether its part action window is open and pinned, and for a wheel whether it touches the ground |
| `part_menu` | opens a part's action window as a right click on it does (the windows not pinned close), pins or unpins it, drags it to a point of the screen by its title bar, or closes every window not pinned as a right click on empty space does |
| `read_part_menus` | the open part action windows, where they are on screen and what they show: buttons, fields, resources, and the resource transfer controls with their buttons, state and links to the other parts' |
| `press_part_button` | presses a button of a part's action window, by its label or the name of its event (the same in every language of the game), or the In, Out or Stop button of a resource transfer |
| `locate_part` | where a part is on screen: the rectangle its colliders cover, how much of it shows, and a point where the mouse is over it, found by casting rays as the game does to find the part under the mouse; its colliders and how many are triggers, and how far it leans from the vertical; with `find_view`, first turns the camera to where the most of it shows |
| `aim_camera` | aims the camera at a part, as the *Aim Camera* button of its action window does, or back at the active vessel, as *Reset Camera* does |
| `construction_tool` | opens or closes the construction panel of EVA construction, as its toolbar button does, and picks its Place, Move or Rotate tool, as its buttons do; answers with the tool in use and the part selected |
| `construction_gizmo` | drags the gizmo of the part selected in EVA construction, as a player drags one of its handles: along an axis of the Move gizmo by some metres, or around an axis of the Rotate gizmo by some degrees, over a number of frames, with the game's snapping; answers with how far the part really went |
| `drive` | drives a rover along a heading, forward or backing away with its front facing it, at a speed, for a distance or until the floating origin moves; slows down before the end and stops with the brakes on |
| `drive_to` | drives a rover to a latitude and longitude, forward or in reverse, along the straight line from where it starts, and stops there with the brakes on, still; stops and turns round when the target falls behind |

Both drives slow down to take a sharp turn and never turn harder than keeps the rover on its wheels. How
sharply a rover turns at each speed they learn as they drive, and remember for that vessel until the game
is closed.
| `stop_drive` | stops a `drive` or a `drive_to` still running, or the wheels `set_controls` holds, and brakes |

A drive stops, the rover braked, when it tips over, when it runs away at twice the speed asked, when the
active vessel changes or when the flight ends.
| `get_member`, `set_member` | reads or writes a field or property of a loaded object of any mod — a window's position, say; `get_member` also follows a path of fields, list indexes and components |
| `call_method` | calls a method of a loaded object of any mod, or a static one, with or without arguments |

These three take the first loaded object of the type, or with `match` (`path=value`) the first one whose path reads that value.

`mouse_move` and `mouse_click` take `x` and `y` in pixels of the game's window, counted from the top left
corner of its client area (frame and title bar left out): `x` to the right, `y` down. Unity's
`Input.mousePosition` counts `y` up from the bottom edge instead: a point at `x`, `y` reads `x`,
`height - 1 - y` there, and both tools give it back as `mousePosition`, with the window's size. A point out of
the window is refused. Their answer names the object the interface has the pointer over (`pointerEnter`) and
lists the objects under it, the topmost first, each with its path and the object a click on it reaches
(`clickHandler`): in the map view, `mouse_move` to the top edge, above the arrow that unfolds the vessel
filters, tells which object a click there would reach. Windows only. They work as a real mouse does: the game
reads them through its own input, without the focus, and also while the Windows session is locked, when
no real mouse reaches it. The cursor of Windows moves with them, since the game reads its position from it.
Once the game's window has lost the focus, Unity's event system ignores the pointer (`EventSystem.isFocused`):
both tools tell it the window has the focus again, as a click on it would, and it stays so until the window
loses the focus once more. `mouse_click` also lifts, for the click only, the lock the game puts on every
control while its window is out of focus (`flightDriver_ApplicationFocus`), as `press_part_button` does: the
EVA construction tools, among others, ignore a click while it holds.

Other mods can add their own tools, without depending on this one: see
[Adding tools from another mod](#adding-tools-from-another-mod).

## Install

Copy `GameData/KSPUtilMCPServer/` into the `GameData` folder of KSP. When KSP starts, its log reads
`[KSPUtilMCPServer] Listening on http://127.0.0.1:8770/mcp/`.

Its settings are in `GameData/KSPUtilMCPServer/PluginData/settings.cfg`, read when KSP starts:

| setting | default | what it does |
|---|---|---|
| `port` | `8770` | the TCP port it listens on |
| `screen_messages` | `true` | whether each tool shows its name at the top of the screen while it runs; `false` turns the messages off |
| `listen_at_start` | `true` | whether it listens as soon as KSP starts; `false` keeps its port closed until it is turned on |

It listens on `127.0.0.1` only: nothing outside the computer KSP runs on can reach it.

**Turning it on and off.** Its button in the toolbar of KSP turns it on and off, in every scene that has
the toolbar (not the main menu): a green dot while it listens, a struck-out grey ring while it does not. A
message at the top of the screen tells which. Turned off, it closes its port and stops the tools still
running, so that nothing drives the game any more. With `listen_at_start = false`, nothing can reach the game
until the player turns the server on: in a game played for real, whoever plays decides when an assistant may
drive it, for instance once the game is saved.

## Using it

**With an AI assistant.** Register it as an MCP server over HTTP, KSP running. With Claude Code:

```
claude mcp add --transport http ksp http://127.0.0.1:8770/mcp/
```

The server stops with KSP. An assistant connected over HTTP tries to reconnect for a few seconds only, which is
less than KSP takes to start again: after each restart of KSP, the connection has to be made again by hand
(in Claude Code, `/mcp`).

**Through the stdio bridge**, the connection survives the restarts of KSP.
[`bridge/stdio-bridge.py`](bridge/stdio-bridge.py), a Python script with no dependency outside its folder, is
started by the assistant and passes each request on to the server. While KSP is not running, the tools of
the game stay listed (the last ones seen) and calling one answers that KSP is not running; when KSP is back,
or comes back with other mods, the assistant gets the new list. It adds three tools of its own, which work without KSP:

| tool | what it does |
|---|---|
| `ksp_status` | whether KSP is running, whether its server answers, and the scene of the game |
| `ksp_start` | starts KSP from one of the folders given with `--ksp`, and waits for the main menu; `keep_log` copies the previous `KSP.log` first, as KSP overwrites it when it starts; on Windows, `window_x` moves the window of KSP sideways as soon as it shows (0 sticks it to the left of the screen), as KSP always opens it in the middle |
| `ksp_quit` | quits KSP, waits until it has ended, and can copy its `KSP.log` |

`ksp_start` starts only the folders given with `--ksp`, each under a name of your choice. With Claude Code:

```
claude mcp add ksp -- python <path>/bridge/stdio-bridge.py --ksp dev=<path of a KSP folder>
```

Without `--ksp`, the folder of the environment variable `KSPDIR`, when it is set, is the one folder
`ksp_start` may start, under the name `KSPDIR`.

The bridge is a convenience for exploring the game with an assistant: a script that plays a protocol should
post to the server itself, as below, and not need it.

**With anything else.** Each tool is a JSON-RPC call, posted to the same address:

```
curl -X POST http://127.0.0.1:8770/mcp/ -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"get_state","arguments":{}}}'
```

`{"method":"tools/list"}` lists the tools with the schema of their arguments. A complete example, a
Python script with no dependency that plays a whole protocol, is
[`run-driving-runway.py`](https://github.com/lhervier/KSP-Fix-TerrainPrecision/blob/main/diag/statics/automation/run-driving-runway.py)
in Terrain Precision Fix.

## Adding tools from another mod

A mod references `KSPUtilMCPServer.dll` (without copying it: `<Private>false</Private>`) and puts
`com.github.lhervier.ksp.util.mcpserver.McpToolAttribute` on the methods it offers. This server looks for that
attribute when KSP starts, in every loaded assembly, and publishes each method as a tool. Nothing reads
the attribute when this server is not installed, so the mod runs the same without it: the reference is
only needed to compile.

```csharp
using com.github.lhervier.ksp.util.mcpserver;

internal static class McpTools
{
    [McpTool("my_mod_record", "Freezes the reading in progress into the table, as the Record button does.")]
    internal static object Record() { return Object.FindObjectOfType<MyMod>().Record(); }
}
```

The attribute is recognised by its name, `McpToolAttribute`: a mod that would rather not reference this
one at all can declare its own attribute class of that name, with `Name` and `Description` properties.

- The method may be static or not, public or not. An instance method is called on the loaded object of
  its type, which has to be a Unity object — typically the mod's `MonoBehaviour`.
- Its parameters become the tool's arguments, by name: numbers, strings, booleans.
- What it returns is written as JSON: numbers, strings, booleans, lists, dictionaries, and the public
  fields of any other object. A `void` method answers `done`.
- It may be called while another tool runs, a `drive_to` say: it should do its work at once, as a button
  of a window does.

## Performance

Timed frame by frame in flight with [KSPProfiler](https://github.com/KSPModdingLibs/KSPProfiler), in low
orbit over the Mun. Installed, and called by nobody, this mod costs a frame nothing measurable: three runs
played by hand with it and three without it overlap on every row. Played by script through it, a run costs
at most about 0.05 ms more per frame, in the coroutines the terrain is updated in, than one played by hand
without it.

**→ Full chapter: [Performance](docs/performance.md)**

## Build

Clone this repository, set `KSPDIR` to your KSP install folder and run `build.bat`. It needs the .NET
SDK, reads the KSP assemblies from your install, and puts the DLL in `GameData/KSPUtilMCPServer/` inside the
repository. It does not install anything.

## License

MIT — see [LICENSE](LICENSE).
