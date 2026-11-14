import { Edges, Html, OrbitControls } from "@react-three/drei";
import { Canvas, useThree } from "@react-three/fiber";
import { useEffect, useMemo, useRef, useState, type ComponentRef, type ReactNode } from "react";
import * as THREE from "three";
import type { Point, PublicDrone, Snapshot } from "./types";
import { getOverviewCamera } from "./mapCamera";
import { getMapDrones } from "./mapDrones";

type Props = {
  snapshot: Snapshot;
  heightLimit: number;
  selectedId: string | null;
  focusRequest: number;
  viewMode: "nest" | "overall";
  onSelect: (id: string) => void;
};

type SurfaceEdge = {
  height: number;
  startX: number;
  startZ: number;
  endX: number;
  endZ: number;
};

export default function MapScene({ snapshot, heightLimit, selectedId, focusRequest, viewMode, onSelect }: Props) {
  const [nestMesh, setNestMesh] = useState<THREE.Mesh | null>(null);
  useEffect(() => {
    const timer = window.setTimeout(() => window.dispatchEvent(new Event("resize")), 50);
    return () => window.clearTimeout(timer);
  }, []);

  const origin = snapshot.tower ?? { x: 0, y: 0, z: 0 };
  const drones = useMemo(() => getMapDrones(snapshot.drones, viewMode), [snapshot.drones, viewMode]);
  const selectedDrone = drones.find((drone) => drone.id === selectedId);
  const selectedPosition = selectedDrone?.positionCurrent ? selectedDrone.position : null;
  const towerRangeCircle = useMemo(() => {
    const positions = new Float32Array(96 * 3);
    for (let index = 0; index < 96; index++) {
      const angle = index * Math.PI * 2 / 96;
      positions[index * 3] = 3 * Math.cos(angle);
      positions[index * 3 + 2] = 3 * Math.sin(angle);
    }
    return positions;
  }, []);
  const terrain = viewMode === "overall" ? snapshot.liveTerrain : snapshot.terrain;
  const visibleTerrain = useMemo(
    () => terrain.filter((cell) => cell.y <= heightLimit),
    [terrain, heightLimit],
  );
  const surfaceEdges = useMemo(() => {
    const solidCells = new Set(visibleTerrain.map((cell) => `${cell.x},${cell.y},${cell.z}`));
    const topSurfaces = new Set<string>();

    for (const cell of visibleTerrain) {
      if (!solidCells.has(`${cell.x},${cell.y + 1},${cell.z}`))
        topSurfaces.add(`${cell.y},${cell.x},${cell.z}`);
    }

    const edges: SurfaceEdge[] = [];
    for (const cell of visibleTerrain) {
      if (!topSurfaces.has(`${cell.y},${cell.x},${cell.z}`)) continue;
      const x0 = cell.x - 0.5;
      const x1 = cell.x + 0.5;
      const z0 = cell.z - 0.5;
      const z1 = cell.z + 0.5;
      if (!topSurfaces.has(`${cell.y},${cell.x - 1},${cell.z}`))
        edges.push({ height: cell.y, startX: x0, startZ: z0, endX: x0, endZ: z1 });
      if (!topSurfaces.has(`${cell.y},${cell.x + 1},${cell.z}`))
        edges.push({ height: cell.y, startX: x1, startZ: z0, endX: x1, endZ: z1 });
      if (!topSurfaces.has(`${cell.y},${cell.x},${cell.z - 1}`))
        edges.push({ height: cell.y, startX: x0, startZ: z0, endX: x1, endZ: z0 });
      if (!topSurfaces.has(`${cell.y},${cell.x},${cell.z + 1}`))
        edges.push({ height: cell.y, startX: x0, startZ: z1, endX: x1, endZ: z1 });
    }
    return edges;
  }, [visibleTerrain]);
  const edgePositions = useMemo(() => {
    const positions = new Float32Array(surfaceEdges.length * 6);
    let offset = 0;
    for (const edge of surfaceEdges) {
      positions[offset++] = edge.startX - origin.x;
      positions[offset++] = edge.height + 1 - origin.y + 0.02;
      positions[offset++] = edge.startZ - origin.z;
      positions[offset++] = edge.endX - origin.x;
      positions[offset++] = edge.height + 1 - origin.y + 0.02;
      positions[offset++] = edge.endZ - origin.z;
    }
    return positions;
  }, [surfaceEdges, origin.x, origin.y, origin.z]);
  const overviewPoints = useMemo(() => {
    const points: THREE.Vector3[] = [];
    for (const x of [-3, 3])
      for (const y of [-3, 3])
        for (const z of [-3, 3]) points.push(new THREE.Vector3(x, y, z));
    for (let index = 0; index < edgePositions.length; index += 3)
      points.push(new THREE.Vector3(edgePositions[index], edgePositions[index + 1], edgePositions[index + 2]));
    for (const point of [...drones.map((drone) => drone.position), snapshot.reportedTarget]) {
      if (!point) continue;
      for (const dx of [-1, 1])
        for (const dy of [-1, 1])
          for (const dz of [-1, 1])
            points.push(new THREE.Vector3(
              point.x - origin.x + dx,
              point.y - origin.y + dy,
              point.z - origin.z + dz,
            ));
    }
    return points;
  }, [edgePositions, drones, snapshot.reportedTarget, origin.x, origin.y, origin.z]);

  return (
    <Canvas
      camera={{ position: [18, 14, 18], rotation: [-0.53, Math.PI / 4, 0], fov: 44 }}
      dpr={[1, 1.6]}
    >
      <color attach="background" args={["#100d08"]} />
      <fog attach="fog" args={["#100d08", 180, 600]} />
      <ambientLight intensity={1.3} />
      <directionalLight position={[20, 30, 12]} intensity={2.2} color="#ffe4b2" />
      {edgePositions.length > 0 && (
        <lineSegments>
          <bufferGeometry>
            <bufferAttribute attach="attributes-position" args={[edgePositions, 3]} />
          </bufferGeometry>
          <lineBasicMaterial color="#ffffff" transparent opacity={0.85} toneMapped={false} />
        </lineSegments>
      )}
      {snapshot.tower && (
        <group>
          <mesh ref={setNestMesh}>
            <cylinderGeometry args={[1.2, 1.2, 2.6, 6]} />
            <meshBasicMaterial transparent opacity={0} depthWrite={false} side={THREE.DoubleSide} />
            <Edges color="#ff9a24" toneMapped={false} />
          </mesh>
          <MapLabel position={[0, 2, 0]}>
            <span className="map-tag nest-tag">NEST</span>
          </MapLabel>
          {[-3, -1.5, 0, 1.5, 3].map((height) => (
            <lineLoop key={height} position={[0, height, 0]}>
              <bufferGeometry>
                <bufferAttribute attach="attributes-position" args={[towerRangeCircle, 3]} />
              </bufferGeometry>
              <lineBasicMaterial color="#ff9a24" transparent opacity={0.45} toneMapped={false} />
            </lineLoop>
          ))}
        </group>
      )}
      {snapshot.reportedTarget && (
        <mesh position={[
          snapshot.reportedTarget.x - origin.x,
          snapshot.reportedTarget.y - origin.y,
          snapshot.reportedTarget.z - origin.z,
        ]}>
          <octahedronGeometry args={[0.8, 0]} />
          <meshStandardMaterial color="#f07862" emissive="#a22e26" emissiveIntensity={0.8} />
        </mesh>
      )}
      {drones.map((drone) => {
        if (!drone.position) return null;
        const position: [number, number, number] = [
          drone.position.x - origin.x,
          drone.position.y - origin.y,
          drone.position.z - origin.z,
        ];
        return (
          <group key={drone.id}>
            {drone.directlyConnectedToTower && (
              <Line start={[0, 0, 0]} end={position} color="#ffb347" opacity={0.3} />
            )}
            {drone.connectedPeers.map((peerId) => {
              const peer = drones.find((candidate) => candidate.id === peerId);
              if (!peer?.position || !peer.connectedToTower || !drone.connectedToTower ||
                  peerId.localeCompare(drone.id) < 0) return null;
              return (
                <Line
                  key={`${drone.id}-${peerId}`}
                  start={position}
                  end={[
                    peer.position.x - origin.x,
                    peer.position.y - origin.y,
                    peer.position.z - origin.z,
                  ]}
                  color="#ffd36a"
                  opacity={0.24}
                />
              );
            })}
            <DroneMarker
              key={`${snapshot.missionId}-${viewMode}-${drone.id}`}
              drone={drone}
              position={position}
              selected={drone.id === selectedId}
              nestMesh={nestMesh}
              onSelect={onSelect}
            />
          </group>
        );
      })}
      <CameraControls position={selectedPosition} origin={origin} focusRequest={focusRequest} viewMode={viewMode} points={overviewPoints} />
    </Canvas>
  );
}

