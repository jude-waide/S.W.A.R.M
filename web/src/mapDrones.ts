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
      reportedTarget: snapshot.reportedTarget,
    };
  }
  const scout = snapshot.drones.find((drone) => drone.id === selectedId);
  if (!scout || !debug || debug.missionId !== snapshot.missionId || debug.droneId !== selectedId) {
    return { terrain: [], drones: [], reportedTarget: null };
  }
  return {
    terrain: debug.cells.filter((cell) => cell.solid),
    drones: [{
      ...scout,
      position: debug.position,
      positionCurrent: scout.livePositionCurrent && debug.gameTick !== null &&
        snapshot.gameTick !== null && debug.gameTick >= snapshot.gameTick - 20,
      connectedPeers: [],
    }],
    reportedTarget: debug.knownTarget,
  };
}
