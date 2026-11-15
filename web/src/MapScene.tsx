import { Edges, Html, Line as VisibleLine, OrbitControls } from "@react-three/drei";
import { Canvas, useFrame, useThree } from "@react-three/fiber";
import { useEffect, useMemo, useRef, useState, type ComponentRef, type ReactNode } from "react";
import * as THREE from "three";
import type { MapViewMode, Point, PublicDrone, Snapshot } from "./types";
import { getOverviewCamera } from "./mapCamera";
import type { MapData } from "./mapDrones";
import { getTerrainGeometry } from "./terrainWireframe";
import { receivePosition, samplePosition, type PositionTrack } from "./motion";

type Props = {
  snapshot: Snapshot;
  mapData: MapData;
  heightLimit: number;
  selectedId: string | null;
  focusRequest: number;
  viewMode: MapViewMode;
  cutaway: boolean;
  interpolate: boolean;
  onSelect: (id: string) => void;
};

type AnimatedPosition = { track: PositionTrack | null; position: THREE.Vector3 };

export default function MapScene({ snapshot, mapData, heightLimit, selectedId, focusRequest, viewMode, cutaway, interpolate, onSelect }: Props) {
  const [nestMesh, setNestMesh] = useState<THREE.Mesh | null>(null);
  useEffect(() => {
    const timer = window.setTimeout(() => window.dispatchEvent(new Event("resize")), 50);
    return () => window.clearTimeout(timer);
  }, []);

  const origin = snapshot.tower ?? { x: 0, y: 0, z: 0 };
  const { drones, terrain, reportedTarget } = mapData;
  const selectedDrone = drones.find((drone) => drone.id === selectedId);
  const selectedPosition = selectedDrone?.positionCurrent ? selectedDrone.position : null;
  const scoutViewId = viewMode === "scout" ? selectedId : null;
  const animation = useMemo(() => new Map<string, AnimatedPosition>(), [snapshot.missionId, viewMode, scoutViewId, interpolate]);
  for (const drone of drones) {
    if (drone.position && !animation.has(drone.id))
      animation.set(drone.id, { track: null, position: new THREE.Vector3(
        drone.position.x - origin.x, drone.position.y - origin.y, drone.position.z - origin.z,
      ) });
  }
  useEffect(() => {
    const now = performance.now();
    for (const drone of drones) {
      if (!drone.position) continue;
      const entry = animation.get(drone.id)!;
      const tick = viewMode === "overall" || (viewMode === "scout" && drone.id === selectedId)
        ? snapshot.gameTick : drone.lastSeenTick;
      entry.track = receivePosition(entry.track, drone.position, tick ?? 0, now,
        interpolate && drone.positionCurrent && !drone.faulted);
    }
  }, [animation, drones, snapshot.gameTick, viewMode, selectedId, interpolate]);
  const selectedAnimated = selectedPosition && selectedId ? animation.get(selectedId)?.position ?? null : null;
  const cutawayPlane = useMemo(() => new THREE.Plane(), []);
  const clippingPlanes = useMemo(() => cutaway ? [cutawayPlane] : [], [cutaway, cutawayPlane]);
  const visibleTerrain = useMemo(
    () => terrain.filter((cell) => cell.y <= heightLimit),
    [terrain, heightLimit],
  );
  const terrainLayout = useRef(visibleTerrain);
  if (terrainLayout.current.length !== visibleTerrain.length ||
      visibleTerrain.some((cell, index) => {
        const previous = terrainLayout.current[index];
        return cell.x !== previous.x || cell.y !== previous.y || cell.z !== previous.z;
      })) terrainLayout.current = visibleTerrain;
  const stableTerrain = terrainLayout.current;
  const terrainGeometry = useMemo(() => getTerrainGeometry(stableTerrain), [stableTerrain]);
  const surfaceEdges = terrainGeometry.edges;
  const facePositions = useMemo(() => new Float32Array(terrainGeometry.faces.flatMap((point) => [
    point.x - origin.x, point.y - origin.y, point.z - origin.z,
  ])), [terrainGeometry.faces, origin.x, origin.y, origin.z]);
  const edgePositions = useMemo(() => {
    const positions = new Float32Array(surfaceEdges.length * 6);
    let offset = 0;
    for (const edge of surfaceEdges) {
      positions[offset++] = edge.start.x - origin.x;
      positions[offset++] = edge.start.y - origin.y;
      positions[offset++] = edge.start.z - origin.z;
      positions[offset++] = edge.end.x - origin.x;
      positions[offset++] = edge.end.y - origin.y;
      positions[offset++] = edge.end.z - origin.z;
    }
    return positions;
  }, [surfaceEdges, origin.x, origin.y, origin.z]);
  const terrainOverviewPoints = useMemo(() => {
    const points: THREE.Vector3[] = [];
    for (const x of [-3, 3])
      for (const y of [-3, 3])
        for (const z of [-3, 3]) points.push(new THREE.Vector3(x, y, z));
    for (let index = 0; index < edgePositions.length; index += 3)
      points.push(new THREE.Vector3(edgePositions[index], edgePositions[index + 1], edgePositions[index + 2]));
    return points;
  }, [edgePositions]);
  const overviewPoints = useMemo(() => {
    const points = [...terrainOverviewPoints];
    for (const point of [...drones.map((drone) => drone.position), reportedTarget]) {
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
  }, [terrainOverviewPoints, drones, reportedTarget, origin.x, origin.y, origin.z]);

  return (
    <Canvas
      camera={{ position: [18, 14, 18], rotation: [-0.53, Math.PI / 4, 0], fov: 44 }}
      dpr={[1, 1.6]}
      gl={{ localClippingEnabled: true }}
    >
      <MotionAnimation animation={animation} origin={origin} />
      <color attach="background" args={["#100d08"]} />
      <fog attach="fog" args={["#100d08", 180, 600]} />
      <ambientLight intensity={1.3} />
      <directionalLight position={[20, 30, 12]} intensity={2.2} color="#ffe4b2" />
      {facePositions.length > 0 && (
        <mesh renderOrder={-1}>
          <bufferGeometry>
            <bufferAttribute attach="attributes-position" args={[facePositions, 3]} />
          </bufferGeometry>
          <meshBasicMaterial
            colorWrite={false}
            depthWrite
            side={THREE.DoubleSide}
            polygonOffset
            polygonOffsetFactor={1}
            polygonOffsetUnits={1}
            clippingPlanes={clippingPlanes}
          />
        </mesh>
      )}
      {edgePositions.length > 0 && (
        <lineSegments>
          <bufferGeometry>
            <bufferAttribute attach="attributes-position" args={[edgePositions, 3]} />
          </bufferGeometry>
          <lineBasicMaterial color="#ffffff" transparent opacity={0.85} toneMapped={false} clippingPlanes={clippingPlanes} />
        </lineSegments>
      )}
      {snapshot.tower && (
        <group>
          <mesh ref={setNestMesh}>
            <cylinderGeometry args={[1.2, 1.2, 2.6, 6]} />
            <meshBasicMaterial transparent opacity={0} depthWrite={false} side={THREE.DoubleSide} />
            <Edges color="#ff9a24" toneMapped={false} transparent depthTest={false} depthWrite={false} renderOrder={1} />
          </mesh>
          <MapLabel position={[0, 2, 0]}>
            <span className="map-tag hive-tag">HIVE</span>
          </MapLabel>
        </group>
      )}
      {reportedTarget && (
        <mesh renderOrder={1} position={[
          reportedTarget.x - origin.x,
          reportedTarget.y - origin.y,
          reportedTarget.z - origin.z,
        ]}>
          <boxGeometry args={[1.2, 1.2, 1.2]} />
          <meshBasicMaterial transparent opacity={0} depthWrite={false} />
          <Edges color="#ffc857" toneMapped={false} transparent depthTest={false} depthWrite={false} renderOrder={1} />
          <MapLabel position={[0, 1.5, 0]}>
            <span className="map-tag target-tag">TARGET</span>
          </MapLabel>
        </mesh>
      )}
      {drones.map((drone) => {
        if (!drone.position) return null;
        const position = animation.get(drone.id)!.position;
        return (
          <group key={drone.id}>
            {drone.directlyConnectedToTower && (
              <AnimatedLink end={position} color="#ffad32" opacity={1} width={2.8} />
            )}
            {drone.connectedPeers.map((peerId) => {
              const peer = drones.find((candidate) => candidate.id === peerId);
              if (!peer?.position || !peer.connectedToTower || !drone.connectedToTower ||
                  peerId.localeCompare(drone.id) < 0) return null;
              return (
                <AnimatedLink
                  key={`${drone.id}-${peerId}`}
                  start={position}
                  end={animation.get(peerId)!.position}
                  color="#ffd36a"
                  opacity={0.85}
                  width={2}
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
      <CameraControls position={selectedPosition} animatedPosition={selectedAnimated} origin={origin} focusRequest={focusRequest} viewMode={viewMode} points={overviewPoints} />
      {cutaway && <TerrainCutaway plane={cutawayPlane} position={selectedId ? animation.get(selectedId)?.position ?? null : null} />}
    </Canvas>
  );
}

function MotionAnimation({ animation, origin }: { animation: Map<string, AnimatedPosition>; origin: Point }) {
  useFrame(() => {
    const now = performance.now();
    for (const entry of animation.values()) {
      if (!entry.track) continue;
      const point = samplePosition(entry.track, now);
      entry.position.set(point.x - origin.x, point.y - origin.y, point.z - origin.z);
    }
  }, -2);
  return null;
}

function AnimatedLink({ start, end, color, opacity, width }: {
  start?: THREE.Vector3; end: THREE.Vector3; color: string; opacity: number; width: number;
}) {
  const line = useRef<ComponentRef<typeof VisibleLine>>(null);
  const points = useMemo(() => [new THREE.Vector3(), new THREE.Vector3()], []);
  useFrame(() => {
    if (!line.current) return;
    const geometry = line.current.geometry;
    const a = geometry.getAttribute("instanceStart");
    const b = geometry.getAttribute("instanceEnd");
    a.setXYZ(0, start?.x ?? 0, start?.y ?? 0, start?.z ?? 0);
    b.setXYZ(0, end.x, end.y, end.z);
    a.needsUpdate = true;
    b.needsUpdate = true;
  });
  return <VisibleLine ref={line} points={points} color={color} transparent opacity={opacity} lineWidth={width}
    depthTest={false} depthWrite={false} toneMapped={false} renderOrder={1} frustumCulled={false} />;
}

function TerrainCutaway({ plane, position }: { plane: THREE.Plane; position: THREE.Vector3 | null }) {
  const anchor = useMemo(() => new THREE.Vector3(), []);
  const normal = useMemo(() => new THREE.Vector3(), []);
  useFrame(({ camera }) => {
    anchor.set(position?.x ?? 0, 0, position?.z ?? 0);
    normal.set(anchor.x - camera.position.x, 0, anchor.z - camera.position.z);
    if (normal.lengthSq() < 0.0001) normal.set(0, 0, -1);
    plane.setFromNormalAndCoplanarPoint(normal.normalize(), anchor);
  });
  return null;
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
  position: THREE.Vector3;
  selected: boolean;
  nestMesh: THREE.Mesh | null;
  onSelect: (id: string) => void;
}) {
  const previousPosition = useRef<Point | null>(null);
  const marker = useRef<THREE.Group>(null);
  const [heading, setHeading] = useState<number | null>(null);
  const opacity = drone.faulted ? 0.42 : drone.positionCurrent ? 1 : 0.3;
  useFrame(() => marker.current?.position.copy(position));

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
    <group ref={marker} rotation={[0, heading ?? 0, 0]} scale={selected ? 1.3 : 1}>
      <mesh onClick={(event) => {
        event.stopPropagation();
        onSelect(drone.id);
      }}>
        <boxGeometry args={[1, 0.6, 1.4]} />
        <meshBasicMaterial transparent opacity={0} depthWrite={false} />
        <Edges color="#ffe234" transparent opacity={opacity} toneMapped={false} depthTest={false} depthWrite={false} renderOrder={1} />
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

function CameraControls({ position, animatedPosition, origin, focusRequest, viewMode, points }: {
  position: Point | null;
  animatedPosition: THREE.Vector3 | null;
  origin: Point;
  focusRequest: number;
  viewMode: MapViewMode;
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
  }, [camera, following, origin.x, origin.y, origin.z, delta, focusRequest, viewMode]);

  useFrame(() => {
    const orbit = controls.current;
    if (!orbit || !animatedPosition) return;
    delta.copy(animatedPosition).sub(orbit.target);
    camera.position.add(delta);
    orbit.target.copy(animatedPosition);
    orbit.update();
  });

  return <OrbitControls ref={controls} makeDefault enableDamping dampingFactor={0.08} enablePan={!position} />;
}

function Line({
  start,
  end,
  color,
  opacity,
  width = 1.5,
}: {
  start: [number, number, number];
  end: [number, number, number];
  color: string;
  opacity: number;
  width?: number;
}) {
  const points = useMemo(() => [new THREE.Vector3(...start), new THREE.Vector3(...end)], [start, end]);
  return (
    <VisibleLine points={points} color={color} transparent opacity={opacity} lineWidth={width}
      depthTest={false} depthWrite={false} toneMapped={false} renderOrder={1} />
  );
}
