# Drone Swarm

The web application is an ASP.NET Core service with a React/Three.js dashboard.
It accepts the version 1 Minecraft plugin protocol, keeps one active mission in
memory, and serves the public dashboard and SignalR updates from the same app.

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

The plugin writes to `/api/plugin/*` with the `X-Plugin-Key` header. The
dashboard is public and read-only. Mission state is in memory and is lost when
The server restarts. Configure HTTPS for hosted deployments; HTTP is only
appropriate on a trusted private hotspot with a separate short-lived key.
When using Vite separately, ensure ASP.NET Core is listening at
`http://localhost:5000`.

The current web implementation includes protocol validation, idempotent retry
handling, in-memory terrain/mission state, local frontier planning, one-hop-
per-cycle multi-hop knowledge relay, isolation rendezvous, assigned peer
rescue, target reporting/recall, recovery status, tower-known position markers,
public snapshots, and SignalR map/debug updates. Physical movement remains the
Minecraft plugin's responsibility. Full resilience, throughput, rendering, and
end-to-end demo targets still require validation against the plugin and the
deployment hardware.

## Map camera

The spatial telemetry view fills the window. Open the left-edge side tab for
mission statistics, the drone roster, connection information, and view controls.
The full-height drawer keeps compact mission counts, drone states, and view
controls, without repeated descriptions or coordinate readouts. It scrolls
without resizing the terrain view. Unit inspection stays in
its own expandable overlay; selecting a drone opens inspection without closing
the drawer. The header's light indicates backend connectivity without a text label.
Click **HEIGHT FILTER** at the bottom right to open a vertical cutoff slider,
with an orange fill and minimum/maximum height markers. The header uses a
seven-cell honeycomb mark; hovering over it shows the backend's online/offline
status.

In **NEST VIEW**, select a drone in the roster or on the map to follow its current
tower-known position. Isolated drones remain at their last tower-reported position and are
shown faded; selecting one does not reveal or follow its disconnected live
position on the tower map. The selected-drone debug panel remains live and
drone-local. The camera keeps its viewing angle and zoom while tracking; orbit
and zoom remain available, but panning is disabled during follow mode. Select
**NEST VIEW** to stop following and recenter on the nest, or **OVERALL VIEW** to
frame all height-visible discovered terrain, live positions of all scouts (including
isolated scouts), and any reported target. Overall view uses plugin telemetry
for markers and follow mode without publishing it into the nest's knowledge.
Clicking a scout in overall view jumps to a close follow view while retaining
the camera's viewing direction. Clicking it again refocuses; subsequent live
updates preserve manual orbit and zoom.
Unavailable or stale telemetry remains marked as last reported; faulted scouts
remain marked as faulted. Overall terrain updates with discoveries from every
scout immediately, including isolated scouts; nest view keeps those discoveries
hidden until they are reported through the communication network. Newer air
observations remove older solid cells in the observer view without modifying
nest knowledge or planner inputs.
If the selected drone has no current position in the chosen view, the camera
returns to the chosen overview. A new mission clears selection and restores nest
view. Switching views or resizing reframes the scene; ordinary updates preserve
manual orbit and zoom.
Overall framing fits the visible terrain's projected edges with a small margin rather than
an enclosing sphere. Floating tags shrink with distance as you zoom out.

Drones use yellow cuboid outlines and floating, selectable ID tags; the taller
tower is labelled `NEST` and uses an orange hexagonal-prism outline, surrounded
by orange radius-3 rings spaced 1.5 blocks apart vertically from -3 to +3.
These rings are a visual range guide, not the exact communication boundary:
communication still uses the obstacle-aware 7x7x7 cube.
A drone's arrow indicates
its last observed horizontal travel direction, not a telemetry-reported facing
angle; it extends from the centre of the cuboid's front face, appears after
movement is observed, and persists while stationary.
In nest view, disconnected markers and their arrows stay faded at their last
reported position. In overall view, they track live plugin telemetry instead;
only stale or faulted markers are faded.
Drone tags are hidden when the nest's hexagonal body blocks them and return when
the camera or drone moves clear. Terrain outlines are white; the interface uses
an amber/orange honeycomb palette.

## Surface-following exploration

Movement dispatch reserves every scout's occupied block and each unfinished
move destination. Scouts cannot be commanded into another occupied block,
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

After 24 successful frontier advances without a direct tower upload, a scout
returns through known free space to share its discoveries, then resumes
exploring. Transit across already-searched terrain does not consume that budget.
The dashboard labels this behaviour `RETURNING TO UPLOAD`. The tower map still
contains only legitimately received observations, never hidden global terrain.

## Run the ground-mapping demo

With the backend running and the same `PLUGIN_KEY` set in a second shell:

```powershell
node scripts\simulate.mjs --warmup 400
```

The simulator supplies full protocol observation cubes for five scouts over
rolling ground covering the 128x128 horizontal mission bounds. It executes the
backend's commands, including surface following and upload trips; it does not
reveal unexplored terrain. Warmup runs real observation/command cycles without
the usual delay to build a visible map, then telemetry continues every 500 ms.
Use `--warmup 0` to watch from the beginning, `--url http://localhost:5000` for
another backend address, or `--reset` to explicitly replace an existing mission.
Stop the simulator with Ctrl+C. Restarting the backend clears the map.

## Backend regression checks

```powershell
dotnet test DroneSwarm.Web.Tests\DroneSwarm.Web.Tests.csproj
```

Frontend camera regression checks (Node.js 22.18+):

```powershell
cd web
npm test
```
