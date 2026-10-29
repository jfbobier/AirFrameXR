#pragma once
#include <algorithm>
#include <cmath>

// The size the game is told to render per eye (GetRecommendedResolution) under a field of view restriction.
// Floor: the stream's eye size scaled by the restricted span per axis (the game renders the same pixel density as the stream, over less angle).
// Ceiling: the stream's eye size itself (the game renders as many pixels per axis as an unrestricted game would), or the panel reference above it
// when the host gives one (the stream fitted to the headset's visible window). quality 0..1 moves between them:
// the pixels the restriction saved are spent on a finer render that composition filters down. Not restricted: floor == ceiling, nothing to choose.
struct RenderSize {
    int w, h;
};

inline int EvenAtLeast64(double v) {
    return std::max(64, ((int)std::lround(v) + 1) / 2 * 2);
}

// ceilW / ceilH: a ceiling above the stream's eye size (the panel reference when the stream is fitted to the headset's visible window, ipc.h renderCeiling);
// 0 = the stream's eye size. The ceiling is never below the floor.
inline RenderSize GameRenderSize(int eyeW, int eyeH, double widthRatio, double heightRatio, float quality, int ceilW = 0, int ceilH = 0) {
    const double q = std::min(1.0, std::max(0.0, (double)quality));
    const double fw = eyeW * widthRatio, fh = eyeH * heightRatio;
    const double cw = std::max(fw, (double)(ceilW > 0 ? ceilW : eyeW)), ch = std::max(fh, (double)(ceilH > 0 ? ceilH : eyeH));
    return {EvenAtLeast64(fw + (cw - fw) * q), EvenAtLeast64(fh + (ch - fh) * q)};
}
