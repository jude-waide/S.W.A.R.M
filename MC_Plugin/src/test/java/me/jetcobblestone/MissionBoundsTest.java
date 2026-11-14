package me.jetcobblestone;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.*;

class MissionBoundsTest {
    @Test
    void centersOnTheHiveIncludingNegativeCoordinatesAndInclusiveEdges() {
        MissionBounds bounds = MissionBounds.centeredOn(-352, 81, -128, -64, 320);
        assertEquals(new MissionBounds(-416, 49, -192), bounds);
        assertTrue(bounds.contains(-416, 49, -192));
        assertTrue(bounds.contains(-289, 112, -65));
        assertFalse(bounds.contains(-417, 49, -192));
        assertFalse(bounds.contains(-288, 112, -65));
        assertFalse(bounds.contains(-352, 113, -128));
    }

    @Test
    void movesTheBoundsWithTheHive() {
        MissionBounds first = MissionBounds.centeredOn(352, 81, -128, -64, 320);
        MissionBounds moved = MissionBounds.centeredOn(652, 91, -328, -64, 320);
        assertEquals(new MissionBounds(first.minX() + 300, first.minY() + 10, first.minZ() - 200), moved);
        assertFalse(moved.contains(352, 81, -128));
    }

    @Test
    void keepsTheFullHeightInsideWorldLimits() {
        assertEquals(-64, MissionBounds.centeredOn(0, -64, 0, -64, 320).minY());
        assertEquals(256, MissionBounds.centeredOn(0, 319, 0, -64, 320).minY());
        assertThrows(IllegalArgumentException.class, () -> MissionBounds.centeredOn(0, 320, 0, -64, 320));
        assertThrows(IllegalArgumentException.class, () -> MissionBounds.centeredOn(0, -65, 0, -64, 320));
        assertThrows(IllegalArgumentException.class, () -> MissionBounds.centeredOn(0, 20, 0, 0, 32));
    }
}
