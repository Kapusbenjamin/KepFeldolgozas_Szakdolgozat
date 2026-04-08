import sys
import os
import re
import json
import cv2
import zxingcpp
import Utils

def checkResult(values, requiredValue):
    match_found = False
    matched_value = None

    for val in values:
        try:
            # regex check
            if re.fullmatch(requiredValue, val):
                matched_value = val
                match_found = True
                break
        except re.error:
            # basic string check
            if val == requiredValue:
                matched_value = val
                match_found = True
                break

    return match_found, matched_value

def process_one(item):
    guid = item.get("logName")

    base_path = Utils.resolve_base_path(item.get("isProd", False), __file__)
    paths = Utils.build_paths(base_path)

    image_path = item.get("originalImage")
    is_base64Image = Utils.is_base64_image(image_path)
    if is_base64Image:
        image_path = Utils.base64_to_temp_image(image_path)
    if not os.path.exists(image_path):
        raise ValueError(f"Image not found: {image_path}")

    image = cv2.imread(image_path)
    if image is None:
        raise ValueError("Cannot load image")

    cropped = Utils.rotate_image(Utils.crop_with_offset(image, item, item.get("inspectionOffset", 0)), item.get("angle", 0))

    # debug
    orig_dbg, crop_dbg = Utils.save_debug_images(paths, guid, image, cropped)
    item["originalImage"] = orig_dbg
    item["isProd"] = False
    Utils.log_conversion(item, guid, paths["conversion_log"])

    # while(not match_found and angle_index * rotate_angle_step < 360):
    #     found_angle = (base_angle + (angle_index * rotate_angle_step)) % 360
    #     rotated = Utils.rotate_image(cropped, angle_index * rotate_angle_step)
    #     angle_index += 1

    #     results = zxingcpp.read_barcodes(rotated)
    #     for z in results:
    #         values.append(z.text)
    
    #     match_found, matched_value = checkResult(values, item.get("requiredValue", ""))

    # --- Barcode read ---
    match_found = False
    matched_value = None
    values = []
    base_angle = item.get("angle", 0)
    found_angle = 0
    rotate_angle_step = 15
    angle_index = 0
        
    for z in [1, 1.6, 2.5]:
        angle_index = 0
        zoom_factor = z
        zoom = cv2.resize(cropped, None, fx=zoom_factor, fy=zoom_factor, interpolation=cv2.INTER_CUBIC)

        while(not match_found and angle_index * rotate_angle_step < 360):
            found_angle = (base_angle + (angle_index * rotate_angle_step)) % 360
            rotated = Utils.rotate_image(zoom, angle_index * rotate_angle_step)
            angle_index += 1

            results = zxingcpp.read_barcodes(rotated)
            for r in results:
                values.append(r.text)
        
            match_found, matched_value = checkResult(values, item.get("requiredValue", ""))
        
        if match_found:
            break

    if not match_found: 
        found_angle = 0
        rotated = cropped

    saved_path = Utils.save_result_image(paths, guid, rotated, match_found)
    Utils.log_prediction(saved_path, matched_value, 0.0, match_found, paths["prediction_log"])

    if is_base64Image:
        Utils.cleanup(image_path)
        
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
        "result": match_found,
        "score": 0.0,
        "value": matched_value,
        "angle": found_angle,
        "insertDate": Utils.get_current_timestamp(),
        "visual": {
            "imagePath": image_path,
            "x": visual_x,
            "y": visual_y,
            "w": cropped.shape[1],
            "h": cropped.shape[0],
            "result": match_found,
            "text": item.get("requiredValue", "")
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
                "angle": 0,
                "insertDate": Utils.get_current_timestamp(),
                "error": str(e)
            }

        results.append(res)
        done += 1
        Utils.emit_progress(done, total)
        
    visuals = [r["visual"] for r in results if r.get("success")]
    Utils.show_grouped_results(visuals, "Barcode Inspection Results")

    print(json.dumps({
        "success": True,
        "results": results
    }, ensure_ascii=False), flush=True)

if __name__ == "__main__":
    main()
