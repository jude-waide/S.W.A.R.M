import type { PublicDrone } from "./types";

export function getMapDrones(drones: PublicDrone[], viewMode: "nest" | "overall"): PublicDrone[] {
  if (viewMode === "nest") return drones;
  return drones.map((drone) => ({
    ...drone,
    position: drone.livePosition,
    positionCurrent: drone.livePositionCurrent,
  }));
}
