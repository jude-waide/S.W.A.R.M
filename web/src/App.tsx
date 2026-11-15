import { HubConnectionBuilder, HubConnectionState, LogLevel } from "@microsoft/signalr";
import { lazy, Suspense, useCallback, useEffect, useMemo, useRef, useState } from "react";
import type { DroneDebug, MapViewMode, MotionSnapshot, PublicDrone, ReplayIndex, Snapshot } from "./types";
import { getMapData } from "./mapDrones";
import { mergeMotion } from "./motion";

const MapScene = lazy(() => import("./MapScene"));

const emptySnapshot: Snapshot = {
  protocolVersion: 1,
  missionId: null,
  version: 0,
  phase: "awaitingMission",
  connectionState: "disconnected",
  worldId: null,
  tower: null,
  targetReported: false,
  targetFound: false,
  reportedTarget: null,
  overallTarget: null,
  terrain: [],
  liveTerrain: [],
  drones: [],
  recoveredCount: 0,
  missingDroneIds: [],
  gameTick: null,
};

const api = import.meta.env.VITE_API_BASE ?? "";
const games = ["minecraft", "skyrim"] as const;
type GameId = (typeof games)[number];

export default function App() {
  const [game, setGame] = useState<GameId>("minecraft");
  const [liveSnapshot, setSnapshot] = useState(emptySnapshot);
  const [replayIndex, setReplayIndex] = useState<ReplayIndex>({ missionId: null, gameTicks: [], recordingEnabled: true });
  const [replaying, setReplaying] = useState(false);
  const [replayFrame, setReplayFrame] = useState(0);
  const [recordedFrame, setRecordedFrame] = useState<{ index: number; snapshot: Snapshot } | null>(null);
  const [replayError, setReplayError] = useState<string | null>(null);
  const [replayIndexError, setReplayIndexError] = useState<string | null>(null);
  const [replayToggleError, setReplayToggleError] = useState<string | null>(null);
  const [replayToggleBusy, setReplayToggleBusy] = useState(false);
  const [playing, setPlaying] = useState(false);
  const [playbackSpeed, setPlaybackSpeed] = useState(1);
  const [cutaway, setCutaway] = useState(false);
  const snapshot = replaying ? recordedFrame?.snapshot ?? emptySnapshot : liveSnapshot;
  const [apiOnline, setApiOnline] = useState(false);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [debug, setDebug] = useState<DroneDebug | null>(null);
  const [debugError, setDebugError] = useState<string | null>(null);
  const [heightLimit, setHeightLimit] = useState(96);
  const [inspectionOpen, setInspectionOpen] = useState(false);
  const [viewMode, setViewMode] = useState<MapViewMode>("hive");
  const [sidebarOpen, setSidebarOpen] = useState(false);
  const [focusRequest, setFocusRequest] = useState(0);
  const snapshotRef = useRef(liveSnapshot);
  const gameRef = useRef(game);
  gameRef.current = game;
  const connectionRef = useRef<import("@microsoft/signalr").HubConnection | null>(null);
  const selectedIdRef = useRef(selectedId);
  const debugRealtimeRef = useRef(false);
  const replayTick = replayIndex.gameTicks[replayFrame];
  const nextReplayTick = replayIndex.gameTicks[replayFrame + 1];

  useEffect(() => {
    snapshotRef.current = liveSnapshot;
    selectedIdRef.current = selectedId;
  }, [liveSnapshot, selectedId]);

  useEffect(() => {
    let active = true;
    const load = async () => {
      try {
        const response = await fetch(`${api}/api/public/${game}/replay`, { cache: "no-store" });
        if (!response.ok) throw new Error(`Replay index request failed (${response.status})`);
        const next = await response.json() as ReplayIndex;
        if (active) {
          setReplayIndex(next);
          setReplayIndexError(null);
        }
      } catch (error) {
        if (active) setReplayIndexError(error instanceof Error ? error.message : "Replay index unavailable");
      }
    };
    void load();
    const timer = window.setInterval(() => void load(), 2500);
    return () => { active = false; window.clearInterval(timer); };
  }, [game]);

  useEffect(() => {
    snapshotRef.current = emptySnapshot;
    setSnapshot(emptySnapshot);
    setReplayIndex({ missionId: null, gameTicks: [], recordingEnabled: true });
    setReplayToggleError(null);
    setReplaying(false);
    setPlaying(false);
    setRecordedFrame(null);
    setSelectedId(null);
    setDebug(null);
    setInspectionOpen(false);
    setViewMode("hive");
    setApiOnline(false);
  }, [game]);

  useEffect(() => {
    setReplaying(false);
    setPlaying(false);
    setRecordedFrame(null);
    setReplayError(null);
  }, [game, replayIndex.missionId]);

  useEffect(() => {
    if (!replaying || !replayIndex.missionId) return;
    const controller = new AbortController();
    const load = async () => {
      try {
        const response = await fetch(
          `${api}/api/public/${game}/replay/${encodeURIComponent(replayIndex.missionId!)}/frames/${replayFrame}`,
          { cache: "no-store", signal: controller.signal },
        );
        if (!response.ok) throw new Error(`Replay frame request failed (${response.status})`);
        const next = await response.json() as Snapshot;
        if (!controller.signal.aborted) {
          setRecordedFrame({ index: replayFrame, snapshot: next });
          setReplayError(null);
        }
      } catch (error) {
        if (!controller.signal.aborted) {
          setPlaying(false);
          setReplayError(error instanceof Error ? error.message : "Replay frame unavailable");
        }
      }
    };
    void load();
    return () => controller.abort();
  }, [game, replaying, replayIndex.missionId, replayFrame]);

  useEffect(() => {
    if (!replaying || !playing || recordedFrame?.index !== replayFrame) return;
    if (nextReplayTick === undefined) {
      setPlaying(false);
      return;
    }
    const delay = Math.max(20,
      (nextReplayTick - replayTick) * 50 / playbackSpeed);
    const timer = window.setTimeout(() => setReplayFrame((frame) => frame + 1), delay);
    return () => window.clearTimeout(timer);
  }, [replaying, playing, recordedFrame, replayFrame, replayTick, nextReplayTick, playbackSpeed]);

  useEffect(() => {
    setSelectedId(null);
    setDebug(null);
    setInspectionOpen(false);
    setViewMode("hive");
  }, [game, snapshot.missionId]);

  useEffect(() => {
    if (snapshot.tower) setHeightLimit(snapshot.tower.y + 32);
  }, [game, snapshot.missionId, snapshot.tower?.y]);

  const refresh = useCallback(async () => {
    try {
      const response = await fetch(`${api}/api/public/${game}/snapshot`, { cache: "no-store" });
      if (!response.ok) throw new Error(`Snapshot request failed (${response.status})`);
      const next = (await response.json()) as Snapshot;
      if (gameRef.current !== game) return;
      const current = snapshotRef.current;
      if (current.missionId === next.missionId && next.version < current.version) return;
      snapshotRef.current = next;
      setSnapshot(next);
      setApiOnline(true);
    } catch {
      setApiOnline(false);
    }
  }, [game]);

  const toggleReplayRecording = async () => {
    const requestedGame = game;
    setReplayToggleBusy(true);
    setReplayToggleError(null);
    try {
      const response = await fetch(`${api}/api/public/${requestedGame}/replay/recording`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ enabled: !replayIndex.recordingEnabled }),
      });
      if (!response.ok) throw new Error(`Replay recording update failed (${response.status})`);
      const next = await response.json() as ReplayIndex;
      if (gameRef.current === requestedGame) {
        setReplayIndex(next);
        setReplayIndexError(null);
      }
    } catch (error) {
      if (gameRef.current === requestedGame)
        setReplayToggleError(error instanceof Error ? error.message : "Replay recording update failed");
    } finally {
      setReplayToggleBusy(false);
    }
  };

  useEffect(() => {
    void refresh();
    const timer = window.setInterval(() => void refresh(), 2500);
    return () => window.clearInterval(timer);
  }, [refresh]);

  useEffect(() => {
    const connection = new HubConnectionBuilder()
      .withUrl(`${api}/hubs/mission/${game}`)
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Error)
      .build();
    let stopped = false;
    let retryTimer = 0;
    connectionRef.current = connection;
    connection.on("missionUpdated", (next: Snapshot) => {
      if (gameRef.current !== game) return;
      const current = snapshotRef.current;
      if (current.missionId === next.missionId && next.version <= current.version) return;
      if (current.missionId === next.missionId && next.version > current.version + 1)
        void connection.invoke("RequestSnapshot").catch(() => void refresh());
      snapshotRef.current = next;
      setSnapshot(next);
      setApiOnline(true);
    });
    connection.on("motionUpdated", (motion: MotionSnapshot) => {
      if (gameRef.current !== game) return;
      const current = snapshotRef.current;
      const next = mergeMotion(current, motion);
      if (!next) {
        void connection.invoke("RequestSnapshot").catch(() => void refresh());
        return;
      }
      if (next === current) return;
      snapshotRef.current = next;
      setSnapshot(next);
      setApiOnline(true);
    });
    connection.on("droneDebugUpdated", (next: DroneDebug) => {
      if (gameRef.current !== game) return;
      if (selectedIdRef.current !== next.droneId || snapshotRef.current.missionId !== next.missionId) return;
      debugRealtimeRef.current = true;
      setDebug((current) =>
        current?.missionId === next.missionId && current.gameTick !== null &&
        next.gameTick !== null && next.gameTick < current.gameTick ? current : next,
      );
      setDebugError(null);
    });
    connection.onreconnected(() => {
      void connection.invoke("RequestSnapshot").catch(() => void refresh());
      if (selectedIdRef.current)
        void connection.invoke("SubscribeDrone", selectedIdRef.current)
          .catch((error: unknown) => console.error("Drone debug subscription failed", error));
    });
    connection.onreconnecting(() => {
      debugRealtimeRef.current = false;
    });
    const scheduleStart = () => {
      if (stopped || retryTimer) return;
      retryTimer = window.setTimeout(() => {
        retryTimer = 0;
        void connect();
      }, 3000);
    };
    const connect = async () => {
      try {
        if (connection.state === HubConnectionState.Disconnected) await connection.start();
        if (connection.state !== HubConnectionState.Connected) return;
        await connection.invoke("RequestSnapshot");
        if (selectedIdRef.current)
          await connection.invoke("SubscribeDrone", selectedIdRef.current);
      } catch {
        void refresh();
        if (connection.state === HubConnectionState.Disconnected) scheduleStart();
      }
    };
    connection.onclose(() => {
      debugRealtimeRef.current = false;
      scheduleStart();
    });
    void connect();
    return () => {
      stopped = true;
      window.clearTimeout(retryTimer);
      connectionRef.current = null;
      void connection.stop();
    };
  }, [game, refresh]);

  useEffect(() => {
    if (!selectedId || replaying) {
      setDebug(null);
      setDebugError(null);
      debugRealtimeRef.current = false;
      return;
    }
    let active = true;
    debugRealtimeRef.current = false;
    setDebug(null);
    setDebugError(null);
    const loadInitial = async () => {
      try {
        const response = await fetch(`${api}/api/public/${game}/drones/${encodeURIComponent(selectedId)}`, {
          cache: "no-store",
        });
        if (!response.ok) throw new Error(`Drone debug request failed (${response.status})`);
        const next = (await response.json()) as DroneDebug;
        if (active) {
          setDebug(next);
          setDebugError(null);
        }
      } catch (error) {
        if (active) {
          setDebug(null);
          setDebugError(error instanceof Error ? error.message : "Drone debug request failed");
        }
      }
    };
    const connection = connectionRef.current;
    if (connection?.state === HubConnectionState.Connected) {
      void connection.invoke("SubscribeDrone", selectedId).catch((error: unknown) => {
        console.error("Drone debug subscription failed", error);
        setDebugError("Live drone inspection is unavailable; retrying snapshot polling.");
        void loadInitial();
      });
    } else {
      void loadInitial();
    }
    const timer = window.setInterval(() => {
      const current = connectionRef.current;
      if (!current || current.state !== HubConnectionState.Connected || !debugRealtimeRef.current)
        void loadInitial();
    }, 2500);
    return () => {
      active = false;
      if (connection?.state === HubConnectionState.Connected)
        void connection.invoke("UnsubscribeDrone", selectedId)
          .catch((error: unknown) => console.error("Drone debug unsubscription failed", error));
      window.clearInterval(timer);
    };
  }, [game, selectedId, replaying]);

  useEffect(() => {
    if (selectedId && !snapshot.drones.some((drone) => drone.id === selectedId)) {
      setSelectedId(null);
      setDebug(null);
      setDebugError(null);
    }
  }, [selectedId, snapshot.drones]);

  const mapData = useMemo(
    () => getMapData(snapshot, viewMode, selectedId, replaying ? null : debug),
    [snapshot, viewMode, selectedId, debug, replaying],
  );
  const scoutReady = debug?.missionId === snapshot.missionId && debug?.droneId === selectedId;
  const counts = useMemo(() => ({
    total: snapshot.drones.length,
    linked: snapshot.drones.filter((drone) => drone.connectedToTower).length,
    terrain: mapData.terrain.length,
    recovered: snapshot.recoveredCount,
    missing: snapshot.missingDroneIds.length,
  }), [snapshot, mapData.terrain.length]);

  const selectedDrone = snapshot.drones.find((drone) => drone.id === selectedId) ?? null;
  const minHeight = snapshot.tower ? snapshot.tower.y - 32 : 32;
  const maxHeight = snapshot.tower ? snapshot.tower.y + 32 : 96;
  const heightPercent = Math.max(0, Math.min(100, (heightLimit - minHeight) / (maxHeight - minHeight) * 100));
  const selectDrone = useCallback((id: string) => {
    setSelectedId(id);
    setInspectionOpen(true);
    setFocusRequest((request) => request + 1);
  }, []);

  return (
    <main className="app-shell">
      <header className="topbar">
        <a className="brand" href="#" aria-label={`Drone Swarm home, backend ${apiOnline ? "online" : "offline"}`} title={apiOnline ? "Online" : "Offline"}>
          <svg className="brand-mark" aria-hidden="true" viewBox="0 0 40 44" fill="none" stroke="currentColor" strokeWidth="1.4" strokeLinejoin="round">
            {[[20, 22], [9.6, 22], [14.8, 13], [25.2, 13], [30.4, 22], [25.2, 31], [14.8, 31]].map(([x, y]) => (
              <polygon key={`${x}-${y}`} points="0,-6 5.2,-3 5.2,3 0,6 -5.2,3 -5.2,-3" transform={`translate(${x} ${y})`} />
            ))}
          </svg>
          <span className="brand-name">SWARM</span>
        </a>
        <nav className="game-switch" aria-label="Game session">
          {games.map((gameId) => (
            <button key={gameId} type="button" aria-pressed={game === gameId}
              onClick={() => setGame(gameId)}>
              {gameId.toUpperCase()}
            </button>
          ))}
        </nav>
        {snapshot.missionId && <div className="mission-milestones" role="status" aria-label="Mission milestones">
          <span className={snapshot.targetFound ? "achieved" : ""}>{snapshot.targetFound ? "TARGET FOUND" : "SEARCHING"}</span>
          {snapshot.targetFound && <span className={snapshot.targetReported ? "achieved" : ""}>
            {snapshot.targetReported ? "HIVE INFORMED" : "AWAITING HIVE REPORT"}
          </span>}
          {snapshot.targetReported && <span className={snapshot.phase === "complete" ? "achieved home" : ""}>
            {snapshot.phase === "complete" ? "ALL DRONES HOME / AT REST" : `RETURNING ${counts.recovered}/${counts.total}`}
          </span>}
        </div>}
        <div className="topbar-status" role="status" aria-label={apiOnline ? "Backend online" : "Backend offline"} title={apiOnline ? "Backend online" : "Backend offline"}>
          <span className={`status-pip ${apiOnline ? "is-live" : ""}`} />
        </div>
      </header>

      <details className="side-drawer" open={sidebarOpen} onToggle={(event) => setSidebarOpen(event.currentTarget.open)}>
        <summary aria-label={sidebarOpen ? "Close mission sidebar" : "Open mission sidebar"} title={sidebarOpen ? "Close sidebar" : "Open sidebar"}>
          <svg aria-hidden="true" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5">
            <rect x="3" y="3" width="18" height="18" rx="2" />
            <path d="M9 3v18" />
            {sidebarOpen && <path d="M4 4h4v16H4z" fill="currentColor" stroke="none" opacity="0.35" />}
          </svg>
        </summary>
        <div className="panel drawer-content">
          <div className="drawer-session">
            <div className="view-switch" role="group" aria-label="Map view">
              <button className="map-overview" type="button" aria-pressed={viewMode === "hive"} onClick={() => {
                setSelectedId(null);
                setViewMode("hive");
              }}>
                HIVE VIEW
              </button>
              <button className="map-overview" type="button" disabled={replaying} title={replaying ? "Individual scout inspection is live-only" : undefined} aria-pressed={viewMode === "scout"} onClick={() => {
                setViewMode("scout");
                if (!selectedId && snapshot.drones[0]) selectDrone(snapshot.drones[0].id);
              }}>
                SCOUT VIEW
              </button>
              <button className="map-overview" type="button" aria-pressed={viewMode === "overall"} onClick={() => {
                setSelectedId(null);
                setViewMode("overall");
              }}>
                OVERALL VIEW
              </button>
            </div>
            <div className="drawer-status">
              <h1>{phaseTitle(snapshot.phase)}</h1>
              <span className={`map-connection ${snapshot.connectionState === "connected" ? "connected" : ""}`} role="status" aria-label={`${game} ${snapshot.connectionState === "connected" ? "connected" : "disconnected"}`} title={`${game} ${snapshot.connectionState === "connected" ? "connected" : "disconnected"}`}>
                <i /> {game.toUpperCase()}
              </span>
            </div>
          </div>

          <section className="metric-row" aria-label="Mission statistics">
            <Metric label="DRONES" value={counts.total.toString().padStart(2, "0")} />
            <Metric label="LINKED" value={counts.linked.toString().padStart(2, "0")} />
            <Metric label="TERRAIN" value={counts.terrain.toLocaleString()} />
            <Metric label="RECOVERED" value={snapshot.targetReported ? `${counts.recovered}/${counts.total}` : "—"} accent />
          </section>
          <section aria-label="Drone roster">
          <div className="roster-list">
            {snapshot.drones.length === 0 ? (
              <div className="empty-roster">
                <span className="empty-glyph">⌁</span>
                <span>NO UNITS DETECTED</span>
              </div>
            ) : snapshot.drones.map((drone) => (
              <DroneRow
                key={drone.id}
                drone={drone}
                selected={selectedId === drone.id}
                onSelect={() => selectDrone(drone.id)}
              />
            ))}
          </div>
          {counts.missing > 0 && (
            <div className="recovery-alert">
              <strong>UNRECOVERED / FAULTED</strong>
              <span>{snapshot.missingDroneIds.join(", ")}</span>
            </div>
          )}
          </section>
        </div>
      </details>

      <section className="workspace" aria-label="Spatial telemetry">
        <section className="panel map-panel">
          <div className="map-viewport">
            {snapshot.tower ? (
              <Suspense fallback={<div className="map-loading">LOADING SPATIAL VIEW…</div>}>
                <MapScene key={`${game}:${snapshot.missionId}`} snapshot={snapshot} mapData={mapData} heightLimit={heightLimit} selectedId={selectedId} focusRequest={focusRequest} viewMode={viewMode} cutaway={cutaway} interpolate={!replaying} onSelect={selectDrone} />
              </Suspense>
            ) : (
              <div className="map-empty">
                <span>AWAITING FIELD DATA</span>
                <small>The live volumetric map will appear when a mission starts</small>
              </div>
            )}
            {snapshot.tower && viewMode === "scout" && (!selectedId || !scoutReady) && (
              <div className="map-empty" role="status">
                <span>{debugError ? "SCOUT VIEW UNAVAILABLE" : selectedId ? "LOADING SCOUT KNOWLEDGE" : "SELECT A SCOUT"}</span>
                <small>{debugError ?? (selectedId
                  ? "Waiting for this unit's individual observations."
                  : "Choose a unit from the roster to see its individual map.")}</small>
              </div>
            )}
          </div>
          <button type="button" className="cutaway-toggle panel" aria-pressed={cutaway} onClick={() => setCutaway((value) => !value)}
            title="Hide the camera-facing half of known terrain, centered on the selected scout or hive">
            CUTAWAY {cutaway ? "ON" : "OFF"}
          </button>
          <section className="replay-control panel" aria-label="Mission replay">
            <button type="button" disabled={!replayIndex.gameTicks.length} aria-pressed={replaying} onClick={() => {
              setReplaying((value) => !value);
              setPlaying(false);
              setReplayFrame(0);
              setRecordedFrame(null);
              setSelectedId(null);
              setInspectionOpen(false);
              setViewMode("hive");
            }}>{replaying ? "BACK TO LIVE" : "REPLAY SEARCH"}</button>
            <button type="button" disabled={replayToggleBusy} aria-pressed={replayIndex.recordingEnabled}
              onClick={() => void toggleReplayRecording()}>
              {replayToggleBusy ? "UPDATING…" : replayIndex.recordingEnabled ? "STOP RECORDING" : "START RECORDING"}
            </button>
            {replaying && <>
              <button type="button" aria-label={playing ? "Pause replay" : "Play replay"} onClick={() => {
                if (replayFrame >= replayIndex.gameTicks.length - 1) setReplayFrame(0);
                setPlaying((value) => !value);
              }}>{playing ? "PAUSE" : "PLAY"}</button>
              <input aria-label="Replay timeline" type="range" min={0} max={Math.max(0, replayIndex.gameTicks.length - 1)}
                value={replayFrame} onChange={(event) => {
                  setPlaying(false);
                  setReplayFrame(Number(event.target.value));
                }} />
              <select aria-label="Replay speed" value={playbackSpeed} onChange={(event) => setPlaybackSpeed(Number(event.target.value))}>
                {[1, 2, 4, 8, 16].map((speed) => <option key={speed} value={speed}>{speed}x</option>)}
              </select>
              <span>{((replayTick - replayIndex.gameTicks[0]) / 20).toFixed(1)}s</span>
              {recordedFrame?.index !== replayFrame && !replayError && <span role="status">LOADING</span>}
            </>}
            {(replayError || replayIndexError || replayToggleError) &&
              <span role="alert">{replayError ?? replayToggleError ?? replayIndexError}</span>}
          </section>
          <details className="height-control">
            <summary>
              HEIGHT FILTER <strong>{heightLimit}</strong>
            </summary>
            <div className="panel height-slider">
              <span className="height-bound" aria-hidden="true">{maxHeight}</span>
              <div className="height-rail">
                <span className="height-track" aria-hidden="true" style={{ background: `linear-gradient(to top, var(--amber) ${heightPercent}%, var(--line) ${heightPercent}%)` }} />
                <input
                  aria-label="Height filter"
                  aria-orientation="vertical"
                  type="range"
                  min={minHeight}
                  max={maxHeight}
                  value={heightLimit}
                  onChange={(event) => setHeightLimit(Number(event.target.value))}
                />
              </div>
              <span className="height-bound" aria-hidden="true">{minHeight}</span>
            </div>
          </details>
        </section>

        {selectedDrone && inspectionOpen && <section
          className="panel overlay-panel detail-panel"
          aria-label={`Unit ${selectedDrone.id} details`}
        >
          <header className="panel-heading">
            <h2>UNIT {selectedDrone.id}</h2>
            <span className={`state-tag ${selectedDrone.faulted ? "fault" : selectedDrone.connectedToTower ? "linked" : "isolated"}`}>
              {selectedDrone.faulted ? "FAULT" : selectedDrone.connectedToTower ? "LINKED" : "ISOLATED"}
            </span>
            <button type="button" className="detail-close" aria-label="Close unit details" onClick={() => setInspectionOpen(false)}>
              <svg aria-hidden="true" width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5">
                <path d="m4 4 8 8M12 4l-8 8" />
              </svg>
            </button>
          </header>
          <div className="overlay-content">
          {replaying ? <div className="detail-empty"><strong>RECORDED POSITION</strong>
            <p>{formatPosition(selectedDrone.position)}<br />{formatState(selectedDrone.behavior)}</p>
            <p>Individual scout inspection is live-only. Replay preserves hive and overall knowledge.</p>
          </div> : debug?.droneId === selectedDrone.id && debug.missionId === snapshot.missionId ? (
            <DroneInspector drone={selectedDrone} debug={debug} />
          ) : (
            <div className="detail-empty" role="status">
              <strong>{debugError ? "INSPECTION UNAVAILABLE" : "CONNECTING TO UNIT"}</strong>
              <p>{debugError ?? "Loading this unit's local knowledge."}</p>
            </div>
          )}
          </div>
        </section>}
      </section>
    </main>
  );
}

