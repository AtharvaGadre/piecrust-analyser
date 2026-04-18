#!/usr/bin/env python3
"""
Stage 1 — Data Ingestion and Surface Extraction
Föppl–von Kármán morphoelastic piecrust analysis pipeline.

Supports both the original directory mode and a single-TIFF ROI mode used by the
desktop application. In single-TIFF mode the script looks for a matching
``.roi.json`` sidecar and restricts all Stage-1 surface extraction work to that
polygonal ROI.
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

import numpy as np

try:
    import tifffile
except ImportError:
    sys.exit("Missing tifffile.  Run: pip install tifffile")

try:
    import h5py
except ImportError:
    sys.exit("Missing h5py.  Run: pip install h5py")

try:
    from skimage.draw import polygon as polygon_rasterize
except ImportError:
    sys.exit("Missing scikit-image.  Run: pip install scikit-image")

try:
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    HAS_MPL = True
except ImportError:
    HAS_MPL = False
    print("[warn] matplotlib not found – diagnostic figures will be skipped.")

from scipy.ndimage import laplace


def load_frames(data_dir: Path, pattern: str, tiff_path: Path | None = None) -> list[tuple[Path, np.ndarray]]:
    """Return a sorted list of (path, array) TIFF frames."""
    if tiff_path is not None:
        if not tiff_path.exists():
            sys.exit(f"TIFF file not found: {tiff_path}")
        arr = tifffile.imread(str(tiff_path))
        print(f"  Loaded 1 TIFF file from {tiff_path.parent}")
        return [(tiff_path, arr.astype(np.float64))]

    paths = sorted(data_dir.glob(pattern))
    if not paths:
        for fallback in ("*.tiff", "*.tif", "*.TIFF", "*.TIF"):
            paths = sorted(data_dir.glob(fallback))
            if paths:
                break
    if not paths:
        sys.exit(f"No TIFF files found in {data_dir} matching '{pattern}'")

    frames: list[tuple[Path, np.ndarray]] = []
    for path in paths:
        arr = tifffile.imread(str(path))
        frames.append((path, arr.astype(np.float64)))

    print(f"  Loaded {len(frames)} TIFF file(s) from {data_dir}")
    return frames


def detect_and_extract_heightmap(arr: np.ndarray) -> np.ndarray:
    """Return (Y, X) heightmap regardless of whether arr is 2-D or 3-D."""
    if arr.ndim == 2:
        return arr.copy()
    if arr.ndim == 3:
        z_count, y_size, x_size = arr.shape
        z_max = np.argmax(arr, axis=0)
        z_c = np.clip(z_max, 1, z_count - 2)
        yy, xx = np.mgrid[0:y_size, 0:x_size]
        i_minus = arr[z_c - 1, yy, xx]
        i_zero = arr[z_c, yy, xx]
        i_plus = arr[z_c + 1, yy, xx]
        denom = i_plus - 2.0 * i_zero + i_minus
        with np.errstate(divide="ignore", invalid="ignore"):
            delta = np.where(np.abs(denom) > 1e-12, -0.5 * (i_plus - i_minus) / denom, 0.0)
        delta = np.clip(delta, -0.5, 0.5)
        z_sub = z_c.astype(float) + delta
        z_sub[z_max == 0] = 0.0
        z_sub[z_max == z_count - 1] = float(z_count - 1)
        return z_sub
    return arr[arr.shape[0] // 2].copy()


def load_roi_metadata(tiff_path: Path | None) -> dict | None:
    """Load a .roi.json sidecar if one is expected."""
    if tiff_path is None:
        return None

    sidecar = tiff_path.with_suffix(".roi.json")
    if not sidecar.exists():
        sys.exit(f"ROI sidecar not found for {tiff_path.name}: expected {sidecar.name}")

    with sidecar.open("r", encoding="utf-8") as handle:
        payload = json.load(handle)

    if "vertices" not in payload or not isinstance(payload["vertices"], list) or len(payload["vertices"]) < 3:
        sys.exit(f"Invalid ROI sidecar {sidecar}: expected at least 3 polygon vertices")
    return payload


def rasterize_roi_mask(roi_payload: dict | None, shape: tuple[int, int]) -> np.ndarray:
    """Convert polygon vertices into a boolean ROI mask."""
    height, width = shape
    if roi_payload is None:
        return np.ones((height, width), dtype=bool)

    vertices = roi_payload["vertices"]
    rows = np.asarray([float(vertex["y"]) for vertex in vertices], dtype=float)
    cols = np.asarray([float(vertex["x"]) for vertex in vertices], dtype=float)
    rr, cc = polygon_rasterize(rows, cols, shape=shape)
    mask = np.zeros((height, width), dtype=bool)
    mask[rr, cc] = True
    if not mask.any():
        sys.exit("ROI polygon rasterized to an empty mask.")
    return mask


def hann_window_2d(shape: tuple[int, int]) -> np.ndarray:
    """Return a separable Hann window for a 2-D crop."""
    height, width = shape
    wy = np.hanning(height) if height > 1 else np.ones(height)
    wx = np.hanning(width) if width > 1 else np.ones(width)
    return np.outer(wy, wx)


def extract_w(hmap: np.ndarray, mask: np.ndarray) -> np.ndarray:
    """
    Fit and subtract a degree-2 background using only ROI pixels.
    Outside the ROI the resulting height field is set to zero.
    """
    if hmap.shape != mask.shape:
        sys.exit("Heightmap and ROI mask shapes do not match.")

    yy, xx = np.mgrid[0:hmap.shape[0], 0:hmap.shape[1]]
    xs = xx[mask].astype(float)
    ys = yy[mask].astype(float)
    zs = hmap[mask].astype(float)

    if zs.size < 6:
        sys.exit("ROI mask contains too few pixels for a degree-2 background fit.")

    design = np.column_stack([np.ones(xs.size), xs, ys, xs ** 2, xs * ys, ys ** 2])
    coeffs, _, _, _ = np.linalg.lstsq(design, zs, rcond=None)
    background = (
        coeffs[0]
        + coeffs[1] * xx
        + coeffs[2] * yy
        + coeffs[3] * xx ** 2
        + coeffs[4] * xx * yy
        + coeffs[5] * yy ** 2
    )

    w = np.zeros_like(hmap, dtype=np.float64)
    w[mask] = hmap[mask] - background[mask]
    return w


def radially_averaged_spectrum(power_2d: np.ndarray, pixel_size: float) -> tuple[np.ndarray, np.ndarray]:
    """Return (k_centers, P_radial) from the 2-D power spectrum."""
    height, width = power_2d.shape
    kx = np.fft.fftfreq(width, d=pixel_size)
    ky = np.fft.fftfreq(height, d=pixel_size)
    kx_grid, ky_grid = np.meshgrid(kx, ky)
    radii = np.sqrt(kx_grid ** 2 + ky_grid ** 2)
    n_bins = max(8, min(width, height) // 2)
    k_max = 0.5 / pixel_size
    k_edges = np.linspace(0.0, k_max, n_bins + 1)
    p_rad = np.zeros(n_bins, dtype=np.float64)
    for index in range(n_bins):
        band = (radii >= k_edges[index]) & (radii < k_edges[index + 1])
        if band.any():
            p_rad[index] = float(power_2d[band].mean())
    return 0.5 * (k_edges[:-1] + k_edges[1:]), p_rad


def dominant_wavelength(k_centers: np.ndarray, p_rad: np.ndarray) -> float | None:
    """Return dominant wavelength (in the same length unit as pixel_size)."""
    skip = max(1, len(k_centers) // 20)
    if len(p_rad) <= skip:
        return None
    idx = int(np.argmax(p_rad[skip:])) + skip
    k_star = k_centers[idx]
    return (1.0 / k_star) if k_star > 0 else None


def get_observables(w: np.ndarray, mask: np.ndarray, pixel_size: float) -> tuple[float, np.ndarray, np.ndarray, np.ndarray, float | None]:
    """
    Compute A(t) and wavelength observables from the ROI-only height field.
    FFT is evaluated on the ROI bounding box, with non-ROI values zeroed and a
    Hann window applied before the transform.
    """
    if not mask.any():
        sys.exit("ROI mask is empty; cannot compute observables.")

    masked_values = w[mask]
    amplitude = float(np.sqrt(np.mean(masked_values ** 2)))

    rows, cols = np.where(mask)
    r0, r1 = rows.min(), rows.max() + 1
    c0, c1 = cols.min(), cols.max() + 1
    crop = w[r0:r1, c0:c1]
    mask_crop = mask[r0:r1, c0:c1]
    roi_crop = np.where(mask_crop, crop, 0.0)
    roi_crop = roi_crop * hann_window_2d(roi_crop.shape)

    w_hat = np.fft.fft2(roi_crop)
    power_2d = (np.abs(w_hat) ** 2) / max(1, roi_crop.shape[0] * roi_crop.shape[1])
    k_centers, p_radial = radially_averaged_spectrum(power_2d, pixel_size)
    lam = dominant_wavelength(k_centers, p_radial)
    return amplitude, power_2d, k_centers, p_radial, lam


def save_diagnostic(frame_idx: int, raw: np.ndarray, mask: np.ndarray, w: np.ndarray, power_2d: np.ndarray, out_dir: Path) -> None:
    if not HAS_MPL:
        return
    fig, axes = plt.subplots(1, 4, figsize=(15, 4))
    axes[0].imshow(raw, cmap="gray", origin="lower")
    axes[0].set_title(f"Raw frame {frame_idx:04d}")
    axes[1].imshow(mask.astype(float), cmap="gray", origin="lower")
    axes[1].set_title("ROI mask")
    im = axes[2].imshow(w, cmap="RdBu_r", origin="lower")
    axes[2].set_title("ROI detrended surface w(x,y)")
    plt.colorbar(im, ax=axes[2], label="a.u.")
    axes[3].imshow(np.log1p(np.fft.fftshift(power_2d)), cmap="inferno", origin="lower")
    axes[3].set_title("log(1+P) FFT power")
    fig.tight_layout()
    fig.savefig(out_dir / f"diag_{frame_idx:04d}.png", dpi=90)
    plt.close(fig)


def write_summary_report(summary_path: Path, out_path: Path, diag_dir: Path, frames_processed: int, a_arr: np.ndarray, lam_arr: np.ndarray, roi_enabled: bool) -> None:
    valid_lambda = lam_arr[~np.isnan(lam_arr)]
    payload = {
        "stage": 1,
        "mode": "roi_single_tiff" if roi_enabled else "directory_stack",
        "surface_data_h5": str(out_path),
        "diagnostics_dir": str(diag_dir),
        "frames_processed": int(frames_processed),
        "mean_A": float(np.nanmean(a_arr)) if a_arr.size else 0.0,
        "mean_lambda_um": float(np.nanmean(valid_lambda)) if valid_lambda.size else None,
        "valid_lambda_count": int(valid_lambda.size)
    }
    summary_path.write_text(json.dumps(payload, indent=2), encoding="utf-8")


def resolve_paths(args: argparse.Namespace) -> tuple[Path, Path | None, Path]:
    tiff_path = Path(args.tiff_path).expanduser() if args.tiff_path else None
    if tiff_path is not None:
        data_dir = tiff_path.parent
    else:
        data_dir = Path(args.data_dir).expanduser()

    output_dir = Path(args.output_dir).expanduser() if args.output_dir else data_dir
    output_dir.mkdir(parents=True, exist_ok=True)
    return data_dir, tiff_path, output_dir


def main() -> None:
    parser = argparse.ArgumentParser(description="Stage 1: surface extraction from TIFF stack")
    parser.add_argument("--data_dir", default="./data", help="Directory containing TIFF files")
    parser.add_argument("--tiff_path", default=None, help="Optional path to a single TIFF analysed with an ROI sidecar")
    parser.add_argument("--output", default="surface_data.h5", help="Output HDF5 filename")
    parser.add_argument("--output_dir", default=None, help="Directory for stage-1 outputs in single-TIFF mode")
    parser.add_argument("--pixel_size_um", type=float, default=0.1, help="Lateral pixel size in micrometres")
    parser.add_argument("--h_um", type=float, default=1.0, help="Estimated crust thickness in micrometres")
    parser.add_argument("--pattern", default="*.tif*", help="Glob pattern for TIFF files")
    args = parser.parse_args()

    data_dir, tiff_path, output_dir = resolve_paths(args)
    out_path = output_dir / args.output if not Path(args.output).is_absolute() else Path(args.output)
    diag_dir = output_dir / "diagnostics"
    diag_dir.mkdir(parents=True, exist_ok=True)

    roi_payload = load_roi_metadata(tiff_path)
    pix = float(roi_payload.get("pixel_size_um", args.pixel_size_um)) if roi_payload else args.pixel_size_um
    h_um = float(roi_payload.get("thickness_um", args.h_um)) if roi_payload else args.h_um

    if out_path.exists():
        print(f"[stage1] {out_path} already exists – skipping recomputation.")
        print("  Delete the file to rerun Stage 1.")
        return

    print("[stage1] Loading frames …")
    frames = load_frames(data_dir, args.pattern, tiff_path)
    n_frames = len(frames)

    a_arr = np.zeros(n_frames, dtype=np.float64)
    lam_arr = np.full(n_frames, np.nan, dtype=np.float64)
    kstar_arr = np.full(n_frames, np.nan, dtype=np.float64)

    roi_mask: np.ndarray | None = None

    print("[stage1] Processing frames …")
    with h5py.File(out_path, "w") as hf:
        hf.attrs["pixel_size_um"] = pix
        hf.attrs["h_um"] = h_um
        hf.attrs["n_frames"] = n_frames
        hf.attrs["roi_enabled"] = bool(roi_payload is not None)

        frames_group = hf.create_group("frames")

        for frame_index, (path, arr) in enumerate(frames):
            print(f"  frame {frame_index + 1}/{n_frames}  {path.name}")
            raw_2d = detect_and_extract_heightmap(arr)
            height, width = raw_2d.shape

            if frame_index == 0:
                hf.attrs["shape_Y"] = height
                hf.attrs["shape_X"] = width
                roi_mask = rasterize_roi_mask(roi_payload, raw_2d.shape)
                hf.create_dataset("roi_mask", data=roi_mask.astype(np.uint8), compression="gzip", compression_opts=4)
                print(f"  ROI pixels kept: {int(roi_mask.sum())}/{roi_mask.size}")
            elif roi_mask is None or roi_mask.shape != raw_2d.shape:
                sys.exit("All frames must share the same shape as the startup ROI mask.")

            w = extract_w(raw_2d, roi_mask)
            a_arr[frame_index], power_2d, k_centers, p_radial, lam = get_observables(w, roi_mask, pix)

            if lam is not None:
                lam_arr[frame_index] = lam
                kstar_arr[frame_index] = 1.0 / lam

            kappa = laplace(w) / (pix ** 2)

            group = frames_group.create_group(f"{frame_index:04d}")
            group.create_dataset("w", data=w.astype(np.float32), compression="gzip", compression_opts=4)
            group.create_dataset("P", data=power_2d.astype(np.float32), compression="gzip", compression_opts=4)
            group.create_dataset("kappa", data=kappa.astype(np.float32), compression="gzip", compression_opts=4)
            group.create_dataset("k_centers", data=k_centers.astype(np.float32))
            group.create_dataset("P_radial", data=p_radial.astype(np.float32))
            group.attrs["filename"] = path.name
            group.attrs["A"] = a_arr[frame_index]
            group.attrs["lambda_um"] = float(lam) if lam is not None else -1.0
            group.attrs["k_star"] = float(1.0 / lam) if lam is not None else -1.0

            save_diagnostic(frame_index, raw_2d, roi_mask, w, power_2d, diag_dir)

        time_series = hf.create_group("time_series")
        time_series.create_dataset("A", data=a_arr)
        time_series.create_dataset("lambda_um", data=lam_arr)
        time_series.create_dataset("k_star", data=kstar_arr)
        time_series.create_dataset("frame_index", data=np.arange(n_frames))
        filenames = [frame_path.name for frame_path, _ in frames]
        time_series.create_dataset("filenames", data=np.array(filenames, dtype=h5py.special_dtype(vlen=str)))

    summary_path = output_dir / "summary_report.json"
    write_summary_report(summary_path, out_path, diag_dir, n_frames, a_arr, lam_arr, roi_payload is not None)

    print(f"[stage1] Done.  Saved -> {out_path}")
    print(f"  Summary report   : {summary_path}")
    print(f"  Frames processed : {n_frames}")
    print(f"  Mean A(t)        : {np.nanmean(a_arr):.4f} um")
    valid_lambda = lam_arr[~np.isnan(lam_arr)]
    if valid_lambda.size:
        print(f"  Mean lambda(t)   : {np.nanmean(valid_lambda):.4f} um")
    else:
        print("  lambda(t): no valid FFT peak found")
    print(f"  Diagnostics      : {diag_dir}/")


if __name__ == "__main__":
    main()
