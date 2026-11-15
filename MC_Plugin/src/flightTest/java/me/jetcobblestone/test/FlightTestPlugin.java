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
        Objects.requireNonNull(getCommand("test_drone_speed")).setExecutor((sender, command, label, args) -> {
            if (args.length != 4 && (args.length != 5 || !args[4].equals("queued"))) return false;
            if (flight != null) {
                sender.sendMessage("A flight test is already running.");
                return true;
            }
            int speed, x, y, z;
            try {
                speed = Integer.parseInt(args[0]);
                x = Integer.parseInt(args[1]);
                y = Integer.parseInt(args[2]);
                z = Integer.parseInt(args[3]);
            } catch (NumberFormatException exception) {
                sender.sendMessage("Speed and coordinates must be whole numbers.");
                return true;
            }
            if (speed < 1 || speed > 5) return false;
            var world = getServer().getWorlds().getFirst();
            var corridor = new BoundingBox(x, y, z, x + 10, y + 2, z + 1);
            if (y < world.getMinHeight() || y + 2 >= world.getMaxHeight() || world.hasCollisionsIn(corridor)) {
                sender.sendMessage("Choose an empty 10x2x1 speed-test corridor.");
                return true;
            }
            for (int bx = x; bx < x + 10; bx++)
                for (int by = y; by < y + 2; by++)
                    if (world.getBlockAt(bx, by, z).isLiquid()) {
                        sender.sendMessage("The speed-test corridor must not contain liquid.");
                        return true;
                    }
            bee = world.spawn(new Location(world, x + 0.5, y + 0.2, z + 0.5), Bee.class);
            flight = new DroneAI(JavaPlugin.getProvidingPlugin(DroneAI.class), bee, speed);
            flight.install();
            boolean queued = args.length == 5;
            UUID first = UUID.randomUUID();
            UUID second = UUID.randomUUID();
            flight.issueMove(first, x + (queued ? 1 : 8), y, z);
            if (queued) flight.issueMove(second, x + 2, y, z, first);
            new BukkitRunnable() {
                int ticks;
                int issued = 2;
                int stoppedTicks;
                double previousX = x + 0.5;
                UUID lastIssued = second;
                @Override
                public void run() {
                    ticks++;
                    var result = flight.getPendingResult();
                    double currentX = bee.getLocation().getX();
                    if (queued && ticks > 2 && currentX < x + 8.4 && currentX - previousX < speed / 80.0)
                        stoppedTicks++;
                    previousX = currentX;
                    if (queued && ticks % 2 == 0 && result != null &&
                            result.status() == DroneAI.Status.COMPLETED && !result.commandId().equals(lastIssued)) {
                        flight.acknowledge(result.commandId());
                        if (issued < 8) {
                            UUID next = UUID.randomUUID();
                            flight.issueMove(next, x + ++issued, y, z, lastIssued);
                            lastIssued = next;
                        }
                        result = flight.getPendingResult();
                    }
                    if (queued && result != null && result.status() == DroneAI.Status.COMPLETED &&
                            (issued < 8 || !result.commandId().equals(lastIssued))) result = null;
                    if (result == null && ticks < 220) return;
                    double distance = bee.getLocation().getX() - x - 0.5;
                    double actual = distance * 20 / ticks;
                    boolean pass = result != null && result.status() == DroneAI.Status.COMPLETED
                            && Math.abs(actual - speed) <= speed * 0.08 && (!queued || stoppedTicks == 0);
                    String message = "SPEED_TEST " + (pass ? "PASS" : "FAIL")
                            + " configured=" + speed + " actual=" + actual + " ticks=" + ticks
                            + " queued=" + queued + " stoppedTicks=" + stoppedTicks;
                    if (pass) getLogger().info(message);
                    else getLogger().severe(message);
                    sender.sendMessage(message);
                    cleanup();
                    cancel();
                }
            }.runTaskTimer(this, 1, 1);
            return true;
        });
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
            if (args.length != 4 || !Set.of("up", "down", "horizontal", "blocked", "ceiling", "queued-blocked", "turn").contains(args[0]))
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
            int depth = args[0].equals("turn") ? 2 : 1;
            if (y - 1 < world.getMinHeight() || y + 3 >= world.getMaxHeight()
                    || world.hasCollisionsIn(new BoundingBox(x, y - 1, z, x + 2, y + 3, z + depth))) {
                sender.sendMessage("Choose an empty 2x4x2 test area inside the world's height bounds.");
                return true;
            }
            for (int bx = x; bx <= x + 1; bx++)
                for (int by = y - 1; by <= y + 2; by++)
                for (int bz = z; bz < z + depth; bz++)
                    if (world.getBlockAt(bx, by, bz).isLiquid()) {
                        sender.sendMessage("The test area must not contain liquid.");
                        return true;
                    }

            String mode = args[0];
            boolean horizontal = Set.of("horizontal", "blocked", "ceiling", "queued-blocked", "turn").contains(mode);
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
            UUID nextId = UUID.randomUUID();
            flight.issueMove(id, targetX, targetY, z);
            if (mode.equals("turn")) flight.issueMove(nextId, targetX, targetY, z + 1, id);
            if (mode.equals("queued-blocked")) flight.issueMove(nextId, targetX, targetY + 1, z, id);
            if (mode.equals("blocked") || mode.equals("queued-blocked")) {
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
                    if (mode.equals("turn") && result != null && result.commandId().equals(id)
                            && result.status() == DroneAI.Status.COMPLETED) {
                        flight.acknowledge(id);
                        result = flight.getPendingResult();
                    }
                    if (result != null && terminalPosition == null) {
                        terminalPosition = bee.getLocation().toVector();
                        terminalTick = ticks;
                    }
                    if (ticks < 140 && (terminalPosition == null || ticks < terminalTick + 20)) return;
                    Location end = bee.getLocation();
                    boolean held = terminalPosition != null && end.toVector().distanceSquared(terminalPosition) < 0.01;
                    boolean pass = result != null && result.commandId().equals(mode.equals("turn") ? nextId : id)
                            && held && bee.isGlowing() && !flight.isBusy();
                    if (mode.equals("blocked") || mode.equals("queued-blocked")) {
                        pass &= result != null && result.status() == DroneAI.Status.BLOCKED
                                && end.getX() < targetX;
                    } else {
                        pass &= result != null && result.status() == DroneAI.Status.COMPLETED
                                && end.getBlockX() == targetX && end.getBlockY() == targetY
                                && end.getBlockZ() == z + (mode.equals("turn") ? 1 : 0)
                                && end.distanceSquared(new Location(world, targetX + 0.5,
                                    targetY + (1 - bee.getHeight()) / 2, z + 0.5 + (mode.equals("turn") ? 1 : 0))) <= 0.06 * 0.06;
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
