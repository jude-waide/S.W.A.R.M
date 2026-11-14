# Drone Swarm

## Project Purpose

Build a Minecraft drone simulation connected to a web application. Drones explore
unknown terrain to find a hidden target, building a map while their communication
is constrained by range.

The primary goal is a compelling demonstration of drones coordinating under
communication constraints, rather than a scientifically rigorous simulator or
a full Minecraft gameplay experience.

### Mission Success

The search objective is achieved when drones discover the hidden target and
relay its location to the central tower. Discovery alone does not satisfy this
objective. Mapping is a by-product of searching, not a separate coverage
requirement. Full mission completion also requires the recovery phase below.

After the target is reported, the system enters a recovery/return phase: drones
pass commands between one another step by step, seek missing drones near their
last-known locations, and collectively return to base. Recovery is complete only
when every drone is directly within the tower's centred 7x7x7 neighbourhood with
an unobstructed link. Multi-hop connectivity alone does not count as arrival.
Do not simply stop all commands when the tower receives the report.

The target must be discovered through sensing, not supplied as prior knowledge
to the drones. The backend receives target coordinates as hidden mission ground
truth, isolated from every drone's decision-making and shared knowledge.
The in-memory web backend only exposes target coordinates to a drone after
detection or to the tower after a legitimate report. The debug Overall View
also shows the actual target through a separate `overallTarget` snapshot field;
this never changes drone knowledge, tower knowledge, or mission milestones.

Tower and target coordinates stay fixed during a mission. Moving either requires
a fresh mission; moving-target tracking is outside this version.

## Scope and Technology

- This project implements the web application.
- Backend: C# with ASP.NET Core on .NET 10 LTS.
- Frontend: React with TypeScript and Vite.
- 3D rendering: Three.js through React Three Fiber.
- Browser live updates: ASP.NET Core SignalR.
- Serve built React assets from the same ASP.NET Core app for Azure and LAN.
- No database, Redis, or separate Azure SignalR service is required for the
  single-instance demo.
- Minecraft integration: a plugin, implemented separately from the web app.
- A teammate is building the plugin in parallel. The integration contract can
  now use the agreed [version-one baseline](docs/integration/PROTOCOL.md).
- The web application owns drone behaviour, including deciding where drones
  should search next and simulating drone-to-drone communication.
- The interface should show a military-green 3D map and each drone's location.
  This is a visual style for the Minecraft simulation.

## Demo Users and Environment

- The audience is the hack day organisers.
- One computer displays the web application and a second computer runs Minecraft.
- The demo should make the relationship between in-game activity and the web
  visualisation clear.
- Azure is primary, with LAN/private-hotspot backup. The public web dashboard
  is read-only; Minecraft controls mission initialisation and reset.

## Delivery Constraints

- Approximately 20 hours of the hackathon remain, as reported during discovery
  on 2026-11-14.
- Scope should prioritise a working two-computer demonstration of the core
  search-and-relay mission.

## Scale Requirements

- Drone count is variable; do not hardcode a ten-drone product limit.
- Validate 50 drones at two batches per second, with backend planning keeping
  up with 500 ms cycles and a target of 30 FPS for the desktop map within the
  full exploration bounds. This is a validation load, not a hard roster cap.
- Variable count is not a promise of unlimited capacity.
- The exploration volume is 128x64x128 blocks (X by Y by Z).
- Minecraft supplies the integer minimum X/Y/Z at initialisation. Minimum
  bounds are inclusive and maximum bounds exclusive.
- The Paper plugin centers this box on the configured hive, shifting its
  vertical extent inward at world height limits. Moving the hive moves the box
  for the next mission; no absolute bounds configuration is required.
- Reject initialisation if tower, target, or initial drone locations are outside
  the box. Backend-planned waypoints must remain inside it.
- Bounds and dimensions stay fixed for the duration of a mission.

## Coordinates

- Use native Minecraft axes in block units, with Y vertical.
- Terrain, tower, and target locations are integer block coordinates.
- Drone telemetry contains actual decimal X/Y/Z positions.
- Floor each drone coordinate to identify its current block, including negative
  coordinates. Cube membership and line of sight use these block coordinates.
- The browser uses actual decimal positions for drone markers.

## Mission Storage

