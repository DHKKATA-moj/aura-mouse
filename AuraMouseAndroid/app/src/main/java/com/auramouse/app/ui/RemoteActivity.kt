package com.auramouse.app.ui

import android.annotation.SuppressLint
import android.app.Activity
import android.content.Context
import android.content.pm.ActivityInfo
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Matrix
import android.graphics.Paint
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.net.wifi.WifiManager
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.text.Editable
import android.text.InputType
import android.text.TextWatcher
import android.view.Gravity
import android.view.HapticFeedbackConstants
import android.view.MotionEvent
import android.view.View
import android.view.ViewConfiguration
import android.view.ViewGroup
import android.view.WindowInsets
import android.view.WindowInsetsController
import android.view.WindowManager
import android.view.inputmethod.InputMethodManager
import android.widget.Button
import android.widget.EditText
import android.widget.FrameLayout
import android.widget.HorizontalScrollView
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import com.auramouse.app.R
import com.auramouse.app.core.FramePieces
import com.auramouse.app.core.Network
import com.auramouse.app.core.PointerScale
import com.auramouse.app.core.RemoteEnd
import com.auramouse.app.core.RemoteEvents
import com.auramouse.app.core.RemoteLink
import com.auramouse.app.core.RemoteSaved
import com.auramouse.app.core.Win
import org.json.JSONArray
import org.json.JSONObject
import java.net.InetAddress
import kotlin.concurrent.thread
import kotlin.math.abs
import kotlin.math.hypot

/** Computers viewed before, kept apart from the mouse connection's. Keys are sealed like its key is. */
class RemoteStore(context: Context) {
    private val prefs = context.getSharedPreferences("remote", Context.MODE_PRIVATE)
    private val sealer = SavedStore(context)

    /** How the finger moves the cursor: like a touchpad, or the cursor goes where it touches. */
    var touchpad: Boolean
        get() = prefs.getBoolean("touchpad", true)
        set(value) { prefs.edit().putBoolean("touchpad", value).apply() }

    val computers: List<RemoteSaved>
        @Synchronized get() {
            val list = JSONArray(prefs.getString("computers", "[]"))
            return List(list.length()) { i ->
                val item = list.getJSONObject(i)
                val key = if (item.isNull("key")) null else runCatching { sealer.unseal(item.getString("key")) }.getOrNull()
                RemoteSaved(item.getString("name"), item.getString("ip"), if (key == null) 0 else item.getInt("id"), key)
            }
        }

    @Synchronized
    fun save(computer: RemoteSaved) = write(listOf(computer) + computers.filterNot { it.name.equals(computer.name, true) })

    @Synchronized
    fun forget(name: String) = write(computers.filterNot { it.name.equals(name, true) })

    @Synchronized
    fun forgetKey(name: String) = write(computers.map { if (it.name.equals(name, true)) RemoteSaved(it.name, it.ip, 0, null) else it })

    private fun write(list: List<RemoteSaved>) {
        val json = JSONArray()
        for (c in list) json.put(JSONObject().put("name", c.name).put("ip", c.ip).put("id", c.id).put("key", c.key?.let(sealer::seal) ?: JSONObject.NULL))
        prefs.edit().putString("computers", json.toString()).apply()
    }
}

/** The other screen, drawn to fit and zoomable. Finger gestures become the computer's mouse. */
@SuppressLint("ViewConstructor")
class RemoteScreen(context: Context, private val link: () -> RemoteLink?) : View(context) {
    private val lock = Any()
    private val paint = Paint(Paint.FILTER_BITMAP_FLAG)
    private val slop = ViewConfiguration.get(context).scaledTouchSlop.toFloat()
    private val density = resources.displayMetrics.density
    private val longPress = Runnable { onLongPress() }
    private val drawMatrix = Matrix()
    private var picture: Bitmap? = null
    private var canvas: Canvas? = null
    private var zoom = 1f
    private var panX = 0f
    private var panY = 0f

