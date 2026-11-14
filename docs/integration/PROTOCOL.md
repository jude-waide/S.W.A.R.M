# Minecraft Plugin Protocol v1

Status: agreed version-one baseline from discovery on 2026-11-14.
The domain rules are captured in [CONTEXT.md](../../CONTEXT.md). Share this
contract with the plugin teammate before implementation; changes require
coordination on both sides.

## Transport and Authentication

- One ASP.NET Core app, JSON over HTTP. Azure requires HTTPS.
- HTTP is permitted only on the team's trusted private hotspot with a separate
  short-lived local key, not an untrusted venue network.
- Plugin endpoints require `X-Plugin-Key`. Store keys in configuration outside
  source control; never include a key in browser code or log it.
- The public dashboard and its SignalR connection are read-only.
- All JSON properties use camelCase. IDs are case-sensitive strings; mission
  and command IDs are UUIDs. Drone IDs are opaque, non-empty strings.
- Coordinates use Minecraft axes and block units, with Y vertical. Block
  coordinates are integers; drone positions are finite decimal numbers.

## Mission Lifecycle

| Method | Route | Purpose |
| ------ | ----- | ------- |
| GET | `/api/plugin/status` | Read active mission ID and lifecycle status. |
| POST | `/api/plugin/missions` | Initialise a mission with its first observation frame. |
| POST | `/api/plugin/missions/{missionId}/observations` | Submit a fresh frame or retry the latest frame. |
| DELETE | `/api/plugin/missions/{missionId}` | Explicitly reset/end that mission. |

Status returns `200` with `protocolVersion`, nullable `activeMissionId`, and a
`phase` string. Before initialisation, use `activeMissionId: null` and
`phase: "awaitingMission"`. Initialisation and accepted frames return `200` with
the accepted-frame response below. Successful reset returns `204` with no body.

Use mission IDs alone; there is no server-instance guard. After a restart, the
plugin must arrange a fresh mission, not pretend the old in-memory mission
survived. It must not reuse mission IDs.

There is one active mission, including while its final view remains inspectable.
Multiple sequential missions can share a world ID. A world ID identifies one
world/dimension; switching dimension needs a new mission.

- Minecraft supplies a new mission UUID and fixed roster.
- Every initial drone must provide available telemetry and have a clear direct
  tower link.
- Initial locations must be inside the exploration bounds.
- Tower and target coordinates remain fixed during the mission.
- Repeat initialisation with the same ID and identical body returns the original
  result without clearing knowledge.
- A new ID cannot replace an active mission without explicit reset.
- Reset cancels the mission administratively; the plugin discards its pending
  requests/commands and ignores delayed responses for that mission.
- Reset retries are harmless while that mission remains the last reset mission;
  they must never reset a newer active mission.
- Do not reuse mission IDs. Retired IDs are rejected for the backend process
  lifetime. After restart the backend has no persisted retired-ID history, so it
  cannot distinguish a delayed old initialisation from a fresh one if no mission
  is active. This limitation is accepted by choosing mission IDs alone.

## Initialisation Body

```json
{
  "protocolVersion": 1,
  "missionId": "cd498fae-48dd-48c6-b22c-d5ea8f6629ad",
  "worldId": "demo-world:overworld",
  "boundsMin": { "x": -64, "y": 32, "z": -64 },
  "tower": { "x": 0, "y": 64, "z": 0 },
  "target": { "x": 40, "y": 48, "z": 30 },
  "initialFrame": {
    "sequence": 0,
    "gameTick": 12000,
    "drones": []
  }
}
```

The empty array illustrates structure only: a real mission requires at least one
drone with a complete available observation as defined below.

Bounds are fixed at 128x64x128 blocks. Minimum is inclusive; maximum is exclusive.
Target coordinates are private simulation ground truth and must never be
copied into a public snapshot or drone planner input.

Initialisation returns an accepted-frame response with no movement commands.
Fresh observations begin at sequence 1.

## Observation Frame

```json
{
  "sequence": 1,
  "gameTick": 12010,
  "drones": [
    {
      "droneId": 0,
      "availability": "available",
      "position": { "x": 1.5, "y": 64.5, "z": 0.5 },
      "cells": [
        { "x": 1, "y": 64, "z": 0, "solid": false }
      ],
      "commandResult": null
    }
  ]
}
```

The cell list is abbreviated to illustrate its shape; sending only that cell is
invalid unless it is the entire clipped cube.

- Include every roster ID exactly once.
- For an available drone, include actual position and the entire local cube:
  floor each position component, then enumerate offsets -3 through +3 on each
  axis, clipped to mission bounds. Include both solid and air.
