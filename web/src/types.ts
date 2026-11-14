export type Point = { x: number; y: number; z: number };
export type MapViewMode = "hive" | "scout" | "overall";

export type PublicDrone = {
  id: string;
  position: { x: number; y: number; z: number } | null;
  connectedToTower: boolean;
  directlyConnectedToTower: boolean;
  positionCurrent: boolean;
  livePosition: Point | null;
  livePositionCurrent: boolean;
  faulted: boolean;
  behavior: string;
  knownCellCount: number;
  lastSeenTick: number | null;
  lastReportAgeSeconds: number | null;
  connectedPeers: string[];
};

export type Snapshot = {
  protocolVersion: number;
  missionId: string | null;
  version: number;
  phase: string;
  connectionState: string;
  worldId: string | null;
  tower: Point | null;
  targetReported: boolean;
  reportedTarget: Point | null;
  overallTarget: Point | null;
  terrain: { x: number; y: number; z: number; sourceTick: number }[];
  liveTerrain: { x: number; y: number; z: number; sourceTick: number }[];
  drones: PublicDrone[];
  recoveredCount: number;
  missingDroneIds: string[];
  gameTick: number | null;
};

export type DroneDebug = {
  missionId: string;
  droneId: string;
  behavior: string;
  position: Point | null;
  lastSeenTick: number | null;
  gameTick: number | null;
  lastReportAgeSeconds: number | null;
  lastContactAgeSeconds: number | null;
  recoveryTargetId: string | null;
  rendezvousTargetId: string | null;
  connectedPeers: string[];
  cells: { x: number; y: number; z: number; solid: boolean; sourceTick: number }[];
  knownTarget: Point | null;
  frontierMovesSinceUpload: number;
};