    private var downX = 0f
    private var downY = 0f
    private var downTime = 0L
    private var moved = false
    private var held = false
    private var dragging = false
    private var two = false
    private var twoMoved = false
    private var lastSpan = 0f
    private var lastMidX = 0f
    private var lastMidY = 0f
    private var wheelRest = 0f

    /** True: the finger moves the cursor like a touchpad and only a tap clicks. False: the cursor goes where the finger touches. */
    var touchpad = true
    private val pointerScale = PointerScale(density, TOUCHPAD_SPEED)
    private var cursorX = 0.5f // where we last put the cursor, 0..1 on the picture
    private var cursorY = 0.5f
    private var lastX = 0f
    private var lastY = 0f
    private var lastTapTime = 0L
    private var dragArmed = false

    /** From the network's thread. */
    fun apply(width: Int, height: Int, update: ByteArray) {
        synchronized(lock) {
            if (picture?.width != width || picture?.height != height) {
                picture?.recycle()
                picture = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888)
                canvas = Canvas(picture!!)
            }
            FramePieces.forEach(update) { x, y, offset, length ->
                // A piece can't be bigger than the picture, so a forged one can't run the phone out of memory.
                val size = BitmapFactory.Options().apply { inJustDecodeBounds = true }
                BitmapFactory.decodeByteArray(update, offset, length, size)
                val piece = if (size.outWidth in 1..width && size.outHeight in 1..height) BitmapFactory.decodeByteArray(update, offset, length) else null
                if (piece != null) {
                    canvas?.drawBitmap(piece, x.toFloat(), y.toFloat(), null)
                    piece.recycle()
                }
            }
        }
        postInvalidateOnAnimation()
    }

    private fun fitScale(p: Bitmap) = minOf(width.toFloat() / p.width, height.toFloat() / p.height)

    override fun onDraw(c: Canvas) {
        c.drawColor(Color.BLACK)
        synchronized(lock) {
            val p = picture ?: return
            val fit = fitScale(p) * zoom
            clampPan(p)
            drawMatrix.setScale(fit, fit)
            drawMatrix.postTranslate((width - p.width * fit) / 2 + panX, (height - p.height * fit) / 2 + panY)
            c.drawBitmap(p, drawMatrix, paint)
        }
    }

    private fun clampPan(p: Bitmap) {
        val fit = fitScale(p) * zoom
        val roomX = ((p.width * fit - width) / 2).coerceAtLeast(0f)
        val roomY = ((p.height * fit - height) / 2).coerceAtLeast(0f)
        panX = panX.coerceIn(-roomX, roomX)
        panY = panY.coerceIn(-roomY, roomY)
    }

    private fun sendPointer(x: Float, y: Float) {
        val p = synchronized(lock) { picture } ?: return
        val fit = fitScale(p) * zoom
        val left = (width - p.width * fit) / 2 + panX
        val top = (height - p.height * fit) / 2 + panY
        val px = ((x - left) / (p.width * fit)).coerceIn(0f, 1f)
        val py = ((y - top) / (p.height * fit)).coerceIn(0f, 1f)
        putCursor(px, py)
    }

    private fun putCursor(x: Float, y: Float) {
        cursorX = x
        cursorY = y
        link()?.pointer((x * FULL).toInt(), (y * FULL).toInt())
    }

    /** Touchpad mode: the same finger movement covers the same part of what's on screen, zoomed in or not. */
    private fun moveCursor(dxPixels: Float, dyPixels: Float) {
        val p = synchronized(lock) { picture } ?: return
        val (dx, dy) = pointerScale.apply(dxPixels / zoom, dyPixels / zoom)
        if (dx == 0 && dy == 0) return
        putCursor((cursorX + dx.toFloat() / p.width).coerceIn(0f, 1f), (cursorY + dy.toFloat() / p.height).coerceIn(0f, 1f))
        followCursor(p)
    }

    /** Zoomed in, the view moves along so the cursor can't wander off the screen. */
    private fun followCursor(p: Bitmap) {
        if (zoom <= 1.01f) return
        val fit = fitScale(p) * zoom
        val margin = FOLLOW_MARGIN_DP * density
        val x = (width - p.width * fit) / 2 + panX + cursorX * p.width * fit
        val y = (height - p.height * fit) / 2 + panY + cursorY * p.height * fit
        if (x < margin) panX += margin - x else if (x > width - margin) panX -= x - (width - margin)
        if (y < margin) panY += margin - y else if (y > height - margin) panY -= y - (height - margin)
        invalidate()
    }

    private fun onLongPress() {
        if (moved || two) return
        held = true
        performHapticFeedback(HapticFeedbackConstants.LONG_PRESS)
    }

    @SuppressLint("ClickableViewAccessibility")
    override fun onTouchEvent(e: MotionEvent): Boolean {
        val active = link() ?: return true
        when (e.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                downX = e.x
                downY = e.y
                downTime = e.eventTime
                moved = false
                held = false
                dragging = false
                two = false
                twoMoved = false
                lastX = e.x
                lastY = e.y
                dragArmed = touchpad && e.eventTime - lastTapTime < DOUBLE_TAP_MS
                postDelayed(longPress, HOLD_MS)
            }
            MotionEvent.ACTION_POINTER_DOWN -> if (e.pointerCount == 2) {
                removeCallbacks(longPress)
                if (dragging) {
                    dragging = false
                    active.button(Win.LEFT_UP)
                }
                two = true
                twoMoved = false
                lastSpan = span(e)
                lastMidX = (e.getX(0) + e.getX(1)) / 2
                lastMidY = (e.getY(0) + e.getY(1)) / 2
                wheelRest = 0f
            }
            MotionEvent.ACTION_MOVE -> if (two && e.pointerCount >= 2) twoFingers(e, active)
            else if (!two) {
                if (!moved && hypot(e.x - downX, e.y - downY) > slop) {
                    moved = true
                    removeCallbacks(longPress)
                    if (!touchpad) sendPointer(downX, downY)
                    if (held || dragArmed) {
                        dragging = true
                        active.button(Win.LEFT_DOWN)
                        performHapticFeedback(HapticFeedbackConstants.LONG_PRESS)
                    }
                }
                if (moved) {
                    if (touchpad) moveCursor(e.x - lastX, e.y - lastY) else sendPointer(e.x, e.y)
                }
                lastX = e.x
                lastY = e.y
            }
            MotionEvent.ACTION_UP -> {
                removeCallbacks(longPress)
                when {
                    dragging -> active.button(Win.LEFT_UP)
                    two -> if (!twoMoved && e.eventTime - downTime < TAP_MS) click(active, e.x, e.y, Win.RIGHT_DOWN, Win.RIGHT_UP)
                    held && !moved -> click(active, e.x, e.y, Win.RIGHT_DOWN, Win.RIGHT_UP)
                    !moved -> {
                        click(active, e.x, e.y, Win.LEFT_DOWN, Win.LEFT_UP)
                        lastTapTime = e.eventTime
                    }
                }
                dragging = false
            }
            MotionEvent.ACTION_CANCEL -> {
                removeCallbacks(longPress)
                if (dragging) active.button(Win.LEFT_UP)
                dragging = false
            }
        }
        return true
    }

    private fun click(active: RemoteLink, x: Float, y: Float, down: Int, up: Int) {
        if (!touchpad) sendPointer(x, y) // otherwise it clicks wherever the cursor already is
        active.button(down)
        active.button(up)
        performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
    }

    /** Spreading zooms. Moving together pans when zoomed in, and otherwise scrolls the computer's page. */
    private fun twoFingers(e: MotionEvent, active: RemoteLink) {
        val newSpan = span(e)
        val midX = (e.getX(0) + e.getX(1)) / 2
        val midY = (e.getY(0) + e.getY(1)) / 2
        val spanChange = newSpan - lastSpan
        val dx = midX - lastMidX
        val dy = midY - lastMidY
        if (abs(spanChange) > 2 * density || hypot(dx, dy) > 1 * density) twoMoved = twoMoved || hypot(newSpan - lastSpan, hypot(dx, dy)) > slop / 2

        if (abs(spanChange) > 2 * density && lastSpan > 0) {
            zoom = (zoom * newSpan / lastSpan).coerceIn(1f, MAX_ZOOM)
            lastSpan = newSpan
        }
        if (zoom > 1.01f) {
            panX += dx
            panY += dy
        } else {
            wheelRest += dy / density * WHEEL_PER_DP
            if (abs(wheelRest) >= WHEEL_STEP) {
                active.wheel(wheelRest.toInt())
                wheelRest = 0f
            }
        }
        lastMidX = midX
        lastMidY = midY
        invalidate()
    }

    private fun span(e: MotionEvent) = hypot(e.getX(0) - e.getX(1), e.getY(0) - e.getY(1))

    fun clear() {
        synchronized(lock) {
            picture?.recycle()
            picture = null
            canvas = null
        }
    }

    private companion object {
        const val FULL = 65535f
        const val HOLD_MS = 450L
        const val TAP_MS = 250L
        const val DOUBLE_TAP_MS = 300L
        const val TOUCHPAD_SPEED = 2f
        const val FOLLOW_MARGIN_DP = 48
        const val MAX_ZOOM = 5f
        const val WHEEL_PER_DP = 6f
        const val WHEEL_STEP = 15f
    }
}

