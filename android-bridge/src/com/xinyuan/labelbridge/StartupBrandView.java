package com.xinyuan.labelbridge;

import android.animation.ValueAnimator;
import android.content.Context;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Paint;
import android.graphics.Path;
import android.graphics.PathMeasure;
import android.graphics.RectF;
import android.os.Build;
import android.os.SystemClock;
import android.util.AttributeSet;
import android.util.Log;
import android.view.View;
import android.view.animation.LinearInterpolator;

/** Finite, decorative animation only. Never controls SDK readiness or navigation. */
public final class StartupBrandView extends View {
    private static final long DURATION_MS = 720L;
    // Shared across the two SDK splash layouts: attaching another view must not replay it.
    private static long firstShownAt;
    private final Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final RectF bounds = new RectF();
    private final Path label = new Path();
    private final Path fold = new Path();
    private final Path check = new Path();
    private final Path checkSegment = new Path();
    private final PathMeasure checkMeasure;
    private ValueAnimator animator;
    private float progress = 1f;

    public StartupBrandView(Context context, AttributeSet attrs) {
        super(context, attrs);
        setImportantForAccessibility(IMPORTANT_FOR_ACCESSIBILITY_NO);
        label.moveTo(35, 23); label.lineTo(61, 23); label.lineTo(79, 41);
        label.lineTo(79, 75); label.quadTo(79, 83, 71, 83);
        label.lineTo(35, 83); label.quadTo(27, 83, 27, 75);
        label.lineTo(27, 31); label.quadTo(27, 23, 35, 23); label.close();
        fold.moveTo(61, 23); fold.lineTo(61, 35); fold.quadTo(61, 41, 67, 41);
        fold.lineTo(79, 41); fold.close();
        check.moveTo(70, 78); check.lineTo(75, 83); check.lineTo(84, 73);
        checkMeasure = new PathMeasure(check, false);
    }

    @Override protected void onAttachedToWindow() {
        super.onAttachedToWindow();
        startIfVisible();
        Log.i("LabelStartup", "Brand view attached");
    }

    @Override protected void onWindowVisibilityChanged(int visibility) {
        super.onWindowVisibilityChanged(visibility);
        if (visibility == VISIBLE) startIfVisible(); else stopAnimation();
    }

    private void startIfVisible() {
        if (!isAttachedToWindow() || getWindowVisibility() != VISIBLE || animator != null) return;
        // SDK loading may run in another process. Its handoff layout is always static.
        if ("label_startup_static".equals(getTag())) { progress = 1f; invalidate(); return; }
        if (Build.VERSION.SDK_INT >= 26 && !ValueAnimator.areAnimatorsEnabled()) {
            progress = 1f; invalidate(); return;
        }
        long now = SystemClock.uptimeMillis();
        if (firstShownAt == 0) firstShownAt = now;
        long elapsed = now - firstShownAt;
        if (elapsed >= DURATION_MS) { progress = 1f; invalidate(); return; }
        progress = (float) elapsed / DURATION_MS;
        animator = ValueAnimator.ofFloat(progress, 1f);
        animator.setDuration(DURATION_MS - elapsed);
        animator.setInterpolator(new LinearInterpolator());
        animator.addUpdateListener(value -> {
            progress = (float) value.getAnimatedValue(); invalidate();
        });
        animator.start();
    }

    private void stopAnimation() {
        if (animator != null) { animator.cancel(); animator.removeAllUpdateListeners(); animator = null; }
    }

    @Override protected void onDetachedFromWindow() {
        stopAnimation();
        super.onDetachedFromWindow();
    }

    private static float clamp(float value) { return Math.max(0f, Math.min(1f, value)); }

    private void fill(int color, int alpha) {
        paint.setStyle(Paint.Style.FILL); paint.setColor(color); paint.setAlpha(alpha);
    }

    @Override protected void onDraw(Canvas canvas) {
        super.onDraw(canvas);
        float enter = clamp(progress / .22f);
        float size = Math.min(getWidth(), getHeight());
        float scale = .94f + .06f * (1f - (1f - enter) * (1f - enter));
        int alpha = Math.round(255f * enter);
        canvas.save();
        canvas.translate((getWidth() - size) / 2f, (getHeight() - size) / 2f);
        canvas.scale(size / 108f, size / 108f);
        canvas.scale(scale, scale, 54, 54);
        fill(Color.rgb(35, 104, 196), alpha);
        bounds.set(0, 0, 108, 108); canvas.drawRoundRect(bounds, 23, 23, paint);
        fill(Color.WHITE, alpha); canvas.drawPath(label, paint);
        fill(Color.rgb(184, 215, 255), alpha); canvas.drawPath(fold, paint);
        bounds.set(37, 73, 57, 76); canvas.drawRoundRect(bounds, 1.5f, 1.5f, paint);
        fill(Color.rgb(35, 104, 196), alpha);
        canvas.drawRect(37, 49, 41, 68, paint); canvas.drawRect(45, 49, 47, 68, paint);
        canvas.drawRect(51, 49, 57, 68, paint); canvas.drawRect(61, 49, 63, 68, paint);
        canvas.drawRect(67, 49, 70, 68, paint);
        float scan = clamp((progress - .16f) / .58f);
        if (progress >= .16f && progress < .74f) {
            fill(Color.rgb(22, 170, 145), Math.round(230 * Math.min(1f, (1f - scan) * 6f)));
            bounds.set(32, 47 + 23 * scan, 75, 49 + 23 * scan);
            canvas.drawRoundRect(bounds, 1, 1, paint);
        }
        float done = clamp((progress - .74f) / .26f);
        if (done > 0f) {
            fill(Color.rgb(22, 170, 145), Math.round(alpha * done));
            canvas.drawCircle(77, 78, 14, paint);
            paint.setStyle(Paint.Style.STROKE); paint.setColor(Color.WHITE);
            paint.setAlpha(alpha); paint.setStrokeWidth(4);
            paint.setStrokeCap(Paint.Cap.ROUND); paint.setStrokeJoin(Paint.Join.ROUND);
            checkSegment.reset(); checkMeasure.getSegment(0, checkMeasure.getLength() * done, checkSegment, true);
            canvas.drawPath(checkSegment, paint);
        }
        canvas.restore();
    }
}
