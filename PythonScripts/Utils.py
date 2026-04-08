import sys
import cv2
import os
from datetime import datetime
import base64
import tempfile
import time
import tkinter as tk

# CONFIG
path_fallback = os.path.dirname(__file__)
path_primary = os.path.dirname(__file__) # same path by default (for app demo), but can be overridden in production

# TIME
def get_current_timestamp():
    return int(time.time() * 1000)
# ---------------

# BASE IMAGE FORMATTING
def rotate_image(image, angle):
    angle = -angle
    (h, w) = image.shape[:2]
    center = (w / 2.0, h / 2.0)
    M = cv2.getRotationMatrix2D(center, angle, 1.0)
    abs_cos = abs(M[0, 0])
    abs_sin = abs(M[0, 1])
    new_w = int(h * abs_sin + w * abs_cos)
    new_h = int(h * abs_cos + h * abs_sin)
    M[0, 2] += new_w / 2.0 - center[0]
    M[1, 2] += new_h / 2.0 - center[1]
    rotated = cv2.warpAffine(image, M, (new_w, new_h), flags=cv2.INTER_CUBIC, borderMode=cv2.BORDER_CONSTANT)
    return rotated

def scale_inspection_props(args, image):
    height, width = image.shape[:2]
    
    scale_x = width / safe_float(args.get("dimensionX", 0.0))
    scale_y = height / safe_float(args.get("dimensionY", 0.0))

    scaled_x = safe_float(args.get("inspectionX", 0.0)) * scale_x
    scaled_y = safe_float(args.get("inspectionY", 0.0)) * scale_y
    scaled_w = safe_float(args.get("inspectionWidth", 0.0)) * scale_x
    scaled_h = safe_float(args.get("inspectionHeight", 0.0)) * scale_y

    # Boundaries korrekcio
    if scale_x < 0:
        scaled_x = 0
    if scale_y < 0:
        scaled_y = 0
    if scaled_x + scaled_w > width:
        scaled_w = width - scaled_x
    if scaled_y + scaled_h > height:
        scaled_h = height - scaled_y

    return scaled_x, scaled_y, scaled_w, scaled_h

def safe_float(value):
    if isinstance(value, str):
        value = value.replace(",", ".").strip()
    try:
        return float(value)
    except ValueError:
        raise ValueError(f"Cannot convert '{value}' to float")

def crop_with_offset(image, args, offset=0):
    scaled_x, scaled_y, scaled_w, scaled_h = scale_inspection_props(args, image)

    x = safe_float(scaled_x)
    y = safe_float(scaled_y)
    w = safe_float(scaled_w)
    h = safe_float(scaled_h)

    # offset_x0 = safe_float(args.get("offsetX", 0.0))
    # offset_0 = safe_float(args.get("offsetY", 0.0))
    # p0 = safe_float(args.get("p0", 0.0))
    # p1 = safe_float(args.get("p1", 0.0))
    # alpha = abs(p1 - p0) * 360 / 310
    # quarter =  math.floor(alpha / 90) + 1

    # offset_x1 = math.sin(math.radians(alpha)) * offset_x0
    # offset_y1 = math.cos(math.radians(alpha)) * offset_0

    # if quarter == 1:
    #     offset_x1 *= -1
    #     offset_y1 *= 1
    # elif quarter == 2:
    #     offset_x1 *= 1
    #     offset_y1 *= -1
    # elif quarter == 3:
    #     offset_x1 *= 1
    #     offset_y1 *= -1
    # elif quarter == 4:
    #     offset_x1 *= -1
    #     offset_y1 *= 1
        
    # x += offset_x1
    # y += offset_y1

    height, width = image.shape[:2]
    
    x = int(safe_float(x))
    y = int(safe_float(y))
    w = int(safe_float(w))
    h = int(safe_float(h))
    offset = int(safe_float(offset))

    x1 = max(0, x - offset)
    y1 = max(0, y - offset)
    x2 = min(width, x + w + offset)
    y2 = min(height, y + h + offset)

    rotation = safe_float(args.get("rotation", 0))
    image = rotate_image(image, rotation)
    cropped = image[y1:y2, x1:x2]
    return cropped
    
def is_base64_image(value: str) -> bool:
    return (
        isinstance(value, str)
        and (value.startswith("data:image") or value.startswith("/9j"))
    )

def base64_to_temp_image(b64: str, suffix=".jpg") -> str:
    if b64.startswith("data:image"):
        b64 = b64.split(",", 1)[1]

    data = base64.b64decode(b64)
    fd, path = tempfile.mkstemp(suffix=suffix)
    with os.fdopen(fd, "wb") as f:
        f.write(data)
    return path

def cleanup(path: str | None):
    if path and os.path.exists(path):
        try:
            os.remove(path)
        except:
            pass