/**
 * Remote desktop on the phone: a list of computers, and the screen of the one being viewed.
 * It has nothing to do with the mouse connection and works whether or not that's paired.
 */
class RemoteActivity : Activity(), RemoteEvents {
    private val main = Handler(Looper.getMainLooper())
    private val density get() = resources.displayMetrics.density
    private lateinit var store: RemoteStore
    private lateinit var root: FrameLayout

    private var found: List<Pair<String, InetAddress>> = emptyList()
    private var searching = false
    private var message: String? = null
    private var typedAddress = ""

    private var link: RemoteLink? = null
    @Volatile private var screen: RemoteScreen? = null
    private var viewing: RemoteSaved? = null // what to reconnect to when coming back to the app
    private var viewingIp: InetAddress? = null
    private var monitors = 1
    private var monitorIndex = 0
    private var catcher: KeyCatcher? = null
    private var wifiLock: WifiManager.WifiLock? = null
    private var menuX = 0f
    private var menuY = 0f
    private var closeMenu: (() -> Unit)? = null

    private fun dp(v: Int) = (v * density).toInt()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        store = RemoteStore(this)
        window.statusBarColor = Style.BACKGROUND
        window.navigationBarColor = Style.BACKGROUND
        root = FrameLayout(this).apply { setBackgroundColor(Style.BACKGROUND) }
        setContentView(root)
        showList()
        search()
    }

    override fun onStart() {
        super.onStart()
        val ip = viewingIp
        if (ip != null && link == null) connect(ip, store.computers.firstOrNull { it.ip == ip.hostAddress })
    }

    /** Nobody is looking, so no reason to keep the other computer's screen streaming. */
    override fun onStop() {
        link?.close()
        link = null
        wifiLock?.takeIf { it.isHeld }?.release()
        super.onStop()
    }

    @Deprecated("Deprecated in Java")
    override fun onBackPressed() {
        val menu = closeMenu
        if (menu != null) menu()
        else if (viewingIp != null) endViewing(null)
        else @Suppress("DEPRECATION") super.onBackPressed()
    }

    // ───── the list ─────

    private fun showList() {
        requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED
        window.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        showSystemBars(true)
        root.removeAllViews()
        catcher = null

        val column = column().apply { setPadding(dp(20), dp(24), dp(20), dp(24)) }
        column.addView(label(getString(R.string.remote_title), 22f, Style.TEXT, bold = true), wrap(bottom = 6))
        column.addView(label(getString(R.string.remote_sub), 13f, Style.MUTED), wrap(bottom = 18))
        message?.let { column.addView(label(it, 14f, Style.BAD), wrap(bottom = 14)) }

        val row = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL }
        val address = EditText(this).apply {
            hint = getString(R.string.remote_address)
            setHintTextColor(Style.MUTED)
            setTextColor(Style.TEXT)
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_URI
            isSingleLine = true
            layoutDirection = View.LAYOUT_DIRECTION_LTR
            setText(typedAddress) // the list is rebuilt when a search finishes, perhaps mid-typing
            setSelection(typedAddress.length)
            addTextChangedListener(object : TextWatcher {
                override fun beforeTextChanged(s: CharSequence?, a: Int, b: Int, c: Int) {}
                override fun onTextChanged(s: CharSequence?, a: Int, b: Int, c: Int) {}
                override fun afterTextChanged(s: Editable?) { typedAddress = s?.toString() ?: "" }
            })
            background = rounded(Style.SURFACE, Style.BORDER)
            setPadding(dp(14), dp(10), dp(14), dp(10))
        }
        row.addView(address, LinearLayout.LayoutParams(0, dp(48), 1f).apply { marginEnd = dp(8) })
        row.addView(primary(getString(R.string.connect)) { byAddress(address.text.toString().trim()) }, LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, dp(48)))
        column.addView(row, wrap(bottom = 18))

        val saved = store.computers
        val rows = found.map { (name, ip) -> Triple(name, ip.hostAddress ?: "", saved.firstOrNull { it.name.equals(name, true) }) } +
            saved.filter { s -> found.none { it.first.equals(s.name, true) } }.map { Triple(it.name, it.ip, it) }
        if (rows.isEmpty()) column.addView(label(getString(if (searching) R.string.remote_searching else R.string.remote_none), 14f, Style.MUTED), wrap(bottom = 14))
        for ((name, ip, known) in rows) column.addView(computerRow(name, ip, known, found.any { it.first == name }), wrap(bottom = 10))

        column.addView(small(getString(if (searching) R.string.remote_searching else R.string.remote_refresh)) { search() }.apply { isEnabled = !searching }, wrap(bottom = 14))
        column.addView(label(getString(R.string.remote_hint), 12f, Style.MUTED), wrap())
        root.addView(ScrollView(this).apply { addView(column) })
    }

    private fun computerRow(name: String, ip: String, known: RemoteSaved?, online: Boolean): View {
        val row = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            background = rounded(Style.SURFACE, Style.BORDER)
            setPadding(dp(14), dp(10), dp(10), dp(10))
        }
        val text = column()
        text.addView(label(name, 15f, Style.TEXT, bold = true, center = false).apply { maxLines = 1 })
        val state = getString(if (known?.key != null) R.string.remote_auto else if (online) R.string.remote_online else R.string.remote_offline)
        text.addView(label("$ip · $state", 12f, if (known?.key != null) Style.GOOD else Style.MUTED, center = false).apply { maxLines = 1 })
        row.addView(text, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f).apply { marginEnd = dp(8) })
        row.addView(primary(getString(R.string.remote_view)) { open(ip, known) }.apply { setPadding(dp(18), 0, dp(18), 0) }, LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, dp(44)).apply { marginEnd = dp(6) })
        if (known != null) row.addView(small("✕") {
            store.forget(name)
            showList()
        }.apply { contentDescription = getString(R.string.remove) }, LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, dp(44)))
        return row
    }

    private fun search() {
        if (searching) return
        searching = true
        if (viewingIp == null) showList()
        thread(isDaemon = true) {
            val result = runCatching { Network.findRemote() }.getOrDefault(emptyList())
            main.post {
                found = result
                searching = false
                if (viewingIp == null && !isDestroyed) showList()
            }
        }
    }

    private fun byAddress(text: String) {
        if (text.isEmpty()) return
        thread(isDaemon = true) {
            val ip = runCatching { InetAddress.getByName(text) }.getOrNull()
            main.post {
                if (ip == null) {
                    message = getString(R.string.remote_bad_address)
                    showList()
                } else connect(ip, store.computers.firstOrNull { it.ip == ip.hostAddress })
            }
        }
    }

    private fun open(ip: String, known: RemoteSaved?) {
        thread(isDaemon = true) {
            val address = runCatching { InetAddress.getByName(ip) }.getOrNull() ?: return@thread
            main.post { connect(address, known) }
        }
    }

    // ───── viewing ─────

    private fun connect(ip: InetAddress, saved: RemoteSaved?) {
        message = null
        viewing = saved
        viewingIp = ip
        // Ready before connecting: the first picture usually beats the screen being shown, and only that one has everything.
        if (screen == null) screen = RemoteScreen(this) { link }
        showStatus(getString(R.string.remote_connecting))
        wifiLock = wifiLock ?: (applicationContext.getSystemService(WIFI_SERVICE) as WifiManager)
            .createWifiLock(WifiManager.WIFI_MODE_FULL_LOW_LATENCY, "Aura Mouse:remote")
        wifiLock?.takeUnless { it.isHeld }?.acquire()
        val name = "${Build.MANUFACTURER} ${Build.MODEL}".trim().take(40)
        link = RemoteLink(ip, name, saved, this).also { it.start() }
    }

    private fun showStatus(text: String) {
        root.removeAllViews()
        val column = column().apply {
            gravity = Gravity.CENTER
            setPadding(dp(28), dp(24), dp(28), dp(24))
        }
        column.addView(label(text, 17f, Style.TEXT), wrap(bottom = 24))
        column.addView(small(getString(R.string.cancel)) { endViewing(null) }, wrap())
        root.addView(column, FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))
    }

    private fun showViewer(name: String) {
        requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        showSystemBars(false)
        root.removeAllViews()

        val view = screen ?: return
        (view.parent as? ViewGroup)?.removeView(view)
        root.addView(view, FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))

        view.touchpad = store.touchpad

        catcher = KeyCatcher(this) { link }
        root.addView(catcher, FrameLayout.LayoutParams(1, 1))
        root.addView(menuButton(), FrameLayout.LayoutParams(dp(MENU_BUTTON_DP), dp(MENU_BUTTON_DP), Gravity.BOTTOM or Gravity.END).apply {
            setMargins(dp(14), dp(14), dp(14), dp(14))
        })
        title = name
    }

    /** All the picture until you ask for something. It can be dragged out of the way of what you're looking at. */
    @SuppressLint("ClickableViewAccessibility")
    private fun menuButton(): View {
        val slop = ViewConfiguration.get(this).scaledTouchSlop
        var startX = 0f
        var startY = 0f
        var dragged = false
        return TextView(this).apply {
            text = "☰"
            textSize = 20f
            gravity = Gravity.CENTER
            setTextColor(Style.TEXT)
            contentDescription = getString(R.string.remote_menu)
            alpha = 0.75f
            background = GradientDrawable().apply {
                shape = GradientDrawable.OVAL
                setColor(Style.SURFACE)
                setStroke(dp(1), Style.BORDER)
            }
            translationX = menuX
            translationY = menuY
            setOnTouchListener { v, e ->
                when (e.actionMasked) {
                    MotionEvent.ACTION_DOWN -> {
                        startX = e.rawX - v.translationX
                        startY = e.rawY - v.translationY
                        dragged = false
                    }
                    MotionEvent.ACTION_MOVE -> {
                        val x = e.rawX - startX
                        val y = e.rawY - startY
                        if (dragged || hypot(x - v.translationX, y - v.translationY) > slop) {
                            dragged = true
                            val parent = v.parent as View
                            // Measured from its corner, so it stays on screen on both sides of that corner.
                            val endward = if (v.layoutDirection == View.LAYOUT_DIRECTION_RTL) -1 else 1
                            val roomX = (parent.width - v.width).toFloat()
                            menuX = if (endward > 0) x.coerceIn(-roomX, 0f) else x.coerceIn(0f, roomX)
                            menuY = y.coerceIn(-(parent.height - v.height).toFloat(), 0f)
                            v.translationX = menuX
                            v.translationY = menuY
                        }
                    }
                    MotionEvent.ACTION_UP -> if (!dragged) showMenu()
                }
                true
            }
        }
    }

    private fun showMenu() {
        val view = screen ?: return
        val scrim = FrameLayout(this).apply {
            setBackgroundColor(Color.argb(90, 0, 0, 0))
            isClickable = true
        }
        val close = {
            root.removeView(scrim)
            closeMenu = null
        }
        closeMenu = close
        scrim.setOnClickListener { close() }

        val panel = column().apply {
            isClickable = true
            setPadding(dp(14), dp(12), dp(14), dp(12))
            background = GradientDrawable().apply {
                setColor(Style.SURFACE)
                cornerRadii = floatArrayOf(18 * density, 18 * density, 18 * density, 18 * density, 0f, 0f, 0f, 0f)
            }
        }

        // How the mouse moves.
        val modes = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL }
        val mode = { text: Int, touchpad: Boolean ->
            val choose = {
                store.touchpad = touchpad
                view.touchpad = touchpad
                close()
                showMenu()
            }
            val button = if (view.touchpad == touchpad) primary(getString(text)) {} else small(getString(text), choose)
            modes.addView(button, LinearLayout.LayoutParams(0, dp(42), 1f).apply { marginEnd = dp(6) })
        }
        mode(R.string.remote_mode_pad, true)
        mode(R.string.remote_mode_touch, false)
        panel.addView(modes, wrap(bottom = 6))
        panel.addView(label(getString(if (view.touchpad) R.string.remote_pad_hint else R.string.remote_touch_hint), 12f, Style.MUTED), wrap(bottom = 10))

        // Keys a phone keyboard doesn't have.
        val keys = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; layoutDirection = View.LAYOUT_DIRECTION_LTR }
        val shortcuts = listOf<Pair<String, IntArray>>(
            "Esc" to intArrayOf(Win.VK_ESCAPE), "Win" to intArrayOf(Win.VK_LWIN), "Tab" to intArrayOf(Win.VK_TAB), "Enter" to intArrayOf(Win.VK_RETURN),
            "⌫" to intArrayOf(Win.VK_BACK), "Del" to intArrayOf(Win.VK_DELETE), "←" to intArrayOf(Win.VK_LEFT), "↑" to intArrayOf(Win.VK_UP),
            "↓" to intArrayOf(Win.VK_DOWN), "→" to intArrayOf(Win.VK_RIGHT), "Alt+Tab" to intArrayOf(Win.VK_MENU, Win.VK_TAB),
            "Ctrl+C" to intArrayOf(Win.VK_CONTROL, 0x43), "Ctrl+V" to intArrayOf(Win.VK_CONTROL, 0x56), "Ctrl+Z" to intArrayOf(Win.VK_CONTROL, 0x5A),
        )
        for ((text, vks) in shortcuts) keys.addView(small(text) { link?.chord(*vks) }, LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, dp(38)).apply { marginEnd = dp(5) })
        panel.addView(HorizontalScrollView(this).apply { addView(keys); isHorizontalScrollBarEnabled = false }, wrap(bottom = 10))

        // The rest.
        val actions = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        val action = { text: String, run: () -> Unit ->
            actions.addView(small(text) { close(); run() }, LinearLayout.LayoutParams(0, dp(42), 1f).apply { marginEnd = dp(6) })
        }
        action(getString(R.string.keyboard)) { showKeyboard() }
        if (monitors > 1) action(getString(R.string.remote_monitor)) {
            monitorIndex = (monitorIndex + 1) % monitors
            link?.monitor(monitorIndex)
        }
        action(getString(R.string.remote_end)) { endViewing(null) }
        panel.addView(actions, wrap())

        scrim.addView(panel, FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.BOTTOM))
        root.addView(scrim, FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))
    }

    private fun showKeyboard() {
        val view = catcher ?: return
        view.requestFocus()
        getSystemService(InputMethodManager::class.java).showSoftInput(view, 0)
    }

    @Suppress("DEPRECATION") // the replacement only exists from Android 11
    private fun showSystemBars(show: Boolean) {
        if (Build.VERSION.SDK_INT < 30) {
            window.decorView.systemUiVisibility = if (show) View.SYSTEM_UI_FLAG_VISIBLE
                else View.SYSTEM_UI_FLAG_FULLSCREEN or View.SYSTEM_UI_FLAG_HIDE_NAVIGATION or View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY
            return
        }
        window.insetsController?.run {
            if (show) show(WindowInsets.Type.systemBars())
            else {
                hide(WindowInsets.Type.systemBars())
                systemBarsBehavior = WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
            }
        }
    }

    private fun endViewing(reason: String?) {
        link?.close()
        link = null
        closeMenu = null
        viewingIp = null
        viewing = null
        wifiLock?.takeIf { it.isHeld }?.release()
        screen?.clear()
        monitorIndex = 0
        message = reason
        showList()
    }

    // ───── from the connection ─────

    override fun onWaiting(code: String) {
        main.post { if (link != null) showStatus(getString(R.string.remote_waiting, code)) }
    }

    override fun onAccepted(computer: RemoteSaved, monitors: Int) {
        store.save(computer)
        main.post {
            if (link == null) return@post
            this.monitors = monitors
            viewing = computer
            link?.viewSize(VIEW_SIDE)
            showViewer(computer.name)
        }
    }

    override fun onKeyRefused(name: String) = store.forgetKey(name)

    override fun onFrame(width: Int, height: Int, update: ByteArray) {
        screen?.apply(width, height, update)
    }

    override fun onEnded(reason: RemoteEnd) {
        main.post {
            if (link == null) return@post
            endViewing(getString(when (reason) {
                RemoteEnd.DECLINED -> R.string.remote_declined
                RemoteEnd.BUSY -> R.string.remote_busy
                RemoteEnd.UNREACHABLE -> R.string.remote_unreachable
                RemoteEnd.LOST -> R.string.remote_lost
            }))
        }
    }

    // ───── small helpers ─────

    private fun column() = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }

    private fun label(text: String, size: Float, color: Int, bold: Boolean = false, center: Boolean = true) = TextView(this).apply {
        this.text = text
        textSize = size
        setTextColor(color)
        if (bold) typeface = Typeface.DEFAULT_BOLD
        if (center) gravity = Gravity.CENTER
    }

    private fun rounded(fill: Int, stroke: Int) = GradientDrawable().apply {
        cornerRadius = 14 * density
        setColor(fill)
        setStroke(dp(1), stroke)
    }

    private fun primary(text: String, click: () -> Unit) = Button(this).apply {
        this.text = text
        isAllCaps = false
        setTextColor(Color.WHITE)
        typeface = Typeface.DEFAULT_BOLD
        background = GradientDrawable(GradientDrawable.Orientation.LEFT_RIGHT, intArrayOf(Style.ACCENT, Style.ACCENT2)).apply { cornerRadius = 24 * density }
        setOnClickListener { click() }
    }

    private fun small(text: String, click: () -> Unit) = Button(this).apply {
        this.text = text
        isAllCaps = false
        textSize = 13f
        setTextColor(Style.TEXT)
        background = rounded(Style.SURFACE, Style.BORDER)
        minWidth = dp(44)
        minimumWidth = dp(44)
        minHeight = dp(34)
        minimumHeight = dp(34)
        setPadding(dp(12), 0, dp(12), 0)
        setOnClickListener { click() }
    }

    private fun wrap(bottom: Int = 0) = LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { bottomMargin = dp(bottom) }

    private companion object {
        const val VIEW_SIDE = 1920
        const val MENU_BUTTON_DP = 46
    }
}