function MapLabel({ position, children, nestMesh }: {
  position: [number, number, number];
  children: ReactNode;
  nestMesh?: THREE.Mesh | null;
}) {
  const gl = useThree((state) => state.gl);
  const portal = useMemo(() => {
    const container = gl.domElement.parentElement;
    if (!container) throw new Error("Map labels require a mounted canvas container.");
    return { current: container };
  }, [gl]);
  const occluders = useMemo(() => nestMesh ? [{ current: nestMesh }] : undefined, [nestMesh]);

  return (
    <Html portal={portal} center position={position} distanceFactor={22} occlude={occluders} zIndexRange={[10000, 0]} style={{ pointerEvents: "none" }}>
      {children}
    </Html>
  );
}

function DroneMarker({
  drone,
  position,
  selected,
  nestMesh,
  onSelect,
}: {
  drone: PublicDrone;
  position: [number, number, number];
  selected: boolean;
  nestMesh: THREE.Mesh | null;
  onSelect: (id: string) => void;
}) {
  const previousPosition = useRef<Point | null>(null);
  const [heading, setHeading] = useState<number | null>(null);
  const opacity = drone.faulted ? 0.42 : drone.positionCurrent ? 1 : 0.3;

  useEffect(() => {
    if (!drone.positionCurrent || !drone.position) return;
    const previous = previousPosition.current;
    if (previous) {
      const dx = drone.position.x - previous.x;
      const dz = drone.position.z - previous.z;
      if (dx !== 0 || dz !== 0) setHeading(Math.atan2(dx, dz));
    }
    previousPosition.current = drone.position;
  }, [drone.position, drone.positionCurrent]);

  return (
    <group position={position} rotation={[0, heading ?? 0, 0]} scale={selected ? 1.3 : 1}>
      <mesh onClick={(event) => {
        event.stopPropagation();
        onSelect(drone.id);
      }}>
        <boxGeometry args={[1, 0.6, 1.4]} />
        <meshBasicMaterial transparent opacity={0} depthWrite={false} />
        <Edges color="#ffe234" transparent opacity={opacity} toneMapped={false} />
      </mesh>
      {nestMesh && <MapLabel position={[0, 1.1, 0]} nestMesh={nestMesh}>
        <button
          type="button"
          className={`map-tag drone-tag ${selected ? "is-selected" : ""}`}
          onClick={() => onSelect(drone.id)}
          aria-label={`Inspect ${drone.id}`}
          aria-pressed={selected}
        >
          {drone.id}
          {(!drone.positionCurrent || drone.faulted) && (
            <small>{drone.faulted ? "FAULT" : "LAST REPORTED"}</small>
          )}
        </button>
      </MapLabel>}
      {heading !== null && (
        <group>
          <Line start={[0, 0, 0.7]} end={[0, 0, 1.5]} color="#ffe234" opacity={opacity} />
          <Line start={[-0.3, 0, 1.15]} end={[0, 0, 1.5]} color="#ffe234" opacity={opacity} />
          <Line start={[0.3, 0, 1.15]} end={[0, 0, 1.5]} color="#ffe234" opacity={opacity} />
        </group>
      )}
    </group>
  );
}

