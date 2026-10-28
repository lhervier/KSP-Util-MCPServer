# TODO

What is left to do on KSP-Util-MCPServer, most urgent first.

- **Check `drive_to` on a tall rover over rough ground**, on the Earth of Real Solar System near Kourou,
  where a rover of 18 parts rolled over three times out of four drives (6 m/s once, 3 m/s twice). The control
  point of that rover faces up: the drives read its speed and heading along an axis that stood upright,
  so they never cut the throttle (seen on Kerbin: 28 m/s for 4 asked). They now drive along the axis the
  wheels drive along, choose forward or reverse again when the target falls behind, and stop when the
  rover tips over or runs away; on Kerbin, that rover now reaches its target at 4, 6 and 10 m/s. Not yet
  driven near Kourou. Since then, the steering learns how sharply the rover turns, slows down for a sharp
  turn and never asks for more than a quarter of the local gravity sideways: on Kerbin, the small
  Diag2-Rover, which rolled over turning 90° at 15 m/s, now takes that turn on its wheels. To try with the
  rover of 18 parts, whose centre of mass stands higher.
- **`set_position` under the colliders of a static**: on the runway and on the grass near the space centre, a
  vessel is lowered onto the terrain (64.8 m) beneath an invisible collider of the space centre's statics (about
  69.2 m), since it is held a few metres above the terrain, below that collider; when the save is loaded again, KSP
  raises it by 4.4 m. Hold the vessel above whatever lies highest under it, statics included. Also: a kerbal moved
  right after leaving the hatch, still on the ladder, was destroyed while held ("destroyed while it was held above
  the ground"); a few seconds later, the same move works.
- **`set_position` over a long jump, in Real Solar System and with a large rover**: the vessel crashed
  through the terrain after a long jump, on Kerbin and near Kourou in Real Solar System. Two causes, both
  fixed: the ground of the arrival had no collider yet when physics resumed, and the vessel was let go
  with the speed of the turning ground (54 m/s at 72° of latitude on Kerbin, 462 m/s at the equator of the
  Earth), where the scene turns with the body and the ground stands still in it. `set_position` now holds
  the vessel still until the quad under it is of the finest level and what lies under it reads the same for
  a second, then lowers it to a few centimetres and lets it go; a capsule on a tank jumped from the KSC to
  72° N unharmed. Still to try: near Kourou in Real Solar System, and the rover of 18 parts that crashed
  twice at the Desert Airfield.
- **`set_position` and the other kerbal on EVA**: a kerbal on EVA left alone came off rails, after the move,
  where its old place in the scene lies, next to the kerbal moved; `set_position` now puts the other loaded
  vessels back where they stood. The cause in the game is not found: find it, so the put-back can go. Once,
  right after KSP started, the tool never answered that the kerbal moved had settled (60 s), though it stood
  still: not seen again.
- **EVA and EVA construction tools**, so that a protocol that places parts no longer needs a player's
  hands (anchors on a static, for Terrain Precision Fix): `eva` (a kerbal out of a crewed part, by
  `FlightEVA.spawnEVA`: public, simple); placing a part from a kerbal's inventory on the ground (the
  editor of EVA construction drops it through private methods driven by the mouse,
  `EVAConstructionModeEditor.DropAttachablePart` among them: the state the mouse sets up has to be set up
  by hand); taking a part off a vessel and attaching it to another (`PickupPart`, `CheckAttach`, which casts
  a ray under the cursor, `AttachPart`: the hardest, not sure to work cleanly).
- **`launch_vessel` with a site mover**: with KSCSwitcher sending the KSC to Kourou
  in Real Solar System, a craft launched by `launch_vessel` from the space centre appeared at Kourou with
  no building of the KSC around it; launched from the VAB by hand, the KSC stood there. KSCSwitcher moves
  the KSC only when a game is created at the space centre. Before its launch, the editor also writes the
  launch site it chose as the game's default (`EditorDriver.saveselectedLaunchSite`), which the launch
  dialog of the space centre does not. Not seen since: to play again with KSCSwitcher.

- **Check the stall recovery of `drive_to`** on the lip of the runway deck: approaching a spot on the
  deck from the grass, the rover used to creep at its lowest speed against the lip until the 300 s limit.
  It now raises that speed after three seconds without progress; not yet seen on that lip.
- **Keep what the steering learns about a rover in a module of the vessel**: at each `drive` or
  `drive_to`, `RoverSteering` learns how sharply the rover turns per unit of steering, for each speed band
  and direction, and `SpeedHold` learns the throttle that holds a speed. The turn model lives in a static
  dictionary keyed by `persistentId` and is lost when the game closes, so every session starts again,
  steering too little at first. Save it with the vessel instead, in a `VesselModule`. Mind docking: a
  rover docked to something else (a truck taking a load, a crane gripping a part) no longer turns the same
  way, and the merged vessel keeps the identity of one of the two, so what was learned before turns
  wrong; same for a lost wheel. To decide: forget the model whenever the vessel's parts change
  (`onVesselWasModified`, `onPartCouple`, `onPartUndock`), keep one per configuration, or let the
  forgetting of the least squares correct it from a cautious start. Not checked: what happens to a
  `VesselModule` and its saved fields on docking and undocking.
