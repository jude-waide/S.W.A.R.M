import type { MotionSnapshot, Point, Snapshot } from "./types";

export function mergeMotion(current: Snapshot, motion: MotionSnapshot): Snapshot | null {
  if (motion.missionId !== current.missionId) return null;
  if (motion.version <= current.version) return current;
  if (motion.version !== current.version + 1) return null;
  const live = new Map(motion.drones.map((drone) => [drone.id, drone]));
  if (live.size !== current.drones.length || current.drones.some((drone) => !live.has(drone.id))) return null;
  return {
    ...current, version: motion.version, gameTick: motion.gameTick, connectionState: "connected",
    drones: current.drones.map((drone) => {
      const update = live.get(drone.id)!;
      return {
        ...drone,
        livePosition: update.position,
        livePositionCurrent: update.positionCurrent,
        faulted: update.faulted,
        ...(update.faulted ? {
          positionCurrent: false, connectedToTower: false, directlyConnectedToTower: false,
          connectedPeers: [], behavior: "faulted",
        } : {}),
      };
    }),
  };
}

export type PositionTrack = {
  from: Point; to: Point; started: number; duration: number; tick: number; current: boolean;
};

export function samplePosition(track: PositionTrack, now: number): Point {
  const amount = track.duration === 0 ? 1 : Math.max(0, Math.min(1, (now - track.started) / track.duration));
  return {
    x: track.from.x + (track.to.x - track.from.x) * amount,
    y: track.from.y + (track.to.y - track.from.y) * amount,
    z: track.from.z + (track.to.z - track.from.z) * amount,
  };
}

export function receivePosition(
  previous: PositionTrack | null, position: Point, tick: number, now: number, current: boolean,
): PositionTrack {
  const from = previous ? samplePosition(previous, now) : position;
  const distanceSq = (from.x - position.x) ** 2 + (from.y - position.y) ** 2 + (from.z - position.z) ** 2;
  if (!previous || !previous.current || !current || distanceSq > 64 || tick < previous.tick)
    return { from: position, to: position, started: now, duration: 0, tick, current };
  if (position.x === previous.to.x && position.y === previous.to.y && position.z === previous.to.z)
    return { ...previous, tick, current };
  return {
    from, to: position, started: now, duration: Math.max(50, Math.min(650, (tick - previous.tick) * 50)), tick, current,
  };
}
