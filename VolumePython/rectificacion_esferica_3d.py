import json
from pathlib import Path

import numpy as np
from scipy.ndimage import (
    binary_propagation,
    binary_closing,
    binary_fill_holes,
    gaussian_filter1d,
    gaussian_filter,
    map_coordinates,
    distance_transform_edt,
)
import matplotlib.pyplot as plt
from matplotlib.widgets import Slider, RadioButtons


# ============================================================
# CONFIGURACIÓN
# ============================================================

BASE_DIR = Path(__file__).resolve().parent

INPUT_RAW_PATH = BASE_DIR / "volume_original.raw"

OUTPUT_DIR = BASE_DIR / "spherical_rectified_output"
OUTPUT_DIR.mkdir(exist_ok=True)

# Salida float32 normal
OUTPUT_RAW_FLOAT32_PATH = OUTPUT_DIR / "volume_spherical_rectified_cropped_filled.raw"
OUTPUT_FLOAT32_JSON_PATH = OUTPUT_DIR / "volume_spherical_rectified_cropped_filled_metadata.json"

# Salida UInt16 para Unity, con histograma igualado al volumen original
OUTPUT_RAW_UINT16_PATH = OUTPUT_DIR / "volume_spherical_rectified_cropped_filled_matched_uint16.raw"
OUTPUT_UINT16_JSON_PATH = OUTPUT_DIR / "volume_spherical_rectified_cropped_filled_matched_uint16_metadata.json"

DIM_X = 211
DIM_Y = 232
DIM_Z = 153
INPUT_SHAPE_ZYX = (DIM_Z, DIM_Y, DIM_X)

DTYPE = np.float32

SCALE_X = 105.7454605102539
SCALE_Y = 116.26988983154297
SCALE_Z = 76.20020294189453

EMPTY_THRESHOLD = 1.0
FILL_VALUE = 0.0

INTERPOLATION_ORDER = 1

MASK_CLOSE_ITERATIONS = 1
FILL_INTERNAL_MASK_HOLES = True

MIN_VALID_PIXELS_IN_PLANE = 150
MIN_VALID_COLUMNS = 8

SIDE_LINE_REJECTION_ITERATIONS = 4
SIDE_LINE_REJECTION_FACTOR = 3.0

CURVE_SMOOTH_SIGMA = 2.0

# Dimensiones iniciales del volumen rectificado antes del recorte
OUT_DIM_R = DIM_Z
OUT_DIM_THETA_Y = DIM_Y
OUT_DIM_THETA_X = DIM_X

# Límites robustos del sector esférico
R_INNER_PERCENTILE = 0.2
R_OUTER_PERCENTILE = 99.8

ANGLE_MIN_PERCENTILE = 0.5
ANGLE_MAX_PERCENTILE = 99.5

CUT_OUTSIDE_ORIGINAL_MASK = False

RADIAL_CHUNK_SIZE = 12

# Recorte de bordes
CROP_R = 3
CROP_THETA_Y = 12
CROP_THETA_X = 6

# Relleno de pequeñas zonas sin soporte
FILL_HOLES_AFTER_CROP = True
SMOOTH_FILLED_HOLES = True
FILLED_HOLES_SMOOTH_SIGMA = 0.6

# Exportación para Unity
SAVE_UINT16_MATCHED_FOR_UNITY = True

# Percentiles usados para evitar que outliers dominen el histograma
HIST_SOURCE_LOW_PERCENTILE = 0.5
HIST_SOURCE_HIGH_PERCENTILE = 99.8

HIST_REFERENCE_LOW_PERCENTILE = 0.5
HIST_REFERENCE_HIGH_PERCENTILE = 99.8

# Apex manual opcional.
# Orden: z0, y0, x0 en índices del volumen original.
# Ejemplo:
# MANUAL_APEX_ZYX = (-120.0, 115.5, 105.0)
MANUAL_APEX_ZYX = None

SHOW_VIEWER = True
INITIAL_AXIS = "z"   # "z" radio, "y" theta_y, "x" theta_x


# ============================================================
# LECTURA Y GUARDADO
# ============================================================

def load_volume():
    flat = np.fromfile(INPUT_RAW_PATH, dtype=DTYPE)
    expected = DIM_X * DIM_Y * DIM_Z

    if flat.size != expected:
        raise ValueError(
            f"El RAW tiene {flat.size} valores, pero se esperaban {expected}. "
            f"Revisa DIM_X, DIM_Y, DIM_Z o DTYPE."
        )

    return flat.reshape(INPUT_SHAPE_ZYX).astype(np.float32)


def save_volume_float32(volume):
    volume.astype(np.float32).tofile(OUTPUT_RAW_FLOAT32_PATH)


def voxel_spacings():
    sx = SCALE_X / max(DIM_X - 1, 1)
    sy = SCALE_Y / max(DIM_Y - 1, 1)
    sz = SCALE_Z / max(DIM_Z - 1, 1)

    return float(sz), float(sy), float(sx)


def crop_slice(length, crop_each_side):
    if crop_each_side <= 0:
        return slice(0, int(length))

    start = int(crop_each_side)
    end = int(length - crop_each_side)

    if start >= end:
        raise ValueError("El recorte es demasiado grande para el tamaño del volumen.")

    return slice(start, end)


