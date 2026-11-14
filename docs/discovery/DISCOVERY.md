# Discovery: Drone Swarm

_Discovery session conducted on 2026-11-14_

## Problem Statement

Demonstrate autonomous coordination under limited communication using drones in
Minecraft and a live military-green 3D web dashboard. This is a hackathon demo
for organisers, not a scientific simulator or a real-world drone system.

Drones search for a hidden, fixed target while mapping terrain. The search
objective is met only when the discovery reaches the central tower. A subsequent
recovery phase recalls drones through legitimate communication, searches for
missing peers, and returns everyone to base.

The web backend owns behaviour and simulated radio communication. A teammate's
Minecraft plugin supplies observations and executes commands.

## Users and Personas

### Demo Presenter

**Role:** Runs the Minecraft/web demonstration on two computers.
**Goals:** Show search, disconnected knowledge, relaying, target reporting, and
recovery clearly and repeatably.
**Pain Points:** Limited development time, integration failures, venue networking,
and graphics that conceal rather than explain coordination.
**Technical Level:** Not formally specified; operation must not require reading
raw JSON.

### Hack Day Organiser / Viewer

**Role:** Watches the demo or opens the public dashboard.
**Goals:** Understand what drones know, why communication matters, and whether
the mission has actually completed.
**Pain Points:** Confusing global knowledge with local knowledge or stale markers
with current positions.
**Technical Level:** No Minecraft-plugin or backend expertise assumed.

## Business Constraints

| Constraint | Details |
| ---------- | ------- |
| Timeline | Approximately 20 hackathon hours remained when discovery began. |
| Budget | Target at most GBP 5 Azure spend for the event; verify portal estimate before provisioning. |
| Integration | Plugin developed in parallel by a teammate; agreed HTTP JSON baseline must be shared. |
| Demo setup | Web application on one computer, Minecraft on another. |
| Hosting | Azure primary, private-hotspot/LAN deployment as a required backup. |
| Non-negotiables | C#/.NET backend, React frontend, isolated per-drone knowledge, meaningful communication constraints, live volumetric map. |
| Out of scope | Plugin implementation, detailed Minecraft physics, real drone control, database persistence, accounts, replay, moving targets, concurrent missions. |

No additional regulatory requirements were identified for this Minecraft demo.
Deployment still requires normal secret handling and appropriate Azure access.

## Functional Requirements

### Core Workflows

1. **Initialise:** Authenticated Minecraft request supplies a new mission ID,
   world/dimension ID, fixed drone roster, tower, hidden target, bounds minimum,
   and initial observations. Every drone starts inside bounds with a clear
   direct tower link.
2. **Observe and plan:** Plugin submits complete local cubes every 500 real ms.
   The backend plans adjacent waypoints; the plugin reports actual execution.
3. **Explore locally:** Each drone uses only its observations and information
   legitimately received through radio links. Its search is deterministic local
   frontier exploration, not global omniscient planning.
4. **Exchange knowledge:** In-range, unblocked drones exchange terrain/search
   updates, peer sightings, target reports, and recall orders one hop per cycle.
5. **Find and report:** A target must be in the local cube with clear line of
   sight. Any drone that learns the discovery stops ordinary exploration and
   helps carry/relay it towards a tower connection.
6. **Recover:** After receiving the report, the tower assigns at most one
   available rescuer per missing peer, based on tower-known information.
   Orders spread through ordinary links; unaware disconnected drones continue
   local behaviour until contacted.
7. **Inspect:** Public viewers orbit/zoom/filter the tower map and select a drone
   to see its separate live local debug view, even when disconnected.
8. **Reset:** Only the authenticated plugin resets the active mission. A fresh
   ID starts with fresh knowledge, even in the same world.

### Coordinates, Bounds, and Sensing

- Native Minecraft axes; Y is vertical; block units.
- Integer terrain/tower/target coordinates; decimal actual drone positions.
- Floor decimal coordinates, including negatives, to determine the drone block.
- Exploration volume: 128x64x128; plugin supplies inclusive minimum coordinates.
  Maximum bounds are exclusive.
- Each available drone reports every cell in its centred offsets -3 through +3
  cube, clipped to bounds: at most 343 solid/air cells.
- Unreported locations are unknown, not air.
- Tower and drones use the same local communication cube.
- Every solid cell obstructs communication and target visibility. Test a
  block-centre segment, exclude endpoint cells, and block on any intermediate
  solid cell touched, including edges/corners.
- Terrain-known and target-searched are separate: target-searched coverage
  requires actual unobstructed visibility, not merely an occupancy report.
- Terrain may change. Newer observations replace older cell states and spread
  only through permitted communication, preserving their source tick.

### Planning and Behaviour

- Initially a drone knows tower coordinates and bounds, not target coordinates
  or global terrain.
- Plan through six face-neighbour known-air cells. The plugin validates physical
  execution; do not model Minecraft flight physics in the web app.
- Choose the nearest useful reachable viewpoint, prefer greater estimated new
  visible coverage on equal-length routes, then stable drone-ID tie-breaking.