- One active mission is held in backend memory.
- Restarting the backend clears mission state.
- Minecraft can initialise a fresh mission.
- Saved missions, replay, and a persistence database are not required for this
  first version.
- Each mission belongs to one Minecraft world/dimension and has a fixed roster
  of drone IDs established at initialisation.
- The same world can be used for multiple missions; world ID and mission ID are
  distinct. Missions are sequential, with only one active at a time.
- Each fresh mission has a new ID and starts with empty maps and drone memories;
  discovery knowledge does not carry between missions in the same world.
- Identical initialisation retries for the same mission ID are idempotent.
- A different mission ID cannot silently replace an active mission: the plugin
  must explicitly reset/end the old mission first.
- Reject stale mission IDs, including old requests after a reset.
- Use mission IDs alone, without a backend-instance guard. Retired IDs can be
  tracked only for the current process lifetime; after restart, a delayed old
  initialisation cannot be distinguished from fresh setup if no mission exists.
  The plugin must use a new ID and never assume lost state survived.
- Adding/removing drones or changing dimension requires a fresh mission.
- Every initial drone must have a clear direct communication link to the tower.
  Reject initialisation otherwise. Drones may subsequently move out of contact.

## Hosting

- Azure is the primary hosting target. Subscription access and permission to
  deploy are available.
- Recommended service layout, not yet deployed: one Azure App Service hosting
  the ASP.NET Core backend and built React assets; the plugin sends HTTPS requests.
- This avoids dependence on peer-to-peer connectivity on the venue LAN, but
  requires working internet and an Azure subscription with deployment access.
- With in-memory mission state, use a single app instance; a restart or recycle
  loses the active mission.
- A local deployment over LAN/private hotspot is a required backup, not an
  optional future enhancement. The plugin's backend address must be configurable
  so it can switch between Azure and the local host.
- Plugin requests require a shared secret stored in environment/app settings,
  never committed to source or embedded in the frontend.
- The dashboard is publicly viewable without login. Displayed mission data,
  including live per-drone debug observations, is therefore public.
- The public dashboard cannot mutate mission state. Mission lifecycle actions
  are restricted to authenticated plugin requests.
- Target Azure spend is at most GBP 5 for the event. Confirm the portal estimate
  before provisioning a small paid single instance with Always On; do not treat
  a budget alert as an automatic spend cap. Plan to remove demo resources after
  the event.
- Azure always uses HTTPS. HTTP is allowed for local backup only on the team's
  trusted private hotspot, using a separate short-lived local key, not on an
  untrusted venue network. HTTP does not encrypt that key or mission data.

## Domain Language

| Term | Meaning |
| ---- | ------- |
| Drone | An individually identified simulated drone in Minecraft. |
| Central tower | The destination to which drones must relay the discovered target location. Its coordinates are supplied by the plugin. |
| Target | A hidden Minecraft location drones must discover through sensing and report to the tower. |
| Communication link | An unobstructed connection to another drone inside the sender's centred 7x7x7 block cube. |
| Tower map | Terrain observations that have reached the tower through permitted communication. |
| Drone debug view | A read-only live inspection of one drone's observations, not a communication channel. |
| Frontier | Known-free space beside unknown terrain that a drone can explore using its own knowledge. |
| Recovered drone | A drone directly within the tower's clear 7x7x7 neighbourhood. |

## Integration Outline

Exchange JSON network messages over HTTP, not files on disk. The plugin
periodically POSTs drone observations to an ASP.NET Core endpoint; the response
contains backend-generated movement commands. Concrete schemas and error
responses are defined in the agreed [protocol](docs/integration/PROTOCOL.md);
the main delivery requirements are summarised below.

The plugin sends one batch covering all drones every 500 ms (two updates per
second), receiving movement commands in each response. Validate this cadence
with the plugin implementation. The browser uses SignalR: a tower snapshot on
connection, then versioned incremental updates, with resnapshot on a version
gap. Clear old map data and selection when the mission changes.

### Delivery Safety

- Every mission has an ID, and observation batches have increasing sequence
  numbers.
- Movement commands have IDs and execution acknowledgements.
- At most one move may be outstanding per drone. Repeat its command ID until
  completion/blockage is reported; the plugin executes each ID at most once.
