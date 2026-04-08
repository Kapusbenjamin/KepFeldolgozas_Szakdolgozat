import sys
import os
import json
import cv2
import numpy as np
import Utils

# --- CONFIG ---
TARGET_ID = 10

def detect_aruco_pose(img):
    if img is None:
        raise ValueError(f"Cannot load image: {img}")

    gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)
    gray = cv2.GaussianBlur(gray, (3, 3), 0)
    gray = cv2.equalizeHist(gray)

    aruco_dict = cv2.aruco.getPredefinedDictionary(cv2.aruco.DICT_4X4_1000)

    if hasattr(cv2.aruco, 'ArucoDetector'):
        parameters = cv2.aruco.DetectorParameters()
        detector = cv2.aruco.ArucoDetector(aruco_dict, parameters)
        corners, ids, _ = detector.detectMarkers(gray)
    else:
        parameters = cv2.aruco.DetectorParameters_create()
        corners, ids, _ = cv2.aruco.detectMarkers(gray, aruco_dict, parameters=parameters)

    if ids is None or len(ids) == 0:
        raise ValueError("No ArUco markers detected")

    results = {}
    for i, corner in enumerate(corners):
        marker_id = int(ids[i][0])
        if marker_id != TARGET_ID:
            continue

        pts = corner[0]

        # Középpont és szög
        center_x = Utils.safe_float(np.mean(pts[:, 0]))
        center_y = Utils.safe_float(np.mean(pts[:, 1]))
        dx = pts[1][0] - pts[0][0]
        dy = pts[1][1] - pts[0][1]
        angle = np.degrees(np.arctan2(dy, dx))

        # Méret (szélesség, magasság)
        width = np.linalg.norm(pts[1] - pts[0])
        height = np.linalg.norm(pts[2] - pts[1])
        aspect_ratio = Utils.safe_float(width / height if height != 0 else 0)

        # Torzítás / dőlés (paralelogramma-hatás)
        # diag1 = np.linalg.norm(pts[0] - pts[2])
        # diag2 = np.linalg.norm(pts[1] - pts[3])
        # skew = safe_float(abs(diag1 - diag2))

        results[int(ids[i][0])] = {
            "center_x": center_x,
            "center_y": center_y,
            "rotation_deg": Utils.safe_float(angle),
            # "width": width,
            # "height": height,
            # "aspect_ratio": aspect_ratio,
            # "skew": skew,
            "corners": [
                {"x": Utils.safe_float(x), "y": Utils.safe_float(y)} for (x, y) in pts
            ]
        }
    return results

def compare_markers(template_markers, image_markers):
    diffs = []
    for marker_id, tmpl in template_markers.items():
        if marker_id not in image_markers:
            continue

        img = image_markers[marker_id]

        dx = img["center_x"] - tmpl["center_x"]
        dy = img["center_y"] - tmpl["center_y"]
        d_angle = img["rotation_deg"] - tmpl["rotation_deg"]

        d_angle = (d_angle + 180) % 360 - 180

        diffs.append({
            "id": marker_id,
            "dx": dx,
            "dy": dy,
            "rotation": d_angle,
            "template": tmpl,
            "image": img
        })

    if not diffs:
        raise ValueError("No matching markers found between template and image")

    return {"dx": diffs[0]["dx"], "dy": diffs[0]["dy"], "rotation_deg": diffs[0]["rotation"]}

def process_one(item):
    guid = item.get("logName")

    base_path = Utils.resolve_base_path(item.get("isProd", False), __file__)
    paths = Utils.build_paths(base_path)

    image_path = item.get("originalImage")
    template_path = item.get("templateImage")
    is_base64Image = Utils.is_base64_image(image_path)
    if is_base64Image:
        image_path = Utils.base64_to_temp_image(image_path)
    if not image_path or not os.path.exists(image_path):
        raise ValueError(f"Image not found: {image_path}")
    if not template_path or not os.path.exists(template_path):
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

    # --- Find ArUco and calculate positioning differences ---
    template_markers = detect_aruco_pose(template)
    image_markers = detect_aruco_pose(cropped)
    diffs = compare_markers(template_markers, image_markers)

    diffs["templateDetails"] = template_markers[TARGET_ID]

    Utils.log_prediction(paths["conversion_log"], diffs, 0.0, True, paths["prediction_log"])

    if is_base64Image:
        Utils.cleanup(image_path)

    return {
        "inspectionId": item.get("inspectionId"),
        "success": True,
        "result": True,
        "score": 0.0,
        "value": diffs,
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
            if str(e) == "No ArUco markers detected":
                err = ""
            else:
                err = str(e)
            res = {
                "inspectionId": item.get("inspectionId"),
                "success": False,
                "result": False,
                "score": 0.0,
                "value": "",
                "insertDate": Utils.get_current_timestamp(),
                "error": err
            }

        results.append(res)

    print(json.dumps({
        "success": True,
        "results": results
    }, ensure_ascii=False), flush=True)

if __name__ == "__main__":
    main()
