# Local Minecraft test notebook

## Prepared environment

- Dashboard: <http://127.0.0.1:5000>
- Minecraft Java server: `127.0.0.1:25565` (Paper 26.3 / Java 25).
- Player `BatteriesAlex` is whitelisted and an operator.
- Server, Java runtime, credentials, logs and worlds stay **outside Git** in
  `C:\repos\Durhack\DroneSwarm.LocalTest`.
- Active normal-terrain world: `terrain-world`. The old flat `world` is preserved.
- Hive-centered bounds: X `288..415`, Y `52..115`, Z `-192..-65`.
- Hive: `352 84 -128`. Search target: `392 77 -98`.
- These free cells avoid old blocks left by an earlier unclean server stop.
  Cleanup restores landmarks placed by the current plugin session; it does not
  remove unrelated or untracked old blocks.

The commands below are Minecraft chat commands. In the server console or RCON,
omit the leading `/`.

The commands use Minecraft's argument prompts and Tab completion. Hive/target
positions accept absolute block coordinates, `~` relative coordinates and `^`
local coordinates. `spawn_drones` prompts for `n_drones`, optional `drone_range`
and optional `speed`, with valid numeric suggestions.

`set_hive` moves the 128x64x128 mission bounds with the hive and prints their
inclusive limits. The vertical range stays inside the world's build height.
There is no bounds configuration to edit. After moving the hive, set a target
inside the new area and reposition the drones before starting.

## First search after starting Paper

For this local-only setup, set `backend.base-url` in the server's
`plugins\DroneSim\config.yml` to `http://127.0.0.1:5000` before starting Paper.
New plugin configurations default to the hosted Azure app instead.

Start the web app first, using the same local-only plugin key as
`plugins\DroneSim\config.yml`; never commit that key. Join Minecraft, then:

```text
/set_hive 352 84 -128
/set_target 392 77 -98
/spawn_drones 20 3 2
/start_mission
```

Newly spawned bees need not have initial line of sight to the hive; no setup
teleport is required. Never place bees inside the hive block. Wait for mission acceptance
in the server log and `MINECRAFT` connected in the dashboard.

Do not run `spawn_drones` again when restarting the same search: it adds units
instead of replacing the current roster. This prepares 20 bees; spawn range 3
provides enough free setup cells before the teleport. The last argument is
speed in blocks per second (`1..5`); normal testing uses `2`.
The plugin now sends positions/results every 2 ticks and terrain every 10 ticks.
One buffered waypoint removes the per-block acknowledgement pause; the browser
interpolates real samples without exposing observer telemetry as hive knowledge.

## Reset and restart the search

For a completely fresh setup in the same world, use `/clear_mission` and wait for
its completion message. This resets the backend, removes managed/loaded tagged
swarm bees, restores hive/target blocks, removes labels and clears the roster.
Then repeat **First search**. Other world content is preserved.
The completed recording remains replayable until the next successful mission start.

```text
/reset_mission
```

**Wait until the dashboard says `AWAITING FIELD DATA`** and the server confirms
the reset. This clears mission/map/command state and stops the units; it does
not return them to the hive or respawn dead bees. Then:

```text
/tp @e[type=minecraft:bee,tag=drone_swarm] 352.5 85.25 -127.5
/set_target 392 77 -98
/start_mission
```

The hive remains configured until Paper restarts. To test fast target reporting
and recovery, use `/set_target 354 84 -128` before `start_mission`. Reset again
and restore the distant target for exploration.

`set_hive` creates a real beehive and `set_target` creates a real honey block,
each with a bright glowing entity outline and floating label. Use free,
non-liquid cells; solid terrain is not overwritten. The labels are for Minecraft
players only: bees still have to discover/report the target through their local
observations. Reset leaves both landmarks visible. Moving one or cleanly
stopping Paper removes its display entities and restores the original block
unless the player changed it.

Never teleport active search units: reset and wait first. If units died, reset
prunes their unavailable entries; `spawn_drones <missing-count> 1 2` can replenish
them before the setup teleport and next start.

## Dashboard controls

- **HIVE VIEW**: knowledge legitimately received by the hive; isolated units
  retain faded last-reported markers.
- **SCOUT VIEW**: choose a unit in the roster; see only that unit's accumulated
  terrain, own position, known target and independently remembered peer sightings.
  No global-map fallback; stale peer locations remain faded.
