# Drone Swarm

The web application is an ASP.NET Core service with a React/Three.js dashboard.
It accepts the version 1 Minecraft mission protocol and provides an isolated
Skyrim session using the same cell-based contract. Minecraft and Skyrim can run
one mission each at the same time; the dashboard switches between their live
maps and SignalR updates.

For a single-instance Linux Azure App Service deployment, see the
[Azure deployment guide](docs/AZURE.md).

## Run locally

1. Set a plugin key in the shell; do not commit it:
   ```powershell
   $env:PLUGIN_KEY = "use-a-local-demo-key"
   ```
2. Build the dashboard and serve it with ASP.NET Core:
   ```powershell
   cd web
   npm install
   npm run build
   cd ..\DroneSwarm.Web
   dotnet run
   ```
3. Open the URL printed by `dotnet run`. For dashboard-only frontend work, run
   `npm run dev` from `web`; Vite uses the same-origin API by default, so proxy
   configuration is needed when running it separately.

The Minecraft plugin writes to `/api/plugin/*`; the Skyrim adapter uses
`/api/plugin/skyrim/*`. Both use the `X-Plugin-Key` header. The dashboard is
public and read-only. Mission state is in memory and is lost when the server
restarts. The native SKSE adapter in [SkyrimAdapter/](SkyrimAdapter/) is an
early prototype: it samples exterior landscape height and moves temporary
references, but does not yet sense structures or other non-landscape obstacles
and still needs an in-game smoke test. Configure HTTPS for hosted deployments;
HTTP is only appropriate on a trusted private hotspot with a separate
short-lived key.
When using Vite separately, ensure ASP.NET Core is listening at
`http://localhost:5000`.

## Run the Paper plugin with the web app

Build the plugin with Java 25:

```powershell
cd MC_Plugin
.\gradlew.bat test
.\gradlew.bat jar
```

Copy `MC_Plugin\build\libs\MC_Plugin-1.0-SNAPSHOT.jar` into the Paper server's
`plugins` directory and start the server once. Edit
`plugins\DroneSim\config.yml`; the default backend URL is
`https://drone-swarm-durhack2026.azurewebsites.net`. Set `backend.plugin-key`
to the hosted app's `PLUGIN_KEY` from Azure; never commit the secret.
Existing server configurations are not overwritten when updating the plugin:
change `backend.base-url` manually if it still points at localhost.
For local development, set `backend.base-url` to `http://127.0.0.1:5000`
and use the same local-only key as ASP.NET Core.
`set_hive` automatically centers a 128x64x128 mission
area on the hive: minimum X/Z are hive X/Z minus 64 and minimum Y is hive Y
minus 32, shifted inward if necessary to stay within the world's height.
The old `mission.bounds-min` configuration is no longer used.

For local development on the same computer, start the built web app on port 5000
(skip this when using the hosted Azure app):

```powershell
$env:PLUGIN_KEY = "use-a-local-demo-key"
cd DroneSwarm.Web
dotnet run --urls http://127.0.0.1:5000
```

Join the Paper world, choose a clear hive cell, and run:

```text
/set_hive 0 64 0
/set_target 40 64 30
/spawn_drones 5 2 2
/start_mission
```

These are native Minecraft commands: the chat prompt shows the expected
arguments, coordinate arguments offer normal coordinate completion (including
`~` relative and `^` local coordinates), and Tab suggests valid drone counts,
ranges and speeds. `spawn_drones` accepts 1-64 units, optional spawn range 1-3
(default 3), and speed 1-5 blocks/s (default 2). Set the hive before the target.
Moving the hive moves the bounds; the command prints the new inclusive limits
and warns if the old target now lies outside them. Reset before moving mission
landmarks, and reposition out-of-bounds drones before starting again.

Open `http://127.0.0.1:5000` for the dashboard. `/reset_mission` explicitly
resets the backend mission and makes the current drones available for another
run. If the plugin was restarted and no longer has the mission ID, restart the
web app to clear its in-memory mission before starting another one.

For a clean retest, use **`/clear_mission`** as an operator. It waits for backend
reset confirmation, removes managed and loaded `drone_swarm`-tagged bees, clears
the roster and restores the original hive/target blocks and removes their labels.
Other entities and player-modified blocks are preserved. Then set the hive and
target, spawn fresh drones and start again in the same world. Do not issue setup
commands until the command confirms completion. `/reset_mission` still keeps
landmarks and surviving drones.

