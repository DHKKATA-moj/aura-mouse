package com.auramouse.app.ui

import android.Manifest
import android.app.Activity
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Color
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.text.Editable
import android.text.InputFilter
import android.text.InputType
import android.text.TextWatcher
import android.text.format.Formatter
import android.view.Gravity
import android.view.MotionEvent
import android.view.View
import android.view.ViewGroup
import android.view.WindowManager
import android.view.inputmethod.InputMethodManager
import android.widget.ImageView
import android.widget.Button
import android.widget.EditText
import android.widget.FrameLayout
import android.widget.HorizontalScrollView
import android.widget.LinearLayout
import android.widget.ProgressBar
import android.widget.ScrollView
import android.widget.TextView
import android.widget.Toast
import com.auramouse.app.R
import com.auramouse.app.core.Transfer
import com.auramouse.app.core.TransferState
import com.auramouse.app.core.Win

object Style {
    val BACKGROUND = Color.parseColor("#0B1020")
    val SURFACE = Color.parseColor("#151C2E")
    val BORDER = Color.parseColor("#2A3350")
    val TEXT = Color.parseColor("#F1F5F9")
    val MUTED = Color.parseColor("#94A3B8")
    val ACCENT = Color.parseColor("#22D3EE")
    val ACCENT2 = Color.parseColor("#2563EB")
    val GOOD = Color.parseColor("#34D399")
    val BAD = Color.parseColor("#F87171")
}

/** The one screen. Pages are rebuilt from Link's state, the same way the Windows window works. */
class MainActivity : Activity() {
    private enum class Page { CONTROL, FILES, POWER }

    private var page = Page.CONTROL
    private var useCode = false
    private var lastKey = ""
    private var powerMessage: String? = null
    private lateinit var root: FrameLayout
    private var catcher: KeyCatcher? = null
    private var transferList: LinearLayout? = null
    private var pendingShare: List<Uri> = emptyList()

