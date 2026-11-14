import assert from "node:assert/strict";
import { randomUUID } from "node:crypto";
import { parseArgs } from "node:util";

const { values } = parseArgs({
  options: {
    url: { type: "string", default: "http://127.0.0.1:5187" },
    warmup: { type: "string", default: "400" },
    reset: { type: "boolean", default: false },
  },
});
const warmup = Number(values.warmup);
assert(Number.isSafeInteger(warmup) && warmup >= 0, "--warmup must be a non-negative integer.");
const pluginKey = process.env.PLUGIN_KEY;
assert(pluginKey, "Set PLUGIN_KEY to the same key used by the backend.");
const baseUrl = values.url.replace(/\/$/, "");
const missionId = randomUUID();
const boundsMin = { x: -64, y: 0, z: -64 };
const tower = { x: 0, y: 20, z: 0 };
const drones = [[0, 0], [2, 0], [0, 2], [-2, 0], [0, -2]].map(([x, z], index) => ({
  id: `SCOUT-${String(index + 1).padStart(2, "0")}`,
  position: { x: x + 0.5, y: tower.y + 0.5, z: z + 0.5 },
  pending: null,
  result: null,
}));

function surfaceY(x, z) {
  return 18 + Math.round(1.7 * Math.sin(x / 9) + 1.2 * Math.cos(z / 11) - 1.2);
}

function cellsAt(position) {
  const cells = [];
  const cx = Math.floor(position.x), cy = Math.floor(position.y), cz = Math.floor(position.z);
  for (let x = Math.max(boundsMin.x, cx - 3); x <= Math.min(63, cx + 3); x++)
    for (let y = Math.max(boundsMin.y, cy - 3); y <= Math.min(63, cy + 3); y++)
      for (let z = Math.max(boundsMin.z, cz - 3); z <= Math.min(63, cz + 3); z++)
        cells.push({ x, y, z, solid: y <= surfaceY(x, z) });
  return cells;
}

function frame(sequence) {
  return {
    sequence,
    gameTick: sequence * 10,
    drones: drones.map((drone) => ({
      droneId: drone.id,
      availability: "available",
      position: drone.position,
      cells: cellsAt(drone.position),
      commandResult: drone.result,
    })),
  };
}

async function request(path, method = "GET", body) {
  const response = await fetch(`${baseUrl}${path}`, {
    method,
    headers: { "Content-Type": "application/json", "X-Plugin-Key": pluginKey },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await response.text();
  if (!response.ok) throw new Error(`${method} ${path}: HTTP ${response.status}: ${text}`);
  return text ? JSON.parse(text) : null;
}

async function main() {
  const status = await request("/api/plugin/status");
  if (status.activeMissionId) {
    assert(values.reset, "An active mission exists. Use --reset to explicitly replace it.");
    await request(`/api/plugin/missions/${status.activeMissionId}`, "DELETE");
  }
  await request("/api/plugin/missions", "POST", {
    protocolVersion: 1,
    missionId,
    worldId: "rolling-ground-demo:overworld",
    boundsMin,
    tower,
    target: { x: 56, y: surfaceY(56, 56) + 1, z: 56 },
    initialFrame: frame(0),
  });
  console.log(`Mission ${missionId}: five surface-following scouts over a 128x128 rolling landscape.`);
  console.log(`Warming up ${warmup} observation frames, then continuing at two frames per second.`);

  for (let sequence = 1; ; sequence++) {
    if (sequence > warmup) await new Promise((resolve) => setTimeout(resolve, 500));
    for (const drone of drones) {
      drone.result = null;
      if (!drone.pending) continue;
      const { destination, commandId } = drone.pending;
      const from = drone.position;
      assert.equal(
        Math.abs(destination.x - Math.floor(from.x)) +
        Math.abs(destination.y - Math.floor(from.y)) +
        Math.abs(destination.z - Math.floor(from.z)),
        1, "The planner must issue a face-adjacent move.",
      );
      assert(destination.y > surfaceY(destination.x, destination.z), "The planner commanded a solid cell.");
      drone.position = { x: destination.x + 0.5, y: destination.y + 0.5, z: destination.z + 0.5 };
      drone.result = { commandId, status: "completed", reason: null };
    }

    const accepted = await request(`/api/plugin/missions/${missionId}/observations`, "POST", frame(sequence));
    const occupied = new Set(drones.map(({ position }) =>
      `${Math.floor(position.x)},${Math.floor(position.y)},${Math.floor(position.z)}`));
    const reserved = new Set();
    for (const command of accepted.commands) {
      const { x, y, z } = command.destination;
      const destination = `${x},${y},${z}`;
      assert(!occupied.has(destination), "The planner commanded a move into an occupied scout cell.");
      assert(!reserved.has(destination), "The planner assigned the same destination to multiple scouts.");
      reserved.add(destination);
    }
    for (const drone of drones) {
      if (drone.result)
        assert(accepted.acknowledgedResults.some((ack) =>
          ack.droneId === drone.id && ack.commandId === drone.result.commandId), "Missing movement acknowledgement.");
      drone.pending = accepted.commands.find((command) => command.droneId === drone.id) ?? null;
    }
    if (sequence % 25 === 0 || sequence === warmup) {
      const snapshot = await request("/api/public/snapshot");
      const returning = snapshot.drones.filter((drone) => drone.behavior === "returningToUpload").length;
      const columns = new Set(snapshot.terrain.map((cell) => `${cell.x},${cell.z}`)).size;
      console.log(`frame=${sequence} phase=${snapshot.phase} sharedSolids=${snapshot.terrain.length} groundColumns=${columns} uploading=${returning}`);
    }
  }
}

main().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
