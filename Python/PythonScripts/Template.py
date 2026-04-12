import sys
import os
import json
import cv2
import Utils

# --- CONFIG ---
THRESHOLD = 0.85

def process_one(item):
    guid = item.get("logName")

    base_path = Utils.resolve_base_path(item.get("isProd", False), __file__)
    paths = Utils.build_paths(base_path)

    image_path = item.get("originalImage")
    template_path = item.get("templateImage")
    if not os.path.exists(image_path):
        raise ValueError(f"Image not found: {image_path}")
    if not os.path.exists(template_path):
        raise ValueError(f"Template image file not found: {template_path}")

    image = cv2.imread(image_path)
    template = cv2.imread(template_path)
    if image is None:
        raise ValueError("Cannot load image")
    if template is None:
        raise ValueError(f"Cannot load template image")

    cropped = Utils.crop_with_offset(image, item, item.get("inspectionOffset", 0))

    # debug
    orig_dbg, crop_dbg = Utils.save_debug_images(paths, guid, image, cropped)
    item["originalImage"] = orig_dbg
    item["isProd"] = False
    Utils.log_conversion(item, guid, paths["conversion_log"])

    # --- Compare images ---
    try:
        result_map = cv2.matchTemplate(cropped, template, cv2.TM_CCOEFF_NORMED)
        _, max_val, _, _ = cv2.minMaxLoc(result_map)
        found = max_val > THRESHOLD
    except Exception as e:
        raise ValueError(f"Template matching failed: {e}")

    saved_path = Utils.save_result_image(paths, guid, cropped, found)
    Utils.log_prediction(saved_path, max_val*100.0, 0.0, found, paths["prediction_log"])

    return {
        "inspectionId": item.get("inspectionId"),
        "success": True,
        "result": found,
        "score": max_val*100.0,
        "value": found,
        "insertDate": Utils.get_current_timestamp()
    }

def main():    
    raw = sys.stdin.readline()
    payload = json.loads(raw)

    items = payload.get("batch", [])
    total = len(items)

    if total == 0:
        print(json.dumps({"success": False, "error": "No items"}), flush=True)
        return

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
            res = process_one(item)
        except Exception as e:
            res = {
                "inspectionId": item.get("inspectionId"),
                "success": False,
                "result": False,
                "score": 0.0,
                "value": "",
                "insertDate": Utils.get_current_timestamp(),
                "error": str(e)
            }

        results.append(res)
        done += 1
        Utils.emit_progress(done, total)

    print(json.dumps({
        "success": True,
        "results": results
    }, ensure_ascii=False), flush=True)

if __name__ == "__main__":
    main()
