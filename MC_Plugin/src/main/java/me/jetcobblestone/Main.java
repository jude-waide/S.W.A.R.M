package me.jetcobblestone;

public final class Main extends JavaPlugin implements Listener {
    @Override
    public void onEnable() {
        this.getServer().getPluginManager().registerEvents(this, this);
    }

    @EventHandler
    public void onPlayerJoin(PlayerJoinEvent event) {
        event.getPlayer().sendPlainMessage("Hello, " + event.getPlayer().getName() + "!");
    }
}