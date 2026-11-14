package me.jetcobblestone;

import org.bukkit.Bukkit;
import org.bukkit.Location;
import org.bukkit.World;
import org.bukkit.block.Block;
import org.bukkit.command.BlockCommandSender;
import org.bukkit.command.Command;
import org.bukkit.command.CommandExecutor;
import org.bukkit.command.CommandSender;
import org.bukkit.entity.Bee;
import org.bukkit.entity.Entity;
import org.jetbrains.annotations.NotNull;

import java.util.concurrent.ThreadLocalRandom;

public class MissionCommands implements CommandExecutor {

    private static final int MAX_DRONES = 64;
    private static final int MAX_RANGE = 3; // protocol: drones observe a +-3 cube

    private final Main plugin;

    public MissionCommands(Main plugin) {
        this.plugin = plugin;
    }

    @Override
    public boolean onCommand(@NotNull CommandSender sender, @NotNull Command command,
                             @NotNull String label, @NotNull String[] args) {
        return switch (command.getName().toLowerCase()) {
            case "set_hive" -> handleSetHive(sender, args);
            case "set_target" -> handleSetTarget(sender, args);
            case "spawn_drones" -> handleSpawnDrones(sender, args);
            case "start_mission" -> handleStartMission(sender, args);
            default -> false;
        };
    }

    // ---------------------------------------------------------------- set_hive

    private boolean handleSetHive(CommandSender sender, String[] args) {
        if (args.length != 3) return false;
        Location loc = parseBlockLocation(sender, args, 0);
        if (loc == null) {
            sender.sendMessage("Invalid coordinates. Use numbers or ~ / ~n.");
            return true;
        }
        setHive(sender, loc);
        return true;
    }

    private void setHive(CommandSender sender, Location hive) {
        // Stored so spawn_drones can use it; add further setup (marker block, etc.) here.
        plugin.setHive(hive);
        sender.sendMessage("Hive set to " + fmt(hive));
    }

    // -------------------------------------------------------------- set_target

    private boolean handleSetTarget(CommandSender sender, String[] args) {
        if (args.length != 3) return false;
        Location loc = parseBlockLocation(sender, args, 0);
        if (loc == null) {
            sender.sendMessage("Invalid coordinates. Use numbers or ~ / ~n.");
            return true;
        }
        setTarget(sender, loc);
        return true;
    }

    private void setTarget(CommandSender sender, Location target) {
        // TODO: implement
    }

    // ------------------------------------------------------------ spawn_drones

    private boolean handleSpawnDrones(CommandSender sender, String[] args) {
        if (args.length < 1 || args.length > 2) return false;

        int nDrones;
        int range = 3; // default
        try {
            nDrones = Integer.parseInt(args[0]);
            if (args.length == 2) range = Integer.parseInt(args[1]);
        } catch (NumberFormatException e) {
            sender.sendMessage("n_drones and drone_range must be whole numbers.");
            return true;
        }

        if (nDrones < 1 || nDrones > MAX_DRONES) {
            sender.sendMessage("n_drones must be between 1 and " + MAX_DRONES + ".");
            return true;
        }
        if (range < 1 || range > MAX_RANGE) {
            sender.sendMessage("drone_range must be between 1 and " + MAX_RANGE + ".");
            return true;
        }

        Location hive = plugin.getHive();
        if (hive == null) {
            sender.sendMessage("Set the hive first with /set_hive.");
            return true;
        }

        int spawned = spawnDrones(hive, nDrones, range);
        sender.sendMessage("Spawned " + spawned + "/" + nDrones + " drones around the hive.");
        if (spawned < nDrones) {
            sender.sendMessage("Not enough free space within range " + range + ".");
        }
        return true;
    }

    /** Spawns bees in distinct free cells within +-range of the hive. Returns how many spawned. */
    private int spawnDrones(Location hive, int count, int range) {
        World world = hive.getWorld();
        int hx = hive.getBlockX(), hy = hive.getBlockY(), hz = hive.getBlockZ();

        // Collect every free cell in the cube, excluding the hive cell itself.
        java.util.List<Block> candidates = new java.util.ArrayList<>();
        for (int dx = -range; dx <= range; dx++) {
            for (int dy = -range; dy <= range; dy++) {
                for (int dz = -range; dz <= range; dz++) {
                    if (dx == 0 && dy == 0 && dz == 0) continue;
                    int y = hy + dy;
                    if (y < world.getMinHeight() || y >= world.getMaxHeight()) continue;
                    Block b = world.getBlockAt(hx + dx, y, hz + dz);
                    if (isFree(b)) candidates.add(b);
                }
            }
        }

        // Pick distinct cells at random (shuffle, then take the first `count`).
        java.util.Collections.shuffle(candidates, ThreadLocalRandom.current());
        int spawned = 0;
        for (Block cell : candidates) {
            if (spawned >= count) break;
            Location spawnLoc = cell.getLocation().add(0.5, 0.0, 0.5);
            Bee bee = world.spawn(spawnLoc, Bee.class, b -> {
                b.setPersistent(true);
                b.setRemoveWhenFarAway(false);
                // TODO: install your DroneAI here (removes vanilla goals)
            });
            plugin.getDrones().add(bee);
            spawned++;
        }
        return spawned;
    }

    /** A cell is free if it and the cell above are passable and contain no liquid. */
    private boolean isFree(Block b) {
        Block above = b.getRelative(0, 1, 0);
        return b.isPassable() && !b.isLiquid()
                && above.isPassable() && !above.isLiquid();
    }

    // ----------------------------------------------------------- start_mission

    private boolean handleStartMission(CommandSender sender, String[] args) {
        if (args.length != 1) return false;
        startMission(sender, args[0]);
        return true;
    }

    private void startMission(CommandSender sender, String url) {
        // TODO: implement
    }

    // ----------------------------------------------------------------- helpers

    /** Parses x y z starting at args[offset]; supports "~" and "~n" relative to the sender. */
    private Location parseBlockLocation(CommandSender sender, String[] args, int offset) {
        Location origin = originOf(sender);
        World world = origin != null ? origin.getWorld() : Bukkit.getWorlds().get(0);
        try {
            int x = parseCoord(args[offset], origin == null ? 0 : origin.getX(), origin != null);
            int y = parseCoord(args[offset + 1], origin == null ? 0 : origin.getY(), origin != null);
            int z = parseCoord(args[offset + 2], origin == null ? 0 : origin.getZ(), origin != null);
            return new Location(world, x, y, z);
        } catch (NumberFormatException e) {
            return null;
        }
    }

    private int parseCoord(String s, double base, boolean relativeAllowed) {
        if (s.startsWith("~")) {
            if (!relativeAllowed) throw new NumberFormatException("relative coords need a position");
            double offset = s.length() > 1 ? Double.parseDouble(s.substring(1)) : 0;
            return (int) Math.floor(base + offset);
        }
        return (int) Math.floor(Double.parseDouble(s));
    }

    private Location originOf(CommandSender sender) {
        if (sender instanceof Entity e) return e.getLocation();
        if (sender instanceof BlockCommandSender b) return b.getBlock().getLocation();
        return null; // console
    }

    private String fmt(Location l) {
        return l.getBlockX() + " " + l.getBlockY() + " " + l.getBlockZ() + " (" + l.getWorld().getName() + ")";
    }
}