function CameraControls({ position, origin, focusRequest, viewMode, points }: {
  position: Point | null;
  origin: Point;
  focusRequest: number;
  viewMode: "nest" | "overall";
  points: THREE.Vector3[];
}) {
  const camera = useThree((state) => state.camera);
  const size = useThree((state) => state.size);
  const controls = useRef<ComponentRef<typeof OrbitControls>>(null);
  const delta = useMemo(() => new THREE.Vector3(), []);
  const focusedRequest = useRef<number | null>(null);
  const latestPoints = useRef(points);
  latestPoints.current = points;
  const following = position !== null;

  useEffect(() => {
    const orbit = controls.current;
    if (!orbit || following || !(camera instanceof THREE.PerspectiveCamera)) return;

    const target = new THREE.Vector3();
    const direction = new THREE.Vector3(18, 14, 18).normalize();
    const distance = new THREE.Vector3(18, 14, 18).length();
    if (viewMode === "overall") {
      const overview = getOverviewCamera(latestPoints.current, camera.fov, size.width / size.height);
      target.copy(overview.target);
      camera.position.copy(overview.position);
    } else {
      camera.position.copy(target).addScaledVector(direction, distance);
    }
    orbit.target.copy(target);
    orbit.update();
  }, [camera, viewMode, following, size.width, size.height]);

  useEffect(() => {
    const orbit = controls.current;
    if (!orbit || !position) {
      focusedRequest.current = null;
      return;
    }

    if (viewMode === "overall" && focusedRequest.current !== focusRequest) {
      delta.copy(camera.position).sub(orbit.target);
      if (delta.lengthSq() < 0.0001) delta.set(18, 14, 18);
      delta.setLength(12);
      orbit.target.set(position.x - origin.x, position.y - origin.y, position.z - origin.z);
      camera.position.copy(orbit.target).add(delta);
    } else {
      delta.set(
        position.x - origin.x,
        position.y - origin.y,
        position.z - origin.z,
      ).sub(orbit.target);
      camera.position.add(delta);
      orbit.target.add(delta);
    }
    focusedRequest.current = focusRequest;
    orbit.update();
  }, [camera, position, origin.x, origin.y, origin.z, delta, focusRequest, viewMode]);

  return <OrbitControls ref={controls} makeDefault enableDamping dampingFactor={0.08} enablePan={!position} />;
}

function Line({
  start,
  end,
  color,
  opacity,
}: {
  start: [number, number, number];
  end: [number, number, number];
  color: string;
  opacity: number;
}) {
  const points = useMemo(() => [new THREE.Vector3(...start), new THREE.Vector3(...end)], [start, end]);
  return (
    <line>
      <bufferGeometry>
        <bufferAttribute attach="attributes-position" args={[new Float32Array(points.flatMap((point) => point.toArray())), 3]} />
      </bufferGeometry>
      <lineBasicMaterial color={color} transparent opacity={opacity} />
    </line>
  );
}