# ---------------

# PATHS AND NAMES
def resolve_base_path(is_prod: bool, caller_file: str):
    base = os.path.join(path_primary, caller_file.split("\\")[-1].split(".")[0]+"Images")
    if is_prod and os.path.exists(base):
        return base
    return path_fallback

def dated_dir(base, name):
    d = os.path.join(base, name, datetime.now().strftime("%Y%m%d"))
    os.makedirs(d, exist_ok=True)
    return d

def build_paths(base):
    return {
        "good": dated_dir(base, "good"),
        "bad": dated_dir(base, "bad"),
        "debug": dated_dir(base, "debug"),
        "prediction_log": os.path.join(base, "predictions.log"),
        "conversion_log": os.path.join(base, "debug", "conversion.log"),
    }
# ---------------

# SAVE DEBUG IMAGES
def save_debug_images(paths, guid, original, cropped):
    orig = os.path.join(paths["debug"], f"{guid}_orig.jpg")
    crop = os.path.join(paths["debug"], f"{guid}_crop.jpg")
    cv2.imwrite(orig, original)
    cv2.imwrite(crop, cropped)
    return orig, crop

def save_result_image(paths, guid, image, ok):
    folder = paths["good"] if ok else paths["bad"]
    path = os.path.join(folder, f"{guid}.jpg")
    cv2.imwrite(path, image)
    return path
# ---------------

# GENERATE LOG FILES
def log_conversion(args, guid, CONVERT_LOG_FILE):
    try:
        cmd = "python " + __file__ + " `\n".join(f"--{k} {v}" for k, v in args.items())
        with open(CONVERT_LOG_FILE, "a") as f:
            f.write(
                f"{datetime.now().strftime('%Y-%m-%d %H:%M:%S')}\n"
                f"IMG={args.get('originalImage')} | guid={guid} | "
                f"offsetX={args.get('offsetX')}, offsetY={args.get('offsetY')}, rotation={args.get('rotation')} | "
                f"p0={args.get('p0', 0.0)}, t0={args.get('t0', 0.0)}, z0={args.get('z0', '0')}, "
                f"p1={args.get('p1', 0.0)}, t1={args.get('t1', 0.0)}, z1={args.get('z1', '0')} | \n"
                f"{cmd}\n\n"
            )
    except Exception as e:
        print(f"Convert log write failed: {e}", file=sys.stderr, flush=True)

def log_prediction(path, value, threshold, ok, log_file):
    with open(log_file, "a") as f:
        f.write(
            f"{datetime.now()} | {path} | "
            f"value={value} | threshold={threshold} | "
            f"{'GOOD' if ok else 'BAD'}\n"
        )
# ---------------

# PROGRESS
def emit_progress(done, total):
    print(f"@@PROGRESS@@ {done} {total}", file=sys.stderr, flush=True)
# ---------------

# SHOW RESULTS
def show_grouped_results(items, window_title="Inspection Results"):
    if not items:
        return

    # --- GROUP BY IMAGE ---
    grouped = {}
    for item in items:
        path = item["imagePath"]
        grouped.setdefault(path, []).append(item)

    root = tk.Tk()
    screen_w = root.winfo_screenwidth()
    screen_h = root.winfo_screenheight()
    root.destroy()

    for path, inspections in grouped.items():
        img = cv2.imread(path)
        if img is None:
            continue

        # --- DRAW INSPECTION ROI ---
        for insp in inspections:
            x, y = int(insp["x"]), int(insp["y"])
            w, h = int(insp["w"]), int(insp["h"])

            result = insp["result"]
            text = insp.get("text", "")

            color = (0, 255, 0) if result else (0, 0, 255)

            # ROI
            cv2.rectangle(img, (x, y), (x + w, y + h), color, 3)

            # LABEL
            label = "OK" if result else "NOK"
            full_text = f"{label} | {text}"

            cv2.putText(
                img,
                full_text,
                (x, max(25, y - 10)),
                cv2.FONT_HERSHEY_SIMPLEX,
                1.4,
                color,
                5,
                cv2.LINE_AA
            )

        # --- CALCULATE WINDOWSIZE ---
        h, w = img.shape[:2]

        scale = min((screen_w * 0.9) / w, (screen_h * 0.9) / h)
        resized = cv2.resize(img, (int(w * scale), int(h * scale)))

        title = f"{window_title} - {os.path.basename(path)}"

        cv2.namedWindow(title, cv2.WINDOW_NORMAL)

        x_pos = int((screen_w - resized.shape[1]) / 2)
        y_pos = 0

        cv2.moveWindow(title, x_pos, y_pos)
        cv2.resizeWindow(title, resized.shape[1], resized.shape[0])

        cv2.imshow(title, resized)
        cv2.waitKey(0)
        cv2.destroyAllWindows()