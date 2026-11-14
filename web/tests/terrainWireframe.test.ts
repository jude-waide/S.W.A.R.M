import assert from "node:assert/strict";
import { test } from "node:test";
import { getTerrainEdges } from "../src/terrainWireframe.ts";

test("a voxel has twelve unique edges including all four vertical corners", () => {
  const edges = getTerrainEdges([{ x: 0, y: 4, z: 0 }]);
  assert.equal(edges.length, 12);
  const vertical = edges.filter(({ start, end }) => start.y !== end.y);
  assert.equal(vertical.length, 4);
  for (const { start, end } of vertical) {
    assert.equal(start.x, end.x);
    assert.equal(start.z, end.z);
    assert.deepEqual([start.y, end.y].sort(), [4, 5]);
  }
  assert.equal(new Set(edges.map((edge) => JSON.stringify(edge))).size, edges.length);
});

test("adjacent voxels have corner connections but no interior coplanar seams", () => {
  const cells = [{ x: 0, y: 4, z: 0 }, { x: 1, y: 4, z: 0 }];
  const edges = getTerrainEdges(cells);
  assert.equal(edges.length, 16);
  assert.equal(edges.filter(({ start, end }) => start.x === 0.5 && end.x === 0.5).length, 0);
  assert.deepEqual(getTerrainEdges([...cells, cells[0]]), edges);
});

test("stepped columns keep vertical corners and never bridge unobserved gaps", () => {
  const edges = getTerrainEdges([
    { x: 0, y: 4, z: 0 }, { x: 1, y: 4, z: 0 }, { x: 1, y: 5, z: 0 },
    { x: 1, y: 8, z: 0 },
  ]);
  assert.ok(edges.some(({ start, end }) =>
    start.x === 0.5 && end.x === 0.5 && start.y !== end.y &&
    Math.min(start.y, end.y) === 5 && Math.max(start.y, end.y) === 6));
  for (const { start, end } of edges) {
    assert.equal(Math.abs(start.x - end.x) + Math.abs(start.y - end.y) + Math.abs(start.z - end.z), 1);
    assert.ok(!(start.y > 6 && start.y < 8));
    assert.ok(!(end.y > 6 && end.y < 8));
  }
});