def effective_cropped_bounds(bounds):
    r_values = np.linspace(
        bounds["r_inner"],
        bounds["r_outer"],
        OUT_DIM_R,
        dtype=np.float32,
    )

    theta_y_values = np.linspace(
        bounds["theta_y_min"],
        bounds["theta_y_max"],
        OUT_DIM_THETA_Y,
        dtype=np.float32,
    )

    theta_x_values = np.linspace(
        bounds["theta_x_min"],
        bounds["theta_x_max"],
        OUT_DIM_THETA_X,
        dtype=np.float32,
    )

    r_slice = crop_slice(OUT_DIM_R, CROP_R)
    ty_slice = crop_slice(OUT_DIM_THETA_Y, CROP_THETA_Y)
    tx_slice = crop_slice(OUT_DIM_THETA_X, CROP_THETA_X)

    r_c = r_values[r_slice]
    ty_c = theta_y_values[ty_slice]
    tx_c = theta_x_values[tx_slice]

    return {
        "r_inner": float(r_c[0]),
        "r_outer": float(r_c[-1]),
        "theta_y_min": float(ty_c[0]),
        "theta_y_max": float(ty_c[-1]),
        "theta_x_min": float(tx_c[0]),
        "theta_x_max": float(tx_c[-1]),
    }


def save_metadata_float32(apex_zyx, bounds, final_shape):
    sz, sy, sx = voxel_spacings()
    cropped_bounds = effective_cropped_bounds(bounds)

    metadata = {
        "datasetName": "0301_spherical_rectified_cropped_filled",
        "rawFileName": OUTPUT_RAW_FLOAT32_PATH.name,
        "dtype": "float32",
        "arrayOrder": "Python shape = (dimR, dimThetaY, dimThetaX)",
        "unityIndexFormula": "index = thetaX + thetaY * dimThetaX + r * (dimThetaX * dimThetaY)",
        "dimX": int(final_shape[2]),
        "dimY": int(final_shape[1]),
        "dimZ": int(final_shape[0]),
        "axis0": "r",
        "axis1": "theta_y",
        "axis2": "theta_x",
        "originalDimX": int(DIM_X),
        "originalDimY": int(DIM_Y),
        "originalDimZ": int(DIM_Z),
        "originalScaleX": float(SCALE_X),
        "originalScaleY": float(SCALE_Y),
        "originalScaleZ": float(SCALE_Z),
        "originalSpacingZ": float(sz),
        "originalSpacingY": float(sy),
        "originalSpacingX": float(sx),
        "volumeScale": 0.0,
        "method": "single_3d_spherical_sector_rectification_with_crop_and_nearest_hole_fill",
        "description": (
            "Se estima un apex 3D y se rectifica el volumen como un sector esférico "
            "en coordenadas r, theta_y, theta_x. Luego se recortan bordes fijos "
            "y se rellenan pequeñas zonas sin soporte usando el vóxel válido más cercano."
        ),
        "apexZYX_index": {
            "z0": float(apex_zyx[0]),
            "y0": float(apex_zyx[1]),
            "x0": float(apex_zyx[2]),
        },
        "originalSphericalBoundsBeforeCrop": {
            "rInner": float(bounds["r_inner"]),
            "rOuter": float(bounds["r_outer"]),
            "thetaYMin": float(bounds["theta_y_min"]),
            "thetaYMax": float(bounds["theta_y_max"]),
            "thetaXMin": float(bounds["theta_x_min"]),
            "thetaXMax": float(bounds["theta_x_max"]),
        },
        "effectiveSphericalBoundsAfterCrop": {
            "rInner": float(cropped_bounds["r_inner"]),
            "rOuter": float(cropped_bounds["r_outer"]),
            "thetaYMin": float(cropped_bounds["theta_y_min"]),
            "thetaYMax": float(cropped_bounds["theta_y_max"]),
            "thetaXMin": float(cropped_bounds["theta_x_min"]),
            "thetaXMax": float(cropped_bounds["theta_x_max"]),
        },
        "crop": {
            "cropR_each_side": int(CROP_R),
            "cropThetaY_each_side": int(CROP_THETA_Y),
            "cropThetaX_each_side": int(CROP_THETA_X),
        },
        "parameters": {
            "emptyThreshold": float(EMPTY_THRESHOLD),
            "interpolationOrder": int(INTERPOLATION_ORDER),
            "maskCloseIterations": int(MASK_CLOSE_ITERATIONS),
            "fillInternalMaskHoles": bool(FILL_INTERNAL_MASK_HOLES),
            "minValidPixelsInPlane": int(MIN_VALID_PIXELS_IN_PLANE),
            "minValidColumns": int(MIN_VALID_COLUMNS),
            "curveSmoothSigma": float(CURVE_SMOOTH_SIGMA),
            "rInnerPercentile": float(R_INNER_PERCENTILE),
            "rOuterPercentile": float(R_OUTER_PERCENTILE),
            "angleMinPercentile": float(ANGLE_MIN_PERCENTILE),
            "angleMaxPercentile": float(ANGLE_MAX_PERCENTILE),
            "cutOutsideOriginalMask": bool(CUT_OUTSIDE_ORIGINAL_MASK),
            "radialChunkSize": int(RADIAL_CHUNK_SIZE),
            "fillHolesAfterCrop": bool(FILL_HOLES_AFTER_CROP),
            "smoothFilledHoles": bool(SMOOTH_FILLED_HOLES),
            "filledHolesSmoothSigma": float(FILLED_HOLES_SMOOTH_SIGMA),
        },
    }

    with open(OUTPUT_FLOAT32_JSON_PATH, "w", encoding="utf-8") as f:
        json.dump(metadata, f, indent=4, ensure_ascii=False)


