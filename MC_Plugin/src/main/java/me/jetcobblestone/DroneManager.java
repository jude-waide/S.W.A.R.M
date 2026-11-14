package me.jetcobblestone;

import com.google.gson.JsonArray;
import com.google.gson.JsonElement;
import com.google.gson.JsonNull;
import com.google.gson.JsonObject;
import org.bukkit.Location;
import org.bukkit.World;
import org.bukkit.block.Block;
import org.bukkit.entity.Entity;
import org.bukkit.entity.Mob;
import org.bukkit.plugin.java.JavaPlugin;

import java.util.AbstractMap;
import java.util.HashMap;
import java.util.Map;
import java.util.UUID;


public class DroneManager {
    private int next_id = 0;
    private final AbstractMap<Integer, DroneAI> drones = new HashMap<>();
    private final int drone_range = 3;
    private final JavaPlugin plugin;

    public DroneManager (JavaPlugin plugin) {
        this.plugin = plugin;
    }

    public void add_drone(Mob mob, double speed) {
        DroneAI drone = new DroneAI(plugin, mob, speed);
        drone.install();
        drones.put(next_id, drone);
    }

    public JsonArray getInfo() {

        JsonArray dronesJson = new JsonArray();

        for (Map.Entry<Integer, DroneAI> entry : drones.entrySet()) {
            Entity drone = entry.getValue().getMob();
            World world = drone.getWorld();
            Location location = drone.getLocation();

            int x = location.getBlockX();
            int y = location.getBlockY();
            int z = location.getBlockZ();

            JsonArray cells = new JsonArray();
            for (int xb = x - drone_range; xb <= x + drone_range; xb++) {
                for (int yb = y - drone_range; yb <= y + drone_range; yb++) {
                    for (int zb = z - drone_range; zb <= z + drone_range; zb++) {
                        Block block = world.getBlockAt(xb, yb, zb);

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

            JsonObject droneJson = new JsonObject();
            droneJson.addProperty("droneId", entry.getKey());
            droneJson.addProperty("availability", drone.isValid() ? "available" : "unavailable");
            droneJson.add("position", position);
            droneJson.add("cells", cells);
            droneJson.add("commandResult", JsonNull.INSTANCE);

            dronesJson.add(droneJson);
        }

        return dronesJson;
    }

    public void moveDrones(JsonArray commands) {

        for (JsonElement command : commands.asList()) {

            JsonObject obj = command.getAsJsonObject();
            int drone_id = obj.get("drone_id").getAsInt();
            UUID command_id = UUID.fromString(obj.get("commandId").getAsString());
            String kind = obj.get("kind").getAsString();

            if (drones.containsKey(drone_id)) {
                DroneAI drone = drones.get(drone_id);

                if (kind.equals("move")) {
                    drone.issueMove(
                            command_id,
                            obj.get("x").getAsInt(),
                            obj.get("y").getAsInt(),
                            obj.get("z").getAsInt()
                    );
                }
            }
            else {
                // Raise error
                plugin.getSLF4JLogger().warn("Tried to move an unrecognised drone: id {}", drone_id);
            }
        }
    }
}