- Continue observations while a move is in progress. One drone waiting for its
  result must not prevent other drones from progressing.
- After 10 simulation seconds without a final move result, mark that drone
  "command stalled". Preserve the outstanding command and issue no conflicting
  replacement until a final result or Minecraft reset resolves it.
- An unexpected plugin execution failure marks the drone faulted. Issue it no
  new movement until mission reset, preserve any available sensing/communication,
  and continue other drones. Do not silently count a faulted drone as recovered.
- Retrying a batch must not advance planning or execute another move; it returns
  the same result.
- The plugin keeps one observation request in flight. On timeout it retries the
  identical sequence and body until the response arrives, then sends a fresh
  batch. Missed intervals are skipped, not queued.
- The backend caches the latest accepted response, rejects older sequences and
  changed-body retries, and does not advance state on duplicate requests.
- Stale commands must not be applied to a different or reset mission.
- Use the protocol's schemas and error responses. Retain the latest accepted
  observation response for duplicate replay.
- Observation batches are validated atomically: exactly the fixed roster,
  coherent same-tick positions, complete clipped cubes, no duplicate/unknown
  IDs, and no contradictory overlapping cell states.
- An explicit unavailable/error entry is allowed for a roster member that
  cannot provide telemetry. It contains no invented observations, faults that
  drone, removes its live links, and preserves its last-known marker while other
  valid entries progress. Omitting its roster ID remains invalid.
- Reject an invalid batch with explicit errors and no state/decision advance;
  preserve the last valid visible state.

### Telemetry Loss

- Simulated radio disconnection is separate from loss of the plugin's HTTP
  connection to the backend.
- After three seconds without a valid new observation batch, the dashboard shows
  "Minecraft connection lost" and freezes the last-known visuals.
- The backend suspends simulation decisions and isolation timers until fresh
  telemetry resumes; a network outage must not trigger missing-drone behaviour.
- On resumption, rebase the simulation tick baseline and reconcile pending
  command results according to the protocol; do not replay missed decisions.

### Simulation Clock

- The plugin supplies an increasing simulation tick counter. Behaviour timers
  use simulation ticks rather than backend wall time.
- Use 20 simulation ticks per second and 10 ticks per decision/relay cycle.
- A fresh same-tick batch is a paused heartbeat: it does not advance behaviour.
- A fresh batch advances at most one cycle; do not replay missed cycles.
- Paused gameplay and unavailable telemetry do not advance behaviour timers.
- On resuming telemetry, do not fast-forward decisions or accumulate timeout
  progress for the unobserved interval.
- HTTP freshness detection still uses real elapsed time for the three-second
  connection-lost indicator.

### Original Outline: Minecraft Plugin to Web Backend

- Central tower coordinates.
- Target coordinates, as hidden mission ground truth rather than drone knowledge.
- A set of drone IDs.

### Original Outline: Web Backend to Minecraft Plugin

- A set of drone IDs.

These original lists are not sufficient as an implementation contract. Discovery
has established additional data needs: observed positions, per-drone solid/air
block observations, movement commands/results, and delivery-safety identifiers.
The [agreed protocol](docs/integration/PROTOCOL.md) replaces this original outline.

Minecraft controls initialisation and reset through the authenticated endpoints.

After initialisation, the web backend controls drone behaviour. Drone-to-drone
communication is simulated exclusively inside the web application, not through
Minecraft entities directly. The plugin and backend must still exchange the
observations and commands needed to execute that behaviour.

The browser is read-only for mission state. It supports viewing, filtering, and
drone inspection, but no Start, Pause, Reset, or manual steering actions.
Mission initialisation and reset are controlled through the authenticated plugin.

### Movement Responsibilities

- The backend plans adjacent waypoints using the drone's known terrain.
- The backend route graph uses six face-neighbour known-air cells: offsets
  +X, -X, +Y, -Y, +Z, -Z. This is a planning abstraction; the plugin validates
  physical execution.
- The plugin executes movement and reports the actual position or a blocked
  result; the backend must not assume a commanded move succeeded.
- The plugin remains authoritative for actual positions and physical execution.
- Detailed drone dimensions, flight physics, and movement animation remain the
  plugin team's responsibility.
