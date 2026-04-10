import sys
import os
import json
import cv2
import numpy as np
from tensorflow.keras.models import load_model
import Utils
os.environ["TF_CPP_MIN_LOG_LEVEL"] = "2"

# --- CONFIG ---
MODEL_PATH = os.path.join(os.path.dirname(sys.executable), "ScrewTorque_model.h5")
IMG_SIZE = (224, 224)
CROP_SIZE = 100
STRIDE = CROP_SIZE // 2
THRESHOLD = 0.6

def sliding_windows(image, window_size, stride):
    h, w = image.shape[:2]
    if h < CROP_SIZE or w < CROP_SIZE:
        return []
    
    rois = []

    ys = sliding_positions(h, window_size, stride)
    xs = sliding_positions(w, window_size, stride)

    for y in ys:
        for x in xs:
            roi = image[y:y+window_size, x:x+window_size]
            rois.append((x, y, roi))

    return rois

def sliding_positions(length, win, step):
    if length <= win:
        return [0]

    positions = list(range(0, length - win + 1, step))

    last = length - win
    if positions[-1] != last:
        positions.append(last)

    return positions

def save_sliding_roi(paths, guid, x, y, score, roi):
    if roi is None or roi.size == 0:
        return
    label = "positive" if score > THRESHOLD else "negative"

    base_dir = os.path.join(
        paths["debug"],
        "sliding",
        label,
        guid
    )
    os.makedirs(base_dir, exist_ok=True)

    filename = f"x{x}_y{y}_s{score*100.0:.3f}.jpg"
    cv2.imwrite(os.path.join(base_dir, filename), roi)

# 1 kephez elofeldolgozas
def preprocess_for_model(image):
    """Resize + RGB konverzió + normalize for model input."""
    resized = cv2.resize(image, IMG_SIZE)
    rgb = cv2.cvtColor(resized, cv2.COLOR_BGR2RGB)
    return np.expand_dims(rgb, axis=0) / 255.0

# batch kep hivashoz
def preprocess_for_model_batch(images):
    processed = []
    for img in images:
        resized = cv2.resize(img, IMG_SIZE)
        rgb = cv2.cvtColor(resized, cv2.COLOR_BGR2RGB)
        processed.append(rgb)

    return np.array(processed, dtype=np.float32) / 255.0

def process_one(item, model):
    guid = item.get("logName")

    base_path = Utils.resolve_base_path(item.get("isProd", False), __file__)
    paths = Utils.build_paths(base_path)

    image_path = item.get("originalImage")
    if not os.path.exists(image_path):
        raise ValueError(f"Image not found: {image_path}")

    image = cv2.imread(image_path)
    if image is None:
        raise ValueError("Cannot load image")

    cropped = Utils.crop_with_offset(image, item, item.get("inspectionOffset", 0))

    # debug
    # orig_dbg, crop_dbg = Utils.save_debug_images(paths, guid, image, cropped)
    # item["originalImage"] = orig_dbg
    # item["isProd"] = False
    # Utils.log_conversion(item, guid, paths["conversion_log"])

    # Sliding window and predict
    windows = sliding_windows(cropped, CROP_SIZE, STRIDE)

    best_score = 0.0
    best_roi = None
    use_sliding = len(windows) > 0
    
    if use_sliding:
        rois = [roi for _, _, roi in windows]
        batch_np = preprocess_for_model_batch(rois)
    
        preds = model.predict(batch_np, verbose=0).reshape(-1)

        for (x, y, roi), score in zip(windows, preds):
            score = float(score)

            # save_sliding_roi(paths, guid, x, y, score, roi)

            if score > best_score:
                best_score = score
                best_roi = roi
    else:
        # --- FALLBACK: teljes cropped kep predikcio ---
        input_np = preprocess_for_model(cropped)
        best_score = float(model.predict(input_np, verbose=0)[0][0])
        best_roi = cropped

    ok = best_score > THRESHOLD
    final_score = best_score * 100.0

    if best_roi is None or best_roi.size == 0:
        raise ValueError("No ROI selected")

    # saved_path = Utils.save_result_image(paths, guid, best_roi, ok)
    # Utils.log_prediction(saved_path, final_score, THRESHOLD, ok, paths["prediction_log"])

    scaled_x, scaled_y, scaled_w, scaled_h = Utils.scale_inspection_props(item, image)
    visual_x = int(scaled_x - item.get("offset", 0))
    visual_y = int(scaled_y - item.get("offset", 0))

    if visual_x < 0:
        visual_x = 0
    if visual_y < 0:
        visual_y = 0

    return {
        "inspectionId": item.get("inspectionId"),
        "success": True,
        "result": ok,
        "score": final_score,
        "value": ok,
        "insertDate": Utils.get_current_timestamp(),
        "visual": {
            "imagePath": image_path,
            "x": visual_x,
            "y": visual_y,
            "w": cropped.shape[1],
            "h": cropped.shape[0],
            "result": ok,
            "text": f"{final_score:.1f}%"
        }
    }

def main():    
    raw = sys.stdin.readline()
    payload = json.loads(raw)

    items = payload.get("batch", [])
    total = len(items)

    if total == 0:
        print(json.dumps({"success": False, "error": "No items"}), flush=True)
        return

    model = load_model(MODEL_PATH)

    results = []
    done = 0

    for item in items:
        try:
            item["offsetX"] = payload.get("offsetX", 0.0)
            item["offsetY"] = payload.get("offsetY", 0.0)
            item["rotation"] = payload.get("rotation", 0.0)
            item["p0"] = payload.get("p0", 0)
            item["t0"] = payload.get("t0", 0)
            item["z0"] = payload.get("z0", "0")
            item["isProd"] = payload.get("isProd", False)
            res = process_one(item, model)
        except Exception as e:
            res = {
                "inspectionId": item.get("inspectionId"),
                "success": False,
                "result": False,
                "score": 0.0,
                "value": False,
                "insertDate": Utils.get_current_timestamp(),
                "error": str(e)
            }

        results.append(res)
        done += 1
        Utils.emit_progress(done, total)

    visuals = [r["visual"] for r in results if r.get("success")]
    Utils.show_grouped_results(visuals, "ScrewTorque Inspection Results")

    print(json.dumps({
        "success": True,
        "results": results
    }, ensure_ascii=False), flush=True)

if __name__ == "__main__":
    main()
