package me.jetcobblestone;

import com.google.gson.JsonObject;

public record MissionBounds(int minX, int minY, int minZ) {
    public static final int WIDTH = 128;
    public static final int HEIGHT = 64;
    public static final int DEPTH = 128;

    public boolean contains(int x, int y, int z) {
        return x >= minX && x < minX + WIDTH
                && y >= minY && y < minY + HEIGHT
                && z >= minZ && z < minZ + DEPTH;
    }

    public JsonObject toJson() {
        JsonObject bounds = new JsonObject();
        bounds.addProperty("x", minX);
        bounds.addProperty("y", minY);
        bounds.addProperty("z", minZ);
        return bounds;
    }
}