- Detailed Minecraft movement and physics are deferred to the plugin team and
  are outside the web-app scope. Use the agreed route graph and movement-result
  contract rather than inventing a flight model in the web app.

## Drone Knowledge and Coordination

- Initial search strategy: local frontier exploration. A drone chooses reachable
  known-free locations beside unknown terrain, based only on its permitted
  knowledge, avoiding areas it knows have already been searched.
- Exploration paths stay within 3 blocks (Euclidean block-centre distance) of
  locally known solid ground, walls, or ceilings. Unsupported air is not a
  search objective. If already away from surfaces, seek the nearest reachable
  known surface; if none is known, hold. Known-air return/report routes are
  exempt. This deliberately narrows the original volumetric search policy.
- After 24 completed frontier advances without a direct tower upload, carry
  discoveries back to the tower, then resume exploration. Known-route transit
  does not count towards this budget. Do not expose unrelayed terrain globally.
- A new direct peer encounter exchanges knowledge, then resets the exploring
  scout with more of its 24 frontier moves remaining. The peer nearer its next
  upload keeps its schedule; equal counters preserve the lower ordinal unit
  ID's schedule. Decide simultaneous encounters from pre-reset counters.
  Continuous contact is not another encounter. Do not cancel an upload trip
  already underway or override target reporting, recall, rendezvous or rescue.
- Terrain-known and target-searched coverage are separate. A cell counts as
  searched only when in the drone's sensing cube with unobstructed line of sight.
  Occupancy reported behind an obstruction does not clear that cell for search.
- Frontier selection must also seek viewpoints into mapped-but-unsearched
  regions, not stop merely because occupancy is already known.
- Choose the nearest useful reachable viewpoint. Prefer greater estimated new
  visible coverage among equally short routes, then use a stable drone-ID-based
  tie-break. Estimates use only legitimately acquired knowledge.
- Replan as valid new knowledge arrives, without interrupting an unresolved move.
- Each drone's decision-making is restricted to its own observations and
  information received through permitted communication.
- Backend access to global simulation state must not give disconnected drones
  access to one another's discoveries.
- A shared omniscient planner must not use undisclosed discoveries to direct
  other drones. Range restrictions affect behaviour, not just displayed links.
- Drones can relay information over multiple hops. Every individual link must
  satisfy the communication-range rule.
- A drone is tower-connected if there is a valid communication path from it to
  the tower, including paths through other drones.
- Latest clarification: drones give out a 7x7x7 region and can communicate only
  with an unblocked drone within that region. This refines the earlier
  distance-based description; do not assume a spherical Euclidean radius.
- The cube is centred on the drone's current Minecraft block and includes
  offsets -3 through +3 on each axis. It defines both terrain sensing and
  the communication neighbourhood.
- The plugin reports blocks in that cube. The backend can use those observations
  to test whether a direct path to another in-cube drone is blocked.
- Every batch includes a complete local solid/air snapshot for each drone,
  clipped to the mission bounds (at most 343 cells per drone). This is not a
  terrain-delta protocol. Cells outside the reported cubes remain unknown unless
  the drone previously observed or legitimately received information about them.
- The tower uses the same centred 7x7x7 communication neighbourhood as drones.
- Every block reported as solid obstructs both communication and target
  visibility, including materials such as glass or leaves if the plugin labels
  them solid. There are no material-specific exceptions in the backend.
- Line of sight uses the segment between source and destination block centres.
  Endpoint cells are excluded; touching any intermediate solid cell, including
  only an edge or corner, blocks the link or target observation.
- All drones initially know the tower coordinates and exploration bounds.
  Terrain must be observed or received through permitted communication; the
  target location remains hidden.
- Return navigation uses the drone's recorded route and legitimately acquired
  map knowledge, not a global terrain map.
- Information travels one hop per 500 ms decision cycle, in both directions.
  A drone cannot forward information newly received in that same cycle.
- Relayed information includes terrain/search updates, legitimately known peer
  sightings, target reports, and recall orders.
- A drone detects the target only when the target is inside its sensed cube and
  the direct line of sight is unobstructed. Being in the cube alone does not count.

### Disconnection and Sharing

- Disconnected drones continue searching using local knowledge and retain their
  discoveries rather than publishing them globally.