Spawned bees no longer need a clear initial hive link to begin a mission.
Bounds and availability checks remain, and real communication/recovery still
require an unobstructed in-range link.

`set_hive` places a real beehive block; `set_target` places a real honey block.
Choose free, non-liquid cells inside the mission bounds. Both get full-bright,
glowing block-display outlines and floating `HIVE` / `SEARCH TARGET` labels.
These display entities provide the outline because Minecraft's glowing effect
does not apply directly to blocks. They do not reveal the target to the backend:
scouts must still discover and report it normally.

The blocks are physical obstacles. Do not teleport drones into the hive cell;
use the air cell directly above it when repositioning a reset mission.
Upload and recovery routes use a free, unobstructed cell within hive range
instead of trying to enter the solid beehive.
Moving a landmark or cleanly stopping Paper removes its displays and restores
the original block, unless a player changed it. Landmark chunks stay loaded
while configured so their glowing displays remain available. Resetting a mission
keeps the configured landmarks in place.

For the prepared normal-terrain world, exact start/reset commands, and the
local server startup script, see the [local test notebook](docs/integration/LOCAL_TEST_NOTEBOOK.md).

For two computers, bind the web app to its LAN interface and set the plugin URL
to that host's address; do this only on a trusted private hotspot with a
short-lived key, never over venue Wi-Fi or public HTTP.

The current web implementation includes protocol validation, idempotent retry
handling, in-memory terrain/mission state, local frontier planning, one-hop-
per-cycle multi-hop knowledge relay, isolation rendezvous, assigned peer
rescue, target reporting/recall, recovery status, tower-known position markers,
public snapshots, and SignalR map/debug updates. Physical movement remains the
Minecraft plugin's responsibility. Full resilience, throughput, rendering, and
end-to-end demo targets still require validation against the plugin and the
deployment hardware.

### Physical flight regression checks

The plugin executes adjacent waypoints with bounded per-tick velocity, real block
collision checks and gravity-free hover. Vanilla bee decisions are disabled
without disabling flying-mob physics. Drones glow and carry the `drone_swarm`
entity tag. `/spawn_drones <count> <range> <speed>` accepts speeds of 1–5 blocks
per second. The backend still owns route planning; the plugin never teleports
drones to complete a move.

Movement uses one collision-safe buffered next waypoint, eliminating the old
arrival/report/acknowledgement stop at every block. Positions/results travel at
up to 10 Hz while terrain sensing, knowledge relay and replay stay at 2 Hz on
the same ordered retry-safe stream. Stops remain valid for obstacles, traffic,
arrival/upload, unavailable next routes and network outages. Update the backend
and plugin together; legacy clients retain single-command dispatch.

Flight needs a real Paper server, not a mocked pathfinder. Build the optional
regression-test plugin with `.\gradlew.bat flightTestJar` in `MC_Plugin`, install
the `*-flight-tests.jar` alongside the production plugin on a **test server**,
and restart. In an empty air region, run these as an operator (or omit `/` in
the server console):

```text
/test_drone_flight up 360 104 -128
/test_drone_flight down 360 104 -128
/test_drone_flight horizontal 360 104 -128
/test_drone_flight blocked 360 104 -128
/test_drone_flight ceiling 360 104 -128
/test_drone_flight queued-blocked 360 104 -128
/test_drone_flight turn 360 104 -128
/test_drone_speed 1 360 110 -128 queued
/test_drone_speed 2 360 110 -128 queued
/test_drone_speed 5 360 110 -128 queued
```

Wait for each `FLIGHT_TEST ... PASS` before starting the next. Tests assert
actual arrival within 0.06 blocks of the bee-sized cell centre, a full second
of stationary hover, glow, an obstacle appearing after dispatch, and horizontal
flight underneath a one-block ceiling. Each test removes its own bee and restores
its temporary block, including on clean shutdown. Remove the test plugin and
restart before normal use. These are separate from Gradle's JVM test task.
The queued speed tests traverse eight adjacent waypoints, simulate 2-tick
acknowledgements, require speed within 8% of the configured blocks/s, and reject
stopped intermediate ticks. Omit `queued` to isolate physics with one long move.
Queued blockage must cancel the successor and hover; `turn` checks a right-angle
transition and retained ordered results.

The same optional companion exposes `/test_mission_setup`, a read-only check
of the live native command tree's count/range/speed completion and prefix
filtering. It reports `SETUP_TEST completion PASS` without spawning any units.

