package me.jetcobblestone;

import org.bukkit.Location;
import org.bukkit.entity.Bee;
import org.bukkit.event.EventHandler;
import org.bukkit.event.Listener;
import org.bukkit.event.player.PlayerJoinEvent;
import org.bukkit.plugin.java.JavaPlugin;

import java.util.ArrayList;
import java.util.List;

public final class Main extends JavaPlugin {


    private Location hive;      // command centre (block cell), null until set
    private Location target;    // target (block cell), null until set
    private final List<Bee> drones = new ArrayList<>();

    @Override
    public void onEnable() {
        MissionCommands commands = new MissionCommands(this);
        for (String name : new String[]{"set_hive", "set_target", "spawn_drones", "start_mission"}) {
            getCommand(name).setExecutor(commands);
        }
    }

    public Location getHive() { return hive; }
    public void setHive(Location hive) { this.hive = hive; }

    public Location getTarget() { return target; }
    public void setTarget(Location target) { this.target = target; }

    public List<Bee> getDrones() { return drones; }


}