- On discovering the target, a drone moves towards a known connection so the
  discovery can be relayed to the tower. If a remembered connection is gone,
  apply the known-alternatives/home/stuck fallback rather than assuming contact.
- A drone that legitimately receives a peer's target discovery also stops
  ordinary exploration and helps relay/carry the report towards a known tower
  connection. It does not wait for global backend notification.
- When drones meet within a valid communication link, they share searched-area
  information so their future decisions avoid areas already known to be searched.
- This reduces duplicate searching but cannot guarantee its absence: disconnected
  drones may independently search the same area before exchanging updates.
- Encounters and relay chains carry discoveries; the isolation fallback below
  also brings long-disconnected drones home. No additional periodic reporting
  schedule has been agreed.

### Recovery After Target Report

- Drones pass recovery/return commands to other drones.
- The tower assigns at most one available rescuer per missing peer, using only
  tower-known information. Assignments propagate through normal relay links.
- Other recalled drones head home. Rescuers seek assigned peers at last-known
  locations, pass on the recall if found, then both navigate home using their
  own legitimately acquired knowledge.
- Recall propagates only through valid communication links, including relays.
  A disconnected drone continues exploring until it receives the recall through
  legitimate contact; there is no global backend mode switch for unaware drones.
- Missing or disconnected drones are sought near their last-known positions.
- The intended outcome is for all drones to return to base.
- The UI distinguishes the target-reported milestone from all-drones-recovered
  completion.
- If a missing drone is absent from its last-known location, the searching drone
  performs a time-bounded local search, then returns to the tower and reports
  that peer as unrecovered.
- Isolation rendezvous and assigned rescues use the same local-search bounds:
  viewpoints within offsets -7 through +7 per axis of the last-known peer block,
  for at most 15 simulation seconds once that vicinity is reached. Clip this
  region to mission bounds; sensing/communication remain the smaller 7x7x7 cube.
- An unrecovered peer must not be counted as recovered just to end the mission.
- If bounded rescue attempts end with drones still missing, keep the mission
  active with an explicit "Target reported - recovery incomplete" status until
  every drone returns or Minecraft resets it.
- List missing/stuck drones, and do not repeatedly assign rescue attempts to
  the same unchanged sighting. New legitimate sightings can justify new work.
- After 30 seconds without successful drone or tower contact, a drone seeks a
  known peer at its last-known location. It searches locally for up to 15 seconds,
  then returns to the tower if no contact is made.
- Pause this exploration watchdog during known-route transit to the selected
  frontier. Apply it during active exploration there; do not label all movement
  as exempt transit. The UI still shows the true last-contact age.
- These are initial demo timeout values that can be tuned during rehearsal.
- If no reachable peer sighting is known, use the home/stuck fallback.
- Peer sightings and recovery assignments travel through permitted messages,
  preserving their original observation time. No global peer-position lookup
  is available to drone decision-making.
- Movement dispatch has a safety-only exception: authoritative plugin positions
  and unfinished move destinations reserve occupied blocks before commands are
  issued. This prevents shared destinations and head-on swaps without publishing
  peer positions or terrain into drone knowledge. Alternative routes still use
  only the drone's acquired terrain. Temporary traffic waits are reported as
  `waitingForClearance`, not exhausted search.

### Failed Routes and Exhausted Local Search

- If no useful reachable search remains and the target has not been reported,
  show "Search exhausted", not success.
- Keep that mission active and inspectable. New observations or legitimate
  knowledge exchange can create new work and resume exploration.
- Try alternative routes using only the drone's acquired knowledge.
- If no useful search or recovery route exists, return home if possible.
- If home is also unreachable, stay in place, keep sensing and communicating,
  and show an explicit stuck status.
- On a blocked move, avoid that directed transition for five simulation seconds,
  try known alternatives, then allow reconsideration after fresh observations
  and expiry of the cooldown.
- Do not teleport, assume a failed command succeeded, or consult hidden terrain
  to escape.

### Behaviour Priority

- Resolve/preserve any outstanding movement before issuing a conflicting move.
- Recall/rescue assignments take precedence over carrying a target report home.
- Carrying a known target report takes precedence over isolation rendezvous.
- Isolation rendezvous takes precedence over ordinary exploration and applies
  only during ordinary exploration, not reporting or recovery.

