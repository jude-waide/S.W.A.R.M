import * as THREE from "three";

export function getOverviewCamera(points: readonly THREE.Vector3[], fov: number, aspect: number) {
  const bounds = new THREE.Box3();
  for (const point of points) bounds.expandByPoint(point);
  const target = bounds.getCenter(new THREE.Vector3());
  const direction = new THREE.Vector3(18, 14, 18).normalize();
  const right = new THREE.Vector3().crossVectors(new THREE.Vector3(0, 1, 0), direction).normalize();
  const up = new THREE.Vector3().crossVectors(direction, right);
  const verticalSlope = Math.tan(THREE.MathUtils.degToRad(fov) / 2) * 0.9;
  const horizontalSlope = verticalSlope * aspect;
  const offset = new THREE.Vector3();
  let distance = 1;
  for (const point of points) {
    offset.copy(point).sub(target);
    const depth = offset.dot(direction);
    distance = Math.max(
      distance,
      depth + Math.abs(offset.dot(right)) / horizontalSlope,
      depth + Math.abs(offset.dot(up)) / verticalSlope,
    );
  }
  const position = target.clone().addScaledVector(direction, distance);
  return { position, target };
}
