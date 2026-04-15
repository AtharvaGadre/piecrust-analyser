#!/usr/bin/env python3
"""Pseudo-time equation discovery for piecrust morphology progression.

This module does NOT claim to recover the unique biological governing law.
It discovers a family of reduced, data-driven progression equations over a
latent pseudo-time variable tau from stage-labelled AFM profile data.
"""

from __future__ import annotations

import json
import math
import os
import sys
import tempfile
import traceback
from dataclasses import dataclass
from pathlib import Path
from typing import Dict, Iterable, List, Sequence, Tuple

import numpy as np
from scipy.ndimage import gaussian_filter1d
from scipy.signal import savgol_filter


DEFAULT_STAGE_MAPPING = {"early": 0.0, "middle": 0.5, "late": 1.0}
DEFAULT_THRESHOLDS = (0.0025, 0.004, 0.006, 0.01, 0.016)
TERM_ALIASES = {
    "1": "1",
    "h": "z",
    "h^2": "z^2",
    "h^3": "z^3",
    "h_s": "dz/ds",
    "h_ss": "d2z/ds2",
    "h_sss": "d3z/ds3",
    "h_ssss": "d4z/ds4",
    "h*h_s": "z*(dz/ds)",
    "h*h_ss": "z*(d2z/ds2)",
    "(h_s)^2": "(dz/ds)^2",
    "h^2*h_s": "z^2*(dz/ds)",
    "h^2*h_ss": "z^2*(d2z/ds2)",
    "tau": "tau",
    "tau^2": "tau^2",
    "h*tau": "z*tau",
    "h_ss*tau": "(d2z/ds2)*tau",
}


@dataclass
class PreparedProfile:
    file_name: str
    file_path: str
    stage: str
    condition_type: str
    unit: str
    dose_ug_per_ml: float
    x_nm: np.ndarray
    y_nm: np.ndarray
    s_nm: np.ndarray
    z_nm: np.ndarray
    aligned_s_nm: np.ndarray
    corrected_z_nm: np.ndarray
    mean_height_nm: float
    mean_width_nm: float
    height_to_width_ratio: float
    roughness_nm: float
    peak_separation_nm: float
    dip_depth_nm: float
    compromise_ratio: float


@dataclass
class CandidateFit:
    term_names: List[str]
    coefficients: Dict[str, float]
    coefficient_vector: np.ndarray
    mapping_name: str
    mapping: Dict[str, float]
    rmse: float
    peak_height_error: float
    width_error: float
    area_error: float
    compromise_consistency: float
    stability_score: float
    complexity_penalty: float
    meta_prior_score: float
    notes: str


def load_json(path: str) -> dict:
    with open(path, "r", encoding="utf-8") as handle:
        return json.load(handle)


def save_json(path: str, payload: dict) -> None:
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(payload, handle, indent=2)


def canonical_term_name(term: str) -> str:
    return TERM_ALIASES.get(term, term)


def ensure_stage_mapping(mapping: Dict[str, float] | None) -> Dict[str, float]:
    merged = dict(DEFAULT_STAGE_MAPPING)
    if mapping:
        for key, value in mapping.items():
            try:
                merged[str(key).strip().lower()] = float(value)
            except Exception:
                continue
    ordered = sorted(merged.items(), key=lambda item: item[1])
    return {key: value for key, value in ordered}


def infer_pseudotime_mapping(stage_labels: Sequence[str], sample_features: Dict[str, float], archive: dict, base_mapping: Dict[str, float]) -> Dict[str, float]:
    labels = {label.strip().lower() for label in stage_labels}
    if "middle" not in labels:
        return dict(base_mapping)

    archive_entries = archive.get("entries", [])
    if not archive_entries:
        return dict(base_mapping)

    feature_vector = np.array([
        sample_features.get("height_mean", 0.0),
        sample_features.get("width_mean", 0.0),
        sample_features.get("ratio_mean", 0.0),
        sample_features.get("roughness_mean", 0.0),
        sample_features.get("peak_separation_mean", 0.0),
    ], dtype=float)

    weighted_middle = []
    for entry in archive_entries:
        mapping = entry.get("stageMapping", {})
        if "middle" not in mapping:
            continue
        entry_features = entry.get("features", {})
        entry_vector = np.array([
            float(entry_features.get("height_mean", 0.0)),
            float(entry_features.get("width_mean", 0.0)),
            float(entry_features.get("ratio_mean", 0.0)),
            float(entry_features.get("roughness_mean", 0.0)),
            float(entry_features.get("peak_separation_mean", 0.0)),
        ], dtype=float)
        distance = float(np.linalg.norm(feature_vector - entry_vector))
        weight = math.exp(-distance / max(1.0, np.linalg.norm(feature_vector) + 1.0))
        weighted_middle.append((weight, float(mapping["middle"])))

    if not weighted_middle:
        return dict(base_mapping)

    numerator = sum(weight * value for weight, value in weighted_middle)
    denominator = sum(weight for weight, _ in weighted_middle)
    learned_middle = numerator / max(1e-9, denominator)
    learned_middle = float(np.clip(learned_middle, 0.2, 0.8))

    output = dict(base_mapping)
    output["middle"] = learned_middle
    return dict(sorted(output.items(), key=lambda item: item[1]))