- **OVERALL VIEW**: debug overview of all observed terrain, live unit positions,
  and the actual target, including before discovery. This does not tell scouts
  or the hive where the target is.
- **HEIGHT FILTER**: reveal lower levels or cut away upper terrain.
- **CUTAWAY**: toggle a camera-facing half-section of known terrain, centered on
  the selected scout or hive, to see underground as you orbit.
- **REPLAY SEARCH**: server recording from mission start; scrub/pause and play
  at 1x-16x in hive/overall view. **BACK TO LIVE** leaves Minecraft unaffected.
- Header milestones distinguish target found, hive informed and all drones home
  with movement finished.
- The white terrain outline now includes vertical corner connections.
- Nearer known terrain hides obstructed white outlines.
- Unit details appear only after selection; the compact card has a close button.
- Managed bees glow and have the `drone_swarm` entity tag.

Useful operator commands:

```text
/tp BatteriesAlex 357 87 -134
/gamemode creative BatteriesAlex
/execute as @e[type=minecraft:bee,tag=drone_swarm] run data get entity @s Pos
```

## Start the local processes if stopped

From the repository root, build/serve the web app in one PowerShell terminal:

```powershell
Push-Location web
npm run build
Pop-Location
$env:PLUGIN_KEY = "<same key as the local plugin config>"
dotnet run --configuration Release --no-restore --project DroneSwarm.Web --urls http://127.0.0.1:5000
```

In another terminal:

```powershell
& 'C:\repos\Durhack\DroneSwarm.LocalTest\start-server.ps1'
```

For console commands from PowerShell, the local RCON helper reads its password
from the uncommitted server configuration:

```powershell
node 'C:\repos\Durhack\DroneSwarm.LocalTest\rcon.mjs' 'reset_mission'
# Wait for reset confirmation before the next commands.
node 'C:\repos\Durhack\DroneSwarm.LocalTest\rcon.mjs' `
  'tp @e[type=minecraft:bee,tag=drone_swarm] 352.5 85.25 -127.5' `
  'start_mission'
```

## Full restart / plugin update

1. Reset the mission and wait for confirmation.
2. Before stopping Paper, remove only the demo's tagged bees:
   `/kill @e[type=minecraft:bee,tag=drone_swarm]`.
3. Use `stop` in the Paper console. Do not use Bukkit `/reload`.
4. If updating the plugin, build with the prepared Java 25 runtime:
   ```powershell
   $env:JAVA_HOME = 'C:\repos\Durhack\DroneSwarm.LocalTest\java\jdk-25.0.5+8'
   .\MC_Plugin\gradlew.bat -p .\MC_Plugin test jar --no-daemon
   ```
   Copy `MC_Plugin\build\libs\MC_Plugin-1.0-SNAPSHOT.jar` to the external
   server's `plugins\DroneSim.jar` while Paper is stopped.
5. Restart Paper and repeat **First search** above.

The plugin's hive, target and managed roster are in memory. Persisted bees are
not automatically re-adopted after a restart. If Paper was stopped without
cleanup, remove the old tagged demo bees **before** spawning replacements.
If the backend still holds an old mission after a plugin restart, restart the
web app too, then initialize a fresh mission.

