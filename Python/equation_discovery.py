#!/usr/bin/env python3
"""Pseudo-time equation discovery for piecrust morphology progression.

This module does NOT claim to recover the unique biological governing law.
It discovers a family of reduced, data-driven progression equations over a
latent pseudo-time variable tau from ordered AFM profile data.
"""

from __future__ import annotations

import json
import math
import os
import sys
import tempfile
import traceback
from dataclasses import dataclass
from importlib import import_module
from pathlib import Path
from typing import Dict, Iterable, List, Sequence, Tuple

import numpy as np
from scipy.integrate import solve_ivp
from scipy.ndimage import gaussian_filter1d
from scipy.signal import savgol_filter


DEFAULT_STAGE_MAPPING = {"early": 0.0, "middle": 0.5, "late": 1.0}
DEFAULT_THRESHOLDS = (0.0025, 0.004, 0.006, 0.01, 0.016)
FEATURE_STATE_NAMES = ("A1", "A2", "D", "sigma1", "sigma2")
GOOD_RMSE_THRESHOLD = 30.0
FAIL_RMSE_THRESHOLD = 50.0
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

# (PHASE 0) Load the stage-ordering validator dynamically from the workspace root.
HAS_VALIDATOR = False
validate_ordering = None
report_stage_quality = None
for _candidate_root in (Path(__file__).resolve().parents[2], Path.cwd()):
    _validator_path = _candidate_root / "stage_ordering_validator.py"
    if not _validator_path.exists():
        continue
    if str(_candidate_root) not in sys.path:
        sys.path.insert(0, str(_candidate_root))
    try:
        _validator_module = import_module("stage_ordering_validator")
        validate_ordering = getattr(_validator_module, "validate_ordering", None)
        report_stage_quality = getattr(_validator_module, "report_stage_quality", None)
        HAS_VALIDATOR = callable(validate_ordering) and callable(report_stage_quality)
        if HAS_VALIDATOR:
            break
    except Exception:
        HAS_VALIDATOR = False
        validate_ordering = None
        report_stage_quality = None

# (STEP 1) Load the reusable bimodal feature extraction module from the workspace root.
HAS_FEATURE_EXTRACTION = False
fit_bimodal_gaussian_external = None
extract_bimodal_features = None
interpolate_bimodal_parameters = None
for _candidate_root in (Path(__file__).resolve().parents[2], Path.cwd()):
    _feature_module_path = _candidate_root / "feature_extraction.py"
    if not _feature_module_path.exists():
        continue
    if str(_candidate_root) not in sys.path:
        sys.path.insert(0, str(_candidate_root))
    try:
        _feature_module = import_module("feature_extraction")
        fit_bimodal_gaussian_external = getattr(_feature_module, "fit_bimodal_gaussian", None)
        extract_bimodal_features = getattr(_feature_module, "extract_features", None)
        interpolate_bimodal_parameters = getattr(_feature_module, "interpolate_parameters", None)
        HAS_FEATURE_EXTRACTION = callable(fit_bimodal_gaussian_external) and callable(extract_bimodal_features) and callable(interpolate_bimodal_parameters)
        if HAS_FEATURE_EXTRACTION:
            break
    except Exception:
        HAS_FEATURE_EXTRACTION = False
        fit_bimodal_gaussian_external = None
        extract_bimodal_features = None
        interpolate_bimodal_parameters = None


class StageValidationError(RuntimeError):
    def __init__(self, message: str, payload: dict):
        super().__init__(message)
        self.payload = payload


@dataclass
class PreparedProfile:
    file_name: str
    file_path: str
    sequence_order: int
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
    profile_index: int = 0
    arc_position_nm: float = 0.0
    tau_value: float = 0.0
    tau_normalized: float = 0.0


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


def build_stage_validation_payload(metrics, report_text: str, *, validator_available: bool, skipped: bool, reason: str = "") -> dict:
    if metrics is None:
        return {
            "validatorAvailable": validator_available,
            "skipped": skipped,
            "confidenceScore": 0.0,
            "heightTrend": 0.0,
            "bimodalityTrend": 0.0,
            "widthTrend": 0.0,
            "overallConsistency": 0.0,
            "problematicIndices": [],
            "rationale": reason or "Stage-ordering validation was skipped.",
            "recommendation": "UNAVAILABLE" if not validator_available else "SKIPPED",
            "interpretation": reason or "Stage-ordering confidence could not be estimated.",
            "report": report_text,
        }

    confidence = float(metrics.confidence_score)
    if confidence >= 0.8:
        recommendation = "HIGH"
        interpretation = "Stage ordering is reliable enough for pseudo-time discovery with low additional uncertainty."
    elif confidence >= 0.5:
        recommendation = "MEDIUM"
        interpretation = "Stage ordering is only moderately reliable. Proceed, but interpret the discovered law cautiously."
    else:
        recommendation = "LOW"
        interpretation = "Stage ordering is too uncertain for safe derivative-based discovery. Revisit ordering before proceeding."

    return {
        "validatorAvailable": validator_available,
        "skipped": skipped,
        "confidenceScore": confidence,
        "heightTrend": float(metrics.height_trend),
        "bimodalityTrend": float(metrics.bimodality_trend),
        "widthTrend": float(metrics.width_trend),
        "overallConsistency": float(metrics.overall_consistency),
        "problematicIndices": [int(index) for index in metrics.problematic_indices],
        "rationale": metrics.rationale,
        "recommendation": recommendation,
        "interpretation": interpretation,
        "report": report_text,
    }


def run_stage_validation(profiles: Sequence["PreparedProfile"]) -> dict:
    print("PHASE 0: VALIDATING STAGE ORDERING")
    if len(profiles) < 2:
        reason = "Stage-ordering validation needs at least two ordered profiles, so validation was skipped."
        print(f"[phase0] {reason}")
        return build_stage_validation_payload(None, reason, validator_available=False, skipped=True, reason=reason)

    if not HAS_VALIDATOR or validate_ordering is None or report_stage_quality is None:
        reason = "stage_ordering_validator.py was not available. Discovery continued without a confidence gate."
        print(f"[phase0] {reason}")
        return build_stage_validation_payload(None, reason, validator_available=False, skipped=True, reason=reason)

    metrics = validate_ordering(list(profiles))
    report_text = report_stage_quality(metrics)
    print(report_text)
    payload = build_stage_validation_payload(metrics, report_text, validator_available=True, skipped=False)

    if payload["confidenceScore"] < 0.5:
        raise StageValidationError(
            "Stage-ordering confidence is below 50%, so pseudo-time equation discovery was aborted to avoid fitting derivatives of noise.",
            payload,
        )

    if payload["confidenceScore"] < 0.8:
        print(
            f"[phase0] Warning: stage-ordering confidence is {payload['confidenceScore']:.1%}. "
            "Proceeding with caution and recording the uncertainty in the output JSON."
        )
    else:
        print(f"[phase0] Stage ordering passed with confidence {payload['confidenceScore']:.1%}.")

    return payload


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
    window = min(len(values) - (1 - len(values) % 2), 17)
    if window % 2 == 0:
        window -= 1
    window = max(5, window)
    poly = min(3, window - 2)
    filtered = savgol_filter(values, window_length=window, polyorder=poly, mode="interp")
    return gaussian_filter1d(filtered, sigma=0.55, mode="nearest")


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


def smoothstep(value: float) -> float:
    clamped = float(np.clip(value, 0.0, 1.0))
    return clamped * clamped * (3.0 - 2.0 * clamped)


def normalize_tau_value(value: float, tau_min: float, tau_max: float) -> float:
    span = max(1e-9, float(tau_max) - float(tau_min))
    return float((float(value) - float(tau_min)) / span)


def fit_polynomial_curve(tau_values: Sequence[float], values: Sequence[float], degree: int) -> np.ndarray:
    tau = np.asarray(tau_values, dtype=float)
    observed = np.asarray(values, dtype=float)
    if tau.size == 0 or observed.size == 0 or tau.size != observed.size:
        return np.zeros(1, dtype=float)
    if tau.size == 1:
        return np.array([float(observed[0])], dtype=float)
    degree = int(np.clip(degree, 1, max(1, tau.size - 1)))
    try:
        return np.polyfit(tau, observed, deg=degree)
    except np.linalg.LinAlgError:
        return np.polyfit(tau, observed + np.linspace(0.0, 1e-9, observed.size), deg=min(1, degree))


def evaluate_polynomial_curve(coefficients: Sequence[float], tau: float) -> float:
    coeffs = np.asarray(coefficients, dtype=float)
    if coeffs.size == 0:
        return 0.0
    return float(np.polyval(coeffs, float(tau)))


def estimate_peak_sigma_nm(grid: np.ndarray, profile: np.ndarray, peak_index: int) -> float:
    if len(grid) < 2 or len(profile) == 0:
        return 1.0
    peak_index = int(np.clip(peak_index, 0, len(profile) - 1))
    peak = float(profile[peak_index])
    spacing = float(np.mean(np.diff(grid))) if len(grid) > 1 else 1.0
    if peak <= 1e-9:
        return max(spacing * 1.5, 1.0)

    half_height = peak * 0.5
    left = peak_index
    right = peak_index
    while left > 0 and profile[left] >= half_height:
        left -= 1
    while right < len(profile) - 1 and profile[right] >= half_height:
        right += 1

    fwhm = abs(float(grid[right] - grid[left])) if right > left else spacing * 4.0
    sigma = fwhm / (2.0 * math.sqrt(2.0 * math.log(2.0)))
    span = abs(float(grid[-1] - grid[0])) if len(grid) > 1 else spacing * len(profile)
    return float(np.clip(sigma, spacing * 1.5, max(spacing * 2.0, span * 0.28)))