def save_metadata_uint16(apex_zyx, bounds, final_shape, hist_stats):
    sz, sy, sx = voxel_spacings()
    cropped_bounds = effective_cropped_bounds(bounds)

    metadata = {
        "datasetName": "0301_spherical_rectified_cropped_filled_matched_uint16",
        "rawFileName": OUTPUT_RAW_UINT16_PATH.name,
        "dtype": "uint16",
        "arrayOrder": "Python shape = (dimR, dimThetaY, dimThetaX)",
        "unityIndexFormula": "index = thetaX + thetaY * dimThetaX + r * (dimThetaX * dimThetaY)",
        "dimX": int(final_shape[2]),
        "dimY": int(final_shape[1]),
        "dimZ": int(final_shape[0]),
        "axis0": "r",
        "axis1": "theta_y",
        "axis2": "theta_x",
        "originalDimX": int(DIM_X),
        "originalDimY": int(DIM_Y),
        "originalDimZ": int(DIM_Z),
        "originalScaleX": float(SCALE_X),
        "originalScaleY": float(SCALE_Y),
        "originalScaleZ": float(SCALE_Z),
        "originalSpacingZ": float(sz),
        "originalSpacingY": float(sy),
        "originalSpacingX": float(sx),
        "volumeScale": 0.0,
        "method": "unity_uint16_export_histogram_matched_to_original_volume",
        "description": (
            "Volumen rectificado esférico exportado como uint16 para Unity. "
            "La distribución de intensidades del volumen rectificado fue igualada "
            "a la distribución del volumen original que ya se visualizaba correctamente."
        ),
        "apexZYX_index": {
            "z0": float(apex_zyx[0]),
            "y0": float(apex_zyx[1]),
            "x0": float(apex_zyx[2]),
        },
        "effectiveSphericalBoundsAfterCrop": {
            "rInner": float(cropped_bounds["r_inner"]),
            "rOuter": float(cropped_bounds["r_outer"]),
            "thetaYMin": float(cropped_bounds["theta_y_min"]),
            "thetaYMax": float(cropped_bounds["theta_y_max"]),
            "thetaXMin": float(cropped_bounds["theta_x_min"]),
            "thetaXMax": float(cropped_bounds["theta_x_max"]),
        },
        "histogramMatching": hist_stats,
        "unityRawImport": {
            "xDimension": int(final_shape[2]),
            "yDimension": int(final_shape[1]),
            "zDimension": int(final_shape[0]),
            "bytesToSkip": 0,
            "dataFormat": "Uint 16",
            "endianness": "Little endian",
        },
    }

    with open(OUTPUT_UINT16_JSON_PATH, "w", encoding="utf-8") as f:
        json.dump(metadata, f, indent=4, ensure_ascii=False)


# ============================================================
# MÁSCARA 3D
# ============================================================

def make_valid_mask(volume):
    empty = volume <= EMPTY_THRESHOLD

    seed = np.zeros_like(empty, dtype=bool)
    seed[0, :, :] = empty[0, :, :]
    seed[-1, :, :] = empty[-1, :, :]
    seed[:, 0, :] = empty[:, 0, :]
    seed[:, -1, :] = empty[:, -1, :]
    seed[:, :, 0] = empty[:, :, 0]
    seed[:, :, -1] = empty[:, :, -1]

    border_empty = binary_propagation(seed, mask=empty)
    mask = ~border_empty

    if MASK_CLOSE_ITERATIONS > 0:
        structure = np.ones((3, 3, 3), dtype=bool)

        for _ in range(MASK_CLOSE_ITERATIONS):
            mask = binary_closing(mask, structure=structure)

    if FILL_INTERNAL_MASK_HOLES:
        mask = binary_fill_holes(mask)

    return mask.astype(bool)


# ============================================================
# ESTIMACIÓN DEL APEX 3D
# ============================================================

def robust_fit_line_col_from_row(rows, cols):
    rows = np.asarray(rows, dtype=np.float64)
    cols = np.asarray(cols, dtype=np.float64)

    if rows.size < 3:
        return None

    keep = np.ones(rows.shape, dtype=bool)

    for _ in range(SIDE_LINE_REJECTION_ITERATIONS):
        if np.sum(keep) < 3:
            break

        a, b = np.polyfit(rows[keep], cols[keep], 1)

        pred = a * rows + b
        residual = cols - pred

        med = np.median(residual[keep])
        mad = np.median(np.abs(residual[keep] - med)) + 1e-6

        threshold = SIDE_LINE_REJECTION_FACTOR * 1.4826 * mad
        new_keep = np.abs(residual - med) <= threshold

        if np.sum(new_keep) < 3:
            break

        keep = new_keep

    if np.sum(keep) < 3:
        return None

    a, b = np.polyfit(rows[keep], cols[keep], 1)

    return float(a), float(b)