## Map camera

The spatial telemetry view fills the window. Open the left-edge side tab for
mission statistics, the drone roster, connection information, and view controls.
The full-height drawer keeps compact mission counts, drone states, and view
controls, without repeated descriptions or coordinate readouts. It scrolls
without resizing the terrain view. Unit inspection is hidden until a drone is
selected, then opens as a compact, closable card without closing the drawer.
Closing it keeps the selected camera target; select the unit again to reopen.
The local-knowledge preview is expandable inside the card. The header's light
indicates backend connectivity without a text label.
Click **HEIGHT FILTER** at the bottom right to open a vertical cutoff slider,
with an orange fill and minimum/maximum height markers. The header uses a
seven-cell honeycomb mark; hovering over it shows the backend's online/offline
status.

In **HIVE VIEW**, select a drone in the roster or on the map to follow its current
tower-known position. Isolated drones remain at their last tower-reported position and are
shown faded; selecting one does not reveal or follow its disconnected live
position on the tower map. The selected-drone debug panel remains live and
drone-local. The camera keeps its viewing angle and zoom while tracking; orbit
and zoom remain available, but panning is disabled during follow mode. Select
**HIVE VIEW** to stop following and recenter on the hive, or **OVERALL VIEW** to
frame all height-visible discovered terrain, live positions of all scouts (including
isolated scouts), and the actual target even before discovery. Overall view uses
plugin telemetry for markers and follow mode without publishing it into the
hive's knowledge.
Live markers interpolate between received samples without predicting unseen
movement. Labels, links and the follow camera share those positions; stale or
faulted markers freeze, and unchanged terrain geometry is reused.
Clicking a scout in overall view jumps to a close follow view while retaining
the camera's viewing direction. Clicking it again refocuses; subsequent live
updates preserve manual orbit and zoom.
Unavailable or stale telemetry remains marked as last reported; faulted scouts
remain marked as faulted. Overall terrain updates with discoveries from every
scout immediately, including isolated scouts; hive view keeps those discoveries
hidden until they are reported through the communication network. Newer air
observations remove older solid cells in the observer view without modifying
hive knowledge or planner inputs.
If the selected drone has no current position in the chosen view, the camera
returns to the chosen overview. A new mission clears selection and restores hive
view. Switching views or resizing reframes the scene; ordinary updates preserve
manual orbit and zoom.
Overall framing fits the visible terrain's projected edges with a small margin rather than
an enclosing sphere. Floating tags shrink with distance as you zoom out.

Drones use yellow cuboid outlines and floating, selectable ID tags; the taller
hive is labelled `HIVE` and uses an orange hexagonal-prism outline without range
rings. Bright, screen-width orange hive links and yellow peer links remain
visible over terrain. Communication still uses the obstacle-aware 7x7x7 cube.
A drone's arrow indicates
its last observed horizontal travel direction, not a telemetry-reported facing
angle; it extends from the centre of the cuboid's front face, appears after
movement is observed, and persists while stationary.
In hive view, disconnected markers and their arrows stay faded at their last
reported position. In overall view, they track live plugin telemetry instead;
only stale or faulted markers are faded.
Drone tags are hidden when the hive's hexagonal body blocks them and return when
the camera or drone moves clear. Terrain outlines are white; the interface uses
an amber/orange honeycomb palette. Terrain wireframes include the vertical corners
and exposed side/bottom boundaries of observed solid cells, without interior
coplanar seams or connections across unobserved gaps. Depth-only faces from those
same known solids hide white outlines behind nearer terrain; they do not add
unknown terrain or change the wireframe palette. Colored mission markers remain
readable above that terrain layer. The height filter applies to
both the outlines and their occluding faces in all three views.

**SCOUT VIEW** shows only the selected unit's accumulated solid-cell knowledge,
its own last observed position and a target it actually knows about. Select
another unit in the roster to switch maps. Other unit markers come only from
that scout's independently retained peer sightings, including legitimate reports
relayed by peers or the hive. Sightings retain their original observation tick
and stale sightings stay faded, never replaced with live observer positions.
Loading, unavailable, mismatched and old-mission
responses never fall back to global terrain or the globally reported target.

**CUTAWAY** toggles a vertical section through the selected scout (or hive).
The camera-facing half of known terrain becomes invisible as you orbit, including
its depth-occluding faces, revealing underground outlines on the far side.
It works alongside the height filter and never reveals unobserved terrain.

