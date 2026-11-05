package com.auramouse.app.ui

import android.annotation.SuppressLint
import android.content.Context
import android.graphics.Canvas
import android.graphics.Paint
import android.graphics.RectF
import android.text.InputType
import android.text.StaticLayout
import android.text.TextPaint
import android.view.HapticFeedbackConstants
import android.view.KeyEvent
import android.view.MotionEvent
import android.view.View
import android.view.ViewConfiguration
import android.view.inputmethod.BaseInputConnection
import android.view.inputmethod.EditorInfo
import android.view.inputmethod.InputConnection
import com.auramouse.app.core.KeySink
import com.auramouse.app.core.PointerScale
import com.auramouse.app.core.TextDiff
import com.auramouse.app.core.Win
import kotlin.math.abs
import kotlin.math.hypot

/** The touchpad. One finger moves, tap clicks, two fingers scroll or right-click, tap then press and move drags. */
class TouchpadView(context: Context, private val hint: String) : View(context) {
    private val density = resources.displayMetrics.density
    private val scale = PointerScale(density)
    private val slop = ViewConfiguration.get(context).scaledTouchSlop.toFloat()
    private val background = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = Style.SURFACE }
    private val border = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = Style.BORDER; style = Paint.Style.STROKE; strokeWidth = density }
    private val hintPaint = TextPaint(Paint.ANTI_ALIAS_FLAG).apply { color = Style.MUTED; textSize = 13 * density }
    private val rect = RectF()

    private var pointer = -1
    private var lastX = 0f
    private var lastY = 0f
    private var startX = 0f
    private var startY = 0f
    private var startTime = 0L
    private var lastTapTime = 0L
    private var moved = false
    private var fingers = 1
    private var armedDrag = false
    private var dragging = false
    private var scrollRestX = 0f
    private var scrollRestY = 0f

    @SuppressLint("ClickableViewAccessibility")
    override fun onTouchEvent(e: MotionEvent): Boolean {
        val link = Link.link ?: return true
        when (e.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                pointer = e.getPointerId(0)
                lastX = e.x; lastY = e.y; startX = e.x; startY = e.y
                startTime = e.eventTime
                moved = false
                fingers = 1
                armedDrag = e.eventTime - lastTapTime < DOUBLE_TAP_MS
                scrollRestX = 0f; scrollRestY = 0f
                parent?.requestDisallowInterceptTouchEvent(true)
            }
            MotionEvent.ACTION_POINTER_DOWN -> {
                fingers = maxOf(fingers, e.pointerCount)
                armedDrag = false
                lastX = averageX(e); lastY = averageY(e)
            }
            MotionEvent.ACTION_POINTER_UP -> {
                // Carry on from whichever finger is left, without a jump.
                val leaving = e.actionIndex
                val keep = (0 until e.pointerCount).first { it != leaving }
                pointer = e.getPointerId(keep)
                lastX = e.getX(keep); lastY = e.getY(keep)
            }
            MotionEvent.ACTION_MOVE -> {
                if (e.pointerCount >= 2) {
                    val x = averageX(e)
                    val y = averageY(e)
                    if (hypot(x - lastX, y - lastY) > 0) scroll(x - lastX, y - lastY)
                    lastX = x; lastY = y
                    moved = true
                } else if (fingers == 1) {
                    val index = e.findPointerIndex(pointer)
                    if (index >= 0) {
                        val x = e.getX(index)
                        val y = e.getY(index)
                        if (!moved && hypot(x - startX, y - startY) > slop) {
                            moved = true
                            if (armedDrag) {
                                dragging = true
                                link.button(Win.LEFT_DOWN)
                                performHapticFeedback(HapticFeedbackConstants.LONG_PRESS)
                            }
                        }
                        if (moved) {
                            val (dx, dy) = scale.apply(x - lastX, y - lastY)
                            link.move(dx, dy)
                        }
                        lastX = x; lastY = y
                    }
                }
            }
            MotionEvent.ACTION_UP -> {
                if (dragging) {
                    dragging = false
                    link.button(Win.LEFT_UP)
                } else if (!moved && e.eventTime - startTime < TAP_MS) {
                    if (fingers >= 2) click(Win.RIGHT_DOWN, Win.RIGHT_UP)
                    else {
                        click(Win.LEFT_DOWN, Win.LEFT_UP)
                        lastTapTime = e.eventTime
                    }
                }
            }
            MotionEvent.ACTION_CANCEL -> if (dragging) {
                dragging = false
                link.button(Win.LEFT_UP)
            }
        }
        return true
    }

    private fun click(down: Int, up: Int) {
        val link = Link.link ?: return
        link.button(down)
        link.button(up)
        performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
    }

    /** Fingers up moves the page up, like on a laptop's touchpad and a phone's own screen. */
    private fun scroll(dx: Float, dy: Float) {
        val link = Link.link ?: return
        scrollRestY += dy / density * WHEEL_PER_DP
        scrollRestX -= dx / density * WHEEL_PER_DP
        if (abs(scrollRestY) >= WHEEL_STEP) {
            link.wheel(scrollRestY.toInt(), false)
            scrollRestY = 0f
        }
        if (abs(scrollRestX) >= WHEEL_STEP) {
            link.wheel(scrollRestX.toInt(), true)
            scrollRestX = 0f
        }
    }

    private fun averageX(e: MotionEvent) = (0 until e.pointerCount).map { e.getX(it) }.average().toFloat()
    private fun averageY(e: MotionEvent) = (0 until e.pointerCount).map { e.getY(it) }.average().toFloat()

    override fun onDraw(canvas: Canvas) {
        rect.set(density, density, width - density, height - density)
        val corner = 20 * density
        canvas.drawRoundRect(rect, corner, corner, background)
        canvas.drawRoundRect(rect, corner, corner, border)
        val inner = (width - 48 * density).toInt().coerceAtLeast(1)
        val layout = StaticLayout.Builder.obtain(hint, 0, hint.length, hintPaint, inner)
            .setAlignment(android.text.Layout.Alignment.ALIGN_CENTER).setLineSpacing(6 * density, 1f).build()
        canvas.save()
        canvas.translate(24 * density, (height - layout.height) / 2f)
        layout.draw(canvas)
        canvas.restore()
    }

    private companion object {
        const val TAP_MS = 250L
        const val DOUBLE_TAP_MS = 300L
        const val WHEEL_PER_DP = 6f
        const val WHEEL_STEP = 15f
    }
}

