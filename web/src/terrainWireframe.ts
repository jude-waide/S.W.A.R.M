import type { Point } from "./types";

type Edge = { start: Point; end: Point };

const key = (point: Point) => `${point.x},${point.y},${point.z}`;

export function getTerrainGeometry(cells: Point[]): { edges: Edge[]; faces: Point[] } {
  const solids = new Set(cells.map(key));
  const edges = new Map<string, Edge & { normals: Set<number>; faces: number }>();
  const triangles: Point[] = [];
  const faces = [
    { offset: [-1, 0, 0], corners: [0, 1, 3, 2] },
    { offset: [1, 0, 0], corners: [4, 6, 7, 5] },
    { offset: [0, -1, 0], corners: [0, 4, 5, 1] },
    { offset: [0, 1, 0], corners: [2, 3, 7, 6] },
    { offset: [0, 0, -1], corners: [0, 2, 6, 4] },
    { offset: [0, 0, 1], corners: [1, 5, 7, 3] },
  ];

  for (const cellKey of solids) {
    const [x, y, z] = cellKey.split(",").map(Number);
    const corners: Point[] = [];
    for (const dx of [-0.5, 0.5])
      for (const dy of [0, 1])
        for (const dz of [-0.5, 0.5]) corners.push({ x: x + dx, y: y + dy, z: z + dz });
    faces.forEach((face, normal) => {
      const [dx, dy, dz] = face.offset;
      if (solids.has(`${x + dx},${y + dy},${z + dz}`)) return;
      const [a, b, c, d] = face.corners.map((index) => corners[index]);
      triangles.push(a, b, c, a, c, d);
      for (let index = 0; index < 4; index++) {
        let start = corners[face.corners[index]];
        let end = corners[face.corners[(index + 1) % 4]];
        if (key(start) > key(end)) [start, end] = [end, start];
        const edgeKey = `${key(start)}:${key(end)}`;
        const existing = edges.get(edgeKey);
        if (existing) {
          existing.normals.add(normal);
          existing.faces++;
        } else {
          edges.set(edgeKey, { start, end, normals: new Set([normal]), faces: 1 });
        }
      }
    });
  }
  // Keep boundaries and corners, not the seams between coplanar voxel faces.
  return {
    edges: [...edges.values()]
      .filter((edge) => edge.faces === 1 || edge.normals.size > 1)
      .map(({ start, end }) => ({ start, end })),
    faces: triangles,
  };
}

export function getTerrainEdges(cells: Point[]): Edge[] {
  return getTerrainGeometry(cells).edges;
}
