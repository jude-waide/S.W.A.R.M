import assert from "node:assert/strict";
import { test } from "node:test";
import { mergeMotion, receivePosition, samplePosition } from "../src/motion.ts";
import type { MotionSnapshot, Snapshot } from "../src/types.ts";

test("lightweight motion changes observer positions, never hive knowledge or terrain references", () => {
  const known = { x: 1, y: 2, z: 3 };
  const live = { x: 8, y: 2, z: 3 };
  const snapshot: Snapshot = {
    protocolVersion: 1, phase: "searching", worldId: "test", tower: null, targetFound: false,
    targetReported: false, reportedTarget: null, overallTarget: null, recoveredCount: 0, missingDroneIds: [],
    missionId: "mission", version: 1, gameTick: 10, connectionState: "connected",
    terrain: [], liveTerrain: [],
    drones: [{ id: "A", position: known, positionCurrent: true, connectedToTower: true,
      directlyConnectedToTower: true, connectedPeers: [], behavior: "moving", knownCellCount: 0,
      lastSeenTick: 10, lastReportAgeSeconds: 0,
      livePosition: known, livePositionCurrent: true, faulted: false }],
  };
  const motion: MotionSnapshot = { missionId: "mission", version: 2, gameTick: 12,
    drones: [{ id: "A", position: live, positionCurrent: true, faulted: false }] };
  const next = mergeMotion(snapshot, motion)!;
  assert.equal(next.drones[0].position, known);
  assert.equal(next.drones[0].livePosition, live);
  assert.equal(next.terrain, snapshot.terrain);
  assert.equal(next.liveTerrain, snapshot.liveTerrain);
  assert.equal(mergeMotion(next, motion), next);
  assert.equal(mergeMotion(snapshot, { ...motion, version: 3 }), null);
  assert.equal(mergeMotion(snapshot, { ...motion, missionId: "other" }), null);
  assert.equal(mergeMotion(next, { ...motion, version: 3, drones: [{ ...motion.drones[0], faulted: true }] })!
    .drones[0].positionCurrent, false);
});

test("received positions interpolate without extrapolation and freeze immediately when stale", () => {
  const a = { x: 0, y: 2, z: 3 }, b = { x: 1, y: 2, z: 3 };
  let track = receivePosition(null, a, 10, 0, true);
  track = receivePosition(track, b, 12, 100, true);
  assert.deepEqual(samplePosition(track, 150), { x: 0.5, y: 2, z: 3 });
  assert.deepEqual(samplePosition(track, 5000), b);
  const stale = receivePosition(track, b, 12, 160, false);
  assert.deepEqual(samplePosition(stale, 160), b);
  const resumed = receivePosition(stale, a, 14, 200, true);
  assert.deepEqual(samplePosition(resumed, 200), a);
  const teleport = receivePosition(track, { ...b, x: 40 }, 14, 200, true);
  assert.deepEqual(samplePosition(teleport, 200), { ...b, x: 40 });
});
