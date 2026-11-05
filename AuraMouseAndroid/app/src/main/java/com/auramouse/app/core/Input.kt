package com.auramouse.app.core

import kotlin.math.hypot

/**
 * Keyboards change the word being typed as they go (autocorrect, Arabic letters joining). This works out
 * what to type on the computer: how many backspaces, then what text.
 */
class TextDiff {
    private var composing = ""

    class Edit(val backspaces: Int, val text: String)

    /** The word in progress is now this. */
    fun update(next: String): Edit {
        val shared = composing.commonPrefixWith(next).length
        val edit = Edit(composing.length - shared, next.substring(shared))
        composing = next
        return edit
    }

    /** The word is final: what comes next starts fresh. */
    fun commit(text: String): Edit {
        val edit = update(text)
        composing = ""
        return edit
    }

    fun finish() { composing = "" }
}

/** Finger movement to cursor movement: precise when slow, far when fast. Keeps the fractions so nothing is lost. */
class PointerScale(private val density: Float, private val speed: Float = 1.6f) {
    private var restX = 0f
    private var restY = 0f

    fun apply(dxPixels: Float, dyPixels: Float): Pair<Int, Int> {
        val dx = dxPixels / density
        val dy = dyPixels / density
        val gain = speed * (1f + (hypot(dx, dy) / 12f).coerceAtMost(2f))
        restX += dx * gain
        restY += dy * gain
        val x = restX.toInt()
        val y = restY.toInt()
        restX -= x
        restY -= y
        return x to y
    }
}
