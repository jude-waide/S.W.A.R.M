package me.jetcobblestone;

import com.destroystokyo.paper.entity.ai.Goal;
import com.destroystokyo.paper.entity.ai.GoalKey;
import com.destroystokyo.paper.entity.ai.GoalType;
import org.bukkit.Bukkit;
import org.bukkit.Location;
import org.bukkit.NamespacedKey;
import org.bukkit.entity.Mob;
import org.bukkit.plugin.java.JavaPlugin;

import java.util.EnumSet;
import java.util.HashSet;
import java.util.Set;
import java.util.UUID;

/** Replaces a Mob's vanilla AI with a single "move to this block cell" goal. */
public class DroneAI implements Goal<Mob> {

    public enum Status { COMPLETED, BLOCKED, FAILED }

    /** Terminal result to report in the next observation frame's commandResult. */
    public record Result(UUID commandId, Status status, String reason) {}

    private static final double ARRIVE_DISTANCE_SQ = 0.5 * 0.5;
    private static final int NO_PROGRESS_TICKS = 100;       // 5 simulation seconds
    private static final double PROGRESS_EPSILON_SQ = 0.05 * 0.05;

    private final Mob mob;
    private final GoalKey<Mob> key;
    private final double speed;

    // Main-thread state only
    private UUID commandId;                 // outstanding command, or null
    private Location target;                // centre of destination cell
    private Result pendingResult;           // terminal result awaiting acknowledgement
    private final Set<UUID> seenCommands = new HashSet<>();

    private double bestDistSq;
    private int ticksSinceProgress;

    public DroneAI(JavaPlugin plugin, Mob mob, double speed) {
        this.mob = mob;
        this.speed = speed;
        this.key = GoalKey.of(Mob.class, new NamespacedKey(plugin, "drone_move"));
    }

    public Mob getMob() {
        return mob;
    }

    /** Remove all vanilla goals and install this one. Call once per drone. */
    public void install() {
        var goals = Bukkit.getMobGoals();
        goals.removeAllGoals(mob);          // movement, targeting, hive, pollination...
        goals.addGoal(mob, 0, this);

        mob.setPersistent(true);
        mob.setRemoveWhenFarAway(false);
    }

    public void uninstall() {
        Bukkit.getMobGoals().removeGoal(mob, key);
        mob.getPathfinder().stopPathfinding();
    }

    /**
     * Accept a command from the server response. The server repeats outstanding
     * commands until a terminal result arrives, so this ignores repeats and
     * executes each ID at most once. Destination is an integer block cell.
     */
    public void issueMove(UUID id, int x, int y, int z) {
        if (id.equals(commandId) || seenCommands.contains(id)) return;
        if (commandId != null) return;      // one move outstanding per drone

        seenCommands.add(id);
        commandId = id;
        target = new Location(mob.getWorld(), x + 0.5, y + 0.5, z + 0.5);

        // Reject early if the destination cell is solid
        if (!target.getBlock().isPassable() || target.getBlock().isLiquid()) {
            finish(Status.BLOCKED, "Destination is obstructed");
            return;
        }

        bestDistSq = mob.getLocation().distanceSquared(target);
        ticksSinceProgress = 0;

        // Ask the pathfinder for a route; false means no path found
        if (!mob.getPathfinder().moveTo(target, speed)) {
            finish(Status.BLOCKED, "No path to destination");
        }
    }

    /** Result to put in commandResult. Keep reporting until the server acknowledges it. */
    public Result getPendingResult() { return pendingResult; }

    /** Call when the response's acknowledgedResults contains this command ID. */
    public void acknowledge(UUID id) {
        if (pendingResult != null && pendingResult.commandId().equals(id)) {
            pendingResult = null;
        }
    }

    public boolean isBusy() { return commandId != null; }

    public void reset() {
        mob.getPathfinder().stopPathfinding();
        commandId = null;
        target = null;
        pendingResult = null;
        seenCommands.clear();
        ticksSinceProgress = 0;
        bestDistSq = 0;
    }

    // ---- Paper Goal implementation ----

    @Override
    public boolean shouldActivate() {
        return commandId != null;
    }

    @Override
    public void tick() {
        if (commandId == null) return;

        Location here = mob.getLocation();
        double distSq = here.distanceSquared(target);

        if (distSq <= ARRIVE_DISTANCE_SQ) {
            finish(Status.COMPLETED, null);
            return;
        }

        // Progress / stall detection
        if (distSq < bestDistSq - PROGRESS_EPSILON_SQ) {
            bestDistSq = distSq;
            ticksSinceProgress = 0;
        } else if (++ticksSinceProgress >= NO_PROGRESS_TICKS) {
            finish(Status.BLOCKED, "No progress towards destination");
            return;
        }

        // Pathfinder can drop its path (e.g. recalculation), so re-issue if idle
        if (mob.getPathfinder().getCurrentPath() == null) {
            if (!mob.getPathfinder().moveTo(target, speed)) {
                finish(Status.BLOCKED, "No path to destination");
            }
        }
    }

    @Override
    public void stop() {
        mob.getPathfinder().stopPathfinding();
    }

    private void finish(Status status, String reason) {
        pendingResult = new Result(commandId, status, reason);
        commandId = null;
        target = null;
        mob.getPathfinder().stopPathfinding();
    }

    @Override public GoalKey<Mob> getKey() { return key; }
    @Override public EnumSet<GoalType> getTypes() { return EnumSet.of(GoalType.MOVE); }
}