    private val density get() = resources.displayMetrics.density
    private fun dp(v: Int) = (v * density).toInt()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        Link.init(this)
        window.statusBarColor = Style.BACKGROUND
        window.navigationBarColor = Style.BACKGROUND
        root = FrameLayout(this).apply { setBackgroundColor(Style.BACKGROUND) }
        setContentView(root)
        if (Build.VERSION.SDK_INT >= 33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED)
            requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 1)
        handleShare(intent)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        handleShare(intent)
    }

    override fun onStart() {
        super.onStart()
        Link.onChange = ::refresh
        if (!useCode) Link.resume()
        render()
    }

    override fun onStop() {
        Link.onChange = null
        Link.release() // the computer's own mouse is free again
        super.onStop()
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) Link.sendClipboardIfNew() // Android only allows this now
    }

    private fun handleShare(intent: Intent?) {
        intent ?: return
        @Suppress("DEPRECATION") // the replacements only exist from Android 13
        val uris: List<Uri> = when (intent.action) {
            Intent.ACTION_SEND -> listOfNotNull(intent.getParcelableExtra<Uri>(Intent.EXTRA_STREAM))
            Intent.ACTION_SEND_MULTIPLE -> intent.getParcelableArrayListExtra<Uri>(Intent.EXTRA_STREAM) ?: emptyList()
            else -> return
        }.filter(::isShareable)
        if (uris.isNotEmpty()) {
            pendingShare = uris
            page = Page.FILES
        } else intent.getStringExtra(Intent.EXTRA_TEXT)?.let { text -> Link.link?.sendClipboardText(text) ?: toast(R.string.not_connected) }
        setIntent(Intent())
        flushShare()
    }

    /** Any app can send here, and reading happens with this app's rights, so its own files must never be reachable. */
    private fun isShareable(uri: Uri) = uri.scheme == "content" && uri.authority != ClipProvider.URI.authority

    private fun flushShare() {
        if (pendingShare.isEmpty() || Link.state != Link.State.CONNECTED) return
        Link.sendFiles(pendingShare)
        pendingShare = emptyList()
    }

    /** Rebuilding the touchpad mid-gesture would break it, so only a real change of state rebuilds the page. */
    private fun refresh() {
        flushShare()
        if (stateKey() != lastKey) render() else updateTransfers()
    }

    private fun stateKey(): String {
        val touchpadShowing = page == Page.CONTROL && Link.state == Link.State.CONNECTED
        val computers = if (touchpadShowing) "" else Link.wakeTargets.joinToString(",") { it.name }
        return "${Link.state}|${Link.saved?.name}|${Link.remoteFiles?.id}|$computers"
    }

    private fun render() {
        lastKey = stateKey()
        root.removeAllViews()
        catcher = null
        transferList = null

        val needsCode = Link.saved == null || useCode
        val connected = Link.state == Link.State.CONNECTED
        if (connected) useCode = false
        if (needsCode && !connected) root.addView(pairPage())
        else root.addView(mainPage(connected))

        if (connected && page == Page.CONTROL) window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        else window.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
    }

    // ───── pairing ─────

    private fun pairPage(): View {
        val column = column().apply {
            gravity = Gravity.CENTER_HORIZONTAL
            setPadding(dp(28), dp(56), dp(28), dp(24))
        }
        column.addView(ImageView(this).apply { setImageResource(R.drawable.ic_logo) }, LinearLayout.LayoutParams(dp(72), dp(72)).apply { bottomMargin = dp(16) })
        column.addView(label(getString(R.string.pair_title), 24f, Style.TEXT, bold = true), wrap(bottom = 12))
        column.addView(label(getString(R.string.pair_sub), 14f, Style.MUTED), wrap(bottom = 28))

        val input = EditText(this).apply {
            inputType = InputType.TYPE_CLASS_NUMBER
            filters = arrayOf(InputFilter.LengthFilter(CODE_DIGITS))
            gravity = Gravity.CENTER
            textSize = 30f
            letterSpacing = 0.25f
            setTextColor(Style.TEXT)
            typeface = Typeface.DEFAULT_BOLD
            background = rounded(Style.SURFACE, Style.BORDER)
            setPadding(dp(16), dp(14), dp(16), dp(14))
            layoutDirection = View.LAYOUT_DIRECTION_LTR
        }
        column.addView(input, LinearLayout.LayoutParams(dp(270), ViewGroup.LayoutParams.WRAP_CONTENT).apply { bottomMargin = dp(20) })

        val status = when (Link.state) {
            Link.State.SEARCHING -> getString(R.string.looking)
            Link.State.WRONG_CODE -> getString(R.string.wrong_code)
            Link.State.NOT_FOUND -> if (useCode || Link.saved == null) getString(R.string.not_found) else ""
            else -> ""
        }
        column.addView(label(status, 14f, if (Link.state == Link.State.SEARCHING) Style.ACCENT else Style.BAD), wrap(bottom = 20))

        val go = primary(getString(R.string.connect)) { input.text.toString().toIntOrNull()?.let(::startPairing) }
        go.isEnabled = false
        go.alpha = 0.5f
        input.addTextChangedListener(object : TextWatcher {
            override fun beforeTextChanged(s: CharSequence?, a: Int, b: Int, c: Int) {}
            override fun onTextChanged(s: CharSequence?, a: Int, b: Int, c: Int) {}
            override fun afterTextChanged(s: Editable?) {
                val full = s?.length == CODE_DIGITS
                go.isEnabled = full
                go.alpha = if (full) 1f else 0.5f
                if (full && Link.state != Link.State.SEARCHING) startPairing(s.toString().toInt()) // no extra click needed
            }
        })
        column.addView(go, full(bottom = 12))
        column.addView(ghost(getString(R.string.remote_open)) { openRemote() }, full(bottom = 12))

        if (Link.saved != null) column.addView(ghost(getString(R.string.back)) {
            useCode = false
            Link.forgetAttempt()
            Link.resume()
            render()
        }, full())

        input.post {
            input.requestFocus()
            getSystemService(InputMethodManager::class.java).showSoftInput(input, 0)
        }
        return ScrollView(this).apply { addView(column); isFillViewport = true }
    }

    private fun openRemote() = startActivity(Intent(this, RemoteActivity::class.java))

    private fun startPairing(code: Int) {
        useCode = true // stay on this page until it connects
        Link.pair(code)
    }

    // ───── connected, or waiting for the saved computer ─────

    private fun mainPage(connected: Boolean): View {
        val column = column()
        val name = Link.link?.hostName?.takeIf { it.isNotEmpty() } ?: Link.saved?.name ?: ""
        column.addView(topBar(name, connected), LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT))

        val body = when {
            page == Page.POWER -> powerPage()
            page == Page.FILES -> filesPage()
            connected -> controlPage()
            else -> waitingPage(name)
        }
        column.addView(body, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, 0, 1f))
        column.addView(tabs(), LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(56)))
        return column
    }

    private fun topBar(name: String, connected: Boolean): View {
        val bar = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setPadding(dp(16), dp(12), dp(12), dp(8))
        }
        val dot = View(this).apply {
            background = GradientDrawable().apply { shape = GradientDrawable.OVAL; setColor(if (connected) Style.GOOD else Style.MUTED) }
        }
        bar.addView(dot, LinearLayout.LayoutParams(dp(10), dp(10)).apply { marginEnd = dp(10) })
        bar.addView(label(name, 15f, Style.TEXT, bold = true, center = false).apply { maxLines = 1 }, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        if (connected) bar.addView(small(getString(R.string.disconnect)) { Link.disconnect() })
        return bar
    }

    private fun tabs(): View {
        val row = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; setBackgroundColor(Style.SURFACE) }
        for ((target, text) in listOf(Page.CONTROL to R.string.tab_control, Page.FILES to R.string.tab_files, Page.POWER to R.string.tab_power)) {
            val tab = label(getString(text), 14f, if (page == target) Style.ACCENT else Style.MUTED, bold = page == target).apply {
                gravity = Gravity.CENTER
                setOnClickListener {
                    page = target
                    render()
                }
            }
            row.addView(tab, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MATCH_PARENT, 1f))
        }
        // Its own screen, with nothing shared with the mouse connection.
        val remote = label(getString(R.string.tab_remote), 14f, Style.MUTED).apply {
            gravity = Gravity.CENTER
            setOnClickListener { openRemote() }
        }
        row.addView(remote, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MATCH_PARENT, 1f))
        return row
    }

    private fun waitingPage(name: String): View {
        val column = column().apply {
            gravity = Gravity.CENTER
            setPadding(dp(28), dp(24), dp(28), dp(24))
        }
        val refused = Link.state == Link.State.REFUSED
        val message = when (Link.state) {
            Link.State.REFUSED -> R.string.refused
            Link.State.SEARCHING -> R.string.looking_for
            else -> R.string.disconnected_from
        }
        column.addView(label(getString(message, name), 16f, if (refused) Style.BAD else Style.TEXT), wrap(bottom = 28))
        if (Link.state == Link.State.NOT_FOUND) column.addView(primary(getString(R.string.connect)) { Link.resume() }, full(bottom = 12))
        addComputers(column)
        column.addView(ghost(getString(R.string.use_code)) {
            useCode = true
            Link.forgetAttempt()
            render()
        }, full())
        return ScrollView(this).apply { addView(column); isFillViewport = true }
    }

    private fun controlPage(): View {
        val column = column().apply { setPadding(dp(12), 0, dp(12), dp(10)) }

        // Keys a phone keyboard doesn't have.
        val keys = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; layoutDirection = View.LAYOUT_DIRECTION_LTR }
        val shortcuts = listOf<Pair<String, IntArray>>(
            "Esc" to intArrayOf(Win.VK_ESCAPE), "Tab" to intArrayOf(Win.VK_TAB), "Enter" to intArrayOf(Win.VK_RETURN), "⌫" to intArrayOf(Win.VK_BACK),
            "←" to intArrayOf(Win.VK_LEFT), "↑" to intArrayOf(Win.VK_UP), "↓" to intArrayOf(Win.VK_DOWN), "→" to intArrayOf(Win.VK_RIGHT),
            "Win" to intArrayOf(Win.VK_LWIN), "Ctrl+C" to intArrayOf(Win.VK_CONTROL, 0x43), "Ctrl+V" to intArrayOf(Win.VK_CONTROL, 0x56),
            "Ctrl+Z" to intArrayOf(Win.VK_CONTROL, 0x5A), "Ctrl+A" to intArrayOf(Win.VK_CONTROL, 0x41), "Alt+Tab" to intArrayOf(Win.VK_MENU, Win.VK_TAB),
            "Del" to intArrayOf(Win.VK_DELETE),
        )
        for ((text, vks) in shortcuts) keys.addView(small(text) { Link.link?.chord(*vks) }, LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, dp(38)).apply { marginEnd = dp(6) })
        column.addView(TouchpadView(this, getString(R.string.pad_hint)), LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, 0, 1f).apply { bottomMargin = dp(10) })
        column.addView(HorizontalScrollView(this).apply { addView(keys); isHorizontalScrollBarEnabled = false }, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { bottomMargin = dp(10) })

        val buttons = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; layoutDirection = View.LAYOUT_DIRECTION_LTR }
        buttons.addView(holdButton(getString(R.string.left), Win.LEFT_DOWN, Win.LEFT_UP), LinearLayout.LayoutParams(0, dp(52), 1f).apply { marginEnd = dp(6) })
        val keyboard = small(getString(R.string.keyboard)) { showKeyboard() }
        buttons.addView(keyboard, LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, dp(52)).apply { marginEnd = dp(6) })
        buttons.addView(holdButton(getString(R.string.right), Win.RIGHT_DOWN, Win.RIGHT_UP), LinearLayout.LayoutParams(0, dp(52), 1f))
        column.addView(buttons)

        catcher = KeyCatcher(this) { Link.link }
        column.addView(catcher, LinearLayout.LayoutParams(1, 1))
        return column
    }

    private fun showKeyboard() {
        val view = catcher ?: return
        view.requestFocus()
        getSystemService(InputMethodManager::class.java).showSoftInput(view, 0)
    }

    private fun holdButton(text: String, down: Int, up: Int): View = small(text) {}.apply {
        setOnTouchListener { v, e ->
            when (e.actionMasked) {
                MotionEvent.ACTION_DOWN -> { Link.link?.button(down); v.isPressed = true; v.performHapticFeedback(android.view.HapticFeedbackConstants.KEYBOARD_TAP) }
                MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> { Link.link?.button(up); v.isPressed = false; v.performClick() }
            }
            true
        }
    }

    // ───── files and clipboard ─────

    private fun filesPage(): View {
        val column = column().apply { setPadding(dp(16), dp(8), dp(16), dp(16)) }
        val connected = Link.state == Link.State.CONNECTED

        column.addView(primary(getString(R.string.send_files)) {
            if (!connected) { toast(R.string.not_connected); return@primary }
            val pick = Intent(Intent.ACTION_OPEN_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE).setType("*/*").putExtra(Intent.EXTRA_ALLOW_MULTIPLE, true)
            startActivityForResult(pick, REQUEST_FILES)
        }, full(bottom = 10))
        column.addView(ghost(getString(R.string.send_clipboard)) {
            if (!connected) toast(R.string.not_connected)
            else toast(if (Link.sendClipboard()) R.string.clipboard_sent else R.string.clipboard_empty)
        }, full(bottom = 14))

        Link.remoteFiles?.let { files ->
            val title = files.title ?: getString(R.string.n_files, files.count)
            column.addView(label(getString(R.string.pc_copied, title), 14f, Style.TEXT), wrap(bottom = 8))
            column.addView(primary(getString(R.string.download)) { Link.download(files.id) }, full(bottom = 14))
        }

        column.addView(label(getString(R.string.files_note), 12f, Style.MUTED), wrap(bottom = 14))
        transferList = column()
        updateTransfers()
        column.addView(ScrollView(this).apply { addView(transferList) }, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, 0, 1f))
        return column
    }

    private fun updateTransfers() {
        val list = transferList ?: return
        list.removeAllViews()
        for (t in Link.transfers) list.addView(transferRow(t), full(bottom = 10))
    }

    private fun transferRow(t: Transfer): View {
        val row = column().apply {
            background = rounded(Style.SURFACE, Style.BORDER)
            setPadding(dp(14), dp(12), dp(14), dp(12))
        }
        val title = t.title ?: getString(R.string.n_files, t.fileCount)
        row.addView(label(title, 14f, Style.TEXT, bold = true, center = false).apply { maxLines = 1 })
        val direction = getString(if (t.outgoing) R.string.to_pc else R.string.from_pc)
        val status = when (t.state) {
            TransferState.WAITING -> getString(R.string.state_waiting)
            TransferState.MOVING -> getString(R.string.progress, Formatter.formatShortFileSize(this, t.done), Formatter.formatShortFileSize(this, t.total))
            TransferState.RETRYING -> getString(R.string.state_retrying)
            TransferState.DONE -> getString(R.string.state_done)
            TransferState.CANCELED -> getString(R.string.state_canceled)
            TransferState.FAILED -> getString(if (t.error?.startsWith("err_space") == true) R.string.err_space else R.string.state_failed)
        }
        row.addView(label("$direction · $status", 12f, Style.MUTED, center = false))
        if (t.active) {
            row.addView(ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal).apply {
                max = 1000
                progress = if (t.total > 0) (t.done * 1000 / t.total).toInt() else 0
            }, full(bottom = 2))
            row.addView(small(getString(R.string.cancel)) { t.cancelRequested = true })
        }
        return row
    }

    @Deprecated("Deprecated in Java")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        if (requestCode != REQUEST_FILES || resultCode != RESULT_OK || data == null) return
        val uris = ArrayList<Uri>()
        data.data?.let(uris::add)
        data.clipData?.let { clip -> for (i in 0 until clip.itemCount) uris.add(clip.getItemAt(i).uri) }
        // Keep access, so a transfer that breaks can resume later.
        uris.forEach { runCatching { contentResolver.takePersistableUriPermission(it, Intent.FLAG_GRANT_READ_URI_PERMISSION) } }
        Link.sendFiles(uris)
    }

    // ───── turning the computer on ─────

    private fun powerPage(): View {
        val column = column().apply {
            gravity = Gravity.CENTER_HORIZONTAL
            setPadding(dp(24), dp(24), dp(24), dp(24))
        }
        column.addView(label(getString(R.string.power_title), 20f, Style.TEXT, bold = true), wrap(bottom = 10))
        column.addView(label(getString(R.string.power_sub), 14f, Style.MUTED), wrap(bottom = 24))
        if (Link.wakeTargets.isEmpty()) column.addView(label(getString(R.string.power_empty), 15f, Style.TEXT), wrap(bottom = 24))
        addComputers(column)
        column.addView(label(getString(R.string.power_needs), 12f, Style.MUTED), wrap())
        return ScrollView(this).apply { addView(column) }
    }

    /** One row per saved computer, then what happened with the last one that was asked to turn on. */
    private fun addComputers(column: LinearLayout) {
        for (target in Link.wakeTargets) column.addView(computerRow(target), full(bottom = 10))
        powerMessage?.let { column.addView(label(it, 14f, Style.ACCENT), wrap(bottom = 14)) }
    }

    private fun computerRow(target: WakeTarget): View {
        val row = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            background = rounded(Style.SURFACE, Style.BORDER)
            setPadding(dp(14), dp(10), dp(10), dp(10))
        }
        row.addView(label(target.name, 15f, Style.TEXT, bold = true, center = false).apply { maxLines = 1 }, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f).apply { marginEnd = dp(8) })
        if (Link.isConnectedTo(target.name))
            row.addView(ghost(getString(R.string.power_off)) { confirmShutdown(target) }.apply { setPadding(dp(14), 0, dp(14), 0) }, LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, dp(44)).apply { marginEnd = dp(6) })
        row.addView(primary(getString(R.string.power_on)) { sendWake(target) }.apply { setPadding(dp(18), 0, dp(18), 0) }, LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, dp(44)).apply { marginEnd = dp(6) })
        row.addView(small("✕") {
            powerMessage = null
            Link.removeWake(target)
        }.apply { contentDescription = getString(R.string.remove) }, LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, dp(44)))
        return row
    }

    private fun confirmShutdown(target: WakeTarget) {
        android.app.AlertDialog.Builder(this)
            .setTitle(getString(R.string.power_off_title, target.name))
            .setMessage(getString(R.string.power_off_sub))
            .setPositiveButton(getString(R.string.power_off)) { _, _ ->
                Link.shutdown()
                powerMessage = getString(R.string.power_off_sent, target.name)
                render()
            }
            .setNegativeButton(getString(R.string.cancel), null)
            .show()
    }

    private fun sendWake(target: WakeTarget) {
        Link.wake(target) { networks ->
            powerMessage = if (networks > 0) getString(R.string.power_sent, target.name) else getString(R.string.power_no_network)
            render()
        }
    }

    // ───── small helpers ─────

    private fun toast(text: Int) = Toast.makeText(this, text, Toast.LENGTH_SHORT).show()

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
        minHeight = dp(48)
        setOnClickListener { click() }
    }

    private fun ghost(text: String, click: () -> Unit) = Button(this).apply {
        this.text = text
        isAllCaps = false
        setTextColor(Style.TEXT)
        background = GradientDrawable().apply { cornerRadius = 24 * density; setColor(Style.SURFACE); setStroke(dp(1), Style.BORDER) }
        minHeight = dp(48)
        setOnClickListener { click() }
    }

    private fun small(text: String, click: () -> Unit) = Button(this).apply {
        this.text = text
        isAllCaps = false
        textSize = 13f
        setTextColor(Style.TEXT)
        background = rounded(Style.SURFACE, Style.BORDER)
        minWidth = dp(48)
        minimumWidth = dp(48)
        minHeight = dp(36)
        minimumHeight = dp(36)
        setPadding(dp(12), 0, dp(12), 0)
        setOnClickListener { click() }
    }

    private fun wrap(bottom: Int = 0) = LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { bottomMargin = dp(bottom) }

    private fun full(bottom: Int = 0) = wrap(bottom)

    private companion object {
        const val REQUEST_FILES = 10
        const val CODE_DIGITS = 6
    }
}