def estimate_apex_from_2d_mask(mask_2d):
    mask_2d = mask_2d.astype(bool)

    if np.sum(mask_2d) < MIN_VALID_PIXELS_IN_PLANE:
        return None

    h, w = mask_2d.shape
    valid_rows = np.where(mask_2d.any(axis=1))[0]

    if valid_rows.size < 10:
        return None

    rows_used = []
    left_cols = []
    right_cols = []

    for r in valid_rows:
        cols = np.flatnonzero(mask_2d[r, :])

        if cols.size < MIN_VALID_COLUMNS:
            continue

        rows_used.append(r)
        left_cols.append(cols[0])
        right_cols.append(cols[-1])

    rows_used = np.asarray(rows_used, dtype=np.float64)
    left_cols = np.asarray(left_cols, dtype=np.float64)
    right_cols = np.asarray(right_cols, dtype=np.float64)

    if rows_used.size < 10:
        return None

    if CURVE_SMOOTH_SIGMA > 0 and rows_used.size >= 5:
        left_cols = gaussian_filter1d(
            left_cols,
            sigma=CURVE_SMOOTH_SIGMA,
            mode="nearest",
        )

        right_cols = gaussian_filter1d(
            right_cols,
            sigma=CURVE_SMOOTH_SIGMA,
            mode="nearest",
        )

    width = right_cols - left_cols
    good = width > MIN_VALID_COLUMNS

    rows_used = rows_used[good]
    left_cols = left_cols[good]
    right_cols = right_cols[good]

    if rows_used.size < 10:
        return None

    left_line = robust_fit_line_col_from_row(rows_used, left_cols)
    right_line = robust_fit_line_col_from_row(rows_used, right_cols)

    if left_line is None or right_line is None:
        return None

    a_left, b_left = left_line
    a_right, b_right = right_line

    denom = a_left - a_right

    if abs(denom) < 1e-8:
        return None

    apex_row = (b_right - b_left) / denom
    apex_col = a_left * apex_row + b_left

    if not np.isfinite(apex_row) or not np.isfinite(apex_col):
        return None

    if apex_col < -w or apex_col > 2.0 * w:
        return None

    return np.array([apex_row, apex_col], dtype=np.float32)


def estimate_apex_3d_from_mask(mask):
    if MANUAL_APEX_ZYX is not None:
        apex = np.asarray(MANUAL_APEX_ZYX, dtype=np.float32)

        if apex.shape != (3,):
            raise ValueError("MANUAL_APEX_ZYX debe tener tres valores: z0, y0, x0.")

        return apex

    projection_zx = np.any(mask, axis=1)  # z, x
    projection_zy = np.any(mask, axis=2)  # z, y

    apex_zx = estimate_apex_from_2d_mask(projection_zx)
    apex_zy = estimate_apex_from_2d_mask(projection_zy)

    fallback_z0 = -float(DIM_Z)
    fallback_y0 = 0.5 * (DIM_Y - 1)
    fallback_x0 = 0.5 * (DIM_X - 1)

    if apex_zx is not None and apex_zy is not None:
        z0 = 0.5 * (float(apex_zx[0]) + float(apex_zy[0]))
        x0 = float(apex_zx[1])
        y0 = float(apex_zy[1])

        return np.array([z0, y0, x0], dtype=np.float32)

    if apex_zx is not None:
        z0 = float(apex_zx[0])
        x0 = float(apex_zx[1])
        y0 = fallback_y0

        return np.array([z0, y0, x0], dtype=np.float32)

    if apex_zy is not None:
        z0 = float(apex_zy[0])
        y0 = float(apex_zy[1])
        x0 = fallback_x0

        return np.array([z0, y0, x0], dtype=np.float32)

    return np.array([fallback_z0, fallback_y0, fallback_x0], dtype=np.float32)


# ============================================================
# CARTESIANAS A COORDENADAS ESFÉRICAS PRÁCTICAS
# ============================================================

def cartesian_indices_to_spherical(mask, apex_zyx):
    sz, sy, sx = voxel_spacings()

    z_idx, y_idx, x_idx = np.nonzero(mask)

    z0, y0, x0 = [float(v) for v in apex_zyx]

    dz = (z_idx.astype(np.float32) - z0) * sz
    dy = (y_idx.astype(np.float32) - y0) * sy
    dx = (x_idx.astype(np.float32) - x0) * sx

    front = dz > 1e-6

    dz = dz[front]
    dy = dy[front]
    dx = dx[front]

    r = np.sqrt(dz * dz + dy * dy + dx * dx)

    good = r > 1e-6

    r = r[good]
    dz = dz[good]
    dy = dy[good]
    dx = dx[good]

    theta_x = np.arctan2(dx, dz)
    theta_y = np.arctan2(dy, dz)

    return (
        r.astype(np.float32),
        theta_y.astype(np.float32),
        theta_x.astype(np.float32),
    )


def estimate_spherical_bounds(mask, apex_zyx):
    r, theta_y, theta_x = cartesian_indices_to_spherical(mask, apex_zyx)

    if r.size < 100:
        raise ValueError(
            "No hay suficientes vóxeles válidos para estimar los límites esféricos."
        )

    r_inner = float(np.percentile(r, R_INNER_PERCENTILE))
    r_outer = float(np.percentile(r, R_OUTER_PERCENTILE))

    theta_y_min = float(np.percentile(theta_y, ANGLE_MIN_PERCENTILE))
    theta_y_max = float(np.percentile(theta_y, ANGLE_MAX_PERCENTILE))

    theta_x_min = float(np.percentile(theta_x, ANGLE_MIN_PERCENTILE))
    theta_x_max = float(np.percentile(theta_x, ANGLE_MAX_PERCENTILE))

    if r_outer <= r_inner:
        raise ValueError("r_outer debe ser mayor que r_inner.")

    if theta_y_max <= theta_y_min:
        raise ValueError("theta_y_max debe ser mayor que theta_y_min.")

    if theta_x_max <= theta_x_min:
        raise ValueError("theta_x_max debe ser mayor que theta_x_min.")

    return {
        "r_inner": r_inner,
        "r_outer": r_outer,
        "theta_y_min": theta_y_min,
        "theta_y_max": theta_y_max,
        "theta_x_min": theta_x_min,
        "theta_x_max": theta_x_max,
    }


