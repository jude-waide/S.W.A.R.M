import assert from "node:assert/strict";
import { test } from "node:test";
import * as THREE from "three";
import { getOverviewCamera } from "../src/mapCamera.ts";

for (const { name, min, max, aspect } of [
  { name: "flat terrain on a phone", min: [-40, -3, -40], max: [40, 3, 40], aspect: 390 / 844 },
  { name: "flat terrain on a desktop", min: [-40, -3, -40], max: [40, 3, 40], aspect: 1600 / 1000 },
  { name: "tall, off-centre terrain", min: [20, -40, 5], max: [30, 60, 15], aspect: 1600 / 1000 },
]) {
  test(`tightly frames ${name} without clipping`, () => {
    const bounds = new THREE.Box3(new THREE.Vector3(...min), new THREE.Vector3(...max));
    const points: THREE.Vector3[] = [];
    for (const x of [bounds.min.x, bounds.max.x])
      for (const y of [bounds.min.y, bounds.max.y])
        for (const z of [bounds.min.z, bounds.max.z]) points.push(new THREE.Vector3(x, y, z));
    const { position, target } = getOverviewCamera(points, 44, aspect);
    const camera = new THREE.PerspectiveCamera(44, aspect, 0.1, 2000);
    camera.position.copy(position);
    camera.lookAt(target);
    camera.updateMatrixWorld();
    let furthestEdge = 0;
    for (const x of [bounds.min.x, bounds.max.x])
      for (const y of [bounds.min.y, bounds.max.y])
        for (const z of [bounds.min.z, bounds.max.z]) {
          const point = new THREE.Vector3(x, y, z).project(camera);
          assert.ok(point.z > -1 && point.z < 1, "terrain must be in front of the camera");
          furthestEdge = Math.max(furthestEdge, Math.abs(point.x), Math.abs(point.y));
        }
    assert.ok(furthestEdge >= 0.85, `terrain only fills ${(furthestEdge * 100).toFixed(1)}% of the limiting axis`);
    assert.ok(furthestEdge <= 0.95, "leave a small margin around terrain");
  });
}

test("fits sparse terrain itself rather than the empty corners of its bounding box", () => {
  const points = [
    new THREE.Vector3(-40, 0, 0),
    new THREE.Vector3(40, 0, 0),
    new THREE.Vector3(0, 0, -40),
    new THREE.Vector3(0, 0, 40),
  ];
  const aspect = 390 / 844;
  const { position, target } = getOverviewCamera(points, 44, aspect);
  const camera = new THREE.PerspectiveCamera(44, aspect, 0.1, 2000);
  camera.position.copy(position);
  camera.lookAt(target);
  camera.updateMatrixWorld();
  const extent = Math.max(...points.map((point) => {
    const projected = point.clone().project(camera);
    return Math.max(Math.abs(projected.x), Math.abs(projected.y));
  }));
  assert.ok(extent >= 0.85 && extent <= 0.95, `sparse terrain fills ${(extent * 100).toFixed(1)}%`);
});