function Metric({ label, value, accent = false }: { label: string; value: string; accent?: boolean }) {
  return <div className={`metric ${accent ? "metric-accent" : ""}`}>
    <span>{label}</span><strong>{value}</strong>
  </div>;
}

function DroneRow({ drone, selected, onSelect }: { drone: PublicDrone; selected: boolean; onSelect: () => void }) {
  const signal = drone.faulted ? "faulted" : drone.connectedToTower ? "linked" : "isolated";
  return (
    <button className={`drone-row ${selected ? "selected" : ""}`} onClick={onSelect} title={drone.faulted ? "Faulted" : drone.connectedToTower ? "Connected to hive" : "Isolated"}>
      <span className={`drone-glyph ${signal}`}><i /></span>
      <span className="drone-info">
        <strong>{drone.id}</strong>
        <small>{formatState(drone.behavior)}{drone.position && !drone.positionCurrent ? " · LAST REPORTED" : ""}</small>
      </span>
      <span className={`row-signal ${signal}`} />
    </button>
  );
}

function DroneInspector({ drone, debug }: { drone: PublicDrone; debug: DroneDebug }) {
  const solids = debug.cells.filter((cell) => cell.solid).length;
  return (
    <div className="inspector">
      <div className="inspector-status">
        <span className={`pulse-ring ${drone.connectedToTower ? "active" : ""}`}><i /></span>
        <div><strong>{formatState(debug.behavior)}</strong><small>BEHAVIOUR STATE</small></div>
      </div>
      <div className="readout-grid">
        <Readout label="ACTUAL POSITION" value={formatPosition(debug.position)} />
        <Readout label="LAST REPORT" value={debug.lastReportAgeSeconds === null ? "STALE / LOST" : `${debug.lastReportAgeSeconds}s ago`} />
        <Readout label="LAST CONTACT" value={debug.lastContactAgeSeconds === null ? "UNKNOWN" : `${debug.lastContactAgeSeconds}s ago`} />
        <Readout label="LOCAL CELLS" value={debug.cells.length.toLocaleString()} />
        <Readout label="SOLID / AIR" value={`${solids} / ${debug.cells.length - solids}`} />
        <Readout label="SIMULATION TICK" value={debug.gameTick?.toLocaleString() ?? "—"} />
        <Readout label="HIVE LINK" value={drone.directlyConnectedToTower ? "DIRECT" : drone.connectedToTower ? "RELAYED" : "NONE"} />
        <Readout label="PEER CONTACTS" value={debug.connectedPeers.length ? debug.connectedPeers.join(", ") : "NONE"} />
        {debug.recoveryTargetId && <Readout label="RESCUE ASSIGNMENT" value={debug.recoveryTargetId} />}
        {debug.rendezvousTargetId && <Readout label="RENDEZVOUS TARGET" value={debug.rendezvousTargetId} />}
      </div>
      <details className="knowledge-box">
        <summary><span>LOCAL KNOWLEDGE</span><span className="knowledge-count">{debug.cells.length.toLocaleString()} CELLS</span></summary>
        <div className="cube-preview" aria-label="Local terrain cube">
          {Array.from({ length: 49 }, (_, index) => {
            const cell = debug.cells[index];
            return <i key={index} className={cell ? (cell.solid ? "solid" : "air") : "unknown"} />;
          })}
        </div>
        <small>OBSERVATIONS · SOLID CELLS / KNOWN AIR / UNKNOWN</small>
      </details>
      {debug.knownTarget && (
        <div className="target-report">
          <span className="target-report-icon">⌖</span>
          <span><strong>TARGET COORDINATES KNOWN</strong><small>{formatPosition(debug.knownTarget)}</small></span>
        </div>
      )}
      <div className="inspector-foot">
        <span>KNOWLEDGE IS DRONE-LOCAL</span>
        <span>{drone.knownCellCount.toLocaleString()} CELLS</span>
      </div>
    </div>
  );
}

function Readout({ label, value }: { label: string; value: string }) {
  return <div className="readout"><span>{label}</span><strong>{value}</strong></div>;
}

function formatPosition(position: { x: number; y: number; z: number } | null) {
  return position ? `${position.x.toFixed(2)}, ${position.y.toFixed(2)}, ${position.z.toFixed(2)}` : "—";
}

function phaseTitle(phase: string) {
  switch (phase) {
    case "searching": return "Search in progress";
    case "recovering": return "Recovery operation";
    case "recoveryIncomplete": return "Recovery incomplete";
    case "complete": return "Mission complete";
    case "searchExhausted": return "Search exhausted";
    default: return "Mission standby";
  }
}

function formatState(state: string) {
  return state.replace(/([a-z0-9])([A-Z])/g, "$1 $2").toUpperCase();
}
