import cv2
from tensorflow.keras.models import load_model
import tensorflow as tf
import numpy as np
import os

MODEL_PATH = "best_model.h5"
TEST_DIR = "test"
IMG_SIZE = (224,224)

model = load_model(MODEL_PATH)

def preprocess_for_model(img):
    img_resized = cv2.resize(img, IMG_SIZE)
    img_resized = img_resized.astype("float32")
    img_resized = tf.keras.applications.mobilenet_v2.preprocess_input(img_resized)
    return img_resized

def preprocess_image(img):
    # RGB → BGR
    img_bgr = cv2.cvtColor(img, cv2.COLOR_RGB2BGR)

    img_bgr_uint8 = (img_bgr * 255).astype(np.uint8) if img_bgr.dtype != np.uint8 else img_bgr

    # BGR → LAB
    lab = cv2.cvtColor(img_bgr_uint8, cv2.COLOR_BGR2LAB)
    l, a, b = cv2.split(lab)
    l = l.astype(np.uint8)

    # CLAHE csak L csatornára
    clahe = cv2.createCLAHE(clipLimit=2.0, tileGridSize=(8,8))
    l_clahe = clahe.apply(l)  # <-- ez most már oké, uint8

    # Összeállítás
    lab_clahe = cv2.merge((l_clahe, a, b))
    img_clahe = cv2.cvtColor(lab_clahe, cv2.COLOR_LAB2BGR)

    # Gaussian blur (enyhe)
    img_blur = cv2.GaussianBlur(img_clahe, (3, 3), 0)

    # Vissza RGB-re, majd normalizálás
    img_rgb = cv2.cvtColor(img_blur, cv2.COLOR_BGR2RGB)
    img_resized = cv2.resize(img_rgb, IMG_SIZE)

    img_array = img_resized.astype("float32") / 255.0
    return img_array

for fname in os.listdir(TEST_DIR):
    fpath = os.path.join(TEST_DIR, fname)
    img = cv2.imread(fpath)
    if img is None: continue
    img_rgb = cv2.cvtColor(img, cv2.COLOR_BGR2RGB)

    # input_tensor = preprocess_image(img_rgb)
    # input_tensor = np.expand_dims(input_tensor, axis=0)
    input_tensor = preprocess_for_model(img_rgb)
    input_tensor = np.expand_dims(input_tensor, axis=0)
    pred = model.predict(input_tensor)[0][0]
    print(f"{fname}: {pred*100:.2f}% valószínűség, hogy jelölt")
