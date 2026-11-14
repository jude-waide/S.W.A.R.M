package me.jetcobblestone;

import net.kyori.adventure.text.Component;
import org.bukkit.Color;
import org.bukkit.Location;
import org.bukkit.Material;
import org.bukkit.block.BlockState;
import org.bukkit.block.data.BlockData;
import org.bukkit.entity.BlockDisplay;
import org.bukkit.entity.Display;
import org.bukkit.entity.TextDisplay;
import org.joml.Matrix4f;

/** A physical mission block with an entity outline and an always-readable label. */
final class MissionLandmark implements AutoCloseable {
    private final BlockState original;
    private final BlockData placedData;
    private BlockDisplay outline;
    private TextDisplay label;

    MissionLandmark(Location location, Material material, String title, Color color) {
        var block = location.getBlock();
        if (!block.isPassable() || block.isLiquid()) {
            throw new IllegalArgumentException("Choose a free, non-liquid cell for the " + title.toLowerCase() + ".");
        }
        original = block.getState();
        placedData = material.createBlockData();
        boolean placed = false;
        try {
            block.setBlockData(placedData, false);
            outline = location.getWorld().spawn(block.getLocation(), BlockDisplay.class, display -> {
                display.setBlock(placedData);
                display.setGlowing(true);
                display.setGlowColorOverride(color);
                display.setBrightness(new Display.Brightness(15, 15));
                display.setTransformationMatrix(new Matrix4f().translation(-0.002f, -0.002f, -0.002f).scale(1.004f));
                display.setDisplayWidth(1.1f);
                display.setDisplayHeight(1.1f);
                display.setViewRange(4);
                display.setPersistent(false);
                display.addScoreboardTag("drone_landmark");
                display.addScoreboardTag(material == Material.BEEHIVE ? "drone_hive" : "drone_target");
            });
            label = location.getWorld().spawn(block.getLocation().add(0.5, 1.65, 0.5), TextDisplay.class, text -> {
                text.text(Component.text(title).color(net.kyori.adventure.text.format.TextColor.color(color.asRGB())));
                text.setBillboard(Display.Billboard.CENTER);
                text.setBrightness(new Display.Brightness(15, 15));
                text.setBackgroundColor(Color.fromARGB(180, 16, 13, 8));
                text.setShadowed(true);
                text.setSeeThrough(true);
                text.setViewRange(4);
                text.setPersistent(false);
                text.addScoreboardTag("drone_landmark");
            });
            placed = true;
        } finally {
            if (!placed) close();
        }
    }

    boolean isIntact() {
        return outline.isValid() && label.isValid() && original.getBlock().getBlockData().equals(placedData);
    }

    @Override
    public void close() {
        if (outline != null) outline.remove();
        if (label != null) label.remove();
        // Do not overwrite a block the player changed after placement.
        if (original.getBlock().getBlockData().equals(placedData)) original.update(true, false);
    }
}