- All positions and cells in a frame refer to the same game tick.
- Overlapping cube observations must agree. Duplicate cells, missing cells,
  invalid positions, or inconsistent observations reject the entire frame.
- Retain the source game tick when sharing observations; delayed relay must not
  make old terrain appear newer than a fresh observation.
- Unknown is distinct from air. Never invent observations.
- `commandResult` is null until a terminal result is available, then has this
  shape:

```json
{
  "commandId": "32b33563-6b18-43ae-b1ed-d41b087d465a",
  "status": "blocked",
  "reason": "Destination is obstructed"
}
```

Terminal statuses are `completed`, `blocked`, and `failed`. Require a non-empty
reason for blocked/failed results. The actual position remains authoritative.
Continue reporting the result until the response acknowledges it.

- Completed: clear the outstanding move.
- Blocked: clear it, avoid that directed transition for five simulation seconds,
  and try known alternatives.
- Failed: clear it and fault that drone; no new movement until mission reset.
- Unknown/conflicting command results reject the frame. Repeating the same
  acknowledged result is harmless.

An unavailable roster member has this alternative shape:

```json
{
  "droneId": "drone-2",
  "availability": "unavailable",
  "reason": "Drone entity no longer exists"
}
```

Do not provide a fabricated position or cells. Mark that drone faulted and remove
its live links. Other valid roster entries may progress. Later telemetry may
show it again, but does not clear a fault or authorise movement before reset.

## Accepted-Frame Response

```json
{
  "protocolVersion": 1,
  "missionId": "cd498fae-48dd-48c6-b22c-d5ea8f6629ad",
  "sequence": 1,
  "gameTick": 12010,
  "acknowledgedResults": [],
  "commands": [
    {
      "droneId": 0,
      "commandId": "32b33563-6b18-43ae-b1ed-d41b087d465a",
      "kind": "move",
      "destination": { "x": 2, "y": 64, "z": 0 }
    }
  ]
}
```

Destinations identify integer block cells, not raw entity-foot coordinates.
The plugin handles physical positioning in that cell and validates execution.
The planner uses six face-neighbour known-air cells and stays inside bounds.

`acknowledgedResults` contains `{ "droneId": "...", "commandId": "..." }` entries.
One move may be outstanding per drone. Responses repeat that move's ID until a
terminal result arrives. An empty command list means no new/outstanding movement,
not a fabricated success. The plugin executes each ID at most once.

After ten simulation seconds without a terminal result, show command stalled,
retain the command identity, and issue no conflicting replacement.

## Sequencing, Clock, and Retry

- One observation request in flight, targeting a batch every 500 real ms.
- Sequence numbers are consecutive, beginning at 0 in initialisation.
- Retry exactly the same body and sequence after a timeout.
- Cache/replay the latest accepted response without another state advance.
- Older sequences, sequence gaps, and changed-body retries return conflicts.
- Discard missed intervals rather than queueing historical batches.
- Game ticks never decrease within a mission. Use 20 ticks per simulation second
  and 10 ticks per decision/relay cycle.
- A same-tick fresh frame is a heartbeat: refresh telemetry freshness, but do not
  advance behaviour or relay.
- Advance at most one decision/relay cycle per fresh frame. Never fast-forward
  decisions over unobserved gaps.
- After three real seconds without a valid fresh frame, show connection lost
  and suspend behaviour timers. Duplicate retries do not count as fresh frames.
- On resumption, rebase the tick baseline without charging the outage to timers.

## Errors

Return explicit ASP.NET Core Problem Details, with a stable machine-readable
error code and validation field errors where relevant.

| Status | Meaning |
| ------ | ------- |
| 400 | Invalid JSON, unsupported protocol version, invalid frame or setup. |
| 401 | Missing/invalid plugin key; never echo it. |
| 404 | No matching active mission; old in-memory state may have been lost. |
| 409 | Stale/conflicting sequence or command, or active mission replacement. |

An error response must not partly mutate the simulation. The plugin must not
treat an error as an empty successful command response.

## Browser Updates

SignalR serves explicitly constructed public DTOs, not serialised mission
objects. A fresh browser/reconnection receives a tower snapshot, then versioned
incremental updates. A version gap requests a fresh snapshot; a new mission
clears old points, markers, and selection.

- Public overview: tower-known terrain, connected-drone overlay, last-reported
  disconnected markers, mission milestones, and connection status.
- Selected-drone subscription: that drone's live cube, current position,
  behaviour, contacts, and report age, clearly labelled debug data.
- Target ground truth is excluded. Reveal the target in a selected drone's
  knowledge only after legitimate detection/receipt, and on the tower map only
  after a legitimate report reaches the tower.
- Inspecting or subscribing must never change simulated knowledge.
- Frozen telemetry means frozen markers, not extrapolated live motion.
