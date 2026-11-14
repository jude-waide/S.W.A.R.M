# Local Minecraft test notebook

## Prepared environment

- Dashboard: <http://127.0.0.1:5000>
- Minecraft Java server: `127.0.0.1:25565` (Paper 26.3 / Java 25).
- Player `BatteriesAlex` is whitelisted and an operator.
- Server, Java runtime, credentials, logs and worlds stay **outside Git** in
  `C:\repos\Durhack\DroneSwarm.LocalTest`.
- Active normal-terrain world: `terrain-world`. The old flat `world` is preserved.
- Hive-centered bounds: X `288..415`, Y `49..112`, Z `-192..-65`.
- Hive: `352 81 -128`. Search target: `392 76 -98`.

The commands below are Minecraft chat commands. In the server console or RCON,
omit the leading `/`.

The commands use Minecraft's argument prompts and Tab completion. Hive/target
positions accept absolute block coordinates, `~` relative coordinates and `^`
local coordinates. `spawn_drones` prompts for `n_drones`, optional `drone_range`
and optional `speed`, with valid numeric suggestions.

`set_hive` moves the 128x64x128 mission bounds with the hive and prints their
inclusive limits. The vertical range stays inside the world's build height.
There is no bounds configuration to edit. After moving the hive, set a target
inside the new area and reposition the drones before starting.

## First search after starting Paper

For this local-only setup, set `backend.base-url` in the server's
`plugins\DroneSim\config.yml` to `http://127.0.0.1:5000` before starting Paper.
New plugin configurations default to the hosted Azure app instead.

Start the web app first, using the same local-only plugin key as
`plugins\DroneSim\config.yml`; never commit that key. Join Minecraft, then:

```text
/set_hive 352 81 -128
/set_target 392 76 -98
/spawn_drones 20 3 2
/tp @e[type=minecraft:bee,tag=drone_swarm] 352.5 82.25 -127.5
/start_mission
```

The explicit teleport is **setup only**, not movement execution: it places all
new units one cell **above the physical beehive**, in clear direct hive range
on uneven terrain. Never place bees inside the hive block. The planner assigns
distinct exits to units initially sharing a cell. Wait for mission acceptance
in the server log and `MINECRAFT` connected in the dashboard.

Do not run `spawn_drones` again when restarting the same search: it adds units
instead of replacing the current roster. This prepares 20 bees; spawn range 3
provides enough free setup cells before the teleport. The last argument is
speed in blocks per second (`1..5`); normal testing uses `2`.

## Reset and restart the search

```text
/reset_mission
```

**Wait until the dashboard says `AWAITING FIELD DATA`** and the server confirms
the reset. This clears mission/map/command state and stops the units; it does
not return them to the hive or respawn dead bees. Then:

```text
/tp @e[type=minecraft:bee,tag=drone_swarm] 352.5 82.25 -127.5
/set_target 392 76 -98
/start_mission
```

The hive remains configured until Paper restarts. To test fast target reporting
and recovery, use `/set_target 354 81 -128` before `start_mission`. Reset again
and restore the distant target for exploration.

`set_hive` creates a real beehive and `set_target` creates a real honey block,
each with a bright glowing entity outline and floating label. Use free,
non-liquid cells; solid terrain is not overwritten. The labels are for Minecraft
players only: bees still have to discover/report the target through their local
observations. Reset leaves both landmarks visible. Moving one or cleanly
stopping Paper removes its display entities and restores the original block
unless the player changed it.

Never teleport active search units: reset and wait first. If units died, reset
prunes their unavailable entries; `spawn_drones <missing-count> 1 2` can replenish
them before the setup teleport and next start.

## Dashboard controls

- **HIVE VIEW**: knowledge legitimately received by the hive; isolated units
  retain faded last-reported markers.
- **SCOUT VIEW**: choose a unit in the roster; see only that unit's accumulated
  terrain, own position and known target. No global-map fallback.
- **OVERALL VIEW**: debug overview of all observed terrain, live unit positions,
  and the actual target, including before discovery. This does not tell scouts
  or the hive where the target is.
- **HEIGHT FILTER**: reveal lower levels or cut away upper terrain.
- The white terrain outline now includes vertical corner connections.
- Nearer known terrain hides obstructed white outlines.
- Unit details appear only after selection; the compact card has a close button.
- Managed bees glow and have the `drone_swarm` entity tag.

Useful operator commands:

```text
/tp BatteriesAlex 354 83 -130
/gamemode creative BatteriesAlex
/execute as @e[type=minecraft:bee,tag=drone_swarm] run data get entity @s Pos
```

## Start the local processes if stopped

From the repository root, build/serve the web app in one PowerShell terminal:

```powershell
Push-Location web
npm run build
Pop-Location
$env:PLUGIN_KEY = "<same key as the local plugin config>"
dotnet run --project DroneSwarm.Web --urls http://127.0.0.1:5000
```

In another terminal:

```powershell
& 'C:\repos\Durhack\DroneSwarm.LocalTest\start-server.ps1'
```

For console commands from PowerShell, the local RCON helper reads its password
from the uncommitted server configuration:

```powershell
node 'C:\repos\Durhack\DroneSwarm.LocalTest\rcon.mjs' 'reset_mission'
# Wait for reset confirmation before the next commands.
node 'C:\repos\Durhack\DroneSwarm.LocalTest\rcon.mjs' `
  'tp @e[type=minecraft:bee,tag=drone_swarm] 352.5 82.25 -127.5' `
  'start_mission'
```

## Full restart / plugin update

1. Reset the mission and wait for confirmation.
2. Before stopping Paper, remove only the demo's tagged bees:
   `/kill @e[type=minecraft:bee,tag=drone_swarm]`.
3. Use `stop` in the Paper console. Do not use Bukkit `/reload`.
4. If updating the plugin, build with the prepared Java 25 runtime:
   ```powershell
   $env:JAVA_HOME = 'C:\repos\Durhack\DroneSwarm.LocalTest\java\jdk-25.0.5+8'
   .\MC_Plugin\gradlew.bat -p .\MC_Plugin test jar --no-daemon
   ```
   Copy `MC_Plugin\build\libs\MC_Plugin-1.0-SNAPSHOT.jar` to the external
   server's `plugins\DroneSim.jar` while Paper is stopped.
5. Restart Paper and repeat **First search** above.

The plugin's hive, target and managed roster are in memory. Persisted bees are
not automatically re-adopted after a restart. If Paper was stopped without
cleanup, remove the old tagged demo bees **before** spawning replacements.
If the backend still holds an old mission after a plugin restart, restart the
web app too, then initialize a fresh mission.

Physical upward/downward/collision/hover regression commands are documented in
the [README](../../README.md#physical-flight-regression-checks). The optional
flight-test companion is not installed during normal interactive use.
