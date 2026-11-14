import assert from "node:assert/strict";
import { test } from "node:test";
import { getMapDrones } from "../src/mapDrones.ts";
import type { PublicDrone } from "../src/types.ts";

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

test("nest view retains last-reported positions for isolated scouts", () => {
  const [marker] = getMapDrones([scout], "nest");
  assert.deepEqual(marker.position, scout.position);
  assert.equal(marker.positionCurrent, false);
});

test("overall view uses live positions even without tower knowledge, without mutating nest data", () => {
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
