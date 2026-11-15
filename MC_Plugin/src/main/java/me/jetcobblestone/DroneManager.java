package me.jetcobblestone;

import com.google.gson.JsonArray;
import com.google.gson.JsonElement;
import com.google.gson.JsonNull;
import com.google.gson.JsonObject;
import org.bukkit.Location;
import org.bukkit.World;
import org.bukkit.block.Block;
import org.bukkit.entity.Mob;
import org.bukkit.plugin.java.JavaPlugin;

import java.util.HashMap;
import java.util.Iterator;
import java.util.Locale;
import java.util.Map;
import java.util.UUID;


public class DroneManager {
    private int nextId;
    private final Map<String, DroneAI> drones = new HashMap<>();
    private final int drone_range = 3;
    private final JavaPlugin plugin;

    public DroneManager(JavaPlugin plugin) {
        this.plugin = plugin;
    }

    public void add_drone(Mob mob, double speed) {
        DroneAI drone = new DroneAI(plugin, mob, speed);
        drone.install();
        drones.put(Integer.toString(nextId++), drone);
    }

    public int size() {
        return drones.size();
    }

    public boolean allDronesInBounds(MissionBounds bounds, World missionWorld) {
        if (drones.isEmpty()) {
            return false;
        }
        for (DroneAI drone : drones.values()) {
            Mob mob = drone.getMob();
            if (!mob.isValid() || !mob.getWorld().equals(missionWorld)) {
                return false;
            }
            Location location = mob.getLocation();
            if (!bounds.contains(location.getBlockX(), location.getBlockY(), location.getBlockZ())) {
                return false;
            }
        }
        return true;
    }

    public JsonArray getInfo(MissionBounds bounds, World missionWorld) {
        return getInfo(bounds, missionWorld, true);
    }

    public JsonArray getInfo(MissionBounds bounds, World missionWorld, boolean includeCells) {

        JsonArray dronesJson = new JsonArray();

        for (Map.Entry<String, DroneAI> entry : drones.entrySet()) {
            Mob drone = entry.getValue().getMob();
            JsonObject droneJson = new JsonObject();
            droneJson.addProperty("droneId", entry.getKey());

            if (!drone.isValid() || !drone.getWorld().equals(missionWorld)) {
                droneJson.addProperty("availability", "unavailable");
                droneJson.addProperty("reason", "Drone entity is unavailable in the mission world");
                dronesJson.add(droneJson);
                continue;
            }

            Location location = drone.getLocation();
            int x = location.getBlockX();
            int y = location.getBlockY();
            int z = location.getBlockZ();
            JsonArray cells = new JsonArray();
            int maxX = bounds.minX() + MissionBounds.WIDTH - 1;
            int maxY = bounds.minY() + MissionBounds.HEIGHT - 1;
            int maxZ = bounds.minZ() + MissionBounds.DEPTH - 1;
            if (includeCells)
            for (int xb = Math.max(bounds.minX(), x - drone_range);
                 xb <= Math.min(maxX, x + drone_range); xb++) {
                for (int yb = Math.max(bounds.minY(), y - drone_range);
                     yb <= Math.min(maxY, y + drone_range); yb++) {
                    for (int zb = Math.max(bounds.minZ(), z - drone_range);
                         zb <= Math.min(maxZ, z + drone_range); zb++) {
                        Block block = missionWorld.getBlockAt(xb, yb, zb);

                        JsonObject cell = new JsonObject();
                        cell.addProperty("x", xb);
                        cell.addProperty("y", yb);
                        cell.addProperty("z", zb);
                        cell.addProperty("solid", !block.isPassable() || block.isLiquid());
                        cells.add(cell);
                    }
                }
            }

            JsonObject position = new JsonObject();
            position.addProperty("x", location.getX());
            position.addProperty("y", location.getY());
            position.addProperty("z", location.getZ());

            droneJson.addProperty("availability", "available");
            droneJson.add("position", position);
            droneJson.add("cells", cells);
            DroneAI.Result result = entry.getValue().getPendingResult();
            if (result == null) {
                droneJson.add("commandResult", JsonNull.INSTANCE);
            } else {
                JsonObject commandResult = new JsonObject();
                commandResult.addProperty("commandId", result.commandId().toString());
                commandResult.addProperty("status", result.status().name().toLowerCase(Locale.ROOT));
                commandResult.addProperty("reason", result.reason());
                droneJson.add("commandResult", commandResult);
            }

            dronesJson.add(droneJson);
        }

        return dronesJson;
    }

    public void applyResponse(JsonObject response) {
        JsonArray acknowledgements = response.getAsJsonArray("acknowledgedResults");
        if (acknowledgements == null) {
            throw new IllegalStateException("Backend response is missing acknowledgedResults");
        }
        for (JsonElement element : acknowledgements) {
            JsonObject acknowledgement = element.getAsJsonObject();
            String droneId = acknowledgement.get("droneId").getAsString();
            DroneAI drone = drones.get(droneId);
            if (drone != null) {
                drone.acknowledge(UUID.fromString(acknowledgement.get("commandId").getAsString()));
            } else {
                plugin.getLogger().warning("Backend acknowledged a result for unknown drone " + droneId);
            }
        }

        JsonArray commands = response.getAsJsonArray("commands");
        if (commands == null) {
            throw new IllegalStateException("Backend response is missing commands");
        }
        for (JsonElement command : commands) {

            JsonObject obj = command.getAsJsonObject();
            String droneId = obj.get("droneId").getAsString();
            UUID commandId = UUID.fromString(obj.get("commandId").getAsString());
            String kind = obj.get("kind").getAsString();

            DroneAI drone = drones.get(droneId);
            if (drone == null) {
                plugin.getLogger().warning("Backend issued a command for unknown drone " + droneId);
                continue;
            }
            if (!kind.equals("move")) {
                plugin.getLogger().warning("Backend issued unsupported command kind " + kind);
                continue;
            }
            JsonObject destination = obj.getAsJsonObject("destination");
            if (destination == null) {
                throw new IllegalStateException("Movement command is missing destination");
            }
            drone.issueMove(
                    commandId,
                    destination.get("x").getAsInt(),
                    destination.get("y").getAsInt(),
                    destination.get("z").getAsInt(),
                    obj.has("afterCommandId") && !obj.get("afterCommandId").isJsonNull()
                            ? UUID.fromString(obj.get("afterCommandId").getAsString()) : null
            );
        }
    }

    public void uninstallAll() {
        for (DroneAI drone : drones.values()) {
            if (drone.getMob().isValid()) {
                drone.uninstall();
            }
        }
    }

    public void resetAll() {
        Iterator<Map.Entry<String, DroneAI>> iterator = drones.entrySet().iterator();
        while (iterator.hasNext()) {
            DroneAI drone = iterator.next().getValue();
            if (!drone.getMob().isValid()) {
                iterator.remove();
            } else {
                drone.reset();
            }
        }

    }

    public int removeAll() {
        int removed = 0;
        for (DroneAI drone : drones.values()) {
            if (drone.getMob().isValid()) {
                drone.uninstall();
                drone.getMob().remove();
                removed++;
            }
        }
        drones.clear();
        nextId = 0;
        return removed;
    }
}