- Replan on legitimate knowledge changes without replacing unresolved movement.
- Priority: outstanding movement safety, recall/rescue, target reporting,
  isolation rendezvous, ordinary exploration.
- A disconnected drone keeps local discoveries and continues exploration.
  Sharing reduces duplicate searching but cannot eliminate independent duplicate
  work before drones meet.
- After 30 simulation seconds without successful contact during active
  exploration, seek a known peer. Pause this watchdog during known-route transit
  to the chosen frontier; display true contact age independently.
- Local peer searches inspect reachable viewpoints within offsets -7 through +7
  of the last-known peer block, clipped to bounds, for at most 15 simulation
  seconds after reaching that vicinity.
- If no peer is known/found, return home if possible. Failed routes try known
  alternatives; if home is also unreachable, stay, sense, communicate, and show
  stuck rather than using hidden terrain or teleportation.
- Do not repeat rescue assignments against an unchanged already-searched
  sighting. Legitimate new sightings may justify another attempt.

### Mission Milestones

- **Searching:** Drones explore and exchange knowledge.
- **Search exhausted:** No useful reachable search remains; not success. Remain
  inspectable and allow new legitimate knowledge to create work.
- **Target reported / recovering:** Tower has received the discovery; recall and
  recovery proceed.
- **Recovery incomplete:** Attempts ended with missing/stuck drones. Keep the
  mission active until everyone returns or Minecraft resets.
- **Complete:** Every roster member has returned directly inside the tower's
  clear 7x7x7 neighbourhood. A relay chain alone is not arrival.

Telemetry loss, paused simulation, stalled commands, and faulted drones are
explicit conditions, not fabricated successful mission outcomes.

### Dashboard

- Military-green volumetric point cloud: one point per tower-known solid cell.
  No surface-only heightmap, mesh, or wireframe-cube requirement.
- Orbit/zoom and Y-height cutoff to reveal underground terrain.
- Main map shows live connected-drone positions/links and faded last-reported
  positions for disconnected drones.
- Top bar: mission phase, HTTP connection state, and recovery counts.
- Selectable markers and drone list.
- Selected-drone debug panel: current 7x7x7 observations, actual coordinates,
  behaviour, contacts, and last-report age.
- Debug data never updates tower knowledge or another drone's memory.
- Reveal target coordinates only through legitimate detection/reporting, never
  by serialising hidden mission ground truth.
- Browser is read-only. No public Start/Pause/Reset/steering actions.

### Data Model

| Entity | Responsibility |
| ------ | -------------- |
| Mission | ID, world/dimension, fixed bounds/roster, hidden fixed target, lifecycle milestones. |
| Tower knowledge | Received terrain, searched coverage, peer reports, target report, recovery assignments. |
| Drone knowledge | Own and legitimately received cells, searched coverage, peer sightings, route history, target/recall knowledge. |
| Observation frame | Sequence, simulation tick, exactly one available/unavailable entry per roster ID. |
| Cell observation | Integer coordinate, solid/air state, original observation tick. |
| Movement command | Unique ID, drone ID, adjacent destination, outstanding/result state. |
| Recovery assignment | Tower-issued missing-peer task based on a legitimate last-known sighting. |

### Edge Cases

- **Retry/out-of-order traffic:** Identical latest request replays its response;
  conflicting/old/gapped sequence numbers are explicit errors.
- **Invalid batch:** Atomic rejection with no partial decision/state advance.
- **Unavailable entity:** Explicit roster error entry, no invented observations;
  fault it, remove live links, preserve last-known marker, continue other drones.
- **Blocked move:** Avoid that directed transition for five simulation seconds,
  use known alternatives, then reconsider after fresh observations/cooldown.
- **Unexpected execution failure:** Fault the drone; no new movement until reset.
- **Unresolved move:** After ten simulation seconds, show stalled and retain the
  command identity, not a conflicting replacement.
- **Radio disconnection:** Local behaviour continues without global discoveries
  or recall magically appearing.
- **HTTP loss:** After three real seconds without fresh valid telemetry, freeze
  visuals and suspend behaviour timers. Resume without charging the outage or
  replaying missed cycles.
- **Paused game:** Same-tick heartbeats refresh HTTP freshness but do not advance
  simulation behaviour.
- **Restart:** In-memory state disappears. New mission required; there is no
  server-instance guard or persisted retired-ID history.

## Non-Functional Requirements

| Requirement | Target | Notes |
| ----------- | ------ | ----- |
| Updates | One batched plugin request per 500 real ms | One request in flight; no historical catch-up queue. |
| Simulation time | 20 ticks/second, 10 ticks/cycle | At most one cycle per fresh frame. |
| Backend performance | Keep up with 500 ms cycles at 50 drones | Validation target, not a hard roster cap. |
| Rendering | Target 30 FPS on demo desktop at full bounds | Must measure with actual data/hardware. |
| Availability | Hackathon demonstration with local backup | No production SLA. |
| Storage | One in-memory active mission | Fresh state after restart/reset; sequential missions per world. |
| Security | Secret-authenticated plugin writes; public read-only dashboard | Exclude hidden target and secrets from public DTOs. |
| Accessibility | Clear text statuses and selectable drone list | Do not communicate stale/error state solely through green shading. |
| Operability | Explicit validation, connectivity, movement and recovery failures | Never substitute invented success for errors. |