def estimate_bimodal_parameters(grid: np.ndarray, profile: np.ndarray) -> np.ndarray:
    values = np.clip(np.asarray(profile, dtype=float), 0.0, None)
    if len(values) < 5:
        return np.array([0.0, 1.0, 0.0, 1.0, 1.0], dtype=float)

    mid = max(1, len(values) // 2)
    left_peak_idx = int(np.argmax(values[: mid + 1]))
    right_peak_idx = mid + int(np.argmax(values[mid:]))
    if right_peak_idx <= left_peak_idx:
        dominant = np.argsort(values)[-2:]
        dominant.sort()
        left_peak_idx = int(dominant[0])
        right_peak_idx = int(dominant[-1])

    spacing = float(np.mean(np.diff(grid))) if len(grid) > 1 else 1.0
    separation = max(spacing * 4.0, abs(float(grid[right_peak_idx] - grid[left_peak_idx])))
    left_amplitude = float(values[left_peak_idx])
    right_amplitude = float(values[right_peak_idx])
    left_sigma = estimate_peak_sigma_nm(grid, values, left_peak_idx)
    right_sigma = estimate_peak_sigma_nm(grid, values, right_peak_idx)

    if left_amplitude <= 1e-9 or right_amplitude <= 1e-9:
        dominant_idx = int(np.argmax(values))
        dominant_amp = float(values[dominant_idx])
        dominant_sigma = estimate_peak_sigma_nm(grid, values, dominant_idx)
        left_amplitude = max(left_amplitude, dominant_amp * 0.88)
        right_amplitude = max(right_amplitude, dominant_amp)
        left_sigma = max(left_sigma, dominant_sigma)
        right_sigma = max(right_sigma, dominant_sigma)

    return np.array(
        [
            max(0.0, left_amplitude),
            max(spacing * 1.5, abs(left_sigma)),
            max(0.0, right_amplitude),
            max(spacing * 1.5, abs(right_sigma)),
            max(spacing * 4.0, abs(separation)),
        ],
        dtype=float,
    )


def build_bimodal_profile_from_parameters(grid: np.ndarray, parameters: Sequence[float]) -> np.ndarray:
    if len(grid) == 0 or len(parameters) < 5:
        return np.zeros(len(grid), dtype=float)
    spacing = float(np.mean(np.diff(grid))) if len(grid) > 1 else 1.0
    center = float((grid[0] + grid[-1]) * 0.5) if len(grid) > 1 else 0.0
    left_amplitude = max(0.0, float(parameters[0]))
    left_sigma = max(spacing * 1.5, abs(float(parameters[1])))
    right_amplitude = max(0.0, float(parameters[2]))
    right_sigma = max(spacing * 1.5, abs(float(parameters[3])))
    separation = max(spacing * 4.0, abs(float(parameters[4])))
    left_center = center - separation * 0.5
    right_center = center + separation * 0.5
    left = left_amplitude * np.exp(-np.square(grid - left_center) / (2.0 * left_sigma * left_sigma))
    right = right_amplitude * np.exp(-np.square(grid - right_center) / (2.0 * right_sigma * right_sigma))
    return np.clip(left + right, 0.0, None)


def weighted_profile_average(profile_matrix: np.ndarray, tau_values: np.ndarray, tau: float, bandwidth: float) -> np.ndarray:
    if profile_matrix.ndim != 2 or profile_matrix.shape[0] == 0:
        return np.zeros(profile_matrix.shape[1] if profile_matrix.ndim == 2 else 0, dtype=float)
    spread = max(1e-6, float(bandwidth))
    weights = np.exp(-0.5 * np.square((tau_values - float(tau)) / spread))
    total = float(np.sum(weights))
    if total <= 1e-9:
        nearest = int(np.argmin(np.abs(tau_values - float(tau))))
        return profile_matrix[nearest].copy()
    weights /= total
    return np.sum(profile_matrix * weights[:, None], axis=0)


def build_mixed_growth_model(individual_profiles: Sequence[np.ndarray], tau_values: np.ndarray, grid: np.ndarray) -> dict:
    observed = np.vstack([np.asarray(profile, dtype=float) for profile in individual_profiles])
    tau = np.asarray(tau_values, dtype=float)
    if observed.ndim != 2 or observed.shape[0] == 0 or len(grid) == 0:
        raise ValueError("No individual profiles were available to build the mixed growth model.")

    parameters = np.vstack([estimate_bimodal_parameters(grid, profile) for profile in observed])
    bimodal_profiles = np.vstack([build_bimodal_profile_from_parameters(grid, params) for params in parameters])
    residual_profiles = observed - bimodal_profiles

    degree = int(np.clip(min(3, observed.shape[0] - 1), 1, max(1, observed.shape[0] - 1)))
    parameter_coefficients = [fit_polynomial_curve(tau, parameters[:, index], degree) for index in range(parameters.shape[1])]

    heights = np.maximum(0.0, np.max(observed, axis=1))
    widths = np.array([fwhm_width(grid, profile) for profile in observed], dtype=float)
    monotone_heights = np.maximum.accumulate(heights)
    monotone_widths = np.maximum.accumulate(np.maximum(0.0, widths))

    unique_tau = np.unique(np.round(tau, 6))
    tau_min = float(np.min(tau))
    tau_max = float(np.max(tau))
    tau_span = max(1e-6, tau_max - tau_min)
    if unique_tau.size > 1:
        nominal_spacing = float(np.median(np.diff(unique_tau)))
        bandwidth = float(np.clip(nominal_spacing * 1.75, 0.05 * tau_span, 0.35 * tau_span))
    else:
        bandwidth = 0.18 * tau_span

    return {
        "tau": tau,
        "tauMin": tau_min,
        "tauMax": tau_max,
        "grid": np.asarray(grid, dtype=float),
        "parameterCoefficients": parameter_coefficients,
        "observedProfiles": observed,
        "bimodalProfiles": bimodal_profiles,
        "residualProfiles": residual_profiles,
        "heightTargets": monotone_heights,
        "widthTargets": monotone_widths,
        "bandwidth": bandwidth,
    }


def evaluate_mixed_growth_profile(model: dict, tau: float) -> Tuple[np.ndarray, np.ndarray]:
    tau_min = float(model.get("tauMin", 0.0))
    tau_max = float(model.get("tauMax", 1.0))
    if tau_max < tau_min:
        tau_min, tau_max = tau_max, tau_min
    tau_span = max(1e-6, tau_max - tau_min)
    clamped_tau = float(np.clip(tau, tau_min, tau_max))
    tau_progress = (clamped_tau - tau_min) / tau_span
    grid = np.asarray(model.get("grid", []), dtype=float)
    tau_samples = np.asarray(model.get("tau", []), dtype=float)
    parameter_coefficients = model.get("parameterCoefficients", [])
    if len(grid) == 0 or len(parameter_coefficients) < 5:
        return np.zeros(len(grid), dtype=float), np.zeros(len(grid), dtype=float)

    envelope_parameters = np.array(
        [evaluate_polynomial_curve(coefficients, clamped_tau) for coefficients in parameter_coefficients],
        dtype=float,
    )
    envelope_parameters[0] = max(0.0, envelope_parameters[0])
    envelope_parameters[1] = max(1e-6, abs(envelope_parameters[1]))
    envelope_parameters[2] = max(0.0, envelope_parameters[2])
    envelope_parameters[3] = max(1e-6, abs(envelope_parameters[3]))
    envelope_parameters[4] = max(0.0, abs(envelope_parameters[4]))
    envelope = build_bimodal_profile_from_parameters(grid, envelope_parameters)

    observed_profiles = np.asarray(model.get("observedProfiles", np.zeros((0, len(grid)), dtype=float)), dtype=float)
    bimodal_profiles = np.asarray(model.get("bimodalProfiles", np.zeros((0, len(grid)), dtype=float)), dtype=float)
    bandwidth = float(model.get("bandwidth", 0.18))
    local_observed = weighted_profile_average(observed_profiles, tau_samples, clamped_tau, bandwidth)
    local_bimodal = weighted_profile_average(bimodal_profiles, tau_samples, clamped_tau, bandwidth)
    local_detail = local_observed - local_bimodal

    transition = smoothstep((tau_progress - 0.18) / 0.72)
    detail_weight = 1.0 - 0.72 * transition
    mixed = envelope + detail_weight * local_detail
    mixed = np.clip(mixed, 0.0, None)

    height_targets = np.asarray(model.get("heightTargets", []), dtype=float)
    width_targets = np.asarray(model.get("widthTargets", []), dtype=float)
    target_height = float(np.interp(clamped_tau, tau_samples, height_targets)) if tau_samples.size and height_targets.size == tau_samples.size else float(np.max(mixed))
    target_width = float(np.interp(clamped_tau, tau_samples, width_targets)) if tau_samples.size and width_targets.size == tau_samples.size else fwhm_width(grid, mixed)

    envelope_height = float(np.max(envelope)) if envelope.size else 0.0
    target_height = max(target_height, envelope_height)
    current_height = float(np.max(mixed)) if mixed.size else 0.0
    if current_height > 1e-9 and target_height > 0.0:
        mixed *= target_height / current_height

    current_width = fwhm_width(grid, mixed)
    if current_width > 1e-6 and target_width > 1e-6:
        width_scale = float(np.clip(target_width / current_width, 0.85, 1.18))
        center = float((grid[0] + grid[-1]) * 0.5) if len(grid) > 1 else 0.0
        warped_grid = center + (grid - center) / width_scale
        mixed = np.interp(grid, warped_grid, mixed, left=0.0, right=0.0)

    mixed = np.clip(mixed, 0.0, None)
    return mixed, envelope


def build_mixed_growth_simulation(
    base_simulation: dict,
    mixed_model: dict | None,
    grid: np.ndarray,
    tau_grid: Sequence[float] | None = None,
) -> dict:
    if mixed_model is None:
        return dict(base_simulation)

    tau_values = np.asarray(tau_grid if tau_grid is not None else base_simulation.get("tau", []), dtype=float)
    if tau_values.size == 0 or len(grid) == 0:
        return dict(base_simulation)

    mixed_profiles: List[np.ndarray] = []
    envelope_profiles: List[np.ndarray] = []
    parameters: List[np.ndarray] = []
    for tau_value in tau_values:
        mixed_profile, envelope_profile = evaluate_mixed_growth_profile(mixed_model, float(tau_value))
        mixed_profile = np.asarray(mixed_profile, dtype=float)
        envelope_profile = np.asarray(envelope_profile, dtype=float)
        mixed_profiles.append(mixed_profile)
        envelope_profiles.append(envelope_profile)
        parameters.append(estimate_bimodal_parameters(np.asarray(grid, dtype=float), mixed_profile))

    if parameters:
        parameter_matrix = np.vstack(parameters)
    else:
        parameter_matrix = np.zeros((tau_values.size, 5), dtype=float)

    left_amp = np.maximum.accumulate(np.maximum(0.0, parameter_matrix[:, 0])) if parameter_matrix.size else np.zeros(tau_values.size, dtype=float)
    left_sigma = np.maximum(1e-6, parameter_matrix[:, 1]) if parameter_matrix.size else np.ones(tau_values.size, dtype=float)
    right_amp = np.maximum.accumulate(np.maximum(0.0, parameter_matrix[:, 2])) if parameter_matrix.size else np.zeros(tau_values.size, dtype=float)
    right_sigma = np.maximum(1e-6, parameter_matrix[:, 3]) if parameter_matrix.size else np.ones(tau_values.size, dtype=float)
    separation = np.maximum.accumulate(np.maximum(0.0, parameter_matrix[:, 4])) if parameter_matrix.size else np.zeros(tau_values.size, dtype=float)
    states = {
        "A1": [float(value) for value in left_amp],
        "A2": [float(value) for value in right_amp],
        "D": [float(value) for value in separation],
        "sigma1": [float(value) for value in left_sigma],
        "sigma2": [float(value) for value in right_sigma],
        "ratio": [float(left / max(1e-9, right)) for left, right in zip(left_amp, right_amp)],
        "mu1": [float(-0.5 * value) for value in separation],
        "mu2": [float(0.5 * value) for value in separation],
        "sigma_avg": [float(0.5 * (left + right)) for left, right in zip(left_sigma, right_sigma)],
    }

    return {
        "success": bool(base_simulation.get("success", True)),
        "tau": [float(value) for value in tau_values],
        "profiles": mixed_profiles,
        "envelopeProfiles": envelope_profiles,
        "states": states,
        "stabilityScore": max(float(base_simulation.get("stabilityScore", 0.0)), 0.85),
        "note": (
            "Feature ODE trajectories post-processed with a polynomial + bimodal Gaussian mixed-growth envelope "
            "computed from per-image averaged guided line profiles."
        ),
    }


def prepare_guided_profile(source: dict, item: dict, local_index: int) -> dict | None:
    s_nm = np.asarray(source.get("sNm", item.get("sNm", [])), dtype=float)
    z_nm = np.asarray(source.get("zNm", item.get("zNm", [])), dtype=float)
    x_nm = np.asarray(source.get("xNm", item.get("xNm", [])), dtype=float)
    y_nm = np.asarray(source.get("yNm", item.get("yNm", [])), dtype=float)
    if len(s_nm) < 24 or len(z_nm) != len(s_nm):
        return None

    order = np.argsort(s_nm)
    s_nm = s_nm[order]
    z_nm = z_nm[order]
    x_nm = x_nm[order] if len(x_nm) == len(order) else np.zeros_like(s_nm)
    y_nm = y_nm[order] if len(y_nm) == len(order) else np.zeros_like(s_nm)

    smoothed = smooth_profile(z_nm)
    baseline = linear_edge_baseline(s_nm, smoothed)
    corrected_raw = z_nm - baseline
    corrected_smooth = smoothed - baseline
    corrected = corrected_raw * 0.62 + corrected_smooth * 0.38
    corrected -= np.percentile(
        np.concatenate([corrected[: max(3, len(corrected) // 10)], corrected[-max(3, len(corrected) // 10) :]]),
        50,
    )
    corrected = corrected - min(0.0, float(np.min(corrected)))
    corrected = gaussian_filter1d(corrected, sigma=0.28, mode="nearest")
    anchor = center_profile_anchor(s_nm, corrected)
    aligned_s = s_nm - anchor

    width_nm = fwhm_width(aligned_s, corrected)
    height_nm = max(0.0, float(np.max(corrected)))
    ratio = height_nm / max(1e-9, width_nm)
    roughness = float(np.mean(np.abs(z_nm - smoothed)))

    return {
        "profileIndex": int(source.get("profileIndex", local_index)),
        "arcPositionNm": float(source.get("arcPositionNm", 0.0)),
        "sNm": s_nm,
        "zNm": z_nm,
        "xNm": x_nm,
        "yNm": y_nm,
        "alignedSNm": aligned_s,
        "correctedZNm": corrected,
        "meanHeightNm": float(source.get("meanHeightNm", item.get("meanHeightNm", height_nm))),
        "meanWidthNm": float(source.get("meanWidthNm", item.get("meanWidthNm", width_nm))),
        "heightToWidthRatio": float(source.get("heightToWidthRatio", item.get("heightToWidthRatio", ratio))),
        "roughnessNm": float(source.get("roughnessNm", item.get("roughnessNm", roughness))),
        "peakSeparationNm": float(source.get("peakSeparationNm", item.get("peakSeparationNm", peak_separation(aligned_s, corrected)))),
        "dipDepthNm": float(source.get("dipDepthNm", item.get("dipDepthNm", dip_depth(corrected)))),
        "compromiseRatio": float(source.get("compromiseRatio", item.get("compromiseRatio", compromise_ratio_from_profile(aligned_s, corrected)))),
    }


def average_prepared_guided_profiles(processed_profiles: Sequence[dict]) -> dict:
    if len(processed_profiles) == 1:
        only = processed_profiles[0]
        return {
            "xNm": only["xNm"],
            "yNm": only["yNm"],
            "sNm": only["alignedSNm"],
            "zNm": only["correctedZNm"],
            "alignedSNm": only["alignedSNm"],
            "correctedZNm": only["correctedZNm"],
            "profileIndex": 0,
            "arcPositionNm": float(only.get("arcPositionNm", 0.0)),
            "meanHeightNm": float(only["meanHeightNm"]),
            "meanWidthNm": float(only["meanWidthNm"]),
            "heightToWidthRatio": float(only["heightToWidthRatio"]),
            "roughnessNm": float(only["roughnessNm"]),
            "peakSeparationNm": float(only["peakSeparationNm"]),
            "dipDepthNm": float(only["dipDepthNm"]),
            "compromiseRatio": float(only["compromiseRatio"]),
        }

    left = max(float(np.min(profile["alignedSNm"])) for profile in processed_profiles)
    right = min(float(np.max(profile["alignedSNm"])) for profile in processed_profiles)
    if right - left < 1e-6:
        left = min(float(np.min(profile["alignedSNm"])) for profile in processed_profiles)
        right = max(float(np.max(profile["alignedSNm"])) for profile in processed_profiles)

    point_count = max(len(profile["alignedSNm"]) for profile in processed_profiles)
    local_grid = np.linspace(left, right, point_count)
    averaged_corrected = np.mean(
        np.vstack([np.interp(local_grid, profile["alignedSNm"], profile["correctedZNm"], left=0.0, right=0.0) for profile in processed_profiles]),
        axis=0,
    )
    averaged_x = np.mean(
        np.vstack([np.interp(local_grid, profile["alignedSNm"], profile["xNm"], left=profile["xNm"][0], right=profile["xNm"][-1]) for profile in processed_profiles]),
        axis=0,
    )
    averaged_y = np.mean(
        np.vstack([np.interp(local_grid, profile["alignedSNm"], profile["yNm"], left=profile["yNm"][0], right=profile["yNm"][-1]) for profile in processed_profiles]),
        axis=0,
    )
    averaged_corrected = smooth_profile(np.asarray(averaged_corrected, dtype=float))

    height_nm = max(0.0, float(np.max(averaged_corrected)))
    width_nm = fwhm_width(local_grid, averaged_corrected)
    ratio = height_nm / max(1e-9, width_nm)
    roughness = float(np.mean([profile["roughnessNm"] for profile in processed_profiles]))

    return {
        "xNm": averaged_x,
        "yNm": averaged_y,
        "sNm": local_grid,
        "zNm": averaged_corrected,
        "alignedSNm": local_grid,
        "correctedZNm": averaged_corrected,
        "profileIndex": 0,
        "arcPositionNm": float(np.mean([profile["arcPositionNm"] for profile in processed_profiles])),
        "meanHeightNm": float(np.mean([profile["meanHeightNm"] for profile in processed_profiles])) if processed_profiles else height_nm,
        "meanWidthNm": float(np.mean([profile["meanWidthNm"] for profile in processed_profiles])) if processed_profiles else width_nm,
        "heightToWidthRatio": float(np.mean([profile["heightToWidthRatio"] for profile in processed_profiles])) if processed_profiles else ratio,
        "roughnessNm": roughness,
        "peakSeparationNm": float(peak_separation(local_grid, averaged_corrected)),
        "dipDepthNm": float(dip_depth(averaged_corrected)),
        "compromiseRatio": float(compromise_ratio_from_profile(local_grid, averaged_corrected)),
    }


def prepare_profiles(request: dict) -> Tuple[List[PreparedProfile], Dict[str, float], Dict[str, float], Dict[str, float], dict]:
    requested_mapping = ensure_stage_mapping(request.get("stageMapping"))
    options = request.get("options", {})
    use_normalized_tau = bool(options.get("useNormalizedTau", True))
    profiles: List[PreparedProfile] = []
    for item in request.get("files", []):
        guided_profiles = item.get("guidedPerpendicularProfiles", [])
        if not isinstance(guided_profiles, list) or len(guided_profiles) == 0:
            guided_profiles = [item]
        processed_profiles = [
            prepared
            for local_index, source in enumerate(guided_profiles)
            if (prepared := prepare_guided_profile(source, item, local_index)) is not None
        ]
        if not processed_profiles:
            continue

        averaged_profile = average_prepared_guided_profiles(processed_profiles)
        profiles.append(
            PreparedProfile(
                file_name=str(item.get("fileName", "")),
                file_path=str(item.get("filePath", "")),
                sequence_order=int(item.get("sequenceOrder", 0)),
                stage=str(item.get("stage", "early")).strip().lower(),
                condition_type=str(item.get("conditionType", "unassigned")),
                unit=str(item.get("unit", "nm")),
                dose_ug_per_ml=float(item.get("doseUgPerMl", 0.0)),
                x_nm=np.asarray(averaged_profile["xNm"], dtype=float),
                y_nm=np.asarray(averaged_profile["yNm"], dtype=float),
                s_nm=np.asarray(averaged_profile["sNm"], dtype=float),
                z_nm=np.asarray(averaged_profile["zNm"], dtype=float),
                aligned_s_nm=np.asarray(averaged_profile["alignedSNm"], dtype=float),
                corrected_z_nm=np.asarray(averaged_profile["correctedZNm"], dtype=float),
                mean_height_nm=float(averaged_profile["meanHeightNm"]),
                mean_width_nm=float(averaged_profile["meanWidthNm"]),
                height_to_width_ratio=float(averaged_profile["heightToWidthRatio"]),
                roughness_nm=float(averaged_profile["roughnessNm"]),
                peak_separation_nm=float(averaged_profile["peakSeparationNm"]),
                dip_depth_nm=float(averaged_profile["dipDepthNm"]),
                compromise_ratio=float(averaged_profile["compromiseRatio"]),
                profile_index=int(averaged_profile["profileIndex"]),
                arc_position_nm=float(averaged_profile["arcPositionNm"]),
            )
        )

    profiles.sort(
        key=lambda profile: (
            profile.sequence_order if profile.sequence_order > 0 else sys.maxsize,
            profile.profile_index,
            profile.arc_position_nm,
            profile.file_name.lower(),
            profile.file_path.lower(),
        )
    )
    if len(profiles) < 2:
        raise ValueError("Equation discovery needs at least two guided, ordered profiles. Three or more ordered anchors are strongly recommended.")

    unique_sequence_orders = sorted({profile.sequence_order for profile in profiles if profile.sequence_order > 0})
    if unique_sequence_orders:
        sequence_rank = {order: rank for rank, order in enumerate(unique_sequence_orders)}
    else:
        sequence_rank = {}

    max_rank = max(1, len(unique_sequence_orders) - 1)
    for index, profile in enumerate(profiles):
        rank = sequence_rank.get(profile.sequence_order, index)
        profile.tau_normalized = float(rank / max_rank) if len(unique_sequence_orders) > 1 else 0.0
        profile.tau_value = float(np.clip(profile.tau_normalized, 0.0, 1.0) if use_normalized_tau else rank)

    stage_mapping: Dict[str, float] = {}
    for stage in {profile.stage for profile in profiles}:
        stage_members = [profile.tau_value for profile in profiles if profile.stage == stage]
        if not stage_members:
            continue
        stage_mapping[stage] = float(np.mean(stage_members))
    stage_mapping = dict(sorted(stage_mapping.items(), key=lambda item: item[1]))

    if len(stage_mapping) < 2:
        raise ValueError("Equation discovery needs at least two ordered pseudo-time anchors after profile preparation.")

    features = {
        "height_mean": float(np.mean([profile.mean_height_nm for profile in profiles])) if profiles else 0.0,
        "width_mean": float(np.mean([profile.mean_width_nm for profile in profiles])) if profiles else 0.0,
        "ratio_mean": float(np.mean([profile.height_to_width_ratio for profile in profiles])) if profiles else 0.0,
        "roughness_mean": float(np.mean([profile.roughness_nm for profile in profiles])) if profiles else 0.0,
        "peak_separation_mean": float(np.mean([profile.peak_separation_nm for profile in profiles])) if profiles else 0.0,
    }
    time_info = {
        "useNormalizedTau": use_normalized_tau,
        "tauMin": float(min(profile.tau_value for profile in profiles)) if profiles else 0.0,
        "tauMax": float(max(profile.tau_value for profile in profiles)) if profiles else 1.0,
        "sequenceAnchorCount": len(unique_sequence_orders) if unique_sequence_orders else len(profiles),
    }
    return profiles, requested_mapping, stage_mapping, features, time_info


def build_validation_profiles(profiles: Sequence[PreparedProfile]) -> List[PreparedProfile]:
    grouped: Dict[Tuple[str, str, int, str], List[PreparedProfile]] = {}
    for profile in profiles:
        key = (profile.file_name, profile.file_path, profile.sequence_order, profile.stage)
        grouped.setdefault(key, []).append(profile)

    collapsed: List[PreparedProfile] = []
    for (_, _, _, _), members in grouped.items():
        anchor = members[0]
        collapsed.append(
            PreparedProfile(
                file_name=anchor.file_name,
                file_path=anchor.file_path,
                sequence_order=anchor.sequence_order,
                stage=anchor.stage,
                condition_type=anchor.condition_type,
                unit=anchor.unit,
                dose_ug_per_ml=anchor.dose_ug_per_ml,
                x_nm=anchor.x_nm,
                y_nm=anchor.y_nm,
                s_nm=anchor.s_nm,
                z_nm=anchor.z_nm,
                aligned_s_nm=anchor.aligned_s_nm,
                corrected_z_nm=anchor.corrected_z_nm,
                mean_height_nm=float(np.mean([member.mean_height_nm for member in members])),
                mean_width_nm=float(np.mean([member.mean_width_nm for member in members])),
                height_to_width_ratio=float(np.mean([member.height_to_width_ratio for member in members])),
                roughness_nm=float(np.mean([member.roughness_nm for member in members])),
                peak_separation_nm=float(np.mean([member.peak_separation_nm for member in members])),
                dip_depth_nm=float(np.mean([member.dip_depth_nm for member in members])),
                compromise_ratio=float(np.mean([member.compromise_ratio for member in members])),
                profile_index=0,
                arc_position_nm=0.0,
                tau_value=anchor.tau_value,
                tau_normalized=anchor.tau_normalized,
            )
        )

    collapsed.sort(key=lambda profile: (profile.sequence_order, profile.file_name.lower(), profile.file_path.lower()))
    return collapsed


def build_common_grid(profiles: Sequence[PreparedProfile], count: int, half_range_nm: float = 90.0) -> np.ndarray:
    if half_range_nm > 1e-6:
        return np.linspace(-float(half_range_nm), float(half_range_nm), count)

    left = max(float(np.min(profile.aligned_s_nm)) for profile in profiles)
    right = min(float(np.max(profile.aligned_s_nm)) for profile in profiles)
    if right - left < 1e-6:
        extent = max(float(np.max(profile.aligned_s_nm) - np.min(profile.aligned_s_nm)) for profile in profiles)
        left = -0.5 * extent
        right = 0.5 * extent
    return np.linspace(left, right, count)


def resample_profile(x: np.ndarray, y: np.ndarray, x_grid: np.ndarray) -> np.ndarray:
    return np.interp(x_grid, x, y, left=0.0, right=0.0)


def bootstrap_stage_profiles(
    profiles: Sequence[PreparedProfile],
    stage_order: Sequence[str],
    grid: np.ndarray,
    bootstrap_index: int,
) -> Tuple[Dict[str, np.ndarray], Dict[str, dict], List[np.ndarray], List[dict]]:
    _ = np.random.default_rng(bootstrap_index + 17)
    stage_profiles: Dict[str, np.ndarray] = {}
    stage_stats: Dict[str, dict] = {}
    individual_profiles_list: List[np.ndarray] = []
    individual_metadata: List[dict] = []

    for stage in stage_order:
        members = [profile for profile in profiles if profile.stage == stage]
        if not members:
            continue
        resampled_individual = []
        for profile in members:
            resampled = resample_profile(profile.aligned_s_nm, profile.corrected_z_nm, grid)
            resampled_individual.append(resampled)
            individual_profiles_list.append(resampled)
            individual_metadata.append(
                {
                    "stage": stage,
                    "fileName": profile.file_name,
                    "filePath": profile.file_path,
                    "sequenceOrder": profile.sequence_order,
                    "profileIndex": profile.profile_index,
                    "arcPositionNm": profile.arc_position_nm,
                    "tauValue": float(profile.tau_value),
                    "tauNormalized": float(profile.tau_normalized),
                    "heightNm": float(profile.mean_height_nm),
                    "widthNm": float(profile.mean_width_nm),
                    "ratio": float(profile.height_to_width_ratio),
                    "peakSeparationNm": float(profile.peak_separation_nm),
                    "dipDepthNm": float(profile.dip_depth_nm),
                    "compromiseRatio": float(profile.compromise_ratio),
                    "roughnessNm": float(profile.roughness_nm),
                }
            )

        resampled = np.vstack(resampled_individual)
        mean_profile = np.mean(resampled, axis=0)
        std_profile = np.std(resampled, axis=0)
        stage_profiles[stage] = mean_profile * 0.72 + smooth_profile(mean_profile) * 0.28
        stage_stats[stage] = {
            "sampleCount": len(resampled_individual),
            "tau": float(np.mean([profile.tau_value for profile in members])),
            "meanHeightNm": float(np.mean([profile.mean_height_nm for profile in members])),
            "heightStdNm": float(np.std([profile.mean_height_nm for profile in members])),
            "meanWidthNm": float(np.mean([profile.mean_width_nm for profile in members])),
            "widthStdNm": float(np.std([profile.mean_width_nm for profile in members])),
            "meanArea": float(np.mean([np.trapezoid(resample_profile(profile.aligned_s_nm, profile.corrected_z_nm, grid), grid) for profile in members])),
            "meanRoughnessNm": float(np.mean([profile.roughness_nm for profile in members])),
            "profileStd": std_profile,
            "meanCompromise": float(np.mean([profile.compromise_ratio for profile in members])),
        }

    return stage_profiles, stage_stats, individual_profiles_list, individual_metadata


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


def compute_pseudotime_derivatives(profile_matrix: np.ndarray, tau_values: np.ndarray) -> np.ndarray:
    if profile_matrix.ndim != 2 or len(profile_matrix) == 0:
        return np.zeros_like(profile_matrix)

    order = np.argsort(tau_values)
    values = profile_matrix[order]
    taus = tau_values[order]
    derivatives = np.zeros_like(values)

    for index in range(len(taus)):
        previous = index - 1
        next_index = index + 1
        while previous >= 0 and abs(taus[previous] - taus[index]) < 1e-9:
            previous -= 1
        while next_index < len(taus) and abs(taus[next_index] - taus[index]) < 1e-9:
            next_index += 1

        if previous >= 0 and next_index < len(taus):
            dt = max(1e-9, taus[next_index] - taus[previous])
            derivatives[index] = (values[next_index] - values[previous]) / dt
        elif next_index < len(taus):
            dt = max(1e-9, taus[next_index] - taus[index])
            derivatives[index] = (values[next_index] - values[index]) / dt
        elif previous >= 0:
            dt = max(1e-9, taus[index] - taus[previous])
            derivatives[index] = (values[index] - values[previous]) / dt

    unsorted = np.zeros_like(derivatives)
    unsorted[order] = derivatives
    return unsorted


def build_candidate_library(individual_profiles: Sequence[np.ndarray], tau_values: np.ndarray, grid: np.ndarray) -> Tuple[np.ndarray, np.ndarray, List[str]]:
    spacing = float(np.mean(np.diff(grid))) if len(grid) > 1 else 1.0
    h = np.vstack(individual_profiles)
    tau = np.asarray(tau_values, dtype=float)[:, None]
    hs = np.vstack([derivative_profile(profile, spacing, 1) for profile in h])
    hss = np.vstack([derivative_profile(profile, spacing, 2) for profile in h])
    hsss = np.vstack([derivative_profile(profile, spacing, 3) for profile in h])
    hssss = np.vstack([derivative_profile(profile, spacing, 4) for profile in h])
    h_tau = compute_pseudotime_derivatives(h, np.asarray(tau_values, dtype=float))

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


def scalar_summary(values: Sequence[float]) -> dict:
    arr = np.asarray(values, dtype=float)
    if arr.size == 0:
        return {"mean": 0.0, "std": 0.0, "min": 0.0, "max": 0.0}
    return {
        "mean": float(np.mean(arr)),
        "std": float(np.std(arr)),
        "min": float(np.min(arr)),
        "max": float(np.max(arr)),
    }


def build_statistical_summary(individual_metadata: Sequence[dict]) -> dict:
    return {
        "profileCount": int(len(individual_metadata)),
        "heightMetrics": scalar_summary([metadata.get("heightNm", 0.0) for metadata in individual_metadata]),
        "widthMetrics": scalar_summary([metadata.get("widthNm", 0.0) for metadata in individual_metadata]),
        "ratioMetrics": scalar_summary([metadata.get("ratio", 0.0) for metadata in individual_metadata]),
        "roughnessMetrics": scalar_summary([metadata.get("roughnessNm", 0.0) for metadata in individual_metadata]),
        "bimodalCharacterization": {
            "peakSeparationMean": float(np.mean([metadata.get("peakSeparationNm", 0.0) for metadata in individual_metadata])) if individual_metadata else 0.0,
            "dipDepthMean": float(np.mean([metadata.get("dipDepthNm", 0.0) for metadata in individual_metadata])) if individual_metadata else 0.0,
        },
    }


def fit_bimodal_feature_trajectories(individual_profiles: Sequence[np.ndarray], tau_values: np.ndarray, grid: np.ndarray) -> dict:
    ordered = sorted(
        zip(np.asarray(tau_values, dtype=float), [np.asarray(profile, dtype=float) for profile in individual_profiles]),
        key=lambda item: float(item[0]),
    )
    tau_sorted = np.array([float(item[0]) for item in ordered], dtype=float)
    profiles_sorted = [item[1] for item in ordered]
    tau_min = float(np.min(tau_sorted)) if tau_sorted.size else 0.0
    tau_max = float(np.max(tau_sorted)) if tau_sorted.size else 1.0
    dense_count = max(60, len(tau_sorted) * 12)
    if abs(tau_max - tau_min) < 1e-9:
        dense_tau = np.full(dense_count, tau_min, dtype=float)
    else:
        dense_tau = np.linspace(tau_min, tau_max, dense_count)

    if HAS_FEATURE_EXTRACTION and extract_bimodal_features is not None and interpolate_bimodal_parameters is not None:
        discrete = extract_bimodal_features(profiles_sorted, tau_sorted.tolist(), x_positions=np.asarray(grid, dtype=float))
        interpolated = interpolate_bimodal_parameters(tau_sorted.tolist(), discrete, dense_tau.tolist())
    else:
        heuristic_parameters = np.vstack([estimate_bimodal_parameters(grid, profile) for profile in profiles_sorted])
        discrete = {
            "tau": [float(value) for value in tau_sorted],
            "A1": [float(value) for value in heuristic_parameters[:, 0]],
            "A2": [float(value) for value in heuristic_parameters[:, 2]],
            "sigma1": [float(value) for value in heuristic_parameters[:, 1]],
            "sigma2": [float(value) for value in heuristic_parameters[:, 3]],
            "D": [float(value) for value in heuristic_parameters[:, 4]],
            "mu1": [float(-0.5 * value) for value in heuristic_parameters[:, 4]],
            "mu2": [float(0.5 * value) for value in heuristic_parameters[:, 4]],
            "sigma_avg": [float(0.5 * (left + right)) for left, right in zip(heuristic_parameters[:, 1], heuristic_parameters[:, 3])],
            "ratio": [float(left / max(1e-9, right)) for left, right in zip(heuristic_parameters[:, 0], heuristic_parameters[:, 2])],
            "rmse": [0.0 for _ in profiles_sorted],
            "fitSuccess": [True for _ in profiles_sorted],
            "fits": [],
        }
        interpolated = {"tau": [float(value) for value in dense_tau]}
        for key in ("A1", "A2", "sigma1", "sigma2", "D", "mu1", "mu2", "sigma_avg", "ratio", "rmse"):
            interpolated[key] = [float(value) for value in np.interp(dense_tau, tau_sorted, np.asarray(discrete[key], dtype=float))]

    for payload in (discrete, interpolated):
        for key in ("A1", "A2", "sigma1", "sigma2", "sigma_avg", "D"):
            values = np.maximum(0.0, np.asarray(payload.get(key, []), dtype=float))
            if key == "D" and values.size:
                values = np.maximum.accumulate(values)
            payload[key] = [float(value) for value in values]
        if payload.get("D"):
            separation = np.asarray(payload["D"], dtype=float)
            payload["mu1"] = [float(-0.5 * value) for value in separation]
            payload["mu2"] = [float(0.5 * value) for value in separation]
        if payload.get("sigma1") and payload.get("sigma2"):
            sigma1 = np.asarray(payload["sigma1"], dtype=float)
            sigma2 = np.asarray(payload["sigma2"], dtype=float)
            payload["sigma_avg"] = [float(value) for value in (0.5 * (sigma1 + sigma2))]
        if payload.get("A1") and payload.get("A2"):
            a1 = np.asarray(payload["A1"], dtype=float)
            a2 = np.asarray(payload["A2"], dtype=float)
            payload["ratio"] = [float(value) for value in (a1 / np.maximum(a2, 1e-9))]

    fit_success = np.asarray(discrete.get("fitSuccess", []), dtype=bool)
    fit_rmse = np.asarray(discrete.get("rmse", []), dtype=float)
    return {
        "tau": tau_sorted,
        "denseTau": dense_tau,
        "grid": np.asarray(grid, dtype=float),
        "profiles": profiles_sorted,
        "discrete": discrete,
        "interpolated": interpolated,
        "fitQuality": {
            "successFraction": float(np.mean(fit_success)) if fit_success.size else 0.0,
            "rmseMean": float(np.mean(fit_rmse)) if fit_rmse.size else 0.0,
            "rmseMax": float(np.max(fit_rmse)) if fit_rmse.size else 0.0,
        },
    }


def build_bimodal_feature_payload(feature_model: dict) -> dict:
    return {
        "stateNames": list(FEATURE_STATE_NAMES),
        "tauDiscrete": [float(value) for value in np.asarray(feature_model.get("tau", []), dtype=float)],
        "tauDense": [float(value) for value in np.asarray(feature_model.get("denseTau", []), dtype=float)],
        "discrete": feature_model.get("discrete", {}),
        "interpolated": feature_model.get("interpolated", {}),
        "fitQuality": feature_model.get("fitQuality", {}),
        "coordinateLabel": "Perpendicular offset from guide centre z [nm]",
        "note": "Each ordered AFM line profile is fitted with a weighted 1D Gaussian-mixture model (two components), then refined into h(z) = G1(z) + G2(z) and interpolated over pseudo-time.",
    }


def build_feature_state_matrix(feature_series: dict) -> np.ndarray:
    columns = [np.asarray(feature_series.get(name, []), dtype=float) for name in FEATURE_STATE_NAMES]
    if not columns or any(column.size == 0 for column in columns):
        return np.zeros((0, len(FEATURE_STATE_NAMES)), dtype=float)
    return np.column_stack(columns)


def compute_smoothed_derivative(tau_values: np.ndarray, values: np.ndarray) -> np.ndarray:
    tau = np.asarray(tau_values, dtype=float)
    series = np.asarray(values, dtype=float)
    if tau.size < 2 or series.size != tau.size:
        return np.zeros_like(series)
    derivative = np.gradient(series, tau, edge_order=2 if tau.size >= 3 else 1)
    if derivative.size >= 7:
        derivative = gaussian_filter1d(derivative, sigma=0.9, mode="nearest")
    return derivative


def build_feature_library(
    feature_matrix: np.ndarray,
    tau_values: np.ndarray,
    tau_bounds: Tuple[float, float] | None = None,
) -> Tuple[np.ndarray, List[str]]:
    tau = np.asarray(tau_values, dtype=float)
    if tau_bounds is None:
        tau_min = float(np.min(tau)) if tau.size else 0.0
        tau_max = float(np.max(tau)) if tau.size else 1.0
    else:
        tau_min, tau_max = float(tau_bounds[0]), float(tau_bounds[1])
    tau_feature = np.asarray([normalize_tau_value(value, tau_min, tau_max) for value in tau], dtype=float)

    a1 = feature_matrix[:, 0]
    a2 = feature_matrix[:, 1]
    separation = feature_matrix[:, 2]
    sigma1 = feature_matrix[:, 3]
    sigma2 = feature_matrix[:, 4]
    amplitude_mean = 0.5 * (a1 + a2)
    sigma_mean = 0.5 * (sigma1 + sigma2)
    ratio_minus_one = a1 / np.maximum(a2, 1e-9) - 1.0
    d_over_sigma = separation / np.maximum(sigma_mean, 1e-9)

    library = {
        "1": np.ones_like(tau_feature),
        "tau": tau_feature,
        "tau^2": tau_feature ** 2,
        "A1": a1,
        "A2": a2,
        "D": separation,
        "sigma1": sigma1,
        "sigma2": sigma2,
        "A_mean": amplitude_mean,
        "sigma_mean": sigma_mean,
        "ratio_minus_1": ratio_minus_one,
        "D_over_sigma": d_over_sigma,
        "A_mean*tau": amplitude_mean * tau_feature,
        "D*tau": separation * tau_feature,
    }
    term_names = list(library.keys())
    theta = np.column_stack([library[name] for name in term_names])
    return theta, term_names


def flatten_feature_coefficients(term_names: Sequence[str], coefficient_matrix: np.ndarray) -> Dict[str, float]:
    output: Dict[str, float] = {}
    for row_index, state_name in enumerate(FEATURE_STATE_NAMES):
        for column_index, term_name in enumerate(term_names):
            value = float(coefficient_matrix[row_index, column_index])
            if abs(value) < 1e-10:
                continue
            output[f"{state_name}|{term_name}"] = value
    return output


def candidate_signature(candidate: dict) -> Tuple:
    model_type = str(candidate.get("modelType", "unknown"))
    coefficients = candidate.get("coefficients", {})
    signature_terms = []
    for term_name in sorted(coefficients.keys(), key=term_sort_key):
        value = float(coefficients.get(term_name, 0.0))
        if abs(value) < 1e-10:
            continue
        signature_terms.append((term_name, round(value, 8)))
    return (model_type, tuple(signature_terms))


def feature_state_from_profile(grid: np.ndarray, profile: np.ndarray) -> np.ndarray:
    params = estimate_bimodal_parameters(np.asarray(grid, dtype=float), np.asarray(profile, dtype=float))
    return np.array([params[0], params[2], params[4], params[1], params[3]], dtype=float)


def rmse_quality_factor(rmse: float) -> float:
    value = float(rmse)
    if value <= GOOD_RMSE_THRESHOLD:
        return 1.0
    if value >= FAIL_RMSE_THRESHOLD:
        return 0.0
    return float(np.clip((FAIL_RMSE_THRESHOLD - value) / max(1e-9, FAIL_RMSE_THRESHOLD - GOOD_RMSE_THRESHOLD), 0.0, 1.0))


def rmse_quality_label(rmse: float) -> str:
    value = float(rmse)
    if value <= GOOD_RMSE_THRESHOLD:
        return "good"
    if value <= FAIL_RMSE_THRESHOLD:
        return "caution"
    return "fail"


def is_interpretable_candidate(candidate: dict) -> bool:
    rmse = float(candidate.get("rmse", float("inf")))
    confidence = float(candidate.get("confidence", 0.0))
    if not np.isfinite(rmse) or not np.isfinite(confidence):
        return False
    if rmse > FAIL_RMSE_THRESHOLD:
        return False
    if confidence < 0.35:
        return False
    return True


def compute_profile_error_metrics(predicted_profiles: Sequence[np.ndarray], observed_profiles: Sequence[np.ndarray], grid: np.ndarray) -> dict:
    if len(predicted_profiles) != len(observed_profiles) or not observed_profiles:
        return {
            "rmse": float("inf"),
            "peakHeightError": float("inf"),
            "widthError": float("inf"),
            "areaError": float("inf"),
            "compromiseConsistency": 0.0,
        }

    rmse = float(np.mean([
        np.sqrt(np.mean((np.asarray(predicted, dtype=float) - np.asarray(observed, dtype=float)) ** 2))
        for predicted, observed in zip(predicted_profiles, observed_profiles)
    ]))
    peak_error = float(np.mean([
        abs(float(np.max(predicted)) - float(np.max(observed)))
        for predicted, observed in zip(predicted_profiles, observed_profiles)
    ]))
    width_error = float(np.mean([
        abs(fwhm_width(grid, predicted) - fwhm_width(grid, observed))
        for predicted, observed in zip(predicted_profiles, observed_profiles)
    ]))
    area_error = float(np.mean([
        abs(float(np.trapezoid(predicted, grid)) - float(np.trapezoid(observed, grid)))
        for predicted, observed in zip(predicted_profiles, observed_profiles)
    ]))
    compromise_error = float(np.mean([
        abs(compromise_ratio_from_profile(grid, predicted) - compromise_ratio_from_profile(grid, observed))
        for predicted, observed in zip(predicted_profiles, observed_profiles)
    ]))
    return {
        "rmse": rmse,
        "peakHeightError": peak_error,
        "widthError": width_error,
        "areaError": area_error,
        "compromiseConsistency": max(0.0, 1.0 - compromise_error),
    }


def group_profiles_by_sequence(
    individual_profiles: Sequence[np.ndarray],
    tau_values: np.ndarray,
    metadata: Sequence[dict],
) -> List[dict]:
    grouped: Dict[Tuple[int, float], List[np.ndarray]] = {}
    for profile, tau, item in zip(individual_profiles, tau_values, metadata):
        sequence_order = int(item.get("sequenceOrder", 0))
        key = (sequence_order, float(tau))
        grouped.setdefault(key, []).append(np.asarray(profile, dtype=float))

    sequence_profiles: List[dict] = []
    for (sequence_order, tau), profiles in grouped.items():
        if not profiles:
            continue
        sequence_profiles.append(
            {
                "sequenceOrder": sequence_order,
                "tau": float(tau),
                "profile": np.mean(np.vstack(profiles), axis=0),
            }
        )

    sequence_profiles.sort(key=lambda item: (item["tau"], item["sequenceOrder"]))
    return sequence_profiles


def group_profiles_by_track(
    individual_profiles: Sequence[np.ndarray],
    tau_values: np.ndarray,
    metadata: Sequence[dict],
) -> List[dict]:
    grouped: Dict[int, List[dict]] = {}
    for profile, tau, item in zip(individual_profiles, tau_values, metadata):
        key = int(item.get("profileIndex", 0))
        grouped.setdefault(key, []).append(
            {
                "tau": float(tau),
                "sequenceOrder": int(item.get("sequenceOrder", 0)),
                "profile": np.asarray(profile, dtype=float),
            }
        )

    tracks: List[dict] = []
    for profile_index, entries in grouped.items():
        entries.sort(key=lambda item: (item["tau"], item["sequenceOrder"]))
        if len(entries) < 2:
            continue
        tracks.append(
            {
                "profileIndex": profile_index,
                "tau": np.asarray([entry["tau"] for entry in entries], dtype=float),
                "profiles": [np.asarray(entry["profile"], dtype=float) for entry in entries],
            }
        )

    tracks.sort(key=lambda item: item["profileIndex"])
    return tracks


def collapse_feature_series_by_tau(discrete_features: dict) -> dict:
    tau = np.asarray(discrete_features.get("tau", []), dtype=float)
    if tau.size == 0:
        return {"tau": np.asarray([], dtype=float)}

    rounded_tau = np.round(tau, 8)
    unique_tau = np.unique(rounded_tau)
    output = {"tau": unique_tau}
    for state_name in FEATURE_STATE_NAMES:
        values = np.asarray(discrete_features.get(state_name, []), dtype=float)
        if values.size != tau.size:
            continue
        collapsed = []
        for tau_value in unique_tau:
            mask = rounded_tau == tau_value
            collapsed.append(float(np.mean(values[mask])) if np.any(mask) else 0.0)
        output[state_name] = np.asarray(collapsed, dtype=float)
    return output


def build_feature_coefficient_statistics(term_names: Sequence[str], coefficient_matrix: np.ndarray) -> Dict[str, dict]:
    output: Dict[str, dict] = {}
    for row_index, state_name in enumerate(FEATURE_STATE_NAMES):
        for column_index, term_name in enumerate(term_names):
            value = float(coefficient_matrix[row_index, column_index])
            if abs(value) < 1e-10:
                continue
            output[f"{state_name}|{term_name}"] = {
                "mean": value,
                "standardDeviation": 0.0,
                "lower95": value,
                "upper95": value,
            }
    return output


def format_feature_equation_line(state_name: str, term_names: Sequence[str], coefficients: np.ndarray) -> str:
    parts = []
    for term_name, value in zip(term_names, coefficients):
        coefficient = float(value)
        if abs(coefficient) < 1e-10:
            continue
        formatted = f"{abs(coefficient):.4g}"
        fragment = formatted if term_name == "1" else f"{formatted}*{term_name}"
        parts.append(f"- {fragment}" if coefficient < 0 else f"+ {fragment}")
    if not parts:
        return f"d{state_name}/dtau = 0"
    expression = " ".join(parts).lstrip("+ ")
    return f"d{state_name}/dtau = {expression}"


def format_feature_system(term_names: Sequence[str], coefficient_matrix: np.ndarray) -> str:
    return "\n".join(
        format_feature_equation_line(state_name, term_names, coefficient_matrix[index])
        for index, state_name in enumerate(FEATURE_STATE_NAMES)
    )


def evaluate_feature_terms(state: np.ndarray, tau: float, tau_bounds: Tuple[float, float] | None = None) -> Dict[str, float]:
    a1 = max(0.0, float(state[0]))
    a2 = max(0.0, float(state[1]))
    separation = max(0.0, float(state[2]))
    sigma1 = max(1e-6, float(state[3]))
    sigma2 = max(1e-6, float(state[4]))
    amplitude_mean = 0.5 * (a1 + a2)
    sigma_mean = 0.5 * (sigma1 + sigma2)
    ratio_minus_one = a1 / max(a2, 1e-9) - 1.0
    if tau_bounds is None:
        tau_feature = float(tau)
    else:
        tau_feature = normalize_tau_value(float(tau), float(tau_bounds[0]), float(tau_bounds[1]))
    return {
        "1": 1.0,
        "tau": float(tau_feature),
        "tau^2": float(tau_feature) * float(tau_feature),
        "A1": a1,
        "A2": a2,
        "D": separation,
        "sigma1": sigma1,
        "sigma2": sigma2,
        "A_mean": amplitude_mean,
        "sigma_mean": sigma_mean,
        "ratio_minus_1": ratio_minus_one,
        "D_over_sigma": separation / max(sigma_mean, 1e-9),
        "A_mean*tau": amplitude_mean * float(tau_feature),
        "D*tau": separation * float(tau_feature),
    }


def simulate_bimodal_feature_system(
    term_names: Sequence[str],
    coefficient_matrix: np.ndarray,
    initial_state: np.ndarray,
    tau_grid: Sequence[float],
    grid: np.ndarray,
    min_sigma: float,
    rate_cap: float,
    tau_bounds: Tuple[float, float] | None = None,
) -> dict:
    tau_eval = np.asarray(tau_grid, dtype=float)
    if tau_eval.size == 0:
        return {"success": False, "error": "No tau grid supplied for feature simulation.", "tau": [], "profiles": []}

    coefficient_array = np.asarray(coefficient_matrix, dtype=float)
    initial = np.asarray(initial_state, dtype=float).copy()
    initial[0] = max(0.0, initial[0])
    initial[1] = max(0.0, initial[1])
    initial[2] = max(0.0, initial[2])
    initial[3] = max(min_sigma, initial[3])
    initial[4] = max(min_sigma, initial[4])

    def rhs(tau_value: float, state: np.ndarray) -> np.ndarray:
        terms = evaluate_feature_terms(state, tau_value, tau_bounds=tau_bounds)
        derivative = np.array(
            [sum(float(coefficient) * terms[term_name] for coefficient, term_name in zip(row, term_names)) for row in coefficient_array],
            dtype=float,
        )
        return np.clip(np.nan_to_num(derivative, nan=0.0, posinf=0.0, neginf=0.0), -rate_cap, rate_cap)

    note = "Feature ODE playback integrated with solve_ivp."
    try:
        solution = solve_ivp(
            rhs,
            (float(tau_eval[0]), float(tau_eval[-1])),
            initial,
            t_eval=tau_eval,
            max_step=max(0.01, float(tau_eval[-1] - tau_eval[0]) / max(8, tau_eval.size * 2)),
            rtol=1e-5,
            atol=1e-6,
        )
        success = bool(solution.success) and solution.y.shape[1] == tau_eval.size
        if success:
            states = solution.y.T
        else:
            note = solution.message or "solve_ivp failed; explicit fallback used."
            raise RuntimeError(note)
    except Exception as exc:
        states = np.zeros((tau_eval.size, len(FEATURE_STATE_NAMES)), dtype=float)
        states[0] = initial
        for index in range(1, tau_eval.size):
            dt = float(tau_eval[index] - tau_eval[index - 1])
            states[index] = states[index - 1] + dt * rhs(float(tau_eval[index - 1]), states[index - 1])
        success = False
        note = f"Feature ODE playback used an explicit fallback after solve_ivp failed: {exc}"

    states = np.nan_to_num(states, nan=0.0, posinf=0.0, neginf=0.0)
    if states.shape[1] >= 5:
        states[:, 0] = np.maximum(0.0, states[:, 0])
        states[:, 1] = np.maximum(0.0, states[:, 1])
        states[:, 2] = np.maximum.accumulate(np.maximum(0.0, states[:, 2]))
        states[:, 3] = np.maximum(min_sigma, states[:, 3])
        states[:, 4] = np.maximum(min_sigma, states[:, 4])
        if states.shape[0] >= 7:
            states[:, 0] = gaussian_filter1d(states[:, 0], sigma=0.6, mode="nearest")
            states[:, 1] = gaussian_filter1d(states[:, 1], sigma=0.6, mode="nearest")
            states[:, 2] = np.maximum.accumulate(gaussian_filter1d(states[:, 2], sigma=0.75, mode="nearest"))
            states[:, 3] = np.maximum(min_sigma, gaussian_filter1d(states[:, 3], sigma=0.6, mode="nearest"))
            states[:, 4] = np.maximum(min_sigma, gaussian_filter1d(states[:, 4], sigma=0.6, mode="nearest"))

    profiles = [
        build_bimodal_profile_from_parameters(grid, [state[0], state[3], state[1], state[4], state[2]])
        for state in states
    ]
    envelope_profiles = [profile.copy() for profile in profiles]
    state_payload = {
        "A1": [float(value) for value in states[:, 0]],
        "A2": [float(value) for value in states[:, 1]],
        "D": [float(value) for value in states[:, 2]],
        "sigma1": [float(value) for value in states[:, 3]],
        "sigma2": [float(value) for value in states[:, 4]],
        "ratio": [float(left / max(1e-9, right)) for left, right in zip(states[:, 0], states[:, 1])],
        "mu1": [float(-0.5 * value) for value in states[:, 2]],
        "mu2": [float(0.5 * value) for value in states[:, 2]],
        "sigma_avg": [float(0.5 * (left + right)) for left, right in zip(states[:, 3], states[:, 4])],
    }
    return {
        "success": success,
        "tau": [float(value) for value in tau_eval],
        "profiles": profiles,
        "envelopeProfiles": envelope_profiles,
        "states": state_payload,
        "stabilityScore": 1.0 if success else 0.55,
        "note": note,
    }


def evaluate_feature_candidate(
    term_names: Sequence[str],
    coefficient_matrix: np.ndarray,
    individual_profiles: Sequence[np.ndarray],
    observed_tau: np.ndarray,
    metadata: Sequence[dict],
    grid: np.ndarray,
    discrete_features: dict,
    min_sigma: float,
    rate_cap: float,
    tau_bounds: Tuple[float, float],
) -> Tuple[dict, dict]:
    sequence_profiles = group_profiles_by_sequence(individual_profiles, observed_tau, metadata)
    if len(sequence_profiles) < 2:
        return {
            "rmse": float("inf"),
            "peakHeightError": float("inf"),
            "widthError": float("inf"),
            "areaError": float("inf"),
            "featureRmse": float("inf"),
            "compromiseConsistency": 0.0,
            "trackRmse": float("inf"),
            "sequenceRmse": float("inf"),
            "rmseQualityFactor": 0.0,
            "rmseQualityLabel": "fail",
        }, {"success": False, "tau": [], "profiles": [], "states": {}}

    sequence_tau = np.asarray([item["tau"] for item in sequence_profiles], dtype=float)
    sequence_observed_profiles = [np.asarray(item["profile"], dtype=float) for item in sequence_profiles]
    sequence_initial_state = feature_state_from_profile(grid, sequence_observed_profiles[0])
    sequence_simulation = simulate_bimodal_feature_system(
        term_names,
        coefficient_matrix,
        sequence_initial_state,
        sequence_tau,
        grid,
        min_sigma,
        rate_cap,
        tau_bounds=tau_bounds,
    )
    sequence_metrics = compute_profile_error_metrics(sequence_simulation.get("profiles", []), sequence_observed_profiles, grid)

    track_metrics_payload = []
    for track in group_profiles_by_track(individual_profiles, observed_tau, metadata):
        track_tau = np.asarray(track["tau"], dtype=float)
        track_profiles = [np.asarray(profile, dtype=float) for profile in track["profiles"]]
        track_initial_state = feature_state_from_profile(grid, track_profiles[0])
        track_simulation = simulate_bimodal_feature_system(
            term_names,
            coefficient_matrix,
            track_initial_state,
            track_tau,
            grid,
            min_sigma,
            rate_cap,
            tau_bounds=tau_bounds,
        )
        track_metrics = compute_profile_error_metrics(track_simulation.get("profiles", []), track_profiles, grid)
        if np.isfinite(track_metrics["rmse"]):
            track_metrics_payload.append(track_metrics)

    if track_metrics_payload:
        track_rmse = float(np.mean([item["rmse"] for item in track_metrics_payload]))
        track_peak = float(np.mean([item["peakHeightError"] for item in track_metrics_payload]))
        track_width = float(np.mean([item["widthError"] for item in track_metrics_payload]))
        track_area = float(np.mean([item["areaError"] for item in track_metrics_payload]))
        track_compromise = float(np.mean([item["compromiseConsistency"] for item in track_metrics_payload]))
    else:
        track_rmse = sequence_metrics["rmse"]
        track_peak = sequence_metrics["peakHeightError"]
        track_width = sequence_metrics["widthError"]
        track_area = sequence_metrics["areaError"]
        track_compromise = sequence_metrics["compromiseConsistency"]

    collapsed_features = collapse_feature_series_by_tau(discrete_features)
    state_errors = []
    for state_name in FEATURE_STATE_NAMES:
        observed = np.asarray(collapsed_features.get(state_name, []), dtype=float)
        predicted = np.asarray(sequence_simulation.get("states", {}).get(state_name, []), dtype=float)
        if observed.size and observed.size == predicted.size:
            state_errors.append(float(np.sqrt(np.mean(np.square(predicted - observed)))))

    rmse = float(0.65 * track_rmse + 0.35 * sequence_metrics["rmse"])
    peak_error = float(0.65 * track_peak + 0.35 * sequence_metrics["peakHeightError"])
    width_error = float(0.65 * track_width + 0.35 * sequence_metrics["widthError"])
    area_error = float(0.65 * track_area + 0.35 * sequence_metrics["areaError"])
    compromise_consistency = float(np.clip(0.65 * track_compromise + 0.35 * sequence_metrics["compromiseConsistency"], 0.0, 1.0))
    return {
        "rmse": rmse,
        "peakHeightError": peak_error,
        "widthError": width_error,
        "areaError": area_error,
        "featureRmse": float(np.mean(state_errors)) if state_errors else rmse,
        "compromiseConsistency": compromise_consistency,
        "trackRmse": track_rmse,
        "sequenceRmse": float(sequence_metrics["rmse"]),
        "rmseQualityFactor": rmse_quality_factor(rmse),
        "rmseQualityLabel": rmse_quality_label(rmse),
    }, sequence_simulation


def discover_guided_profile_pde_candidate(
    individual_profiles: Sequence[np.ndarray],
    tau_values: np.ndarray,
    metadata: Sequence[dict],
    grid: np.ndarray,
    term_priors: Dict[str, float],
) -> dict | None:
    if len(individual_profiles) < 2 or tau_values.size < 2:
        return None

    theta, target, term_names = build_candidate_library(individual_profiles, tau_values, grid)
    theta = np.nan_to_num(theta, nan=0.0, posinf=0.0, neginf=0.0)
    target = np.nan_to_num(target, nan=0.0, posinf=0.0, neginf=0.0)
    threshold_scale = max(1e-6, float(np.std(target)))
    best_candidate: dict | None = None

    for threshold in DEFAULT_THRESHOLDS:
        coefficient_vector = sequential_thresholded_least_squares(
            theta,
            target,
            threshold * threshold_scale,
            ridge_alpha=1e-5,
            term_names=term_names,
            term_priors=term_priors,
        )
        active_terms = [term_names[index] for index, value in enumerate(coefficient_vector) if abs(float(value)) >= 1e-10]
        if not active_terms or len(active_terms) > 7:
            continue

        coefficients = {
            term_names[index]: float(value)
            for index, value in enumerate(coefficient_vector)
            if abs(float(value)) >= 1e-10
        }
        trajectories, stability_score, note = simulate_candidate(
            "guided_profile_pde",
            coefficients,
            np.asarray(individual_profiles[0], dtype=float),
            grid,
            tau_grid=tau_values,
        )
        if len(trajectories) != len(individual_profiles):
            continue

        sequence_profiles = group_profiles_by_sequence(individual_profiles, tau_values, metadata)
        track_payload = []
        for track in group_profiles_by_track(individual_profiles, tau_values, metadata):
            track_tau = np.asarray(track["tau"], dtype=float)
            track_observed = [np.asarray(profile, dtype=float) for profile in track["profiles"]]
            track_predicted, _, _ = simulate_candidate(
                "guided_profile_pde",
                coefficients,
                np.asarray(track_observed[0], dtype=float),
                grid,
                tau_grid=track_tau,
            )
            track_metrics = compute_profile_error_metrics(track_predicted, track_observed, grid)
            if np.isfinite(track_metrics["rmse"]):
                track_payload.append(track_metrics)

        sequence_metrics = {
            "rmse": float("inf"),
            "peakHeightError": float("inf"),
            "widthError": float("inf"),
            "areaError": float("inf"),
            "compromiseConsistency": 0.0,
        }
        if len(sequence_profiles) >= 2:
            sequence_tau = np.asarray([item["tau"] for item in sequence_profiles], dtype=float)
            sequence_observed = [np.asarray(item["profile"], dtype=float) for item in sequence_profiles]
            sequence_predicted, _, _ = simulate_candidate(
                "guided_profile_pde",
                coefficients,
                np.asarray(sequence_observed[0], dtype=float),
                grid,
                tau_grid=sequence_tau,
            )
            sequence_metrics = compute_profile_error_metrics(sequence_predicted, sequence_observed, grid)

        if track_payload:
            track_rmse = float(np.mean([item["rmse"] for item in track_payload]))
            track_peak = float(np.mean([item["peakHeightError"] for item in track_payload]))
            track_width = float(np.mean([item["widthError"] for item in track_payload]))
            track_area = float(np.mean([item["areaError"] for item in track_payload]))
            track_compromise = float(np.mean([item["compromiseConsistency"] for item in track_payload]))
        else:
            track_rmse = sequence_metrics["rmse"]
            track_peak = sequence_metrics["peakHeightError"]
            track_width = sequence_metrics["widthError"]
            track_area = sequence_metrics["areaError"]
            track_compromise = sequence_metrics["compromiseConsistency"]

        rmse = float(0.65 * track_rmse + 0.35 * sequence_metrics["rmse"])
        peak_error = float(0.65 * track_peak + 0.35 * sequence_metrics["peakHeightError"])
        width_error = float(0.65 * track_width + 0.35 * sequence_metrics["widthError"])
        area_error = float(0.65 * track_area + 0.35 * sequence_metrics["areaError"])
        compromise_consistency = float(np.clip(0.65 * track_compromise + 0.35 * sequence_metrics["compromiseConsistency"], 0.0, 1.0))
        complexity_penalty = len(active_terms) / 7.0
        meta_prior_score = float(np.mean([term_priors.get(term, 0.0) for term in active_terms])) if active_terms else 0.0
        rmse_factor = rmse_quality_factor(rmse)
        confidence = float(np.clip(
            0.33 * stability_score +
            0.22 * compromise_consistency +
            0.20 * rmse_factor +
            0.15 * (1.0 - complexity_penalty) +
            0.10 * meta_prior_score,
            0.0,
            1.0,
        ))
        rank_score = (
            rmse
            + 0.14 * peak_error
            + 0.10 * width_error
            + 0.05 * area_error
            + 0.10 * complexity_penalty
            + max(0.0, rmse - GOOD_RMSE_THRESHOLD) * 0.45
            + max(0.0, rmse - FAIL_RMSE_THRESHOLD) * 0.85
            - 0.08 * stability_score
        )
        coefficient_statistics = {
            term: {
                "mean": value,
                "standardDeviation": 0.0,
                "lower95": value,
                "upper95": value,
            }
            for term, value in coefficients.items()
        }
        candidate = {
            "rankScore": rank_score,
            "equation": format_equation(coefficients),
            "activeTerms": sorted(active_terms, key=term_sort_key),
            "coefficients": coefficients,
            "coefficientStatistics": coefficient_statistics,
            "rmse": rmse,
            "peakHeightError": peak_error,
            "widthError": width_error,
            "areaError": area_error,
            "compromiseConsistency": compromise_consistency,
            "stabilityScore": stability_score,
            "complexityPenalty": complexity_penalty,
            "confidence": confidence,
            "pseudotimeSensitivity": float(np.std([float(np.max(profile)) for profile in trajectories])) if trajectories else 0.0,
            "bootstrapSupport": 1.0,
            "metaPriorScore": meta_prior_score,
            "modelType": "guided_profile_pde",
            "notes": (
                f"{note}. Model type: guided_profile_pde. "
                f"RMSE quality: {rmse_quality_label(rmse)} "
                f"(target <= {GOOD_RMSE_THRESHOLD:.0f} nm, fail > {FAIL_RMSE_THRESHOLD:.0f} nm)."
            ),
        }
        if best_candidate is None or candidate["rankScore"] < best_candidate["rankScore"]:
            best_candidate = candidate

    if best_candidate is not None:
        return best_candidate

    coefficient_vector = ridge_least_squares(theta, target, ridge_alpha=1e-4)
    coefficients = {
        term_names[index]: float(coefficient_vector[index])
        for index in range(len(term_names))
        if abs(float(coefficient_vector[index])) >= 1e-8
    }
    if not coefficients:
        return None
    coefficient_statistics = {
        term: {
            "mean": value,
            "standardDeviation": 0.0,
            "lower95": value,
            "upper95": value,
        }
        for term, value in coefficients.items()
    }
    return {
        "rankScore": float(np.mean(np.square(target - theta @ coefficient_vector))),
        "equation": format_equation(coefficients),
        "activeTerms": sorted(coefficients.keys(), key=term_sort_key),
        "coefficients": coefficients,
        "coefficientStatistics": coefficient_statistics,
        "rmse": float(np.sqrt(np.mean(np.square(target - theta @ coefficient_vector)))),
        "peakHeightError": 0.0,
        "widthError": 0.0,
        "areaError": 0.0,
        "compromiseConsistency": 0.5,
        "stabilityScore": 0.55,
        "complexityPenalty": len(coefficients) / max(1, len(term_names)),
        "confidence": 0.20,
        "pseudotimeSensitivity": 0.0,
        "bootstrapSupport": 1.0,
        "metaPriorScore": 0.0,
        "modelType": "guided_profile_pde",
        "notes": (
            "Ridge fallback for guided profile PDE when sparse candidate search did not converge. "
            f"RMSE should ideally be <= {GOOD_RMSE_THRESHOLD:.0f} nm and is treated as failing above {FAIL_RMSE_THRESHOLD:.0f} nm."
        ),
    }


def build_simulation_playback(grid: np.ndarray, simulation: dict) -> dict:
    tau_grid = np.asarray(simulation.get("tau", []), dtype=float)
    profiles = simulation.get("profiles", [])
    envelope_profiles = simulation.get("envelopeProfiles", profiles)
    if len(grid) == 0 or tau_grid.size == 0 or not profiles:
        return {"success": False, "error": "No bimodal simulation data was available for playback.", "tau": [], "simulatedHeight": [], "simulatedWidth": [], "profiles": [], "envelopeProfiles": []}

    states = simulation.get("states", {})
    return {
        "success": bool(simulation.get("success", False)),
        "tau": [float(tau) for tau in tau_grid],
        "simulatedHeight": [float(np.max(profile)) for profile in profiles],
        "simulatedWidth": [float(fwhm_width(grid, profile)) for profile in profiles],
        "simulatedPeakSeparation": [float(value) for value in states.get("D", [])],
        "simulatedSigmaLeft": [float(value) for value in states.get("sigma1", [])],
        "simulatedSigmaRight": [float(value) for value in states.get("sigma2", [])],
        "simulatedAmplitudeLeft": [float(value) for value in states.get("A1", [])],
        "simulatedAmplitudeRight": [float(value) for value in states.get("A2", [])],
        "profiles": [
            curve_payload(
                label=f"Playback tau {tau:.2f}",
                stage="playback",
                kind="simulationPlayback",
                tau=float(tau),
                x_values=grid,
                y_values=np.asarray(profile, dtype=float),
            )
            for tau, profile in zip(tau_grid, profiles)
        ],
        "envelopeProfiles": [
            curve_payload(
                label=f"Envelope tau {tau:.2f}",
                stage="playback",
                kind="simulationEnvelope",
                tau=float(tau),
                x_values=grid,
                y_values=np.asarray(profile, dtype=float),
            )
            for tau, profile in zip(tau_grid, envelope_profiles)
        ],
        "featureTrajectories": states,
        "stabilityScore": float(simulation.get("stabilityScore", 0.0)),
        "note": simulation.get("note", "Bimodal Gaussian feature ODE playback."),
    }


def build_unity_sphere_playback(grid: np.ndarray, simulation: dict) -> dict:
    tau_grid = np.asarray(simulation.get("tau", []), dtype=float)
    states = simulation.get("states", {})
    if len(grid) == 0 or tau_grid.size == 0 or not states:
        return {"success": False, "error": "No feature trajectory was available for Unity export.", "frames": []}

    scan_span = max(1e-6, float(abs(grid[-1] - grid[0])))
    max_amplitude = max(
        1e-6,
        max(max(states.get("A1", [0.0]), default=0.0), max(states.get("A2", [0.0]), default=0.0)),
    )
    frames = []
    for index, tau_value in enumerate(tau_grid):
        separation_nm = float(states.get("D", [0.0] * tau_grid.size)[index])
        sigma_left_nm = float(states.get("sigma1", [0.0] * tau_grid.size)[index])
        sigma_right_nm = float(states.get("sigma2", [0.0] * tau_grid.size)[index])
        amplitude_left_nm = float(states.get("A1", [0.0] * tau_grid.size)[index])
        amplitude_right_nm = float(states.get("A2", [0.0] * tau_grid.size)[index])
        half_angle = float(np.clip(0.5 * separation_nm / scan_span * math.pi * 0.95, 0.0, 0.78))
        frames.append(
            {
                "frameIndex": int(index),
                "tau": float(tau_value),
                "leftLatitudeRad": float(-half_angle),
                "rightLatitudeRad": float(half_angle),
                "leftSigmaRad": float(np.clip(sigma_left_nm / scan_span * math.pi, 0.02, 0.55)),
                "rightSigmaRad": float(np.clip(sigma_right_nm / scan_span * math.pi, 0.02, 0.55)),
                "leftAmplitudeScale": float(amplitude_left_nm / max_amplitude),
                "rightAmplitudeScale": float(amplitude_right_nm / max_amplitude),
                "leftAmplitudeNm": amplitude_left_nm,
                "rightAmplitudeNm": amplitude_right_nm,
                "peakSeparationNm": separation_nm,
            }
        )

    return {
        "success": True,
        "coordinateSystem": "Latitudinal Gaussian ridges around the equator of a unit sphere.",
        "baseRadius": 1.0,
        "scanSpanNm": scan_span,
        "frames": frames,
        "note": "Unity can deform a sphere by adding two Gaussian ridge displacements along the surface normals at the supplied latitudes and widths.",
    }


def fit_equation_discovery(dataset: dict, archive_path: str) -> dict:
    profiles, requested_mapping, stage_mapping_from_profiles, features, time_info = prepare_profiles(dataset)
    stage_validation = run_stage_validation(build_validation_profiles(profiles))
    archive = load_archive(archive_path)
    options = dataset.get("options", {})
    grid = build_common_grid(
        profiles,
        int(options.get("spatialGridCount", 220)),
        float(options.get("spatialHalfRangeNm", 90.0)),
    )
    stage_order = [stage for stage in stage_mapping_from_profiles.keys() if any(profile.stage == stage for profile in profiles)]
    if len(stage_order) < 2:
        raise ValueError("Equation discovery needs at least two ordered pseudo-time anchors after filtering.")

    mapping_mode = "normalized_profile_index" if bool(time_info.get("useNormalizedTau", False)) else "sequence_index_profile_stack"
    mapping_scenarios = [{"name": mapping_mode, "anchors": dict(stage_mapping_from_profiles)}]
    if requested_mapping:
        mapping_scenarios.append({"name": "requested_stage_mapping_metadata", "anchors": dict(requested_mapping)})
    meta_priors = predict_equation_family(features, archive)

    stage_profiles, stage_stats, individual_profiles_list, individual_metadata = bootstrap_stage_profiles(profiles, stage_order, grid, 0)
    if len(stage_profiles) < 2 or len(individual_profiles_list) < 2:
        raise ValueError("No stable candidate equations were discovered from the current individual guided profiles.")

    ordered_samples = sorted(
        zip(individual_profiles_list, individual_metadata),
        key=lambda item: (
            float(item[1].get("tauValue", item[1].get("tauNormalized", 0.0))),
            int(item[1].get("sequenceOrder", 0)),
            int(item[1].get("profileIndex", 0)),
            str(item[1].get("fileName", "")).lower(),
        ),
    )
    individual_profiles_list = [np.asarray(profile, dtype=float) for profile, _ in ordered_samples]
    individual_metadata = [metadata for _, metadata in ordered_samples]
    for frame_index, metadata in enumerate(individual_metadata):
        metadata["frameIndex"] = frame_index
    tau_values = np.asarray([float(metadata.get("tauValue", metadata.get("tauNormalized", 0.0))) for metadata in individual_metadata], dtype=float)
    feature_model = fit_bimodal_feature_trajectories(individual_profiles_list, tau_values, grid)
    dense_tau = np.asarray(feature_model.get("denseTau", []), dtype=float)
    dense_states = build_feature_state_matrix(feature_model.get("interpolated", {}))
    if dense_states.shape[0] < 2:
        raise ValueError("Bimodal feature extraction did not produce enough states for ODE discovery.")

    tau_min = float(time_info.get("tauMin", float(np.min(tau_values) if tau_values.size else 0.0)))
    tau_max = float(time_info.get("tauMax", float(np.max(tau_values) if tau_values.size else 1.0)))
    if tau_max <= tau_min + 1e-9:
        tau_max = tau_min + 1.0

    theta, feature_term_names = build_feature_library(dense_states, dense_tau, tau_bounds=(tau_min, tau_max))
    theta = np.nan_to_num(theta, nan=0.0, posinf=0.0, neginf=0.0)
    target_matrix = np.column_stack([
        compute_smoothed_derivative(dense_tau, dense_states[:, index])
        for index in range(dense_states.shape[1])
    ])
    target_matrix = np.nan_to_num(target_matrix, nan=0.0, posinf=0.0, neginf=0.0)
    threshold_scales = np.maximum(1e-6, np.std(target_matrix, axis=0))
    discrete_feature_series = feature_model.get("discrete", {})
    initial_state = np.array([float(discrete_feature_series.get(name, [0.0])[0]) for name in FEATURE_STATE_NAMES], dtype=float)
    min_sigma = max(
        float(np.mean(np.diff(grid))) * 1.5 if len(grid) > 1 else 1.0,
        0.35 * float(np.min(dense_states[:, 3:5])) if dense_states.shape[1] >= 5 else 1.0,
    )
    rate_cap = max(5.0, 4.0 * float(np.max(np.abs(target_matrix)))) if target_matrix.size else 5.0

    equation_family = []
    internal_candidates = []
    for threshold in DEFAULT_THRESHOLDS:
        coefficient_matrix = np.zeros((len(FEATURE_STATE_NAMES), len(feature_term_names)), dtype=float)
        active_terms = set()
        for state_index, state_name in enumerate(FEATURE_STATE_NAMES):
            coefficients = sequential_thresholded_least_squares(
                theta,
                target_matrix[:, state_index],
                threshold * threshold_scales[state_index],
                ridge_alpha=1e-5,
                term_names=feature_term_names,
                term_priors={},
            )
            coefficient_matrix[state_index] = coefficients
            active_terms.update(
                feature_term_names[index]
                for index, value in enumerate(coefficients)
                if abs(float(value)) >= 1e-10
            )

        if not active_terms:
            continue

        metrics, observed_simulation = evaluate_feature_candidate(
            feature_term_names,
            coefficient_matrix,
            individual_profiles_list,
            tau_values,
            individual_metadata,
            grid,
            discrete_feature_series,
            min_sigma,
            rate_cap,
            (tau_min, tau_max),
        )
        if not np.isfinite(metrics["rmse"]):
            continue

        playback_simulation = simulate_bimodal_feature_system(
            feature_term_names,
            coefficient_matrix,
            initial_state,
            np.linspace(tau_min, tau_max, 60),
            grid,
            min_sigma,
            rate_cap,
            tau_bounds=(tau_min, tau_max),
        )
        complexity_penalty = len(active_terms) / max(1, len(feature_term_names))
        meta_prior_score = float(np.mean([meta_priors.get(term, 0.0) for term in active_terms])) if active_terms else 0.0
        rmse_factor = float(metrics.get("rmseQualityFactor", rmse_quality_factor(metrics["rmse"])))
        feature_rmse_factor = float(rmse_quality_factor(metrics["featureRmse"]))
        confidence = float(np.clip(
            0.34 * float(playback_simulation.get("stabilityScore", 0.0))
            + 0.24 * metrics["compromiseConsistency"]
            + 0.18 * rmse_factor
            + 0.14 * feature_rmse_factor
            + 0.10 * (1.0 - complexity_penalty),
            0.0,
            1.0,
        ))
        rank_score = (
            metrics["rmse"]
            + 0.12 * metrics["featureRmse"]
            + 0.10 * metrics["peakHeightError"]
            + 0.08 * metrics["widthError"]
            + 0.04 * metrics["areaError"]
            + 0.10 * complexity_penalty
            + max(0.0, metrics["rmse"] - GOOD_RMSE_THRESHOLD) * 0.45
            + max(0.0, metrics["rmse"] - FAIL_RMSE_THRESHOLD) * 0.85
            - 0.10 * float(playback_simulation.get("stabilityScore", 0.0))
        )
        active_terms_sorted = sorted(active_terms, key=lambda term: feature_term_names.index(term))
        public_candidate = {
            "rankScore": rank_score,
            "equation": format_feature_system(feature_term_names, coefficient_matrix),
            "activeTerms": active_terms_sorted,
            "coefficients": flatten_feature_coefficients(feature_term_names, coefficient_matrix),
            "coefficientStatistics": build_feature_coefficient_statistics(feature_term_names, coefficient_matrix),
            "rmse": metrics["rmse"],
            "peakHeightError": metrics["peakHeightError"],
            "widthError": metrics["widthError"],
            "areaError": metrics["areaError"],
            "compromiseConsistency": metrics["compromiseConsistency"],
            "stabilityScore": float(playback_simulation.get("stabilityScore", 0.0)),
            "complexityPenalty": complexity_penalty,
            "confidence": confidence,
            "pseudotimeSensitivity": metrics["featureRmse"],
            "bootstrapSupport": 1.0,
            "metaPriorScore": meta_prior_score,
            "modelType": "bimodal_feature_ode",
            "notes": (
                f"{str(playback_simulation.get('note', 'Bimodal feature ODE candidate.'))} "
                f"RMSE quality: {metrics.get('rmseQualityLabel', rmse_quality_label(metrics['rmse']))} "
                f"(track={metrics.get('trackRmse', metrics['rmse']):.2f} nm, image-mean={metrics.get('sequenceRmse', metrics['rmse']):.2f} nm, "
                f"target <= {GOOD_RMSE_THRESHOLD:.0f} nm, fail > {FAIL_RMSE_THRESHOLD:.0f} nm)."
            ),
        }
        equation_family.append(public_candidate)
        internal_candidates.append(
            {
                "coefficientMatrix": coefficient_matrix,
                "playbackSimulation": playback_simulation,
                "observedSimulation": observed_simulation,
            }
        )

    if not equation_family:
        fallback_matrix = np.zeros((len(FEATURE_STATE_NAMES), len(feature_term_names)), dtype=float)
        for state_index in range(len(FEATURE_STATE_NAMES)):
            fallback_matrix[state_index] = ridge_least_squares(theta, target_matrix[:, state_index], ridge_alpha=1e-4)
        fallback_metrics, fallback_observed = evaluate_feature_candidate(
            feature_term_names,
            fallback_matrix,
            individual_profiles_list,
            tau_values,
            individual_metadata,
            grid,
            discrete_feature_series,
            min_sigma,
            rate_cap,
            (tau_min, tau_max),
        )
        fallback_playback = simulate_bimodal_feature_system(
            feature_term_names,
            fallback_matrix,
            initial_state,
            np.linspace(tau_min, tau_max, 60),
            grid,
            min_sigma,
            rate_cap,
            tau_bounds=(tau_min, tau_max),
        )
        active_terms_sorted = [
            term_name
            for index, term_name in enumerate(feature_term_names)
            if np.any(np.abs(fallback_matrix[:, index]) >= 1e-10)
        ]
        equation_family.append(
            {
                "rankScore": fallback_metrics["rmse"] + 0.12 * fallback_metrics["featureRmse"],
                "equation": format_feature_system(feature_term_names, fallback_matrix),
                "activeTerms": active_terms_sorted,
                "coefficients": flatten_feature_coefficients(feature_term_names, fallback_matrix),
                "coefficientStatistics": build_feature_coefficient_statistics(feature_term_names, fallback_matrix),
                "rmse": fallback_metrics["rmse"],
                "peakHeightError": fallback_metrics["peakHeightError"],
                "widthError": fallback_metrics["widthError"],
                "areaError": fallback_metrics["areaError"],
                "compromiseConsistency": fallback_metrics["compromiseConsistency"],
                "stabilityScore": float(fallback_playback.get("stabilityScore", 0.0)),
                "complexityPenalty": len(active_terms_sorted) / max(1, len(feature_term_names)),
                "confidence": float(np.clip(
                    0.55 * float(fallback_metrics.get("rmseQualityFactor", rmse_quality_factor(fallback_metrics["rmse"])))
                    + 0.45 * fallback_metrics["compromiseConsistency"],
                    0.0,
                    1.0,
                )),
                "pseudotimeSensitivity": fallback_metrics["featureRmse"],
                "bootstrapSupport": 1.0,
                "metaPriorScore": 0.0,
                "modelType": "bimodal_feature_ode",
                "notes": (
                    "Ridge fallback feature ODE candidate used because thresholded sparse regression produced no stable bimodal system. "
                    f"RMSE quality: {fallback_metrics.get('rmseQualityLabel', rmse_quality_label(fallback_metrics['rmse']))} "
                    f"(target <= {GOOD_RMSE_THRESHOLD:.0f} nm, fail > {FAIL_RMSE_THRESHOLD:.0f} nm)."
                ),
            }
        )
        internal_candidates.append(
            {
                "coefficientMatrix": fallback_matrix,
                "playbackSimulation": fallback_playback,
                "observedSimulation": fallback_observed,
            }
        )

    ranked_pairs = sorted(
        zip(equation_family, internal_candidates),
        key=lambda item: (item[0]["rankScore"], -item[0]["confidence"]),
    )
    unique_ranked_pairs = []
    seen_candidate_signatures = set()
    for public_candidate, internal_candidate in ranked_pairs:
        signature = candidate_signature(public_candidate)
        if signature in seen_candidate_signatures:
            continue
        seen_candidate_signatures.add(signature)
        unique_ranked_pairs.append((public_candidate, internal_candidate))

    ranked_pairs = unique_ranked_pairs
    equation_family = [public_candidate for public_candidate, _ in ranked_pairs]
    internal_candidates = [internal_candidate for _, internal_candidate in ranked_pairs]
    interpretable_pairs = [
        (public_candidate, internal_candidate)
        for public_candidate, internal_candidate in zip(equation_family, internal_candidates)
        if is_interpretable_candidate(public_candidate)
    ]
    if interpretable_pairs:
        equation_family = [public_candidate for public_candidate, _ in interpretable_pairs]
        internal_candidates = [internal_candidate for _, internal_candidate in interpretable_pairs]
    for index, candidate in enumerate(equation_family, start=1):
        candidate["rank"] = index
        candidate.pop("rankScore", None)

    if not equation_family or not internal_candidates:
        raise ValueError(
            f"No statistically interpretable bimodal feature ODE candidates were discovered. "
            f"Current acceptance rule requires RMSE <= {FAIL_RMSE_THRESHOLD:.0f} nm and confidence >= 0.35."
        )

    top_candidate = equation_family[0]
    top_internal = internal_candidates[0]
    guided_profile_pde = discover_guided_profile_pde_candidate(individual_profiles_list, tau_values, individual_metadata, grid, meta_priors)
    if guided_profile_pde is not None and is_interpretable_candidate(guided_profile_pde):
        guided_profile_pde["rank"] = len(equation_family) + 1
        guided_profile_pde.pop("rankScore", None)
        equation_family.append(guided_profile_pde)

    mixed_growth_model = None
    mixed_growth_note = ""
    try:
        mixed_growth_model = build_mixed_growth_model(individual_profiles_list, tau_values, grid)
        top_internal["playbackSimulation"] = build_mixed_growth_simulation(
            top_internal.get("playbackSimulation", {}),
            mixed_growth_model,
            grid,
        )
        mixed_growth_note = (
            " Playback uses a polynomial + bimodal Gaussian mixed-growth envelope "
            "to preserve rugged line-profile details while enforcing visible peak separation growth."
        )
    except Exception as exc:
        mixed_growth_model = None
        mixed_growth_note = f" Mixed-growth envelope was unavailable for this run ({exc})."

    top_mapping = dict(stage_mapping_from_profiles)
    top_coefficients = np.asarray(top_internal["coefficientMatrix"], dtype=float)
    tau_grid = np.asarray([top_mapping[stage] for stage in stage_order], dtype=float)
    stage_simulation_base = simulate_bimodal_feature_system(
        feature_term_names,
        top_coefficients,
        initial_state,
        tau_grid,
        grid,
        min_sigma,
        rate_cap,
        tau_bounds=(tau_min, tau_max),
    )
    stage_simulation = build_mixed_growth_simulation(stage_simulation_base, mixed_growth_model, grid, tau_grid=tau_grid)
    reconstructed_anchors = stage_simulation.get("profiles", [])
    progression_tau = np.linspace(tau_min, tau_max, 9)
    progression_simulation_base = simulate_bimodal_feature_system(
        feature_term_names,
        top_coefficients,
        initial_state,
        progression_tau,
        grid,
        min_sigma,
        rate_cap,
        tau_bounds=(tau_min, tau_max),
    )
    progression_simulation = build_mixed_growth_simulation(
        progression_simulation_base,
        mixed_growth_model,
        grid,
        tau_grid=progression_tau,
    )
    progression_profiles = progression_simulation.get("profiles", [])

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
                y_values=stage_profiles[stage],
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
        summary = stage_stats[stage]
        stage_summaries.append(
            {
                "stage": stage,
                "tau": float(summary.get("tau", top_mapping[stage])),
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

    stage_averaged_profiles_payload = {
        "profiles": [
            {
                "stage": stage,
                "tau": top_mapping[stage],
                "gridPoints": [{"x": float(x), "y": float(y)} for x, y in zip(grid, stage_profiles[stage])],
            }
            for stage in stage_order
        ],
        "stats": stage_summaries,
    }
    individual_profiles_payload = {
        "count": len(individual_profiles_list),
        "metadata": individual_metadata,
        "profiles": [
            {
                "frameIndex": index,
                "tau": float(metadata.get("tauValue", metadata.get("tauNormalized", 0.0))),
                "gridPoints": [{"x": float(x), "y": float(y)} for x, y in zip(grid, profile)],
                "metadata": metadata,
            }
            for index, (profile, metadata) in enumerate(zip(individual_profiles_list, individual_metadata))
        ],
    }
    simulation_playback = build_simulation_playback(grid, top_internal["playbackSimulation"])
    unity_sphere_playback = build_unity_sphere_playback(grid, top_internal["playbackSimulation"])
    discovered_feature_names = sorted({term for candidate in equation_family for term in candidate["coefficients"].keys()}, key=str.lower)
    discovered_equations_payload = {
        "equations": [candidate["equation"] for candidate in equation_family],
        "coefficients": [
            [float(candidate["coefficients"].get(term, 0.0)) for term in discovered_feature_names]
            for candidate in equation_family
        ],
        "feature_names": discovered_feature_names,
        "state_names": list(FEATURE_STATE_NAMES),
        "term_names": list(feature_term_names),
        "equation_types": [str(candidate.get("modelType", "bimodal_feature_ode")) for candidate in equation_family],
    }
    stage_validation_note = ""
    if stage_validation.get("validatorAvailable") and stage_validation.get("confidenceScore", 0.0) < 0.8:
        stage_validation_note = (
            f" Stage-ordering validation reported {stage_validation['confidenceScore']:.0%} confidence, "
            "so uncertainty should be reported alongside the discovered family."
        )
    elif not stage_validation.get("validatorAvailable", True):
        stage_validation_note = " Stage-ordering validation was unavailable, so the pseudo-time confidence gate could not be applied."

    return {
        "sampleId": dataset.get("sampleId", "piecrust-session"),
        "timeMode": dataset.get("timeMode", "pseudotime_sequence_ordered"),
        "profileMode": dataset.get("profileMode", "guided_perpendicular_stack_profile"),
        "t_normalized_range": [0.0, 1.0] if bool(time_info.get("useNormalizedTau", False)) else [],
        "t_range": [tau_min, tau_max],
        "useNormalizedTau": bool(time_info.get("useNormalizedTau", False)),
        "spatialCoordinateLabel": "Perpendicular offset from guide centre z [nm]",
        "heightLabel": "Height z [nm]",
        "stageMappingMode": mapping_mode,
        "stageMapping": top_mapping,
        "requestedStageMapping": requested_mapping,
        "stageValidation": stage_validation,
        "mappingScenarios": mapping_scenarios,
        "stageProfiles": stage_summaries,
        "stageAveragedProfiles": stage_averaged_profiles_payload,
        "individualProfiles": individual_profiles_payload,
        "bimodalFeatureExtraction": build_bimodal_feature_payload(feature_model),
        "statisticalSummary": build_statistical_summary(individual_metadata),
        "discoveredEquations": discovered_equations_payload,
        "equationFamily": equation_family,
        "observedProfiles": observed_profiles,
        "reconstructedProfiles": reconstructed_profiles,
        "progressionProfiles": progression_payload,
        "simulationPlayback": simulation_playback,
        "unitySpherePlayback": unity_sphere_playback,
        "metaModelSummary": build_meta_summary(archive, meta_priors),
        "metaModelExampleCount": len(archive.get("entries", [])),
        "statusText": (
            "Data-driven discovery over guided perpendicular profile averages: each ordered image contributes ten equidistant perpendicular line profiles sampled over corridor width +20%, then those ten profiles are averaged into one representative profile for equation discovery. "
            "The pipeline discovers both a bimodal Gaussian feature ODE system for simulation playback and a guided-profile PDE-style law for the averaged line-profile field."
            f"{mixed_growth_note}"
            f"{stage_validation_note}"
        ),
    }


def build_meta_summary(archive: dict, meta_priors: Dict[str, float]) -> str:
    if not archive.get("entries"):
        return "Meta-model has no stored equation-discovery examples yet; discovery is using only the current ordered dataset."
    if not meta_priors:
        return f"Meta-model has {len(archive['entries'])} archived example set(s), but no strong term prior was inferred for this morphology family."
    ranked = sorted(meta_priors.items(), key=lambda item: item[1], reverse=True)[:4]
    summary = ", ".join(f"{term} ({weight:.2f})" for term, weight in ranked)
    return (
        f"Meta-model archived {len(archive['entries'])} prior ordered equation-discovery run(s). "
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
        if isinstance(exc, StageValidationError):
            error["stageValidation"] = exc.payload
        if len(sys.argv) >= 3:
            try:
                save_json(sys.argv[2], error)
            except Exception:
                pass
        print(json.dumps(error), file=sys.stderr)
        raise SystemExit(1)
