import assert from "node:assert/strict";
import { test } from "node:test";
import { getMapData, getMapDrones } from "../src/mapDrones.ts";
import type { DroneDebug, PublicDrone, Snapshot } from "../src/types.ts";

const scout: PublicDrone = {
  id: "SCOUT-01",
  position: { x: 0.5, y: 20.5, z: 0.5 },
  positionCurrent: false,
  livePosition: { x: 30.5, y: 21.5, z: -12.5 },
  livePositionCurrent: true,
  connectedToTower: false,
  directlyConnectedToTower: false,
  faulted: false,
  behavior: "exploring",
  knownCellCount: 0,
  lastSeenTick: 0,
  lastReportAgeSeconds: 10,
  connectedPeers: [],
};

test("hive view retains last-reported positions for isolated scouts", () => {
  const [marker] = getMapDrones([scout], "hive");
  assert.deepEqual(marker.position, scout.position);
  assert.equal(marker.positionCurrent, false);
});

test("overall view uses live positions even without tower knowledge, without mutating hive data", () => {
  const unseen = { ...scout, id: "SCOUT-02", position: null };
  const markers = getMapDrones([scout, unseen], "overall");
  for (const marker of markers) {
    assert.deepEqual(marker.position, scout.livePosition);
    assert.equal(marker.positionCurrent, true);
    assert.equal(marker.connectedToTower, false);
  }
  assert.equal(scout.position!.x, 0.5);
  assert.equal(unseen.position, null);
});

test("overall view preserves stale/fault status when live telemetry is unavailable", () => {
  const [marker] = getMapDrones([{ ...scout, faulted: true, livePositionCurrent: false }], "overall");
  assert.deepEqual(marker.position, scout.livePosition);
  assert.equal(marker.positionCurrent, false);
  assert.equal(marker.faulted, true);
});

const snapshot: Snapshot = {
  protocolVersion: 1, missionId: "mission-a", version: 1, phase: "searching",
  connectionState: "connected", worldId: "test", tower: { x: 0, y: 20, z: 0 },
  targetReported: true, reportedTarget: { x: 99, y: 20, z: 99 },
  overallTarget: { x: 99, y: 20, z: 99 },
  terrain: [{ x: 90, y: 19, z: 90, sourceTick: 20 }],
  liveTerrain: [{ x: 91, y: 19, z: 91, sourceTick: 20 }],
  drones: [scout, { ...scout, id: "SCOUT-02" }],
  recoveredCount: 0, missingDroneIds: [], gameTick: 20,
};
const debug: DroneDebug = {
  missionId: "mission-a", droneId: scout.id, behavior: "exploring",
  position: scout.livePosition, lastSeenTick: 20, gameTick: 20,
  lastReportAgeSeconds: 0, lastContactAgeSeconds: 10,
  recoveryTargetId: null, rendezvousTargetId: null, connectedPeers: ["SCOUT-02"],
  cells: [
    { x: 30, y: 20, z: -12, solid: true, sourceTick: 20 },
    { x: 30, y: 21, z: -12, solid: false, sourceTick: 20 },
  ],
  knownTarget: null,
  frontierMovesSinceUpload: 0,
};

test("overall view shows the true target before discovery without revealing it in hive or scout views", () => {
  const searching = { ...snapshot, targetReported: false, reportedTarget: null };
  assert.deepEqual(getMapData(searching, "overall", scout.id, debug).reportedTarget, snapshot.overallTarget);
  assert.equal(getMapData(searching, "hive", scout.id, debug).reportedTarget, null);
  assert.equal(getMapData(searching, "scout", scout.id, debug).reportedTarget, null);
  assert.equal(searching.targetReported, false);
  assert.equal(debug.knownTarget, null);
});

test("scout view shows only the selected unit's solid knowledge, own position and known target", () => {
  const data = getMapData(snapshot, "scout", scout.id, debug);
  assert.deepEqual(data.terrain, [debug.cells[0]]);
  assert.equal(data.reportedTarget, null);
  assert.equal(data.drones.length, 1);
  assert.deepEqual(data.drones[0].position, debug.position);
  assert.equal(data.drones[0].positionCurrent, true);
  assert.deepEqual(data.drones[0].connectedPeers, []);
  const target = { x: 31, y: 21, z: -12 };
  assert.deepEqual(getMapData(snapshot, "scout", scout.id, { ...debug, knownTarget: target }).reportedTarget, target);
});

test("scout view never falls back to global knowledge while loading or switching unit/mission", () => {
  for (const stale of [null, { ...debug, missionId: "old" }, { ...debug, droneId: "SCOUT-02" }]) {
    assert.deepEqual(getMapData(snapshot, "scout", scout.id, stale),
      { terrain: [], drones: [], reportedTarget: null });
  }
  assert.deepEqual(getMapData(snapshot, "scout", null, debug),
    { terrain: [], drones: [], reportedTarget: null });
  const stalePosition = getMapData(snapshot, "scout", scout.id, { ...debug, gameTick: -10 });
  assert.equal(stalePosition.drones[0].positionCurrent, false);
});

test("hive and overall terrain remain distinct when scout debug data is present", () => {
  assert.deepEqual(getMapData(snapshot, "hive", scout.id, debug).terrain, snapshot.terrain);
  assert.deepEqual(getMapData(snapshot, "overall", scout.id, debug).terrain, snapshot.liveTerrain);
});
