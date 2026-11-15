import type { DroneDebug, MapViewMode, PublicDrone, Snapshot } from "./types";

export function getMapDrones(drones: PublicDrone[], viewMode: "hive" | "overall"): PublicDrone[] {
  if (viewMode === "hive") return drones;
  return drones.map((drone) => ({
    ...drone,
    position: drone.livePosition,
    positionCurrent: drone.livePositionCurrent,
  }));
}

export type MapData = Pick<Snapshot, "terrain" | "drones" | "reportedTarget">;

export function getMapData(
  snapshot: Snapshot,
  viewMode: MapViewMode,
  selectedId: string | null,
  debug: DroneDebug | null,
): MapData {
  if (viewMode !== "scout") {
    return {
      terrain: viewMode === "overall" ? snapshot.liveTerrain : snapshot.terrain,
      drones: getMapDrones(snapshot.drones, viewMode),
      reportedTarget: viewMode === "overall" ? snapshot.overallTarget : snapshot.reportedTarget,
    };
  }
  const scout = snapshot.drones.find((drone) => drone.id === selectedId);
  if (!scout || !debug || debug.missionId !== snapshot.missionId || debug.droneId !== selectedId) {
    return { terrain: [], drones: [], reportedTarget: null };
  }
  const positionCurrent = scout.livePositionCurrent && debug.gameTick !== null &&
    snapshot.gameTick !== null && debug.gameTick >= snapshot.gameTick - 20;
  return {
    terrain: debug.cells.filter((cell) => cell.solid),
    drones: [{
      ...scout,
      position: positionCurrent ? scout.livePosition : debug.position,
      positionCurrent,
      connectedPeers: [],
    }, ...debug.peerPositions.filter((peer) => peer.droneId !== selectedId).map((peer): PublicDrone => ({
      id: peer.droneId,
      position: peer.position,
      positionCurrent: positionCurrent && debug.connectedPeers.includes(peer.droneId) && peer.sourceTick === debug.gameTick,
      livePosition: null,
      livePositionCurrent: false,
      connectedToTower: false,
      directlyConnectedToTower: false,
      connectedPeers: [],
      faulted: false,
      behavior: "lastSeenByScout",
      knownCellCount: 0,
      lastSeenTick: peer.sourceTick,
      lastReportAgeSeconds: debug.gameTick === null ? null : Math.max(0, debug.gameTick - peer.sourceTick) / 20,
    }))],
    reportedTarget: debug.knownTarget,
  };
}
