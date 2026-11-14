package me.jetcobblestone;

import com.google.gson.Gson;
import com.google.gson.JsonObject;
import com.google.gson.JsonParseException;
import org.bukkit.Bukkit;
import org.bukkit.Location;
import org.bukkit.World;
import org.bukkit.scheduler.BukkitTask;

import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.time.Duration;
import java.util.Locale;
import java.util.UUID;
import java.util.concurrent.atomic.AtomicBoolean;

public class SessionManager {
    private final Main plugin;
    private final DroneManager droneManager;
    private final Location hive;
    private final Location target;
    private final MissionBounds bounds;
    private final String baseUrl;
    private final String pluginKey;
    private final String missionId = UUID.randomUUID().toString();
    private final World world;
    private final HttpClient client = HttpClient.newBuilder()
            .connectTimeout(Duration.ofSeconds(2))
            .build();
    private final Gson gson = new Gson();
    private final AtomicBoolean inFlight = new AtomicBoolean(false);
    private final AtomicBoolean resetInFlight = new AtomicBoolean(false);

    private BukkitTask pollingTask;
    private boolean started;
    private boolean stopped;
    private boolean initializing = true;
    private boolean resetting;
    private boolean resetFailed;
    private long lastAcceptedSequence;
    private long pendingSequence;
    private String pendingPath;
    private String pendingBody;

    public SessionManager(Main plugin, DroneManager droneManager, Location hive, Location target,
                          MissionBounds bounds, String baseUrl, String pluginKey) {
        this.plugin = plugin;
        this.droneManager = droneManager;
        this.hive = hive.clone();
        this.target = target.clone();
        this.bounds = bounds;
        this.baseUrl = baseUrl.replaceAll("/+$", "");
        this.pluginKey = pluginKey;
        this.world = hive.getWorld();
    }

    public void start() {
        if (started) {
            throw new IllegalStateException("Mission session has already been started");
        }
        started = true;

        JsonObject request = new JsonObject();
        request.addProperty("protocolVersion", 1);
        request.addProperty("missionId", missionId);
        request.addProperty("worldId", world.getName() + ":"
                + world.getEnvironment().name().toLowerCase(Locale.ROOT));
        request.add("boundsMin", bounds.toJson());
        request.add("tower", blockPoint(hive));
        request.add("target", blockPoint(target));
        request.add("initialFrame", createFrame(0));
        pendingPath = "/api/plugin/missions";
        pendingSequence = 0;
        pendingBody = gson.toJson(request);
        pollingTask = Bukkit.getScheduler().runTaskTimer(plugin, this::postObservation, 10L, 10L);
        sendPending();
    }

    /** Called on the main thread every 10 ticks after mission initialisation. */
    public void postObservation() {
        if (stopped || inFlight.get()) {
            return;
        }
        if (pendingBody == null) {
            if (initializing) {
                return;
            }
            pendingSequence = lastAcceptedSequence + 1;
            pendingPath = "/api/plugin/missions/" + missionId + "/observations";
            pendingBody = gson.toJson(createFrame(pendingSequence));
        }
        sendPending();
    }

    public boolean isActive() {
        return started && (!stopped || resetting || resetFailed);
    }

    public void stop() {
        stopped = true;
        if (pollingTask != null) {
            pollingTask.cancel();
            pollingTask = null;
        }
    }

    public boolean resetMission() {
        if (initializing) {
            plugin.getLogger().warning("Wait for mission initialisation before requesting a reset.");
            return false;
        }
        if (!resetInFlight.compareAndSet(false, true)) {
            return false;
        }
        resetting = true;
        resetFailed = false;
        stop();

        HttpRequest request = HttpRequest.newBuilder()
                .uri(URI.create(baseUrl + "/api/plugin/missions/" + missionId))
                .timeout(Duration.ofSeconds(2))
                .header("X-Plugin-Key", pluginKey)
                .DELETE()
                .build();
        client.sendAsync(request, HttpResponse.BodyHandlers.ofString())
                .whenComplete((response, error) -> {
                    if (!plugin.isEnabled()) {
                        resetInFlight.set(false);
                        return;
                    }
                    Bukkit.getScheduler().runTask(plugin, () -> {
                        resetInFlight.set(false);
                        resetting = false;
                        if (error != null) {
                            resetFailed = true;
                            plugin.getLogger().severe("Mission reset failed ("
                                    + error.getClass().getSimpleName() + "). Retry /reset_mission.");
                        } else if (response.statusCode() == 204) {
                            resetFailed = false;
                            droneManager.resetAll();
                            plugin.setSessionManager(null);
                            plugin.getLogger().info("Mission " + missionId + " reset.");
                        } else if (response.statusCode() == 404) {
                            resetFailed = false;
                            droneManager.resetAll();
                            plugin.setSessionManager(null);
                            plugin.getLogger().warning("Backend no longer has mission " + missionId
                                    + " (404); local drone state was reset.");
                        } else {
                            resetFailed = true;
                            plugin.getLogger().severe("Mission reset returned " + response.statusCode()
                                    + ": " + response.body());
                        }
                    });
                });
        return true;
    }

