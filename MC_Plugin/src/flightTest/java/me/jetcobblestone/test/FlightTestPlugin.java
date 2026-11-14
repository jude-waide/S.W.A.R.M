package me.jetcobblestone.test;

import me.jetcobblestone.DroneAI;
import org.bukkit.Location;
import org.bukkit.Material;
import org.bukkit.block.BlockState;
import org.bukkit.entity.Bee;
import org.bukkit.plugin.java.JavaPlugin;
import org.bukkit.scheduler.BukkitRunnable;
import org.bukkit.util.BoundingBox;
import org.bukkit.util.Vector;

import java.util.Objects;
import java.util.Set;
import java.util.UUID;

/** Opt-in real-Paper regression tests; never included in the production plugin. */
public final class FlightTestPlugin extends JavaPlugin {
    private DroneAI flight;
    private Bee bee;
    private BlockState changedBlock;

    @Override
    public void onEnable() {
        Objects.requireNonNull(getCommand("test_mission_setup")).setExecutor((sender, command, label, args) -> {
            if (args.length != 0) return false;
            var commands = getServer().getCommandMap();
            var counts = Set.copyOf(Objects.requireNonNull(commands.tabComplete(sender, "spawn_drones ")));
            var filtered = Set.copyOf(Objects.requireNonNull(commands.tabComplete(sender, "spawn_drones 6")));
            var ranges = Set.copyOf(Objects.requireNonNull(commands.tabComplete(sender, "spawn_drones 5 ")));
            var speeds = Set.copyOf(Objects.requireNonNull(commands.tabComplete(sender, "spawn_drones 5 1 ")));
            boolean pass = counts.equals(Set.of("1", "5", "10", "20", "64"))
                    && filtered.equals(Set.of("64"))
                    && ranges.equals(Set.of("1", "2", "3"))
                    && speeds.equals(Set.of("1", "2", "3", "4", "5"));
            String message = "SETUP_TEST completion " + (pass ? "PASS" : "FAIL")
                    + " counts=" + counts + " filtered=" + filtered + " ranges=" + ranges + " speeds=" + speeds;
            if (pass) getLogger().info(message);
            else getLogger().severe(message);
            sender.sendMessage(message);
            return true;
        });
        Objects.requireNonNull(getCommand("test_drone_flight")).setExecutor((sender, command, label, args) -> {
            if (args.length != 4 || !Set.of("up", "down", "horizontal", "blocked", "ceiling").contains(args[0]))
                return false;
            if (flight != null) {
                sender.sendMessage("A flight test is already running.");
                return true;
            }
            int x, y, z;
            try {
                x = Integer.parseInt(args[1]);
                y = Integer.parseInt(args[2]);
                z = Integer.parseInt(args[3]);
            } catch (NumberFormatException exception) {
                sender.sendMessage("Test coordinates must be whole numbers.");
                return true;
            }
            var world = getServer().getWorlds().getFirst();
            if (y - 1 < world.getMinHeight() || y + 3 >= world.getMaxHeight()
                    || world.hasCollisionsIn(new BoundingBox(x, y - 1, z, x + 2, y + 3, z + 1))) {
                sender.sendMessage("Choose an empty 2x4x1 test area inside the world's height bounds.");
                return true;
            }
            for (int bx = x; bx <= x + 1; bx++)
                for (int by = y - 1; by <= y + 2; by++)
                    if (world.getBlockAt(bx, by, z).isLiquid()) {
                        sender.sendMessage("The test area must not contain liquid.");
                        return true;
                    }

            String mode = args[0];
            boolean horizontal = Set.of("horizontal", "blocked", "ceiling").contains(mode);
            int targetX = horizontal ? x + 1 : x;
            int targetY = mode.equals("up") ? y + 1 : mode.equals("down") ? y - 1 : y;
            Location start = new Location(world, x + 0.5, y + 0.2, z + 0.5);
            bee = world.spawn(start, Bee.class);
            JavaPlugin main = Objects.requireNonNull(JavaPlugin.getProvidingPlugin(DroneAI.class));
            flight = new DroneAI(main, bee, 2);
            flight.install();
            if (mode.equals("ceiling")) {
                changedBlock = world.getBlockAt(targetX, targetY + 1, z).getState();
                changedBlock.getBlock().setType(Material.STONE);
            }
            UUID id = UUID.randomUUID();
            flight.issueMove(id, targetX, targetY, z);
            if (mode.equals("blocked")) {
                changedBlock = world.getBlockAt(targetX, targetY, z).getState();
                changedBlock.getBlock().setType(Material.STONE);
            }
            sender.sendMessage("Started " + mode + " flight test.");
            new BukkitRunnable() {
                int ticks;
                int terminalTick;
                Vector terminalPosition;

                @Override
                public void run() {
                    ticks++;
                    DroneAI.Result result = flight.getPendingResult();
                    if (result != null && terminalPosition == null) {
                        terminalPosition = bee.getLocation().toVector();
                        terminalTick = ticks;
                    }
                    if (ticks < 140 && (terminalPosition == null || ticks < terminalTick + 20)) return;
                    Location end = bee.getLocation();
                    boolean held = terminalPosition != null && end.toVector().distanceSquared(terminalPosition) < 0.01;
                    boolean pass = result != null && result.commandId().equals(id) && held && bee.isGlowing();
                    if (mode.equals("blocked")) {
                        pass &= result != null && result.status() == DroneAI.Status.BLOCKED
                                && end.getX() < targetX;
                    } else {
                        pass &= result != null && result.status() == DroneAI.Status.COMPLETED
                                && end.getBlockX() == targetX && end.getBlockY() == targetY && end.getBlockZ() == z
                                && end.distanceSquared(new Location(world, targetX + 0.5,
                                    targetY + (1 - bee.getHeight()) / 2, z + 0.5)) <= 0.06 * 0.06;
                    }
                    String message = "FLIGHT_TEST " + mode + " " + (pass ? "PASS" : "FAIL")
                            + " startY=" + start.getY() + " end=" + end
                            + " result=" + result + " ticks=" + ticks;
                    if (pass) getLogger().info(message);
                    else getLogger().severe(message);
                    sender.sendMessage(message);
                    cleanup();
                    cancel();
                }
            }.runTaskTimer(this, 1, 1);
            return true;
        });
    }

    @Override
    public void onDisable() {
        cleanup();
    }

    private void cleanup() {
        if (flight != null) flight.uninstall();
        if (bee != null) bee.remove();
        if (changedBlock != null) changedBlock.update(true, false);
        flight = null;
        bee = null;
        changedBlock = null;
    }
}