## Integration Points

| System | Direction | Protocol | Auth | Notes |
| ------ | --------- | -------- | ---- | ----- |
| Minecraft plugin | Both | HTTP JSON | Shared plugin key | Complete observations in; commands/results acknowledgements out. |
| React dashboard | Both, read-only subscriptions | SignalR plus snapshot/resync | Public | Versioned updates, fresh snapshot after reconnect/gap. |
| Azure App Service | Hosting | HTTPS | Azure deployment access | Single instance; verify event cost first. |
| Local fallback | Hosting | HTTP on trusted private hotspot | Separate short-lived plugin key | Configurable plugin backend URL; never untrusted venue HTTP. |

The agreed endpoint names, request/response shapes, lifecycle, errors, and
acknowledgements are in [PROTOCOL.md](../integration/PROTOCOL.md).

## Considered Alternatives

| Alternative | Outcome / Trade-off |
| ----------- | ------------------- |
| Literal JSON files / shared folder | Rejected in favour of HTTP JSON across two computers. |
| Plugin WebSocket connection | HTTP request/response chosen for simpler integration; browser uses SignalR separately. |
| Global shared drone map | Rejected: would make communication constraints cosmetic. |
| Instant connected-component sharing | Rejected: one-hop-per-cycle makes propagation meaningful and visible. |
| Surface heightmap or block wireframe cubes | Rejected: volumetric point cloud supports underground observations with simpler rendering. |
| Mesh reconstruction | Not required after choosing the points-only first version. |
| Database, saved missions, replay | Not required for the single-active-mission demo. |
| Public mission-control actions | Rejected: public dashboard is read-only. |
| Local-only hosting | Retained as backup; Azure is primary. |
| Server-instance restart guard | Explicitly declined; mission IDs alone accepted with restart limitation. |

## Open Questions

No blocking product or version-one interface decision remains from this
discovery. The following are implementation/validation gates, not claims that
the system already works:

- Share the agreed baseline with the plugin teammate and test actual adherence.
- Confirm Azure region/plan availability and cost within the event budget before
  provisioning. No unverified paid deployment is approved.
- Validate the throughput/rendering targets, knowledge isolation, atomicity,
  reconnect handling, and end-to-end search/report/recovery.
- Rehearse both Azure and private-hotspot modes on the two actual machines.

Scope changes, contract changes, or extra spend require further agreement.

## Key Decisions

| Decision | Rationale | ADR |
| -------- | --------- | --- |
| Backend owns behaviour; plugin owns physical execution | Clear parallel implementation boundary. | - |
| Separate per-drone/tower knowledge | Communication limits must affect decisions. | - |
| Cube sensing plus blocked one-hop communication | Matches the agreed local-data contract. | - |
| Target report followed by recovery | Demonstrates coordination beyond target detection. | - |
| Public read-only UI with isolated live debug | Explain disconnected behaviour without cheating. | - |
| In-memory, single instance, sequential missions | Fits the time budget and avoids distributed state. | - |
| Azure primary plus private-hotspot backup | Avoid venue LAN reliance while preserving a fallback. | - |

## Technology Stack

**Recommended and agreed stack:** React/.NET hybrid.

**Rationale:** C# controls simulation and HTTP integration; React and Three.js
support interactive volumetric visualisation; one deployable application keeps
hosting and the local backup straightforward.

| Component | Technology | Notes |
| --------- | ---------- | ----- |
| Backend | ASP.NET Core, C#, .NET 10 LTS | SDK already available locally. |
| Frontend | React, TypeScript, Vite | Built assets served by ASP.NET Core. |
| 3D | Three.js through React Three Fiber | Point cloud, markers, links, height cutoff. |
| Live updates | ASP.NET Core SignalR | Snapshot plus versioned incremental updates. |
| Storage | Backend memory | No database/Redis/separate Azure SignalR service. |
| Cloud | One Azure App Service instance | Small paid plan with Always On, subject to cost confirmation. |

## Status

Discovery and the protocol baseline are documented. An initial ASP.NET Core
backend and React/Three.js dashboard now implement in-memory mission state,
authenticated plugin endpoints, surface-following frontier planning with
periodic map-upload trips, live snapshots, and
the public read-only map. One-hop-per-cycle multi-hop relay, isolation
rendezvous, tower-assigned bounded missing-peer rescue, target reporting and
recall, recovery status reporting, and selected-drone live diagnostics are
implemented. The Minecraft plugin integration, deployment, and performance
verification remain outstanding. See the [README](../../README.md) for local
run instructions and [CONTEXT.md](../../CONTEXT.md) for the domain reference.