def build_mapping_scenarios(base_mapping: Dict[str, float], stage_jitter: float) -> List[dict]:
    scenarios = [{"name": "fixed_stage_anchor", "anchors": dict(base_mapping)}]
    if "middle" in base_mapping:
        for direction, name in ((-1.0, "middle_compressed"), (1.0, "middle_expanded")):
            adjusted = dict(base_mapping)
            adjusted["middle"] = float(np.clip(adjusted["middle"] + direction * stage_jitter, 0.15, 0.85))
            ordered = dict(sorted(adjusted.items(), key=lambda item: item[1]))
            if ordered["early"] < ordered["middle"] < ordered["late"]:
                scenarios.append({"name": name, "anchors": ordered})
    return scenarios


def linear_edge_baseline(s_nm: np.ndarray, z_nm: np.ndarray) -> np.ndarray:
    edge = max(3, int(len(z_nm) * 0.12))
    left_x = float(np.mean(s_nm[:edge]))
    right_x = float(np.mean(s_nm[-edge:]))
    left_y = float(np.mean(z_nm[:edge]))
    right_y = float(np.mean(z_nm[-edge:]))
    if abs(right_x - left_x) < 1e-9:
        return np.full_like(z_nm, left_y)
    slope = (right_y - left_y) / (right_x - left_x)
    intercept = left_y - slope * left_x
    return slope * s_nm + intercept


def smooth_profile(values: np.ndarray) -> np.ndarray:
    if len(values) < 7:
        return values.copy()
    window = min(len(values) - (1 - len(values) % 2), 21)
    if window % 2 == 0:
        window -= 1
    window = max(5, window)
    poly = min(3, window - 2)
    filtered = savgol_filter(values, window_length=window, polyorder=poly, mode="interp")
    return gaussian_filter1d(filtered, sigma=1.0, mode="nearest")


def center_profile_anchor(s_nm: np.ndarray, z_nm: np.ndarray) -> float:
    positive = np.clip(z_nm, 0.0, None)
    if np.sum(positive) <= 1e-9:
        return float(s_nm[np.argmax(z_nm)])
    return float(np.sum(s_nm * positive) / np.sum(positive))


def fwhm_width(x_nm: np.ndarray, z_nm: np.ndarray) -> float:
    if len(z_nm) < 3:
        return 0.0
    peak_index = int(np.argmax(z_nm))
    peak = float(z_nm[peak_index])
    if peak <= 1e-9:
        return 0.0
    half = peak * 0.5
    left = peak_index
    right = peak_index
    while left > 0 and z_nm[left] >= half:
        left -= 1
    while right < len(z_nm) - 1 and z_nm[right] >= half:
        right += 1
    return max(0.0, float(x_nm[right] - x_nm[left]))


