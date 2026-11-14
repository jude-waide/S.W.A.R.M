package me.jetcobblestone;

import com.google.gson.JsonObject;

public record MissionBounds(int minX, int minY, int minZ) {
    public static final int WIDTH = 128;
    public static final int HEIGHT = 64;
    public static final int DEPTH = 128;

    public static MissionBounds centeredOn(int x, int y, int z, int worldMinY, int worldMaxY) {
        if (worldMaxY - worldMinY < HEIGHT || y < worldMinY || y >= worldMaxY) {
            throw new IllegalArgumentException("Place the hive inside a world with at least " + HEIGHT + " blocks of height.");
        }
        int minY = Math.clamp(y - HEIGHT / 2, worldMinY, worldMaxY - HEIGHT);
        return new MissionBounds(x - WIDTH / 2, minY, z - DEPTH / 2);
    }

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