    private JsonObject createFrame(long sequence) {
        JsonObject frame = new JsonObject();
        frame.addProperty("sequence", sequence);
        frame.addProperty("gameTick", Bukkit.getCurrentTick());
        frame.add("drones", droneManager.getInfo(bounds, world));
        return frame;
    }

    private JsonObject blockPoint(Location location) {
        JsonObject point = new JsonObject();
        point.addProperty("x", location.getBlockX());
        point.addProperty("y", location.getBlockY());
        point.addProperty("z", location.getBlockZ());
        return point;
    }

    private void sendPending() {
        if (stopped || pendingBody == null || !inFlight.compareAndSet(false, true)) {
            return;
        }

        long sentSequence = pendingSequence;
        boolean sentInitialization = initializing;
        HttpRequest request = HttpRequest.newBuilder()
                .uri(URI.create(baseUrl + pendingPath))
                .timeout(Duration.ofSeconds(2))
                .header("Content-Type", "application/json")
                .header("X-Plugin-Key", pluginKey)
                .POST(HttpRequest.BodyPublishers.ofString(pendingBody))
                .build();

        client.sendAsync(request, HttpResponse.BodyHandlers.ofString())
                .whenComplete((response, error) -> {
                    if (!plugin.isEnabled()) {
                        inFlight.set(false);
                        return;
                    }
                    Bukkit.getScheduler().runTask(plugin, () -> {
                        inFlight.set(false);
                        if (stopped) {
                            return;
                        }
                        if (error != null) {
                            plugin.getLogger().warning("Plugin request for sequence " + sentSequence
                                    + " failed (" + error.getClass().getSimpleName() + "); it will be retried.");
                            return;
                        }
                        handleResponse(sentSequence, sentInitialization, response);
                    });
                });
    }

    private void handleResponse(long sentSequence, boolean sentInitialization, HttpResponse<String> response) {
        switch (response.statusCode()) {
            case 200 -> acceptResponse(sentSequence, sentInitialization, response.body());
            case 400 -> fail("Backend rejected the plugin frame (400): " + response.body());
            case 401 -> fail("Backend rejected the plugin key (401). Check plugins/DroneSim/config.yml.");
            case 404 -> fail("Backend mission was not found (404). Start a new mission after checking the backend.");
            case 409 -> fail("Backend reported a mission or sequence conflict (409): " + response.body());
            default -> {
                if (response.statusCode() >= 500) {
                    plugin.getLogger().warning("Backend returned " + response.statusCode()
                            + " for sequence " + sentSequence + "; the exact request will be retried.");
                } else {
                    fail("Unexpected backend response " + response.statusCode() + ": " + response.body());
                }
            }
        }
    }

    private void acceptResponse(long sentSequence, boolean sentInitialization, String body) {
        try {
            JsonObject accepted = gson.fromJson(body, JsonObject.class);
            if (accepted == null
                    || !accepted.has("protocolVersion")
                    || !accepted.has("missionId")
                    || !accepted.has("sequence")
                    || accepted.get("protocolVersion").getAsInt() != 1
                    || !missionId.equals(accepted.get("missionId").getAsString())
                    || accepted.get("sequence").getAsLong() != sentSequence) {
                fail("Backend response did not match the pending mission request.");
                return;
            }
            droneManager.applyResponse(accepted);
        } catch (IllegalStateException | IllegalArgumentException | NullPointerException
                 | JsonParseException exception) {
            fail("Backend returned an invalid accepted-frame response: " + exception.getMessage());
            return;
        }

        lastAcceptedSequence = sentSequence;
        pendingBody = null;
        pendingPath = null;
        if (sentInitialization) {
            initializing = false;
            plugin.getLogger().info("Mission " + missionId + " accepted; sending observations every 10 ticks.");
        }
    }

    private void fail(String message) {
        plugin.getLogger().severe(message);
        stop();
        pendingBody = null;
    }
}
