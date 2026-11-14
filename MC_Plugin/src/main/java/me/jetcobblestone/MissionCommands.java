package me.jetcobblestone;

import com.mojang.brigadier.arguments.IntegerArgumentType;
import com.mojang.brigadier.context.CommandContext;
import com.mojang.brigadier.exceptions.CommandSyntaxException;
import com.mojang.brigadier.suggestion.Suggestions;
import com.mojang.brigadier.suggestion.SuggestionsBuilder;
import io.papermc.paper.command.brigadier.CommandSourceStack;
import io.papermc.paper.command.brigadier.Commands;
import io.papermc.paper.command.brigadier.argument.ArgumentTypes;
import io.papermc.paper.command.brigadier.argument.resolvers.BlockPositionResolver;
import org.bukkit.Location;
import org.bukkit.World;
import org.bukkit.block.Block;
import org.bukkit.command.CommandSender;
import org.bukkit.entity.Bee;

import java.net.URI;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.ThreadLocalRandom;

public class MissionCommands {

    private static final int MAX_DRONES = 64;
    private static final int MAX_RANGE = 3; // protocol: drones observe a +-3 cube

    private final Main plugin;

    public MissionCommands(Main plugin) {
        this.plugin = plugin;
    }

    public void register(Commands registrar) {
        registrar.register(Commands.literal("set_hive")
                .then(Commands.argument("position", ArgumentTypes.blockPosition())
                        .executes(context -> handleSetHive(context.getSource().getSender(), position(context)) ? 1 : 0))
                .build(), "Place a glowing beehive and center the mission bounds on it.");
        registrar.register(Commands.literal("set_target")
                .then(Commands.argument("position", ArgumentTypes.blockPosition())
                        .executes(context -> handleSetTarget(context.getSource().getSender(), position(context)) ? 1 : 0))
                .build(), "Place a glowing honey-block target inside the hive's mission bounds.");
        registrar.register(Commands.literal("spawn_drones")
                .then(Commands.argument("n_drones", IntegerArgumentType.integer(1, MAX_DRONES))
                        .suggests((context, builder) -> suggestNumbers(builder, 1, 5, 10, 20, MAX_DRONES))
                        .executes(context -> handleSpawnDrones(context.getSource().getSender(),
                                IntegerArgumentType.getInteger(context, "n_drones"), 3, 2) ? 1 : 0)
                        .then(Commands.argument("drone_range", IntegerArgumentType.integer(1, MAX_RANGE))
                                .suggests((context, builder) -> suggestNumbers(builder, 1, 2, 3))
                                .executes(context -> handleSpawnDrones(context.getSource().getSender(),
                                        IntegerArgumentType.getInteger(context, "n_drones"),
                                        IntegerArgumentType.getInteger(context, "drone_range"), 2) ? 1 : 0)
                                .then(Commands.argument("speed", IntegerArgumentType.integer(1, 5))
                                        .suggests((context, builder) -> suggestNumbers(builder, 1, 2, 3, 4, 5))
                                        .executes(context -> handleSpawnDrones(context.getSource().getSender(),
                                                IntegerArgumentType.getInteger(context, "n_drones"),
                                                IntegerArgumentType.getInteger(context, "drone_range"),
                                                IntegerArgumentType.getInteger(context, "speed")) ? 1 : 0))))
                .build(), "Spawn 1-64 drones; optional range 1-3 (default 3), speed 1-5 blocks/s (default 2).");
        registrar.register(Commands.literal("start_mission")
                .executes(context -> handleStartMission(context.getSource().getSender()) ? 1 : 0)
                .build(), "Begin the configured mission.");
        registrar.register(Commands.literal("reset_mission")
                .executes(context -> handleResetMission(context.getSource().getSender()) ? 1 : 0)
                .build(), "Reset the active mission and stop telemetry.");
    }

    // ---------------------------------------------------------------- set_hive

