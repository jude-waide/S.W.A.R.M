package me.jetcobblestone;

import org.bukkit.Location;
import org.bukkit.block.Block;
import org.bukkit.entity.Mob;
import org.bukkit.plugin.java.JavaPlugin;
import org.bukkit.scheduler.BukkitTask;
import org.bukkit.util.BoundingBox;
import org.bukkit.util.Vector;

import java.util.HashSet;
import java.util.ArrayDeque;
import java.util.Deque;
import java.util.Set;
import java.util.UUID;

/** Executes the backend's adjacent waypoints without vanilla bee navigation. */
public class DroneAI {
    public enum Status { COMPLETED, BLOCKED, FAILED }

    public record Result(UUID commandId, Status status, String reason) {}

    private static final double ARRIVE_DISTANCE_SQ = 0.06 * 0.06;
    private static final int NO_PROGRESS_TICKS = 100;
    private static final double PROGRESS_EPSILON_SQ = 0.01 * 0.01;

    private final JavaPlugin plugin;
    private final Mob mob;
    private final double speed;
    private final boolean originalAI;
    private final boolean originalAware;
    private final boolean originalGravity;
    private final boolean originalCollidable;
    private final boolean originalGlowing;
    private final Set<UUID> seenCommands = new HashSet<>();

    private BukkitTask flightTask;
    private UUID commandId;
    private Location target;
    private final Deque<Result> pendingResults = new ArrayDeque<>();
    private Move nextMove;
    private UUID lastCompletedCommand;
    private record Move(UUID id, int x, int y, int z) {}
    private double bestDistSq;
    private int ticksSinceProgress;

    public DroneAI(JavaPlugin plugin, Mob mob, double speed) {
        this.plugin = plugin;
        this.mob = mob;
        this.speed = speed;
        this.originalAI = mob.hasAI();
        this.originalAware = mob.isAware();
        this.originalGravity = mob.hasGravity();
        this.originalCollidable = mob.isCollidable();
        this.originalGlowing = mob.isGlowing();
    }

    public Mob getMob() {
        return mob;
    }

    public void install() {
        mob.getPathfinder().stopPathfinding();
        // NoAI also disables velocity integration for flying mobs. Awareness only
        // disables autonomous decisions, leaving real collision/flight physics active.
        mob.setAI(true);
        mob.setAware(false);
        mob.setGravity(false);
        mob.setCollidable(false);
        mob.setGlowing(true);
        mob.setPersistent(true);
        mob.setRemoveWhenFarAway(false);
        mob.addScoreboardTag("drone_swarm");
        mob.setVelocity(new Vector());
        flightTask = plugin.getServer().getScheduler().runTaskTimer(plugin, this::tick, 1L, 1L);
    }

    public void uninstall() {
        if (flightTask != null) {
            flightTask.cancel();
            flightTask = null;
        }
        mob.setVelocity(new Vector());
        mob.setAI(originalAI);
        mob.setAware(originalAware);
        mob.setGravity(originalGravity);
        mob.setCollidable(originalCollidable);
        mob.setGlowing(originalGlowing);
    }

    public void issueMove(UUID id, int x, int y, int z) {
        issueMove(id, x, y, z, null);
    }

    public void issueMove(UUID id, int x, int y, int z, UUID afterCommandId) {
        if (id.equals(commandId) || seenCommands.contains(id)) return;
        if (commandId != null && commandId.equals(afterCommandId) && nextMove == null) {
            seenCommands.add(id);
            nextMove = new Move(id, x, y, z);
            return;
        }
        if (commandId != null || (afterCommandId == null && !pendingResults.isEmpty()) ||
                (afterCommandId != null && !afterCommandId.equals(lastCompletedCommand))) {
            plugin.getLogger().warning("Rejected overlapping movement command for drone " + mob.getUniqueId());
            return;
        }
        seenCommands.add(id);
        beginMove(new Move(id, x, y, z));
    }

    private void beginMove(Move move) {
        commandId = move.id();
        // Entity locations are feet positions. Centre the whole bee inside its cell.
        target = new Location(mob.getWorld(), move.x() + 0.5, move.y() + (1 - mob.getHeight()) / 2, move.z() + 0.5);
        Block destination = target.getBlock();
        if (!destination.isPassable() || destination.isLiquid()) {
            finish(Status.BLOCKED, "Destination is obstructed");
            return;
        }
        bestDistSq = mob.getLocation().distanceSquared(target);
        ticksSinceProgress = 0;
    }

    public Result getPendingResult() { return pendingResults.peekFirst(); }

    public void acknowledge(UUID id) {
        if (getPendingResult() != null && getPendingResult().commandId().equals(id)) {
            pendingResults.removeFirst();
        }
    }

    public boolean isBusy() { return commandId != null; }

    public void reset() {
        mob.setVelocity(new Vector());
        commandId = null;
        target = null;
        pendingResults.clear();
        nextMove = null;
        lastCompletedCommand = null;
        seenCommands.clear();
        ticksSinceProgress = 0;
        bestDistSq = 0;
    }

    private void tick() {
        if (!mob.isValid()) {
            if (commandId != null) finish(Status.FAILED, "Drone entity is no longer available");
            if (flightTask != null) flightTask.cancel();
            return;
        }
        if (commandId == null) {
            mob.setVelocity(new Vector());
            return;
        }

        Location here = mob.getLocation();
        double distSq = here.distanceSquared(target);
        while (distSq <= ARRIVE_DISTANCE_SQ
                && here.getBlockX() == target.getBlockX()
                && here.getBlockY() == target.getBlockY()
                && here.getBlockZ() == target.getBlockZ()) {
            finish(Status.COMPLETED, null);
            if (commandId == null) return;
            distSq = here.distanceSquared(target);
        }
        if (distSq < bestDistSq - PROGRESS_EPSILON_SQ) {
            bestDistSq = distSq;
            ticksSinceProgress = 0;
        } else if (++ticksSinceProgress >= NO_PROGRESS_TICKS) {
            finish(Status.BLOCKED, "No progress towards destination");
            return;
        }

        Vector step = target.toVector().subtract(here.toVector());
        double distance = Math.sqrt(distSq);
        step.multiply(Math.min(speed / 20.0, distance) / distance);
        BoundingBox swept = mob.getBoundingBox().expandDirectional(step);
        if (mob.getWorld().hasCollisionsIn(swept) || touchesLiquid(swept)) {
            finish(Status.BLOCKED, "Flight path is obstructed");
            return;
        }
        if (step.getX() != 0 || step.getZ() != 0) {
            mob.setRotation((float) Math.toDegrees(Math.atan2(-step.getX(), step.getZ())), 0);
        }
        mob.setVelocity(step);
    }

    private boolean touchesLiquid(BoundingBox box) {
        for (int x = (int) Math.floor(box.getMinX()); x <= Math.floor(box.getMaxX()); x++)
            for (int y = (int) Math.floor(box.getMinY()); y <= Math.floor(box.getMaxY()); y++)
                for (int z = (int) Math.floor(box.getMinZ()); z <= Math.floor(box.getMaxZ()); z++)
                    if (mob.getWorld().getBlockAt(x, y, z).isLiquid()) return true;
        return false;
    }

    private void finish(Status status, String reason) {
        pendingResults.addLast(new Result(commandId, status, reason));
        lastCompletedCommand = status == Status.COMPLETED ? commandId : null;
        commandId = null;
        target = null;
        Move next = nextMove;
        nextMove = null;
        if (status == Status.COMPLETED && next != null) {
            beginMove(next);
            return;
        }
        mob.setVelocity(new Vector());
    }
}
