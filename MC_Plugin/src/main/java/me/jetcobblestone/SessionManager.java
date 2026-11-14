package me.jetcobblestone;

import com.google.gson.Gson;
import com.google.gson.JsonArray;
import com.google.gson.JsonElement;
import com.google.gson.JsonObject;
import org.bukkit.Bukkit;
import org.bukkit.plugin.java.JavaPlugin;

import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.time.Duration;
import java.util.concurrent.atomic.AtomicBoolean;

public class SessionManager {

    private final JavaPlugin plugin;
    private final DroneManager droneManager;
    private final String baseUrl;      // e.g. "https://myapp.azurewebsites.net"
    private final String pluginKey;    // from config.yml, never logged
    private final String missionId;    // the UUID you used at initialisation

    private final HttpClient client = HttpClient.newBuilder()
            .connectTimeout(Duration.ofSeconds(2))
            .build();
    private final Gson gson = new Gson();

    private final AtomicBoolean inFlight = new AtomicBoolean(false);

    // Accessed on the main thread only (callbacks hop back via the scheduler)
    private int lastAcceptedSequence = 0;   // initialisation used sequence 0
    private String pendingBody = null;      // frame awaiting acceptance (for exact retry)
    private int pendingSequence = -1;

    public SessionManager(JavaPlugin plugin, DroneManager droneManager, String baseUrl, String pluginKey, String missionId) {
        this.plugin = plugin;
        this.droneManager = droneManager;
        this.baseUrl = baseUrl;
        this.pluginKey = pluginKey;
        this.missionId = missionId;
    }

    /** Call from the main thread roughly every 10 ticks (500 ms). */
    public void postObservation() {
        // One request in flight; discard this interval rather than queueing it.
        if (!inFlight.compareAndSet(false, true)) return;

        // If the previous attempt didn't get an answer, resend the exact same body.
        // Only build a new frame once the last one was accepted.
        if (pendingBody == null) {
            pendingSequence = lastAcceptedSequence + 1;
            JsonObject frame = new JsonObject();
            frame.addProperty("sequence", pendingSequence);
            frame.addProperty("gameTick", Bukkit.getCurrentTick());
            frame.add("drones", droneManager.getInfo());
            pendingBody = gson.toJson(frame);   // freeze the body now, on the main thread
        }

        HttpRequest request = HttpRequest.newBuilder()
                .uri(URI.create(baseUrl + "/api/plugin/missions/" + missionId + "/observations"))
                .timeout(Duration.ofSeconds(2))
                .header("Content-Type", "application/json")
                .header("X-Plugin-Key", pluginKey)
                .POST(HttpRequest.BodyPublishers.ofString(pendingBody))
                .build();

        final int sentSequence = pendingSequence;

        client.sendAsync(request, HttpResponse.BodyHandlers.ofString())
                .whenComplete((response, error) ->
                        // Back to the main thread before touching plugin/world state
                        Bukkit.getScheduler().runTask(plugin, () -> {
                            inFlight.set(false);
                            if (error != null) {
                                // Timeout/network error: keep pendingBody and retry identically
                                plugin.getLogger().warning("Observation " + sentSequence
                                        + " failed: " + error.getClass().getSimpleName());
                                return;
                            }
                            handleResponse(sentSequence, response);
                        }));
    }

    private void handleResponse(int sentSequence, HttpResponse<String> response) {
        switch (response.statusCode()) {
            case 200 -> {
                lastAcceptedSequence = sentSequence;
                pendingBody = null;
                JsonObject accepted = gson.fromJson(response.body(), JsonObject.class);
                applyResponse(accepted);
            }
            case 401 -> plugin.getLogger().severe("Plugin key rejected (401). Check config.");
            case 404 -> {
                // Backend lost the mission (restart?) - must arrange a fresh mission with a NEW UUID.
                plugin.getLogger().severe("Mission not found (404). Start a new mission.");
                pendingBody = null;
            }
            case 409 -> {
                // Stale/conflicting sequence or command. Don't blindly retry; log the Problem Details.
                plugin.getLogger().severe("Conflict (409): " + response.body());
                pendingBody = null; // decide how to resync; don't treat as success
            }
            case 400 -> {
                // Your frame is invalid. Retrying the same body won't help.
                plugin.getLogger().severe("Invalid frame (400): " + response.body());
                pendingBody = null;
            }
            default -> {
                // 5xx etc.: keep pendingBody and retry the identical frame next tick
                plugin.getLogger().warning("Server error " + response.statusCode());
            }
        }
    }

    private void applyResponse(JsonObject accepted) {
        // 1. acknowledgedResults: stop reporting those commandResults in future frames.
        // 2. commands: for each {droneId, commandId, kind:"move", destination:{x,y,z}},
        //    execute each commandId at most once; the server repeats outstanding
        //    commands until a terminal result arrives.
        // An empty list means "no movement", not success.
        JsonArray commands = (JsonArray) accepted.get("commands");
        if (commands == null) {
            // Raise error
        }
        droneManager.moveDrones(commands);
    }
}