# ============================================================
# REMAP 3D: ESFÉRICAS A CARTESIANAS
# ============================================================

def spherical_grid_to_original_indices(r_values, theta_y_values, theta_x_values, apex_zyx):
    sz, sy, sx = voxel_spacings()

    z0, y0, x0 = [float(v) for v in apex_zyx]

    R = r_values[:, None, None].astype(np.float32)
    TY = theta_y_values[None, :, None].astype(np.float32)
    TX = theta_x_values[None, None, :].astype(np.float32)

    tan_y = np.tan(TY)
    tan_x = np.tan(TX)

    denom = np.sqrt(1.0 + tan_y * tan_y + tan_x * tan_x).astype(np.float32)

    dz_phys = R / denom
    dy_phys = tan_y * dz_phys
    dx_phys = tan_x * dz_phys

    sample_z = z0 + dz_phys / sz
    sample_y = y0 + dy_phys / sy
    sample_x = x0 + dx_phys / sx

    return sample_z, sample_y, sample_x


def rectify_volume_spherical_sector(volume, mask, apex_zyx, bounds):
    r_values = np.linspace(
        bounds["r_inner"],
        bounds["r_outer"],
        OUT_DIM_R,
        dtype=np.float32,
    )

    theta_y_values = np.linspace(
        bounds["theta_y_min"],
        bounds["theta_y_max"],
        OUT_DIM_THETA_Y,
        dtype=np.float32,
    )

    theta_x_values = np.linspace(
        bounds["theta_x_min"],
        bounds["theta_x_max"],
        OUT_DIM_THETA_X,
        dtype=np.float32,
    )

    rectified = np.zeros(
        (OUT_DIM_R, OUT_DIM_THETA_Y, OUT_DIM_THETA_X),
        dtype=np.float32,
    )

    sampled_support = np.zeros_like(rectified, dtype=bool)

    mask_float = mask.astype(np.float32)

    for start in range(0, OUT_DIM_R, RADIAL_CHUNK_SIZE):
        end = min(start + RADIAL_CHUNK_SIZE, OUT_DIM_R)

        r_chunk = r_values[start:end]

        sample_z, sample_y, sample_x = spherical_grid_to_original_indices(
            r_chunk,
            theta_y_values,
            theta_x_values,
            apex_zyx,
        )

        coords = np.vstack(
            [
                sample_z.ravel(),
                sample_y.ravel(),
                sample_x.ravel(),
            ]
        ).astype(np.float32)

        values = map_coordinates(
            volume,
            coords,
            order=INTERPOLATION_ORDER,
            mode="constant",
            cval=FILL_VALUE,
            prefilter=False if INTERPOLATION_ORDER <= 1 else True,
        )

        support_values = map_coordinates(
            mask_float,
            coords,
            order=0,
            mode="constant",
            cval=0.0,
            prefilter=False,
        )

        values = values.reshape(
            end - start,
            OUT_DIM_THETA_Y,
            OUT_DIM_THETA_X,
        )

        support_values = support_values.reshape(
            end - start,
            OUT_DIM_THETA_Y,
            OUT_DIM_THETA_X,
        )

        support_chunk = support_values > 0.5

        if CUT_OUTSIDE_ORIGINAL_MASK:
            values[~support_chunk] = FILL_VALUE

        rectified[start:end, :, :] = values.astype(np.float32)
        sampled_support[start:end, :, :] = support_chunk

        print(f"Remap esférico: radios {start + 1}-{end}/{OUT_DIM_R}")

    if CUT_OUTSIDE_ORIGINAL_MASK:
        rectified_mask = sampled_support
    else:
        rectified_mask = np.ones_like(sampled_support, dtype=bool)

    return rectified, rectified_mask, sampled_support


# ============================================================
# RECORTE Y COMPLETADO DEL ORTOEDRO
# ============================================================

def crop_rectified_volume(volume, mask, support):
    r_slice = crop_slice(volume.shape[0], CROP_R)
    y_slice = crop_slice(volume.shape[1], CROP_THETA_Y)
    x_slice = crop_slice(volume.shape[2], CROP_THETA_X)

    volume_c = volume[r_slice, y_slice, x_slice].copy()
    mask_c = mask[r_slice, y_slice, x_slice].copy()
    support_c = support[r_slice, y_slice, x_slice].copy()

    return volume_c, mask_c, support_c


def fill_invalid_with_nearest(volume, valid_mask):
    volume = volume.astype(np.float32).copy()
    valid_mask = valid_mask.astype(bool)

    if np.all(valid_mask):
        return volume

    if not np.any(valid_mask):
        return np.zeros_like(volume, dtype=np.float32)

    invalid = ~valid_mask

    _, indices = distance_transform_edt(
        invalid,
        return_distances=True,
        return_indices=True,
    )

    filled = volume.copy()

    filled[invalid] = volume[
        indices[0][invalid],
        indices[1][invalid],
        indices[2][invalid],
    ]

    return filled.astype(np.float32)


