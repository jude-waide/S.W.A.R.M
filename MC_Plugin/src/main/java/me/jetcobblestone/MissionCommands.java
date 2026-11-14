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

import java.net.URI;
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
            case "reset_mission" -> handleResetMission(sender, args);
            default -> false;
        };
    }

    // ---------------------------------------------------------------- set_hive

    private boolean handleSetHive(CommandSender sender, String[] args) {
        if (args.length != 3) return false;
        if (isMissionActive()) {
            sender.sendMessage("Stop the active mission before changing its hive.");
            return true;
        }
        Location loc = parseBlockLocation(sender, args, 0);
        if (loc == null) {
            sender.sendMessage("Invalid coordinates. Use numbers or ~ / ~n.");
            return true;
        }
        plugin.setHive(loc);
        sender.sendMessage("Hive set to " + fmt(loc));
        return true;
    }


    // -------------------------------------------------------------- set_target

    private boolean handleSetTarget(CommandSender sender, String[] args) {
        if (args.length != 3) return false;
        if (isMissionActive()) {
            sender.sendMessage("Stop the active mission before changing its target.");
            return true;
        }
        Location loc = parseBlockLocation(sender, args, 0);
        if (loc == null) {
            sender.sendMessage("Invalid coordinates. Use numbers or ~ / ~n.");
            return true;
        }

        sender.sendMessage("Target set to " + fmt(loc));
        plugin.setTarget(loc);
        return true;
    }

    // ------------------------------------------------------------ spawn_drones

    private boolean handleSpawnDrones(CommandSender sender, String[] args) {
        if (args.length < 1 || args.length > 3) return false;
        if (isMissionActive()) {
            sender.sendMessage("Stop the active mission before spawning drones.");
            return true;
        }

        int nDrones;
        int range = 3; // default
        int speed = 2; // default
        try {
            nDrones = Integer.parseInt(args[0]);
            if (args.length == 2) range = Integer.parseInt(args[1]);
            if (args.length == 3) speed = Integer.parseInt(args[2]);
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
        MissionBounds bounds = configuredBounds();
        if (!bounds.contains(hive.getBlockX(), hive.getBlockY(), hive.getBlockZ())) {
            sender.sendMessage("Set the hive inside the configured mission bounds before spawning drones.");
            return true;
        }

        int spawned = spawnDrones(hive, nDrones, range, speed, bounds);
        sender.sendMessage("Spawned " + spawned + "/" + nDrones + " drones around the hive.");
        if (spawned < nDrones) {
            sender.sendMessage("Not enough free space within range " + range + ".");
        }
        return true;
    }

    /** Spawns bees in distinct free cells within +-range of the hive. Returns how many spawned. */
    private int spawnDrones(Location hive, int count, int range, int speed, MissionBounds bounds) {
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
                    if (!bounds.contains(hx + dx, y, hz + dz)) continue;
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
                b.setPersistenceRequired(true);
            });
            plugin.getDroneManager().add_drone(bee, speed);

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
        if (args.length != 0) return false;
        if (isMissionActive()) {
            sender.sendMessage("A mission is already running.");
            return true;
        }

        Location hive = plugin.getHive();
        if (hive == null) {
            sender.sendMessage("Set the hive first with /set_hive.");
            return true;
        }
        Location target = plugin.getTarget();
        if (target == null) {
            sender.sendMessage("Set the target first with /set_target.");
            return true;
        }
        if (!hive.getWorld().equals(target.getWorld())) {
            sender.sendMessage("The hive and target must be in the same world.");
            return true;
        }
        if (plugin.getDroneManager().size() == 0) {
            sender.sendMessage("Spawn at least one drone before starting the mission.");
            return true;
        }
        if (!plugin.getDroneManager().allDronesInBounds(configuredBounds(), hive.getWorld())) {
            sender.sendMessage("Every drone must be available in the hive's world and inside the mission bounds.");
            return true;
        }

        String baseUrl = plugin.getConfig().getString("backend.base-url", "");
        String pluginKey = plugin.getConfig().getString("backend.plugin-key", "");
        baseUrl = baseUrl == null ? "" : baseUrl.trim();
        pluginKey = pluginKey == null ? "" : pluginKey.trim();
        if (baseUrl.isEmpty() || pluginKey.isEmpty()) {
            sender.sendMessage("Set backend.base-url and backend.plugin-key in plugins/DroneSim/config.yml.");
            return true;
        }

        MissionBounds bounds = configuredBounds();
        if (!bounds.contains(hive.getBlockX(), hive.getBlockY(), hive.getBlockZ())
                || !bounds.contains(target.getBlockX(), target.getBlockY(), target.getBlockZ())) {
            sender.sendMessage("Hive and target must be inside the configured mission bounds.");
            return true;
        }

        try {
            URI backendUri = URI.create(baseUrl);
            if (!("http".equalsIgnoreCase(backendUri.getScheme())
                    || "https".equalsIgnoreCase(backendUri.getScheme()))
                    || backendUri.getHost() == null) {
                sender.sendMessage("backend.base-url must be an absolute HTTP or HTTPS URL.");
                return true;
            }
        } catch (IllegalArgumentException exception) {
            sender.sendMessage("backend.base-url must be an absolute HTTP or HTTPS URL.");
            return true;
        }

        SessionManager session = new SessionManager(plugin, plugin.getDroneManager(), hive, target, bounds,
                baseUrl, pluginKey);
        plugin.setSessionManager(session);
        session.start();
        sender.sendMessage("Starting mission. Check the server log for backend acceptance.");

        return true;
    }

    private boolean isMissionActive() {
        SessionManager session = plugin.getSessionManager();
        return session != null && session.isActive();
    }

    private MissionBounds configuredBounds() {
        return new MissionBounds(
                plugin.getConfig().getInt("mission.bounds-min.x", -64),
                plugin.getConfig().getInt("mission.bounds-min.y", 32),
                plugin.getConfig().getInt("mission.bounds-min.z", -64));
    }

    private boolean handleResetMission(CommandSender sender, String[] args) {
        if (args.length != 0) return false;
        SessionManager session = plugin.getSessionManager();
        if (session == null) {
            sender.sendMessage("No mission session is available to reset. Restart the backend if it still has an old mission.");
            return true;
        }
        if (!session.resetMission()) {
            sender.sendMessage("Reset could not start; wait for mission initialisation or a pending reset to finish.");
            return true;
        }
        sender.sendMessage("Reset requested. Check the server log for backend confirmation.");
        return true;
    }


    // ----------------------------------------------------------------- helpers

    /** Parses x y z starting at args[offset]; supports "~" and "~n" relative to the sender. */
    private Location parseBlockLocation(CommandSender sender, String[] args, int offset) {
        Location origin = originOf(sender);
        World world = origin != null ? origin.getWorld() : Bukkit.getWorlds().getFirst();
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