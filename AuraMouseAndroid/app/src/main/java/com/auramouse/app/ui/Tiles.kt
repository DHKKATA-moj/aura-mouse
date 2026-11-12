package com.auramouse.app.ui

import android.os.Handler
import android.os.Looper
import android.service.quicksettings.Tile
import android.service.quicksettings.TileService
import android.widget.Toast
import com.auramouse.app.R

/** The two Control Center buttons: turn the saved computer on, and turn it off, without opening the app. */
abstract class PcTileService : TileService() {
    private val main = Handler(Looper.getMainLooper())

    override fun onStartListening() {
        super.onStartListening()
        Link.init(applicationContext)
        qsTile?.apply {
            state = if (Link.hasSavedComputer()) Tile.STATE_INACTIVE else Tile.STATE_UNAVAILABLE
            updateTile()
        }
    }

    protected fun toast(resId: Int) = main.post { Toast.makeText(applicationContext, resId, Toast.LENGTH_SHORT).show() }

    /** Briefly light the tile up while the request is sent, then settle it back. */
    protected fun blink() {
        qsTile?.apply { state = Tile.STATE_ACTIVE; updateTile() }
        main.postDelayed({
            qsTile?.apply { state = if (Link.hasSavedComputer()) Tile.STATE_INACTIVE else Tile.STATE_UNAVAILABLE; updateTile() }
        }, 1200)
    }
}

/** Wake on LAN to the saved computer. */
class WakeTileService : PcTileService() {
    override fun onClick() {
        super.onClick()
        Link.init(applicationContext)
        if (!Link.hasSavedComputer()) { toast(R.string.tile_none); return }
        blink()
        Link.wakeSaved { ok -> toast(if (ok) R.string.tile_wake_sent else R.string.power_no_network) }
    }
}

/** Turn the saved computer off (it still counts down and can be stopped there). */
class ShutdownTileService : PcTileService() {
    override fun onClick() {
        super.onClick()
        Link.init(applicationContext)
        if (!Link.hasSavedComputer()) { toast(R.string.tile_none); return }
        blink()
        toast(R.string.tile_off_sending)
        Link.shutdownSaved { ok -> if (!ok) toast(R.string.tile_off_fail) }
    }
}