def smooth_only_filled_voxels(volume, original_valid_mask, sigma):
    volume = volume.astype(np.float32)
    original_valid_mask = original_valid_mask.astype(bool)

    smoothed = gaussian_filter(
        volume,
        sigma=sigma,
        mode="nearest",
    ).astype(np.float32)

    out = volume.copy()
    filled_voxels = ~original_valid_mask
    out[filled_voxels] = smoothed[filled_voxels]

    return out.astype(np.float32)


def crop_and_complete_orthohedron(rectified, rectified_mask, sampled_support):
    rectified_c, rectified_mask_c, sampled_support_c = crop_rectified_volume(
        rectified,
        rectified_mask,
        sampled_support,
    )

    original_valid_mask = sampled_support_c.copy()

    if FILL_HOLES_AFTER_CROP:
        rectified_c = fill_invalid_with_nearest(
            rectified_c,
            original_valid_mask,
        )

        if SMOOTH_FILLED_HOLES:
            rectified_c = smooth_only_filled_voxels(
                rectified_c,
                original_valid_mask,
                sigma=FILLED_HOLES_SMOOTH_SIGMA,
            )

    final_mask_c = np.ones_like(rectified_c, dtype=bool)

    return (
        rectified_c.astype(np.float32),
        final_mask_c,
        original_valid_mask,
    )


# ============================================================
# HISTOGRAM MATCHING PARA UNITY
# ============================================================

def histogram_match_values(source_values, reference_values):
    source_values = np.asarray(source_values, dtype=np.float32)
    reference_values = np.asarray(reference_values, dtype=np.float32)

    source_unique, source_counts = np.unique(source_values, return_counts=True)
    reference_unique, reference_counts = np.unique(reference_values, return_counts=True)

    source_quantiles = np.cumsum(source_counts).astype(np.float64)
    source_quantiles /= source_quantiles[-1]

    reference_quantiles = np.cumsum(reference_counts).astype(np.float64)
    reference_quantiles /= reference_quantiles[-1]

    source_cdf = np.interp(
        source_values,
        source_unique,
        source_quantiles,
        left=0.0,
        right=1.0,
    )

    matched = np.interp(
        source_cdf,
        reference_quantiles,
        reference_unique,
    )

    return matched.astype(np.float32)


def save_uint16_matched_to_original_for_unity(
    rectified_volume,
    original_volume,
    rectified_valid_mask,
    original_mask,
    output_path,
):
    rectified_volume = rectified_volume.astype(np.float32)
    original_volume = original_volume.astype(np.float32)

    rectified_valid_mask = rectified_valid_mask.astype(bool)
    original_mask = original_mask.astype(bool)

    source_valid = (
        np.isfinite(rectified_volume)
        & rectified_valid_mask
        & (rectified_volume > EMPTY_THRESHOLD)
    )

    reference_valid = (
        np.isfinite(original_volume)
        & original_mask
        & (original_volume > EMPTY_THRESHOLD)
    )

    if np.sum(source_valid) < 100:
        raise ValueError("No hay suficientes vóxeles válidos en el volumen rectificado.")

    if np.sum(reference_valid) < 100:
        raise ValueError("No hay suficientes vóxeles válidos en el volumen original.")

    source_values = rectified_volume[source_valid]
    reference_values = original_volume[reference_valid]

    source_low = float(np.percentile(source_values, HIST_SOURCE_LOW_PERCENTILE))
    source_high = float(np.percentile(source_values, HIST_SOURCE_HIGH_PERCENTILE))

    reference_low = float(np.percentile(reference_values, HIST_REFERENCE_LOW_PERCENTILE))
    reference_high = float(np.percentile(reference_values, HIST_REFERENCE_HIGH_PERCENTILE))

    if source_high <= source_low:
        source_high = source_low + 1.0

    if reference_high <= reference_low:
        reference_high = reference_low + 1.0

    source_values_clipped = np.clip(source_values, source_low, source_high)
    reference_values_clipped = np.clip(reference_values, reference_low, reference_high)

    matched_values = histogram_match_values(
        source_values_clipped,
        reference_values_clipped,
    )

    matched_volume = np.zeros_like(rectified_volume, dtype=np.float32)
    matched_volume[source_valid] = matched_values
    matched_volume[~source_valid] = 0.0

    matched_norm = (matched_volume - reference_low) / (reference_high - reference_low)
    matched_norm = np.clip(matched_norm, 0.0, 1.0)
    matched_norm[~source_valid] = 0.0

    volume_uint16 = np.round(matched_norm * 65535.0).astype(np.uint16)
    volume_uint16[~source_valid] = 0

    volume_uint16.tofile(output_path)

    valid_uint16 = volume_uint16[source_valid]

    hist_stats = {
        "sourceLowPercentile": float(HIST_SOURCE_LOW_PERCENTILE),
        "sourceHighPercentile": float(HIST_SOURCE_HIGH_PERCENTILE),
        "referenceLowPercentile": float(HIST_REFERENCE_LOW_PERCENTILE),
        "referenceHighPercentile": float(HIST_REFERENCE_HIGH_PERCENTILE),
        "sourceLowValue": float(source_low),
        "sourceHighValue": float(source_high),
        "referenceLowValue": float(reference_low),
        "referenceHighValue": float(reference_high),
        "uint16Min": int(volume_uint16.min()),
        "uint16Max": int(volume_uint16.max()),
        "uint16ValidP50": float(np.percentile(valid_uint16, 50)),
        "uint16ValidP90": float(np.percentile(valid_uint16, 90)),
        "uint16ValidP99": float(np.percentile(valid_uint16, 99)),
        "uint16ValidP999": float(np.percentile(valid_uint16, 99.9)),
    }

    print("RAW UInt16 con histograma igualado:", output_path)
    print("source_low:", source_low)
    print("source_high:", source_high)
    print("reference_low:", reference_low)
    print("reference_high:", reference_high)
    print("uint16 min:", hist_stats["uint16Min"])
    print("uint16 max:", hist_stats["uint16Max"])
    print("uint16 p50:", hist_stats["uint16ValidP50"])
    print("uint16 p90:", hist_stats["uint16ValidP90"])
    print("uint16 p99:", hist_stats["uint16ValidP99"])
    print("uint16 p99.9:", hist_stats["uint16ValidP999"])

    return hist_stats


