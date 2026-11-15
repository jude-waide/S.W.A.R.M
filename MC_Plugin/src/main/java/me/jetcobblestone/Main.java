package me.jetcobblestone;

import org.bukkit.Location;
import org.bukkit.Color;
import org.bukkit.Material;
import io.papermc.paper.plugin.lifecycle.event.types.LifecycleEvents;
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
        if (getConfig().isSet("mission.bounds-min")) {
            getLogger().warning("mission.bounds-min is no longer used; mission bounds are centered on the hive.");
        }
        droneManager = new DroneManager(this);
        MissionCommands commands = new MissionCommands(this);
        getLifecycleManager().registerEventHandler(LifecycleEvents.COMMANDS,
                event -> commands.register(event.registrar()));
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

    public int clearMission() {
        int removed = droneManager.removeAll();
        for (var world : getServer().getWorlds()) {
            for (var bee : world.getEntitiesByClass(org.bukkit.entity.Bee.class)) {
                if (bee.getScoreboardTags().contains("drone_swarm")) {
                    bee.remove();
                    removed++;
                }
            }
        }
        if (hiveLandmark != null) hiveLandmark.close();
        if (targetLandmark != null) targetLandmark.close();
        hiveLandmark = null;
        targetLandmark = null;
        hive = null;
        target = null;
        retainLandmarkChunks();
        return removed;
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