/**
 * An invisible text box for the phone's keyboard: whatever is typed goes straight to the computer in
 * any language. Backspace and Enter arrive differently from one keyboard app to another, so all the ways are handled.
 */
class KeyCatcher(context: Context, private val sink: () -> KeySink?) : View(context) {
    private val diff = TextDiff()

    init {
        isFocusable = true
        isFocusableInTouchMode = true
    }

    override fun onCheckIsTextEditor() = true

    override fun onCreateInputConnection(info: EditorInfo): InputConnection {
        info.inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_FLAG_MULTI_LINE
        info.imeOptions = EditorInfo.IME_FLAG_NO_ENTER_ACTION or EditorInfo.IME_FLAG_NO_EXTRACT_UI
        diff.finish()
        return Connection()
    }

    private fun apply(edit: TextDiff.Edit) {
        val link = sink() ?: return
        repeat(edit.backspaces) { link.tapKey(Win.VK_BACK) }
        val lines = edit.text.split('\n')
        lines.forEachIndexed { i, line ->
            if (line.isNotEmpty()) link.text(line)
            if (i < lines.lastIndex) link.tapKey(Win.VK_RETURN)
        }
    }

    private inner class Connection : BaseInputConnection(this@KeyCatcher, false) {
        override fun commitText(text: CharSequence, cursor: Int): Boolean {
            apply(diff.commit(text.toString()))
            return true
        }

        override fun setComposingText(text: CharSequence, cursor: Int): Boolean {
            apply(diff.update(text.toString()))
            return true
        }

        override fun finishComposingText(): Boolean {
            diff.finish()
            return true
        }

        override fun deleteSurroundingText(before: Int, after: Int): Boolean {
            val link = sink() ?: return true
            repeat(before) { link.tapKey(Win.VK_BACK) }
            repeat(after) { link.tapKey(Win.VK_DELETE) }
            return true
        }

        override fun sendKeyEvent(event: KeyEvent): Boolean {
            if (event.action != KeyEvent.ACTION_DOWN) return true
            val link = sink() ?: return true
            when (event.keyCode) {
                KeyEvent.KEYCODE_DEL -> link.tapKey(Win.VK_BACK)
                KeyEvent.KEYCODE_FORWARD_DEL -> link.tapKey(Win.VK_DELETE)
                KeyEvent.KEYCODE_ENTER, KeyEvent.KEYCODE_NUMPAD_ENTER -> link.tapKey(Win.VK_RETURN)
                KeyEvent.KEYCODE_TAB -> link.tapKey(Win.VK_TAB)
                else -> event.unicodeChar.takeIf { it > 0 }?.let { link.text(String(Character.toChars(it))) }
            }
            return true
        }

        override fun performEditorAction(actionCode: Int): Boolean {
            sink()?.tapKey(Win.VK_RETURN)
            return true
        }
    }
}