# ============================================================
# VISUALIZADOR
# ============================================================

def get_view(volume, axis, index):
    if axis == "z":
        index = int(np.clip(index, 0, volume.shape[0] - 1))
        return volume[index, :, :]

    if axis == "y":
        index = int(np.clip(index, 0, volume.shape[1] - 1))
        return volume[:, index, :]

    if axis == "x":
        index = int(np.clip(index, 0, volume.shape[2] - 1))
        return volume[:, :, index]

    raise ValueError("axis debe ser 'z', 'y' o 'x'.")


def axis_max(volume, axis):
    if axis == "z":
        return volume.shape[0] - 1

    if axis == "y":
        return volume.shape[1] - 1

    if axis == "x":
        return volume.shape[2] - 1

    raise ValueError("axis debe ser 'z', 'y' o 'x'.")


def axis_mid(volume, axis):
    return axis_max(volume, axis) // 2


def original_axis_name(axis):
    if axis == "z":
        return "z original"

    if axis == "y":
        return "y original"

    if axis == "x":
        return "x original"

    return axis


def rectified_axis_name(axis):
    if axis == "z":
        return "r"

    if axis == "y":
        return "theta_y"

    if axis == "x":
        return "theta_x"

    return axis


def display_limits(volume, mask):
    values = volume[mask]
    values = values[values > EMPTY_THRESHOLD]

    if values.size < 10:
        values = volume[volume > EMPTY_THRESHOLD]

    if values.size < 10:
        values = volume[np.isfinite(volume)]

    vmin = float(np.percentile(values, 1))
    vmax = float(np.percentile(values, 99))

    if vmax <= vmin:
        vmax = vmin + 1.0

    return vmin, vmax


def viewer_spherical(before, before_mask, rectified, rectified_mask, sampled_support):
    axis = INITIAL_AXIS if INITIAL_AXIS in ["x", "y", "z"] else "z"

    before_idx = axis_mid(before, axis)
    rectified_idx = axis_mid(rectified, axis)

    vmin, vmax = display_limits(before, before_mask)

    fig, axs = plt.subplots(2, 3, figsize=(15, 8))
    plt.subplots_adjust(left=0.20, bottom=0.18)

    ax_before = axs[0, 0]
    ax_before_mask = axs[0, 1]
    ax_support = axs[0, 2]

    ax_rectified = axs[1, 0]
    ax_rectified_mask = axs[1, 1]
    ax_empty = axs[1, 2]

    im_before = ax_before.imshow(
        get_view(before, axis, before_idx),
        cmap="gray",
        vmin=vmin,
        vmax=vmax,
    )

    im_before_mask = ax_before_mask.imshow(
        get_view(before_mask, axis, before_idx),
        cmap="gray",
        vmin=0,
        vmax=1,
    )

    im_support = ax_support.imshow(
        get_view(sampled_support, axis, rectified_idx),
        cmap="gray",
        vmin=0,
        vmax=1,
    )

    im_rectified = ax_rectified.imshow(
        get_view(rectified, axis, rectified_idx),
        cmap="gray",
        vmin=vmin,
        vmax=vmax,
    )

    im_rectified_mask = ax_rectified_mask.imshow(
        get_view(rectified_mask, axis, rectified_idx),
        cmap="gray",
        vmin=0,
        vmax=1,
    )

    ax_empty.axis("off")

    for ax in axs.ravel():
        ax.axis("off")

    state = {
        "axis": axis,
        "before_index": before_idx,
        "rectified_index": rectified_idx,
    }

    slider_ax = plt.axes([0.28, 0.07, 0.60, 0.035])
    slider = Slider(
        ax=slider_ax,
        label="Índice rectificado",
        valmin=0,
        valmax=axis_max(rectified, axis),
        valinit=rectified_idx,
        valstep=1,
    )

    radio_ax = plt.axes([0.03, 0.35, 0.14, 0.20])
    active = {"z": 0, "y": 1, "x": 2}[axis]

    radio = RadioButtons(
        radio_ax,
        ("z / r", "y / theta_y", "x / theta_x"),
        active=active,
    )

    def update_titles():
        a = state["axis"]
        bi = state["before_index"]
        ri = state["rectified_index"]

        ax_before.set_title(f"Original | eje {original_axis_name(a)} | índice {bi}")
        ax_before_mask.set_title(f"Máscara original | eje {original_axis_name(a)} | índice {bi}")
        ax_support.set_title(f"Soporte original tras recorte | eje {rectified_axis_name(a)} | índice {ri}")
        ax_rectified.set_title(f"Rectificado esférico final | eje {rectified_axis_name(a)} | índice {ri}")
        ax_rectified_mask.set_title(f"Máscara final ortoedro | eje {rectified_axis_name(a)} | índice {ri}")

    def update_images():
        a = state["axis"]

        ri = int(np.clip(state["rectified_index"], 0, axis_max(rectified, a)))
        state["rectified_index"] = ri

        max_before = axis_max(before, a)
        max_rectified = axis_max(rectified, a)

        if max_rectified > 0:
            bi = int(round((ri / max_rectified) * max_before))
        else:
            bi = 0

        bi = int(np.clip(bi, 0, max_before))
        state["before_index"] = bi

        im_before.set_data(get_view(before, a, bi))
        im_before_mask.set_data(get_view(before_mask, a, bi))
        im_support.set_data(get_view(sampled_support, a, ri))
        im_rectified.set_data(get_view(rectified, a, ri))
        im_rectified_mask.set_data(get_view(rectified_mask, a, ri))

        update_titles()
        fig.canvas.draw_idle()

    def on_slider(value):
        state["rectified_index"] = int(value)
        update_images()

    def on_radio(label):
        if label.startswith("z"):
            a = "z"
        elif label.startswith("y"):
            a = "y"
        else:
            a = "x"

        state["axis"] = a
        state["rectified_index"] = axis_mid(rectified, a)

        slider.valmax = axis_max(rectified, a)
        slider.ax.set_xlim(0, slider.valmax)
        slider.set_val(state["rectified_index"])

        update_images()

    slider.on_changed(on_slider)
    radio.on_clicked(on_radio)

    update_images()
    plt.show()