    private boolean handleSetHive(CommandSender sender, Location loc) {
        if (isMissionActive()) {
            sender.sendMessage("Stop the active mission before changing its hive.");
            return true;
        }
        try {
            MissionBounds bounds = boundsAt(loc);
            plugin.setHive(loc);
            sender.sendMessage("Glowing beehive placed at " + fmt(loc));
            sender.sendMessage("Mission bounds: " + bounds.minX() + " " + bounds.minY() + " " + bounds.minZ()
                    + " to " + (bounds.minX() + MissionBounds.WIDTH - 1) + " "
                    + (bounds.minY() + MissionBounds.HEIGHT - 1) + " "
                    + (bounds.minZ() + MissionBounds.DEPTH - 1) + ".");
            Location target = plugin.getTarget();
            if (target != null && (!target.getWorld().equals(loc.getWorld())
                    || !bounds.contains(target.getBlockX(), target.getBlockY(), target.getBlockZ()))) {
                sender.sendMessage("The existing target is outside the new mission area; move it with /set_target before starting.");
            }
        } catch (IllegalArgumentException exception) {
            sender.sendMessage("Could not place hive: " + exception.getMessage());
        }
        return true;
    }


    // -------------------------------------------------------------- set_target

    private boolean handleSetTarget(CommandSender sender, Location loc) {
        if (isMissionActive()) {
            sender.sendMessage("Stop the active mission before changing its target.");
            return true;
        }
        Location hive = plugin.getHive();
        if (hive == null) {
            sender.sendMessage("Set the hive first with /set_hive <x> <y> <z>.");
            return true;
        }

        if (!hive.getWorld().equals(loc.getWorld())
                || !boundsAt(hive).contains(loc.getBlockX(), loc.getBlockY(), loc.getBlockZ())) {
            sender.sendMessage("Place the target inside the hive's mission bounds and world.");
            return true;
        }
        try {
            plugin.setTarget(loc);
            sender.sendMessage("Glowing honey target placed at " + fmt(loc));
        } catch (IllegalArgumentException exception) {
            sender.sendMessage("Could not place target: " + exception.getMessage());
        }
        return true;
    }

    // ------------------------------------------------------------ spawn_drones

    private boolean handleSpawnDrones(CommandSender sender, int nDrones, int range, int speed) {
        if (isMissionActive()) {
            sender.sendMessage("Stop the active mission before spawning drones.");
            return true;
        }

        Location hive = plugin.getHive();
        if (hive == null) {
            sender.sendMessage("Set the hive first with /set_hive.");
            return true;
        }
        MissionBounds bounds = boundsAt(hive);

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
            Location spawnLoc = cell.getLocation().add(0.5, 0.2, 0.5);
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

    private boolean handleStartMission(CommandSender sender) {
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
        if (!plugin.getDroneManager().allDronesInBounds(boundsAt(hive), hive.getWorld())) {
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

        MissionBounds bounds = boundsAt(hive);
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

    private MissionBounds boundsAt(Location hive) {
        return MissionBounds.centeredOn(hive.getBlockX(), hive.getBlockY(), hive.getBlockZ(),
                hive.getWorld().getMinHeight(), hive.getWorld().getMaxHeight());
    }

    private boolean handleResetMission(CommandSender sender) {
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

    private static CompletableFuture<Suggestions> suggestNumbers(SuggestionsBuilder builder, int... values) {
        for (int value : values) {
            if (Integer.toString(value).startsWith(builder.getRemaining())) builder.suggest(value);
        }
        return builder.buildFuture();
    }

    private Location position(CommandContext<CommandSourceStack> context) throws CommandSyntaxException {
        return context.getArgument("position", BlockPositionResolver.class).resolve(context.getSource())
                .toLocation(context.getSource().getLocation().getWorld());
    }

    private String fmt(Location l) {
        return l.getBlockX() + " " + l.getBlockY() + " " + l.getBlockZ() + " (" + l.getWorld().getName() + ")";
    }
}