## Web Map Visibility

- Nest-view terrain displays only discoveries that have reached the tower.
- The overlay shows current positions and communication links for drones
  currently connected to the tower.
- In nest view, disconnected drones remain at their last tower-known positions, rather than
  revealing their live positions.
- Overall view is an observer view: it shows all scouts' latest plugin-reported
  positions, including isolated scouts. Stale or faulted telemetry stays marked.
  Its terrain also merges all scouts' observations by source tick, revealing
  discoveries immediately even before they reach the nest. This is display-only:
  it does not update tower knowledge or planner inputs. Nest view still hides
  discoveries that have not reached the tower.
- Connectivity includes multi-hop paths through intermediary drones.
- The UI must distinguish last-known positions from current connected positions.
- Each drone is clickable to open a live view of its current 7x7x7 observations,
  even when it is disconnected from the tower.
- This is a clearly labelled drone debug view, separate from the tower map.
  Inspection must not publish the selected drone's observations to the tower
  or any other drone.
- The main tower map keeps disconnected markers at their last reported
  positions even while a live debug view is open.

### Dashboard Layout

- Desktop layout with a large main tower-map viewport.
- Top status bar shows mission phase, HTTP connection state, and recovery counts.
- A side panel lists the mission's drones; markers and list entries are selectable.
- Selecting a drone opens a separate live 7x7x7 debug view showing its current
  coordinates, behaviour, current contacts, and last-report age.
- Disconnected main-map markers are faded and remain at their last reported
  coordinates, even while the selected-drone debug panel is live.

### Terrain Presentation

- Each returned terrain block is represented by a single coordinate point.
- Each observation includes coordinates and a solid/air state. Unreported
  locations are unknown, not implicitly air.
- Terrain may change during a mission. Fresh observations replace older cell
  states; changes propagate only through valid communication links.
- A drone can retain stale terrain knowledge while disconnected or before
  receiving an update. The plugin reports blocked movement rather than assuming
  a planned route is still valid.
- The agreed first-version renderer is a green 3D point cloud: one point per
  known solid cell. Known air has no rendered point; unknown remains unrendered.
- A reconstructed mesh, individual wireframe block cubes, and adjacency edges
  are not required for this version.
- The map must support volumetric terrain, including underground areas; a
  surface-only heightmap is not sufficient.
- The map supports orbit and zoom controls plus a height-cutoff slider that
  hides terrain above a selected Y level to reveal underground areas.

## Implementation and Validation Obligations

- Keep hidden target ground truth outside planner inputs and public DTOs.
  Prove disconnected discoveries, recall, and debug subscriptions cannot leak
  knowledge into another drone or the tower.
- Verify cube boundaries, negative-coordinate flooring, endpoint-excluded
  conservative line of sight, and one-hop-per-cycle propagation.
- Verify retry idempotency, atomic invalid-frame rejection, unavailable entries,
  failed/stalled commands, explicit reset, and stale mission rejection.
- Exercise paused ticks, HTTP loss/resume, changed terrain, blocked routes,
  isolation/transit behaviour, and incomplete recovery without false completion.
- Check snapshots/deltas and reconnect/reset behaviour in an actual browser.
- Measure 50-drone throughput and desktop frame rate against the agreed targets.
- Rehearse Azure and private-hotspot connectivity with the actual plugin on
  the two demo computers.
- Confirm Azure region/SKU availability and estimated event cost before
  provisioning. No deployment has been authorised at an unverified price.
- If time forces scope changes, obtain agreement rather than silently dropping
  recovery, knowledge isolation, or backup hosting.

## Discovery Status

The decisions above were captured during interactive discovery on 2026-11-14.
The version-one wire contract is agreed; share it with the plugin teammate.
An initial ASP.NET Core API and React/Three.js dashboard are implemented. The
backend provides in-memory mission state, authenticated plugin endpoints,
surface-following local-frontier planning, periodic map-upload trips,
one-hop-per-cycle multi-hop knowledge relay, isolation rendezvous, tower-assigned
bounded missing-peer rescue, target reporting/recall, recovery-incomplete
reporting, and selected-drone SignalR diagnostics. The Minecraft plugin,
end-to-end integration, deployment, and performance validation remain
outstanding.