- **Publish**: create the GitHub repository (`lhervier/KSP-Util-MCPServer`, the address the README and the mods that
  offer it tools link to), and a first release with the DLL.
- **Make the server work on Linux**: never run there yet. What is known to depend on Windows: `mouse_click`
  and `mouse_move` post their messages to the game's window through `user32.dll` (`MouseTools`), which does
  not exist on Linux; the bridge only places the window of KSP (`window_x`) on Windows (`kspwindow.py`), and
  only finds a running KSP by `pgrep` elsewhere; the build is a `build.bat` that reads the assemblies of
  the game from `KSP_x64_Data`, where the Linux build of KSP has `KSP_Data`. To do: a way to click without
  `user32` (through Unity's event system, or X11), placing the window under X11 or Wayland, a build script
  for Linux, then a full session on a Linux install of KSP to find what else breaks.

- **Let the agent drive a rover itself over rough ground** (Eve, the Mun with Parallax's rocks), choosing its
  own path to a given spot, instead of `drive_to`'s straight line. Today `drive` and `drive_to` hold the call
  until the rover stops, and see nothing ahead. Wanted:
  - **Start and return at once**: a drive that starts and answers straight away, with an id; a call that changes
    its heading and speed while it runs; `stop_drive` as today.
  - **Follow the drive by polling** (simpler than server-sent events, whose notifications an MCP client does
    not necessarily hand to the model): each poll answers the rover's state as a navball would tell it
    (heading, pitch, roll, speed, vertical speed, wheels touching the ground, slip), the distance and bearing to
    the target, the drive's state (driving, arrived, stalled, tipped over, stopped), the shape of the ground
    ahead (a grid of heights and slopes in the rover's frame, a few tens of metres deep, from the collider
    rather than the computed height, which differ between vertices), and the obstacles ahead (position, size,
    height above the ground).
  - **Parallax's rocks cannot be found by their colliders**: Parallax only creates a rock's collider once a
    vessel is on it. `CollisionManager` activates the colliders within the vessel's own size, plus a lookahead
    along its velocity (`colliderLookaheadTime` × speed, at most 125 m), and that lookahead is 0 in the
    `ParallaxGlobalSettings.cfg` of 1.0.6. Seen on the Mun with Diag Colliders (2026-10-21): rocks all
    around a still rover, not one collider. So the way Diag Colliders finds obstacles only works for statics
    (`PQSCity`, `PQSCity2`); the rocks have to come from Parallax's own instance data, as Bulldozer's preview
    reads it (`ScatterHighlighter`): `CollisionManager.collisionData`, per loaded quad, the local position,
    rotation and scale of every collideable instance, posed as `quad.localToWorldMatrix × TRS(localPos,
    RotationFromUp(local normal) × AngleAxis(rotation, up), localScale)` since Parallax 1.0.5. Without a hard
    dependency on Parallax (the server must load without it): reflection, or a tool offered by a small
    companion mod built against Parallax, as the Diags offer theirs.
    What this data cannot tell, and the poll must say so rather than call the way clear:
    - only the quads Parallax has populated, around the vessel and the camera: beyond, nothing is known
      (how far that reaches is not measured yet);
    - only the collideable families (`collisionLevel` of each scatter against the global one, 2 by default):
      the smaller rocks are not in it, and the rover drives over them;
    - an instance is often a **cluster**, one mesh whose pivot is the anchor and whose boulders spread 15-25 m
      around it: an obstacle is the footprint of its mesh bounds, posed as above, never the anchor alone.
  - **The server keeps the reflexes**: between two polls the agent is blind for several seconds, so the server
    holds the heading and speed asked, and keeps today's guards (tip-over, runaway, stall, turns no harder
    than keeps the rover on its wheels). A dead man's switch brakes when no poll or command comes for some
    seconds. A turn-by-turn mode is worth trying: the game paused while the agent decides, then run for a
    given time.
  - **Poll only when something may happen**: the agent sets when it polls next from what the last poll told it,
    so it does not read again and again a rover that just rolls on. Over flat ground with no obstacle in the
    next 500 m, it drives on and polls again only once the rover is 500 m further. For that, a poll should say
    how far the way ahead is clear (no slope above a limit, no obstacle), looking as far as the agent asks
    rather than a few tens of metres, **and how far it could see**: with Parallax, no further than the quads
    it has populated, so on rocky ground the polls come as often as that range demands, whatever the agent
    asked; and the agent should be able to wait by distance rather than by time
    ("answer when the rover has gone 500 m, or earlier if a guard fires or the drive ends"). The dead man's
    switch then follows the delay the agent announces, not a fixed few seconds.
