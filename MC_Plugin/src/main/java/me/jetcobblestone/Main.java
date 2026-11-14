package me.jetcobblestone;

import org.bukkit.Location;
import org.bukkit.plugin.PluginCommand;
import org.bukkit.plugin.java.JavaPlugin;

public final class Main extends JavaPlugin {
    private Location hive;
    private Location target;
    private DroneManager droneManager;
    private SessionManager sessionManager;

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
    }

    public Location getHive() { return hive; }
    public void setHive(Location hive) { this.hive = hive; }

    public Location getTarget() { return target; }
    public void setTarget(Location target) { this.target = target; }

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