Physical upward/downward/collision/hover regression commands are documented in
the [README](../../README.md#physical-flight-regression-checks). The optional
flight-test companion is not installed during normal interactive use.

## Continuous-flight verification

Measured locally on real Paper after separating motion from sensing:

- The isolated 8-block move already achieved 1.975 blocks/s at configured 2:
  the old slowdown came from waiting for report/acknowledgement at every
  waypoint, not a velocity-unit mismatch or a missing drag multiplier.
- Eight buffered adjacent waypoints achieved 0.993, 1.975 and 4.848 blocks/s at
  configured speeds 1, 2 and 5, with zero stopped intermediate ticks.
- Up/down, horizontal, dynamic blockage, ceiling clearance, queued blockage
  cancellation and right-angle queued transitions all passed real-server checks.
- A 20-bee search sampled for 20 seconds averaged 1.898-2.000 blocks/s per bee
  at configured 2; moving-sample median was 2.000, with no faulted bees. The
  previous same-world search averaged approximately 0.66 blocks/s.
- The stream delivered 40 full updates and 154 lightweight motion updates in
  that interval; motion payloads averaged about 2.6 KB versus 262 KB for full
  updates. Replay still records only full observations.
- Browser sampling measured approximately 58 FPS with interpolated marker
  positions; this is a local 20-bee check, not validation of the 50-drone target.
- Five-bee target discovery, hive reporting and all-five-at-rest completion
  passed after the protocol update.

## Mature-map slowdown regression

The short fresh-map check above did not catch a later slowdown. With more than
64,000 known cells per scout, full-map copying/relay blocked the shared request
stream: the live mission dropped to roughly 0.9-1.0 blocks/s, lightweight updates
disappeared, and plugin requests repeatedly hit their two-second timeout.
Paper itself remained near 20 TPS.

The backend now relays terrain/search deltas on maintained links, sends a
baseline on new/restored links, and freezes sources before transferring anything.
Received changes stay pending for the next cycle, preserving one-hop knowledge.
Observer terrain is updated only from actual observation cubes and remains
separate from planner knowledge. Public terrain and scout solid indexes are
reused rather than rebuilding growing maps during every update.

The 20-connected-scout regression includes over 60,000 cells per scout, both
matching and varied observation ages, and a 500 ms full-frame budget.
The original matching-age case took over 2.5 seconds in Debug. The final Release
build measured 73-236 ms for initial exchange and 9-28 ms for steady updates.
All 45 backend cases passed, including one-hop relay, new-contact baselines,
maintained-link solid-to-air deltas, more than 64 source owners, and replay
history preservation. Run the local demo in Release mode as shown above.

A ten-minute live check then exposed expensive long-distance return/peer searches
as maps passed 100,000 cells. Goal-directed known-air routing replaces those
breadth-first searches, preserving shortest paths, collision/traffic checks and
nearest-region peer selection. The added 100,000-cell, 20-bee return regression
measured 188 ms in the final Release build against the same 500 ms budget.

## Buffered-frontier circling regression

The subsequent live search exposed circling despite healthy native movement:
several searching bees travelled about 30 blocks while revisiting only 5-8 cells.
The mission was reset, preserving bees, landmarks and replay. Minecraft remained
stopped throughout offline diagnosis and restarted only after explicit approval.

Offline reproduction uses the recorded starting poses and saved world terrain,
with scouts receiving only their actual simulated observation cubes. Saved block
passability is checked against recorded observations; extra terrain uses explicit
block collision/liquid classifications, not an unknown-is-air fallback. This
fixture assumes static terrain and approximates non-empty collision shapes as
full voxels; no collision failures occurred in either comparison run.

With 20 scouts, 20 Hz movement, 2 Hz sensing and full responses delayed three
ticks, one searching bee travelled 29.7 blocks through five distinct cells with
only 1.1 blocks of displacement over the final 20 seconds. Frontier diagnostics
showed the same one-cell visibility gain remaining unsearched: the buffered turn
carried the bee away between sensing frames, so replanning kept sending it back.
An otherwise equivalent one-tick-response run did not show that final loop.

The accepted fix retains a pending frontier viewpoint until a real sensing cycle
observes that cell. It survives motion completion acknowledgements and clears on
blocked/failed execution. Transit remains buffered; a frontier may briefly wait
for sensing, while sensing during arrival permits a queued departure.

The three-tick-delay reproduction now visits 31-40 distinct cells per bee in the
final 20 seconds. The previously looping bee visits 39, travels 38.5 blocks and
displaces 23.9 blocks. The regression test failed before the fix, and all 49
Release backend cases pass afterwards. NuGet audit warnings concern unavailable
pre-existing private feeds, not build/test failures.

After approval, the updated Release backend was deployed and the mission restarted
with the existing 20 bees. No bees or landmarks were repositioned or removed.
The browser reconnected and displayed the active search and its canvas.

A three-minute live check maintained 2 Hz full observations, 7.03-8 Hz lightweight
updates and approximately 20 simulation ticks/s. The moving-sample median was
2.000 blocks/s at configured speed 2; whole-window swarm means were
1.897-1.979 blocks/s, including legitimate sensing/upload/traffic pauses.
No bees faulted. Every sampled 20-second search-only window visited at least
29 cells; no searching bee met the former circling signature of travelling
20+ blocks through eight or fewer cells. The formerly affected IDs 10, 12 and 13
visited 39, 39 and 38 cells respectively in the final window.

This verifies the original three-minute failure interval, not indefinite runtime
or the 50-drone target. Minecraft and the browser are left running for inspection.