Header milestones show **TARGET FOUND**, **HIVE INFORMED**, and
**ALL DRONES HOME / AT REST**. Finding alone does not count as reporting, and
completion waits for all movement commands to finish.

**REPLAY SEARCH** loads the server's full mission recording, even if the browser
was closed during the search. Pause/play, scrub the timeline and choose 1x-16x.
Minecraft continues running independently; **BACK TO LIVE** returns to current
telemetry. Replay supports hive and overall views, not individual scout inspection.
Recording ends at mission completion, survives reset/clear, and is replaced only
when a new mission starts successfully (or lost on backend restart). Terrain
history stores material changes rather than repeatedly copying whole maps; replay
terrain source ticks indicate when that material first appeared in the recording.
Use **STOP RECORDING** / **START RECORDING** in the replay controls to pause or
resume capture. Recording is on by default. Pausing preserves the current replay;
frames observed while paused are not added. The control is public, like the
read-only replay endpoints.
Replay read endpoints are `/api/public/replay` (mission ID and frame ticks) and
`/api/public/replay/{missionId}/frames/{frame}` (zero-based snapshot).
The public recording toggle uses `POST /api/public/{game}/replay/recording` with
`{"enabled":true}` or `{"enabled":false}`.

## Surface-following exploration

Movement dispatch reserves every scout's occupied block and each unfinished
move destination, plus the active source and buffered destination for continuous
flight. Scouts cannot be commanded into another occupied block,
share a destination, or swap blocks in the same cycle. A conflicting move
tries an alternative route through the scout's own known terrain; if traffic
blocks all routes, it reports `waitingForClearance` and retries on later cycles
instead of declaring the search exhausted. Scouts already sharing a block
are assigned distinct exits when space is available. These are command-level
safety checks; physical movement remains the plugin's responsibility.

Exploration paths stay within 3 blocks (Euclidean block-centre distance) of
observed solid terrain, including walls and ceilings. This uses only each
drone's local/legitimately relayed knowledge. A drone already in open air seeks
the nearest reachable known surface; without any observed surface it holds
instead of searching empty sky. Return/report routes may cross known open air.
This intentionally stops treating unsupported air as useful search space.

Short sightlines inspect only their bounded voxel region, rather than every
known solid, while retaining exact edge/corner occlusion. Out-of-range hive
and peer links are rejected before merging solid-cell sets. This bounds local
visibility work as maps grow without changing knowledge permissions, flight
speed, or the 10-tick observation/decision cadence.

After 24 successful frontier advances without a direct tower upload, a scout
returns through known free space to share its discoveries, then resumes
exploring. Transit across already-searched terrain does not consume that budget.
The dashboard labels this behaviour `RETURNING TO UPLOAD`. The tower map still
contains only legitimately received observations, never hidden global terrain.

When two scouts establish a new direct link and exchange knowledge, the one
with more search moves remaining before its next upload resets its counter.
The nearer-to-return scout keeps its schedule; equal counters keep the lower
ordinal unit ID on schedule. A continuous link does not repeatedly reset the
budget. Upload trips already underway, target reporting, recall and rescue
behavior take priority. Selected-drone debug data includes
`frontierMovesSinceUpload` so this budget can be inspected.

## Run the ground-mapping demo

With the backend running and the same `PLUGIN_KEY` set in a second shell:

```powershell
node scripts\simulate.mjs --warmup 400
```

The legacy single-command simulator supplies full protocol observation cubes for five scouts over
rolling ground covering the 128x128 horizontal mission bounds. It executes the
backend's commands, including surface following and upload trips; it does not
reveal unexplored terrain. Warmup runs real observation/command cycles without
the usual delay to build a visible map, then telemetry continues every 500 ms.
Use `--warmup 0` to watch from the beginning, `--url` for another backend
address, or `--reset` to explicitly replace an existing mission. Stop the
simulator with Ctrl+C. Restarting the backend clears the map.
Use `--game skyrim` to run the same protocol simulator against the isolated
Skyrim session while Minecraft remains active; this validates backend and
dashboard session separation, not the in-game Skyrim mod. Add `--frames 5` for
a bounded local smoke test; the default `--frames 0` runs continuously.

## Backend regression checks

```powershell
dotnet test DroneSwarm.Web.Tests\DroneSwarm.Web.Tests.csproj
```

Frontend camera regression checks (Node.js 22.18+):

```powershell
cd web
npm test
```
