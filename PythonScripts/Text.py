import sys
import os
import re
import json
import cv2
import easyocr
from itertools import permutations
import Utils

# --- OCR and helper functions ---
def normalize_text(results):
    norm_results = []
    for r in results:
        _, text, _ = r  # csak a text kell
        norm_results.append({"Text": text.upper()})
    return norm_results

similar_chars = {
    '0': {'O', 'Q'}, 'O': {'0', 'Q'}, 'Q': {'0', 'O'},
    '1': {'I', 'L', 'T'}, 'I': {'1', 'L', 'T'}, 'L': {'1','I'},
    ')': {'1','I'}, '?': {'3'},
    '5': {'S'}, 'S': {'5'},
    '2': {'Z'}, 'Z': {'2'},
    '8': {'B', 'R', '3'}, 'B': {'8', 'R', '3'}, 'R': {'8', 'B', '3'}, '3': {'B', 'R', '8'},
    'V': {'W'}, 'W': {'V'},
    'A': {'4'}, '4': {'A'},
    'P': {'F'}, 'F': {'P', '7'}, '7': {'F'}
}

def is_similar(actual: str, expected: str, similar_chars: dict) -> bool:
    actual = actual.upper().strip()
    expected = expected.upper().strip()

    if(len(actual) < len(expected)):
        return False

    def char_equal(a, e):
        if a == e:
            return True
        if e in similar_chars and a in similar_chars[e]:
            return True
        if a in similar_chars and e in similar_chars[a]:
            return True
        return False

    # find expected in actual with similar chars
    for i in range(len(actual) - len(expected) + 1):
        match = True
        for j in range(len(expected)):
            if not char_equal(actual[i + j], expected[j]):
                match = False
                break
        if match:
            return True
    return False

def get_combinations(items, target_length):
    results = []
    def backtrack(path, index, total_len):
        if total_len > target_length:
            return
        if total_len == target_length:
            results.append(list(path))
            return
        for i in range(index, len(items)):
            path.append(items[i])
            backtrack(path, i + 1, total_len + len(items[i]))
            path.pop()
    backtrack([], 0, 0)
    for combo in results:
        for perm in permutations(combo):
            yield perm

def is_acceptably_similar(ocr_results, expected):
    expected = expected.upper()
    # new_results = []
    for res in ocr_results:
        if not res.get("Text"):
            continue
        ocr_result = res["Text"].upper()
        replacements = {')':'1', '?':'3', ':':'1'}
        for k,v in replacements.items():
            ocr_result = ocr_result.replace(k,v)
        ocr_result = re.sub(r'[^A-Z0-9]', '', ocr_result)
        res["Text"] = ocr_result
        # new_results.append(res)
        if is_similar(ocr_result, expected, similar_chars):
            return True
    # texts = [r["Text"] for r in new_results]
    # for combination in get_combinations(texts, len(expected)):
    #     joined = ''.join(combination)
    #     if len(joined) == len(expected) and is_similar(joined, expected):
    #         return True
    return False

def try_ocr_with_rotations(reader, image, required_value, initial_angle=None):
    """
    Optimized rotation logic:
    - Try the given initial_angle first
    - Then try 10 and 20 degrees
    - Repeat the same set after adding 180 degrees
    - Then try with all degrees
    """
    if initial_angle is None:
        initial_angle = 0

    base_angles = [initial_angle, initial_angle - 10, initial_angle + 10, initial_angle - 20, initial_angle + 20]
    extended_angles = base_angles + [a + 180 for a in base_angles]
    angles = sorted(set([a % 360 for a in extended_angles]))

    all_results = []
    # Try each angle
    for angle in angles:
        rotated = Utils.rotate_image(image, angle)
        results = reader.readtext(rotated)
        if not results:
            continue

        results = normalize_text(results)
        all_results.append(results)

        if is_acceptably_similar(results, required_value):
            return True, results, rotated, angle

    # Define rotation step hierarchy
    rotation_steps = [90, 60, 30, 15]

    # Generate ordered unique angles
    angles2 = []
    for step in rotation_steps:
        for angle in range(0, 360, step):
            if angle not in angles and angle not in angles2:
                angles2.append(angle)

    # Try each angle until a match is found
    for angle in angles2:
        rotated = Utils.rotate_image(image, angle)
        results = reader.readtext(rotated)
        if not results:
            continue

        results = normalize_text(results)
        if is_acceptably_similar(results, required_value):
            return True, results, rotated, angle
        
    # No match found
    return False, all_results, image, 0

def process_one(item, reader):
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

    cropped = Utils.crop_with_offset(image, item, item.get("inspectionOffset", 0))

    # debug
    # orig_dbg, crop_dbg = Utils.save_debug_images(paths, guid, image, cropped)
    # item["originalImage"] = orig_dbg
    # item["isProd"] = False
    # Utils.log_conversion(item, guid, paths["conversion_log"])

    # --- OCR Text read ---
    try:
        similarity, results, rotated_image, found_angle = try_ocr_with_rotations(reader, cropped, item.get("requiredValue", ""), item.get("angle", 0))
    except Exception as e:
        raise ValueError(f"OCR failed: {e}")

    # saved_path = Utils.save_result_image(paths, guid, rotated_image, similarity)
    # Utils.log_prediction(saved_path, results, 0.0, similarity, paths["prediction_log"])
    
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
        "result": similarity,
        "score": 0.0,
        "value": results,
        "angle": found_angle,
        "insertDate": Utils.get_current_timestamp(),
        "visual": {
            "imagePath": image_path,
            "x": visual_x,
            "y": visual_y,
            "w": cropped.shape[1],
            "h": cropped.shape[0],
            "result": similarity,
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

    reader = easyocr.Reader(['en'], gpu=False)

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
            res = process_one(item, reader)
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
    Utils.show_grouped_results(visuals, "Text Inspection Results")

    print(json.dumps({
        "success": True,
        "results": results
    }, ensure_ascii=False), flush=True)

if __name__ == "__main__":
    main()
