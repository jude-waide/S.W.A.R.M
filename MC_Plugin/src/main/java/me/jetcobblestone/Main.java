package me.jetcobblestone;

import org.bukkit.Location;
import org.bukkit.Color;
import org.bukkit.Material;
import org.bukkit.command.PluginCommand;
import org.bukkit.plugin.java.JavaPlugin;

public final class Main extends JavaPlugin {
    private Location hive;
    private Location target;
    private DroneManager droneManager;
    private SessionManager sessionManager;
    private MissionLandmark hiveLandmark;
    private MissionLandmark targetLandmark;

    @Override
    public void onEnable() {
        saveDefaultConfig();
        droneManager = new DroneManager(this);
        MissionCommands commands = new MissionCommands(this);
        for (String name : new String[]{
                "set_hive", "set_target", "spawn_drones", "start_mission", "reset_mission"}) {
            PluginCommand command = getCommand(name);
            if (command == null) {
                throw new IllegalStateException("Command is missing from plugin.yml: " + name);
            }
            command.setExecutor(commands);
        }
    }

    @Override
    public void onDisable() {
        if (sessionManager != null) {
            sessionManager.stop();
        }
        if (droneManager != null) {
            droneManager.uninstallAll();
        }
        if (hiveLandmark != null) hiveLandmark.close();
        if (targetLandmark != null) targetLandmark.close();
        getServer().getWorlds().forEach(world -> world.removePluginChunkTickets(this));
    }

    public Location getHive() { return hive; }
    public void setHive(Location hive) {
        if (hive.equals(this.hive)) {
            if (hiveLandmark.isIntact()) return;
            hiveLandmark.close();
            hiveLandmark = null;
            this.hive = null;
            retainLandmarkChunks();
        }
        MissionLandmark replacement = new MissionLandmark(hive, Material.BEEHIVE, "HIVE", Color.ORANGE);
        if (hiveLandmark != null) hiveLandmark.close();
        hiveLandmark = replacement;
        this.hive = hive.clone();
        retainLandmarkChunks();
    }

    public Location getTarget() { return target; }
    public void setTarget(Location target) {
        if (target.equals(this.target)) {
            if (targetLandmark.isIntact()) return;
            targetLandmark.close();
            targetLandmark = null;
            this.target = null;
            retainLandmarkChunks();
        }
        MissionLandmark replacement = new MissionLandmark(target, Material.HONEY_BLOCK, "SEARCH TARGET", Color.YELLOW);
        if (targetLandmark != null) targetLandmark.close();
        targetLandmark = replacement;
        this.target = target.clone();
        retainLandmarkChunks();
    }

    private void retainLandmarkChunks() {
        // Rebuild tickets together: the two landmarks may share a chunk.
        getServer().getWorlds().forEach(world -> world.removePluginChunkTickets(this));
        if (hive != null) hive.getChunk().addPluginChunkTicket(this);
        if (target != null) target.getChunk().addPluginChunkTicket(this);
    }

    public DroneManager getDroneManager() {
        return droneManager;
    }

    public SessionManager getSessionManager() {
        return sessionManager;
    }

    public void setSessionManager(SessionManager sessionManager) {
        this.sessionManager = sessionManager;
    }
}