# ============================================================
# MAIN
# ============================================================

def main():
    print("Cargando volumen original...")
    volume = load_volume()

    print("Shape original:", volume.shape)
    print("Min original:", float(np.min(volume)))
    print("Max original:", float(np.max(volume)))

    print("Calculando máscara original...")
    mask = make_valid_mask(volume)

    print("Vóxeles válidos originales:", int(np.sum(mask)))
    print("Porcentaje válido original:", float(np.mean(mask) * 100.0), "%")

    print("Estimando apex 3D...")
    apex_zyx = estimate_apex_3d_from_mask(mask)

    print("Apex estimado en índices ZYX:")
    print("z0:", float(apex_zyx[0]))
    print("y0:", float(apex_zyx[1]))
    print("x0:", float(apex_zyx[2]))

    print("Estimando límites esféricos...")
    bounds = estimate_spherical_bounds(mask, apex_zyx)

    print("Límites antes del recorte:")
    print("r_inner:", bounds["r_inner"])
    print("r_outer:", bounds["r_outer"])
    print("theta_y_min:", bounds["theta_y_min"])
    print("theta_y_max:", bounds["theta_y_max"])
    print("theta_x_min:", bounds["theta_x_min"])
    print("theta_x_max:", bounds["theta_x_max"])

    print("Rectificando volumen como sector esférico 3D...")
    rectified, rectified_mask, sampled_support = rectify_volume_spherical_sector(
        volume,
        mask,
        apex_zyx,
        bounds,
    )

    print("Shape rectificado antes del recorte:", rectified.shape)
    print("Soporte antes del recorte:", int(np.sum(sampled_support)))
    print("Porcentaje de soporte antes del recorte:", float(np.mean(sampled_support) * 100.0), "%")

    print("Recortando bordes y completando ortoedro...")
    final_rectified, final_mask, cropped_support = crop_and_complete_orthohedron(
        rectified,
        rectified_mask,
        sampled_support,
    )

    print("Shape final:", final_rectified.shape)
    print("Soporte original tras recorte:", int(np.sum(cropped_support)))
    print("Porcentaje de soporte original tras recorte:", float(np.mean(cropped_support) * 100.0), "%")

    print("Guardando volumen float32 final...")
    save_volume_float32(final_rectified)

    print("Guardando metadata float32...")
    save_metadata_float32(apex_zyx, bounds, final_rectified.shape)

    if SAVE_UINT16_MATCHED_FOR_UNITY:
        print("Guardando versión UInt16 con histograma igualado al original...")

        hist_stats = save_uint16_matched_to_original_for_unity(
            rectified_volume=final_rectified,
            original_volume=volume,
            rectified_valid_mask=final_mask,
            original_mask=mask,
            output_path=OUTPUT_RAW_UINT16_PATH,
        )

        print("Guardando metadata UInt16...")
        save_metadata_uint16(
            apex_zyx,
            bounds,
            final_rectified.shape,
            hist_stats,
        )

    print("Salida float32:")
    print("RAW:", OUTPUT_RAW_FLOAT32_PATH)
    print("JSON:", OUTPUT_FLOAT32_JSON_PATH)

    if SAVE_UINT16_MATCHED_FOR_UNITY:
        print("Salida UInt16 Unity:")
        print("RAW:", OUTPUT_RAW_UINT16_PATH)
        print("JSON:", OUTPUT_UINT16_JSON_PATH)

    print("Final min:", float(np.min(final_rectified)))
    print("Final max:", float(np.max(final_rectified)))

    if SHOW_VIEWER:
        viewer_spherical(
            before=volume,
            before_mask=mask,
            rectified=final_rectified,
            rectified_mask=final_mask,
            sampled_support=cropped_support,
        )


if __name__ == "__main__":
    main()