def dip_depth(z_nm: np.ndarray) -> float:
    if len(z_nm) < 5:
        return 0.0
    left_peak_idx = int(np.argmax(z_nm[: max(2, len(z_nm) // 2)]))
    right_half = z_nm[len(z_nm) // 2 :]
    right_peak_idx = len(z_nm) // 2 + int(np.argmax(right_half))
    if right_peak_idx <= left_peak_idx + 1:
        return 0.0
    valley = float(np.min(z_nm[left_peak_idx:right_peak_idx + 1]))
    return max(0.0, ((float(z_nm[left_peak_idx]) + float(z_nm[right_peak_idx])) * 0.5) - valley)


def peak_separation(x_nm: np.ndarray, z_nm: np.ndarray) -> float:
    if len(z_nm) < 5:
        return 0.0
    left_peak_idx = int(np.argmax(z_nm[: max(2, len(z_nm) // 2)]))
    right_half = z_nm[len(z_nm) // 2 :]
    right_peak_idx = len(z_nm) // 2 + int(np.argmax(right_half))
    return max(0.0, float(abs(x_nm[right_peak_idx] - x_nm[left_peak_idx])))


def compromise_ratio_from_profile(x_nm: np.ndarray, z_nm: np.ndarray) -> float:
    height = max(0.0, float(np.max(z_nm)))
    width = fwhm_width(x_nm, z_nm)
    removal = max(0.0, dip_depth(z_nm) + 0.15 * width)
    return removal / max(1e-9, height + removal)


def prepare_profiles(request: dict) -> Tuple[List[PreparedProfile], Dict[str, float], Dict[str, float]]:
    mapping = ensure_stage_mapping(request.get("stageMapping"))
    profiles: List[PreparedProfile] = []
    for item in request.get("files", []):
        s_nm = np.asarray(item.get("sNm", []), dtype=float)
        z_nm = np.asarray(item.get("zNm", []), dtype=float)
        x_nm = np.asarray(item.get("xNm", []), dtype=float)
        y_nm = np.asarray(item.get("yNm", []), dtype=float)
        if len(s_nm) < 24 or len(z_nm) != len(s_nm):
            continue
        order = np.argsort(s_nm)
        s_nm = s_nm[order]
        z_nm = z_nm[order]
        x_nm = x_nm[order] if len(x_nm) == len(order) else np.zeros_like(s_nm)
        y_nm = y_nm[order] if len(y_nm) == len(order) else np.zeros_like(s_nm)

        smoothed = smooth_profile(z_nm)
        baseline = linear_edge_baseline(s_nm, smoothed)
        corrected = smoothed - baseline
        corrected -= np.percentile(np.concatenate([corrected[: max(3, len(corrected) // 10)], corrected[-max(3, len(corrected) // 10) :]]), 50)
        corrected = corrected - min(0.0, float(np.min(corrected)))
        corrected = gaussian_filter1d(corrected, sigma=0.8, mode="nearest")
        anchor = center_profile_anchor(s_nm, corrected)
        aligned_s = s_nm - anchor

        width_nm = fwhm_width(aligned_s, corrected)
        height_nm = max(0.0, float(np.max(corrected)))
        ratio = height_nm / max(1e-9, width_nm)
        roughness = float(np.mean(np.abs(z_nm - smoothed)))

        profiles.append(
            PreparedProfile(
                file_name=str(item.get("fileName", "")),
                file_path=str(item.get("filePath", "")),
                stage=str(item.get("stage", "early")).strip().lower(),
                condition_type=str(item.get("conditionType", "unassigned")),
                unit=str(item.get("unit", "nm")),
                dose_ug_per_ml=float(item.get("doseUgPerMl", 0.0)),
                x_nm=x_nm,
                y_nm=y_nm,
                s_nm=s_nm,
                z_nm=z_nm,
                aligned_s_nm=aligned_s,
                corrected_z_nm=corrected,
                mean_height_nm=float(item.get("meanHeightNm", height_nm)),
                mean_width_nm=float(item.get("meanWidthNm", width_nm)),
                height_to_width_ratio=float(item.get("heightToWidthRatio", ratio)),
                roughness_nm=float(item.get("roughnessNm", roughness)),
                peak_separation_nm=float(item.get("peakSeparationNm", peak_separation(aligned_s, corrected))),
                dip_depth_nm=float(item.get("dipDepthNm", dip_depth(corrected))),
                compromise_ratio=float(item.get("compromiseRatio", compromise_ratio_from_profile(aligned_s, corrected))),
            )
        )

    stage_labels = sorted({profile.stage for profile in profiles if profile.stage in mapping}, key=lambda key: mapping[key])
    if not profiles or len(stage_labels) < 2:
        raise ValueError("Equation discovery needs at least two guided, stage-labelled profiles. Three ordered stages are strongly recommended.")

    features = {
        "height_mean": float(np.mean([profile.mean_height_nm for profile in profiles])) if profiles else 0.0,
        "width_mean": float(np.mean([profile.mean_width_nm for profile in profiles])) if profiles else 0.0,
        "ratio_mean": float(np.mean([profile.height_to_width_ratio for profile in profiles])) if profiles else 0.0,
        "roughness_mean": float(np.mean([profile.roughness_nm for profile in profiles])) if profiles else 0.0,
        "peak_separation_mean": float(np.mean([profile.peak_separation_nm for profile in profiles])) if profiles else 0.0,
    }
    return profiles, mapping, features


def build_common_grid(profiles: Sequence[PreparedProfile], count: int) -> np.ndarray:
    left = max(float(np.min(profile.aligned_s_nm)) for profile in profiles)
    right = min(float(np.max(profile.aligned_s_nm)) for profile in profiles)
    if right - left < 1e-6:
        extent = max(float(np.max(profile.aligned_s_nm) - np.min(profile.aligned_s_nm)) for profile in profiles)
        left = -0.5 * extent
        right = 0.5 * extent
    return np.linspace(left, right, count)


def resample_profile(x: np.ndarray, y: np.ndarray, x_grid: np.ndarray) -> np.ndarray:
    return np.interp(x_grid, x, y)


def bootstrap_stage_profiles(profiles: Sequence[PreparedProfile], stage_order: Sequence[str], grid: np.ndarray, bootstrap_index: int) -> Tuple[Dict[str, np.ndarray], Dict[str, dict]]:
    rng = np.random.default_rng(bootstrap_index + 17)
    stage_profiles: Dict[str, np.ndarray] = {}
    stage_stats: Dict[str, dict] = {}

    for stage in stage_order:
        members = [profile for profile in profiles if profile.stage == stage]
        if not members:
            continue
        chosen = [members[idx] for idx in rng.integers(0, len(members), size=len(members))]
        resampled = np.vstack([resample_profile(profile.aligned_s_nm, profile.corrected_z_nm, grid) for profile in chosen])
        mean_profile = np.mean(resampled, axis=0)
        std_profile = np.std(resampled, axis=0)
        stage_profiles[stage] = smooth_profile(mean_profile)
        stage_stats[stage] = {
            "sampleCount": len(chosen),
            "meanHeightNm": float(np.mean([profile.mean_height_nm for profile in chosen])),
            "heightStdNm": float(np.std([profile.mean_height_nm for profile in chosen])),
            "meanWidthNm": float(np.mean([profile.mean_width_nm for profile in chosen])),
            "widthStdNm": float(np.std([profile.mean_width_nm for profile in chosen])),
            "meanArea": float(np.mean([np.trapezoid(resample_profile(profile.aligned_s_nm, profile.corrected_z_nm, grid), grid) for profile in chosen])),
            "meanRoughnessNm": float(np.mean([profile.roughness_nm for profile in chosen])),
            "profileStd": std_profile,
            "meanCompromise": float(np.mean([profile.compromise_ratio for profile in chosen])),
        }

    return stage_profiles, stage_stats


def derivative_profile(values: np.ndarray, spacing: float, order: int) -> np.ndarray:
    if order <= 0:
        return values.copy()
    if len(values) < 7:
        output = values.copy()
        for _ in range(order):
            output = np.gradient(output, spacing, edge_order=1)
        return output
    window = min(len(values) - (1 - len(values) % 2), 21)
    if window % 2 == 0:
        window -= 1
    window = max(5, window)
    poly = min(4, window - 2)
    return savgol_filter(values, window_length=window, polyorder=poly, deriv=order, delta=spacing, mode="interp")


def compute_pseudotime_derivatives(stage_profiles: Dict[str, np.ndarray], stage_order: Sequence[str], stage_mapping: Dict[str, float]) -> np.ndarray:
    values = np.vstack([stage_profiles[stage] for stage in stage_order])
    taus = np.asarray([stage_mapping[stage] for stage in stage_order], dtype=float)
    derivatives = np.zeros_like(values)
    for index in range(len(stage_order)):
        if index == 0:
            dt = max(1e-9, taus[index + 1] - taus[index])
            derivatives[index] = (values[index + 1] - values[index]) / dt
        elif index == len(stage_order) - 1:
            dt = max(1e-9, taus[index] - taus[index - 1])
            derivatives[index] = (values[index] - values[index - 1]) / dt
        else:
            dt = max(1e-9, taus[index + 1] - taus[index - 1])
            derivatives[index] = (values[index + 1] - values[index - 1]) / dt
    return derivatives


def build_candidate_library(stage_profiles: Dict[str, np.ndarray], stage_order: Sequence[str], stage_mapping: Dict[str, float], grid: np.ndarray) -> Tuple[np.ndarray, np.ndarray, List[str]]:
    spacing = float(np.mean(np.diff(grid))) if len(grid) > 1 else 1.0
    h = np.vstack([stage_profiles[stage] for stage in stage_order])
    tau = np.asarray([stage_mapping[stage] for stage in stage_order], dtype=float)[:, None]
    hs = np.vstack([derivative_profile(profile, spacing, 1) for profile in h])
    hss = np.vstack([derivative_profile(profile, spacing, 2) for profile in h])
    hsss = np.vstack([derivative_profile(profile, spacing, 3) for profile in h])
    hssss = np.vstack([derivative_profile(profile, spacing, 4) for profile in h])
    h_tau = compute_pseudotime_derivatives(stage_profiles, stage_order, stage_mapping)

    library = {
        "1": np.ones_like(h),
        "z": h,
        "z^2": h ** 2,
        "z^3": h ** 3,
        "dz/ds": hs,
        "d2z/ds2": hss,
        "d3z/ds3": hsss,
        "d4z/ds4": hssss,
        "z*(dz/ds)": h * hs,
        "z*(d2z/ds2)": h * hss,
        "(dz/ds)^2": hs ** 2,
        "z^2*(dz/ds)": (h ** 2) * hs,
        "z^2*(d2z/ds2)": (h ** 2) * hss,
        "tau": np.broadcast_to(tau, h.shape),
        "tau^2": np.broadcast_to(tau ** 2, h.shape),
        "z*tau": h * tau,
        "(d2z/ds2)*tau": hss * tau,
    }

    term_names = list(library.keys())
    theta = np.column_stack([library[name].reshape(-1) for name in term_names])
    target = h_tau.reshape(-1)
    return theta, target, term_names


def load_archive(path: str) -> dict:
    archive_path = Path(path)
    if not archive_path.exists():
        return {"entries": []}
    try:
        with archive_path.open("r", encoding="utf-8") as handle:
            return json.load(handle)
    except Exception:
        return {"entries": []}


def update_meta_model(archive: dict, entry: dict, archive_path: str) -> dict:
    entries = archive.setdefault("entries", [])
    entries.append(entry)
    archive["entries"] = entries[-60:]
    Path(archive_path).parent.mkdir(parents=True, exist_ok=True)
    with open(archive_path, "w", encoding="utf-8") as handle:
        json.dump(archive, handle, indent=2)
    return archive


def predict_equation_family(new_sample_features: Dict[str, float], archive: dict) -> Dict[str, float]:
    priors: Dict[str, float] = {}
    entries = archive.get("entries", [])
    if not entries:
        return priors

    feature_vector = np.array([
        new_sample_features.get("height_mean", 0.0),
        new_sample_features.get("width_mean", 0.0),
        new_sample_features.get("ratio_mean", 0.0),
        new_sample_features.get("roughness_mean", 0.0),
        new_sample_features.get("peak_separation_mean", 0.0),
    ], dtype=float)

    scores = []
    for entry in entries:
        entry_features = entry.get("features", {})
        entry_vector = np.array([
            float(entry_features.get("height_mean", 0.0)),
            float(entry_features.get("width_mean", 0.0)),
            float(entry_features.get("ratio_mean", 0.0)),
            float(entry_features.get("roughness_mean", 0.0)),
            float(entry_features.get("peak_separation_mean", 0.0)),
        ], dtype=float)
        distance = float(np.linalg.norm(feature_vector - entry_vector))
        weight = math.exp(-distance / max(1.0, np.linalg.norm(feature_vector) + 1.0))
        scores.append((weight, entry))

    total_weight = max(1e-9, sum(weight for weight, _ in scores))
    for weight, entry in scores:
        for term in entry.get("topTerms", []):
            canonical = canonical_term_name(str(term))
            priors[canonical] = priors.get(canonical, 0.0) + weight / total_weight
    return priors


def ridge_least_squares(theta: np.ndarray, target: np.ndarray, ridge_alpha: float) -> np.ndarray:
    lhs = theta.T @ theta + ridge_alpha * np.eye(theta.shape[1])
    rhs = theta.T @ target
    return np.linalg.solve(lhs, rhs)


def sequential_thresholded_least_squares(theta: np.ndarray, target: np.ndarray, threshold: float, ridge_alpha: float, term_names: Sequence[str], term_priors: Dict[str, float]) -> np.ndarray:
    column_scales = np.linalg.norm(theta, axis=0)
    column_scales[column_scales < 1e-9] = 1.0
    prior_gain = np.array([1.0 + 0.25 * term_priors.get(name, 0.0) for name in term_names], dtype=float)
    scaled_theta = theta / column_scales * prior_gain
    coeffs_scaled = ridge_least_squares(scaled_theta, target, ridge_alpha)

    active = np.ones_like(coeffs_scaled, dtype=bool)
    for _ in range(12):
        effective_thresholds = threshold * (1.05 - 0.35 * np.clip([term_priors.get(name, 0.0) for name in term_names], 0.0, 1.0))
        new_active = np.abs(coeffs_scaled) >= effective_thresholds
        if not np.any(new_active):
            break
        if np.array_equal(new_active, active):
            active = new_active
            break
        active = new_active
        coeffs_scaled[:] = 0.0
        coeffs_scaled[active] = ridge_least_squares(scaled_theta[:, active], target, ridge_alpha)

    coeffs = coeffs_scaled * prior_gain / column_scales
    coeffs[~active] = 0.0
    return coeffs


def build_term_features(profile: np.ndarray, tau: float, spacing: float) -> Dict[str, np.ndarray]:
    hs = derivative_profile(profile, spacing, 1)
    hss = derivative_profile(profile, spacing, 2)
    hsss = derivative_profile(profile, spacing, 3)
    hssss = derivative_profile(profile, spacing, 4)
    tau_array = np.full_like(profile, tau)
    return {
        "1": np.ones_like(profile),
        "z": profile,
        "z^2": profile ** 2,
        "z^3": profile ** 3,
        "dz/ds": hs,
        "d2z/ds2": hss,
        "d3z/ds3": hsss,
        "d4z/ds4": hssss,
        "z*(dz/ds)": profile * hs,
        "z*(d2z/ds2)": profile * hss,
        "(dz/ds)^2": hs ** 2,
        "z^2*(dz/ds)": (profile ** 2) * hs,
        "z^2*(d2z/ds2)": (profile ** 2) * hss,
        "tau": tau_array,
        "tau^2": tau_array ** 2,
        "z*tau": profile * tau_array,
        "(d2z/ds2)*tau": hss * tau_array,
    }


def evaluate_rhs(profile: np.ndarray, tau: float, spacing: float, coefficients: Dict[str, float]) -> np.ndarray:
    features = build_term_features(profile, tau, spacing)
    rhs = np.zeros_like(profile)
    for term, coefficient in coefficients.items():
        rhs += float(coefficient) * features[term]
    return rhs


def simulate_candidate(candidate_id: str, coefficients: Dict[str, float], initial_profile: np.ndarray, spatial_grid: np.ndarray, tau_grid: Sequence[float] | None = None) -> Tuple[List[np.ndarray], float, str]:
    if tau_grid is None:
        tau_grid = np.linspace(0.0, 1.0, 9)
    tau_grid = np.asarray(tau_grid, dtype=float)
    spacing = float(np.mean(np.diff(spatial_grid))) if len(spatial_grid) > 1 else 1.0
    current = initial_profile.copy()
    current = np.clip(current, 0.0, None)
    trajectories = []
    current_tau = float(tau_grid[0])
    max_observed = max(1.0, float(np.max(initial_profile)))
    stable = True
    note = "Stable explicit pseudo-time reconstruction"

    for target_tau in tau_grid:
        dt_total = float(target_tau - current_tau)
        steps = max(1, int(abs(dt_total) / 0.01))
        dt = dt_total / steps
        for _ in range(steps):
            rhs = evaluate_rhs(current, current_tau, spacing, coefficients)
            rhs = np.nan_to_num(rhs, nan=0.0, posinf=0.0, neginf=0.0)
            current = current + dt * rhs
            current = smooth_profile(current)
            current = np.clip(current, 0.0, max_observed * 6.0)
            current_tau += dt
            if not np.all(np.isfinite(current)):
                stable = False
                note = "Numerical instability encountered during pseudo-time integration"
                current = np.nan_to_num(current, nan=0.0, posinf=max_observed * 6.0, neginf=0.0)
                break
        trajectories.append(current.copy())

    stability_score = 1.0 if stable else 0.35
    return trajectories, stability_score, note


def fit_equation_discovery(dataset: dict, archive_path: str) -> dict:
    profiles, base_mapping, features = prepare_profiles(dataset)
    archive = load_archive(archive_path)
    learned_mapping = infer_pseudotime_mapping([profile.stage for profile in profiles], features, archive, base_mapping)
    options = dataset.get("options", {})
    grid = build_common_grid(profiles, int(options.get("spatialGridCount", 220)))
    stage_order = [stage for stage in learned_mapping.keys() if any(profile.stage == stage for profile in profiles)]
    if len(stage_order) < 2:
        raise ValueError("Equation discovery needs at least two ordered stages after filtering.")

    mapping_scenarios = build_mapping_scenarios(learned_mapping, float(options.get("stageJitter", 0.10)))
    meta_priors = predict_equation_family(features, archive)

    raw_candidates: List[CandidateFit] = []
    stage_summary_payload = None
    fallback_bundle = None
    for bootstrap_index in range(int(options.get("bootstrapCount", 20))):
        stage_profiles, stage_stats = bootstrap_stage_profiles(profiles, stage_order, grid, bootstrap_index)
        if len(stage_profiles) < 2:
            continue
        if stage_summary_payload is None:
            stage_summary_payload = stage_stats
        for mapping_scenario in mapping_scenarios:
            mapping = mapping_scenario["anchors"]
            theta, target, term_names = build_candidate_library(stage_profiles, stage_order, mapping, grid)
            if fallback_bundle is None:
                fallback_bundle = (stage_profiles, stage_stats, mapping, theta, target, term_names)
            if not np.any(np.isfinite(theta)) or not np.any(np.isfinite(target)):
                continue
            target = np.nan_to_num(target, nan=0.0, posinf=0.0, neginf=0.0)
            theta = np.nan_to_num(theta, nan=0.0, posinf=0.0, neginf=0.0)
            threshold_scale = max(1e-6, float(np.std(target)))
            for threshold in DEFAULT_THRESHOLDS:
                coefficient_vector = sequential_thresholded_least_squares(
                    theta,
                    target,
                    threshold * threshold_scale,
                    ridge_alpha=1e-5,
                    term_names=term_names,
                    term_priors=meta_priors,
                )
                active = [term_names[index] for index, value in enumerate(coefficient_vector) if abs(value) >= 1e-10]
                if not active or len(active) > 6:
                    continue
                coefficients = {term_names[index]: float(value) for index, value in enumerate(coefficient_vector) if abs(value) >= 1e-10}
                tau_grid = [mapping[stage] for stage in stage_order]
                trajectories, stability_score, note = simulate_candidate(
                    "candidate",
                    coefficients,
                    stage_profiles[stage_order[0]],
                    grid,
                    tau_grid=tau_grid,
                )
                predicted_by_stage = {stage: trajectories[index] for index, stage in enumerate(stage_order)}
                rmse = float(np.mean([
                    np.sqrt(np.mean((predicted_by_stage[stage] - stage_profiles[stage]) ** 2))
                    for stage in stage_order
                ]))
                peak_error = float(np.mean([
                    abs(float(np.max(predicted_by_stage[stage])) - float(np.max(stage_profiles[stage])))
                    for stage in stage_order
                ]))
                width_error = float(np.mean([
                    abs(fwhm_width(grid, predicted_by_stage[stage]) - fwhm_width(grid, stage_profiles[stage]))
                    for stage in stage_order
                ]))
                area_error = float(np.mean([
                    abs(float(np.trapezoid(predicted_by_stage[stage], grid)) - float(np.trapezoid(stage_profiles[stage], grid)))
                    for stage in stage_order
                ]))
                compromise_error = float(np.mean([
                    abs(compromise_ratio_from_profile(grid, predicted_by_stage[stage]) - stage_stats[stage]["meanCompromise"])
                    for stage in stage_order
                ]))
                complexity_penalty = len(active) / 6.0
                meta_prior_score = float(np.mean([meta_priors.get(term, 0.0) for term in active])) if active else 0.0
                raw_candidates.append(
                    CandidateFit(
                        term_names=active,
                        coefficients=coefficients,
                        coefficient_vector=coefficient_vector,
                        mapping_name=mapping_scenario["name"],
                        mapping=mapping,
                        rmse=rmse,
                        peak_height_error=peak_error,
                        width_error=width_error,
                        area_error=area_error,
                        compromise_consistency=max(0.0, 1.0 - compromise_error),
                        stability_score=stability_score,
                        complexity_penalty=complexity_penalty,
                        meta_prior_score=meta_prior_score,
                        notes=note,
                    )
                )

    if not raw_candidates and fallback_bundle is not None:
        stage_profiles, stage_stats, mapping, theta, target, term_names = fallback_bundle
        fallback_term_sets = [
            ["1", "z", "d2z/ds2"],
            ["1", "z", "z^2", "d2z/ds2"],
            ["1", "z", "d2z/ds2", "(dz/ds)^2"],
            ["1", "z", "z*(dz/ds)", "d4z/ds4"],
        ]
        tau_grid = [mapping[stage] for stage in stage_order]
        for subset in fallback_term_sets:
            indices = [term_names.index(term) for term in subset if term in term_names]
            if len(indices) != len(subset):
                continue
            theta_subset = theta[:, indices]
            coefficient_vector = ridge_least_squares(theta_subset, target, ridge_alpha=1e-4)
            coefficients = {subset[index]: float(coefficient_vector[index]) for index in range(len(subset))}
            trajectories, stability_score, note = simulate_candidate(
                "fallback_candidate",
                coefficients,
                stage_profiles[stage_order[0]],
                grid,
                tau_grid=tau_grid,
            )
            predicted_by_stage = {stage: trajectories[index] for index, stage in enumerate(stage_order)}
            rmse = float(np.mean([
                np.sqrt(np.mean((predicted_by_stage[stage] - stage_profiles[stage]) ** 2))
                for stage in stage_order
            ]))
            peak_error = float(np.mean([
                abs(float(np.max(predicted_by_stage[stage])) - float(np.max(stage_profiles[stage])))
                for stage in stage_order
            ]))
            width_error = float(np.mean([
                abs(fwhm_width(grid, predicted_by_stage[stage]) - fwhm_width(grid, stage_profiles[stage]))
                for stage in stage_order
            ]))
            area_error = float(np.mean([
                abs(float(np.trapezoid(predicted_by_stage[stage], grid)) - float(np.trapezoid(stage_profiles[stage], grid)))
                for stage in stage_order
            ]))
            compromise_error = float(np.mean([
                abs(compromise_ratio_from_profile(grid, predicted_by_stage[stage]) - stage_stats[stage]["meanCompromise"])
                for stage in stage_order
            ]))
            raw_candidates.append(
                CandidateFit(
                    term_names=subset,
                    coefficients=coefficients,
                    coefficient_vector=np.zeros(theta.shape[1], dtype=float),
                    mapping_name="ridge_fallback",
                    mapping=mapping,
                    rmse=rmse,
                    peak_height_error=peak_error,
                    width_error=width_error,
                    area_error=area_error,
                    compromise_consistency=max(0.0, 1.0 - compromise_error),
                    stability_score=stability_score,
                    complexity_penalty=len(subset) / 6.0,
                    meta_prior_score=float(np.mean([meta_priors.get(term, 0.0) for term in subset])) if subset else 0.0,
                    notes=f"{note}. Conservative ridge fallback candidate used because sparse discovery produced no stable equation on the current dataset.",
                )
            )

    if not raw_candidates or stage_summary_payload is None:
        raise ValueError("No stable candidate equations were discovered from the current stage-labelled profiles.")

    grouped: Dict[Tuple[str, ...], List[CandidateFit]] = {}
    for candidate in raw_candidates:
        key = tuple(candidate.term_names)
        grouped.setdefault(key, []).append(candidate)

    equation_family = []
    for key, fits in grouped.items():
        aggregate_coefficients = {}
        coefficient_stats = {}
        for term in key:
            values = np.array([fit.coefficients.get(term, 0.0) for fit in fits], dtype=float)
            aggregate_coefficients[term] = float(np.mean(values))
            coefficient_stats[term] = {
                "mean": float(np.mean(values)),
                "standardDeviation": float(np.std(values)),
                "lower95": float(np.percentile(values, 2.5)),
                "upper95": float(np.percentile(values, 97.5)),
            }
        rmse = float(np.mean([fit.rmse for fit in fits]))
        peak_error = float(np.mean([fit.peak_height_error for fit in fits]))
        width_error = float(np.mean([fit.width_error for fit in fits]))
        area_error = float(np.mean([fit.area_error for fit in fits]))
        compromise_consistency = float(np.mean([fit.compromise_consistency for fit in fits]))
        stability = float(np.mean([fit.stability_score for fit in fits]))
        complexity = float(np.mean([fit.complexity_penalty for fit in fits]))
        meta_prior = float(np.mean([fit.meta_prior_score for fit in fits]))
        sensitivity = float(np.std([fit.rmse for fit in fits]))
        bootstrap_support = float(len(fits) / max(1, len(raw_candidates)))
        confidence = float(np.clip(0.30 * bootstrap_support + 0.25 * stability + 0.20 * compromise_consistency + 0.15 * (1.0 / (1.0 + rmse)) + 0.10 * meta_prior, 0.0, 1.0))
        score = rmse + 0.12 * peak_error + 0.08 * width_error + 0.04 * area_error + 0.18 * sensitivity + 0.15 * complexity - 0.10 * stability - 0.08 * meta_prior
        equation_family.append(
            {
                "rankScore": score,
                "equation": format_equation(aggregate_coefficients),
                "activeTerms": list(key),
                "coefficients": aggregate_coefficients,
                "coefficientStatistics": coefficient_stats,
                "rmse": rmse,
                "peakHeightError": peak_error,
                "widthError": width_error,
                "areaError": area_error,
                "compromiseConsistency": compromise_consistency,
                "stabilityScore": stability,
                "complexityPenalty": complexity,
                "confidence": confidence,
                "pseudotimeSensitivity": sensitivity,
                "bootstrapSupport": bootstrap_support,
                "metaPriorScore": meta_prior,
                "notes": summarize_notes(fits),
            }
        )

    equation_family.sort(key=lambda item: (item["rankScore"], -item["confidence"]))
    for index, candidate in enumerate(equation_family, start=1):
        candidate["rank"] = index
        candidate.pop("rankScore", None)

    top_candidate = equation_family[0]
    top_mapping = learned_mapping
    top_stage_profiles, top_stage_stats = bootstrap_stage_profiles(profiles, stage_order, grid, 0)
    tau_grid = np.asarray([top_mapping[stage] for stage in stage_order], dtype=float)
    reconstructed_anchors, _, _ = simulate_candidate(
        "top_candidate",
        top_candidate["coefficients"],
        top_stage_profiles[stage_order[0]],
        grid,
        tau_grid=tau_grid,
    )
    progression_tau = np.linspace(float(tau_grid[0]), float(tau_grid[-1]), 7)
    progression_profiles, _, _ = simulate_candidate(
        "top_candidate_progression",
        top_candidate["coefficients"],
        top_stage_profiles[stage_order[0]],
        grid,
        tau_grid=progression_tau,
    )

    observed_profiles = []
    reconstructed_profiles = []
    progression_payload = []
    for index, stage in enumerate(stage_order):
        observed_profiles.append(
            curve_payload(
                label=f"Observed {stage.title()}",
                stage=stage,
                kind="observed",
                tau=top_mapping[stage],
                x_values=grid,
                y_values=top_stage_profiles[stage],
            )
        )
        reconstructed_profiles.append(
            curve_payload(
                label=f"Reconstructed {stage.title()}",
                stage=stage,
                kind="reconstructed",
                tau=top_mapping[stage],
                x_values=grid,
                y_values=reconstructed_anchors[index],
            )
        )

    for index, tau in enumerate(progression_tau):
        progression_payload.append(
            curve_payload(
                label=f"tau {tau:.2f}",
                stage="progression",
                kind="progression",
                tau=float(tau),
                x_values=grid,
                y_values=progression_profiles[index],
            )
        )

    stage_summaries = []
    for stage in stage_order:
        summary = top_stage_stats[stage]
        stage_summaries.append(
            {
                "stage": stage,
                "tau": top_mapping[stage],
                "sampleCount": int(summary["sampleCount"]),
                "meanHeightNm": float(summary["meanHeightNm"]),
                "heightStdNm": float(summary["heightStdNm"]),
                "meanWidthNm": float(summary["meanWidthNm"]),
                "widthStdNm": float(summary["widthStdNm"]),
                "meanArea": float(summary["meanArea"]),
                "meanRoughnessNm": float(summary["meanRoughnessNm"]),
            }
        )

    archive_entry = {
        "sampleId": dataset.get("sampleId", "piecrust-session"),
        "stageMapping": top_mapping,
        "features": features,
        "topTerms": top_candidate["activeTerms"],
        "topEquation": top_candidate["equation"],
        "rmse": top_candidate["rmse"],
        "confidence": top_candidate["confidence"],
    }
    archive = update_meta_model(archive, archive_entry, archive_path)

    return {
        "sampleId": dataset.get("sampleId", "piecrust-session"),
        "timeMode": "pseudotime_stage_ordered",
        "profileMode": dataset.get("profileMode", "centerline_arc_length_profile"),
        "spatialCoordinateLabel": "Aligned centreline position s [nm]",
        "heightLabel": "Height z [nm]",
        "stageMappingMode": "fixed_stage_anchor_with_sensitivity_checks",
        "stageMapping": top_mapping,
        "mappingScenarios": mapping_scenarios,
        "stageProfiles": stage_summaries,
        "equationFamily": equation_family[:5],
        "observedProfiles": observed_profiles,
        "reconstructedProfiles": reconstructed_profiles,
        "progressionProfiles": progression_payload,
        "metaModelSummary": build_meta_summary(archive, meta_priors),
        "metaModelExampleCount": len(archive.get("entries", [])),
        "statusText": (
            "Data-driven discovery of a family of reduced progression laws for z(s,tau) over pseudo-time tau. "
            "These equations are stage-order-consistent reconstructions from x-y-z AFM data, not true physical kinetics."
        ),
    }


def build_meta_summary(archive: dict, meta_priors: Dict[str, float]) -> str:
    if not archive.get("entries"):
        return "Meta-model has no stored equation-discovery examples yet; discovery is using only the current stage-labelled dataset."
    if not meta_priors:
        return f"Meta-model has {len(archive['entries'])} archived example set(s), but no strong term prior was inferred for this morphology family."
    ranked = sorted(meta_priors.items(), key=lambda item: item[1], reverse=True)[:4]
    summary = ", ".join(f"{term} ({weight:.2f})" for term, weight in ranked)
    return (
        f"Meta-model archived {len(archive['entries'])} prior labelled equation-discovery run(s). "
        f"For this sample family it currently favours: {summary}."
    )


def summarize_notes(fits: Sequence[CandidateFit]) -> str:
    stable = sum(1 for fit in fits if fit.stability_score >= 0.99)
    if stable == len(fits):
        return "Stable across bootstrap fits and pseudo-time mapping checks."
    if stable == 0:
        return "Numerically fragile; use for qualitative comparison only."
    return "Mostly stable, but some bootstrap or pseudo-time variants showed numerical sensitivity."


def curve_payload(label: str, stage: str, kind: str, tau: float, x_values: np.ndarray, y_values: np.ndarray) -> dict:
    return {
        "label": label,
        "stage": stage,
        "kind": kind,
        "tau": float(tau),
        "points": [{"x": float(x), "y": float(y)} for x, y in zip(x_values, y_values)],
    }


def format_equation(coefficients: Dict[str, float]) -> str:
    ordered_terms = sorted(coefficients.items(), key=lambda item: (term_sort_key(item[0]), item[0]))
    parts = []
    for term, coefficient in ordered_terms:
        if abs(coefficient) < 1e-10:
            continue
        formatted = f"{abs(coefficient):.4g}"
        if term == "1":
            fragment = formatted
        else:
            simple = term in {"z", "z^2", "z^3", "tau", "tau^2"}
            fragment = f"{formatted}*{term}" if simple else f"{formatted}*({term})"
        if coefficient < 0:
            parts.append(f"- {fragment}")
        else:
            parts.append(f"+ {fragment}")
    if not parts:
        return "dz/dtau = 0"
    expression = " ".join(parts).lstrip("+ ")
    return f"dz/dtau = {expression}"


def term_sort_key(term: str) -> int:
    order = {
        "1": 0,
        "z": 1,
        "z^2": 2,
        "z^3": 3,
        "dz/ds": 4,
        "d2z/ds2": 5,
        "d3z/ds3": 6,
        "d4z/ds4": 7,
        "z*(dz/ds)": 8,
        "z*(d2z/ds2)": 9,
        "(dz/ds)^2": 10,
        "z^2*(dz/ds)": 11,
        "z^2*(d2z/ds2)": 12,
        "tau": 13,
        "tau^2": 14,
        "z*tau": 15,
        "(d2z/ds2)*tau": 16,
    }
    return order.get(term, 99)


def rank_candidates(candidates: Sequence[dict], observed_profiles: Sequence[dict]) -> List[dict]:
    return sorted(candidates, key=lambda item: (item.get("rank", 999), -item.get("confidence", 0.0)))


def main(argv: Sequence[str]) -> int:
    if len(argv) < 3:
        raise SystemExit("Usage: equation_discovery.py <input.json> <output.json> [archive.json]")

    input_path = argv[1]
    output_path = argv[2]
    archive_path = argv[3] if len(argv) > 3 else os.path.join(tempfile.gettempdir(), "piecrust-equation-discovery-archive.json")
    payload = load_json(input_path)
    result = fit_equation_discovery(payload, archive_path)
    save_json(output_path, result)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main(sys.argv))
    except Exception as exc:  # pragma: no cover - defensive CLI wrapper
        error = {
            "error": str(exc),
            "traceback": traceback.format_exc(),
        }
        if len(sys.argv) >= 3:
            try:
                save_json(sys.argv[2], error)
            except Exception:
                pass
        print(json.dumps(error), file=sys.stderr)
        raise SystemExit(1)
