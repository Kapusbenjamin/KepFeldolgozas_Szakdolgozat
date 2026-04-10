import os
import cv2
import numpy as np
import tensorflow as tf
from sklearn.utils import class_weight
from tensorflow.keras.preprocessing.image import ImageDataGenerator
from tensorflow.keras.applications import MobileNetV2
from tensorflow.keras import layers, models, optimizers
from tensorflow.keras.callbacks import EarlyStopping, ModelCheckpoint
from tensorflow.keras import metrics

IMG_SIZE = (224, 224)

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

# --- Adatgenerátor augmentationnel ---
train_datagen = ImageDataGenerator(
    # preprocessing_function=preprocess_image,
    preprocessing_function=tf.keras.applications.mobilenet_v2.preprocess_input,
    rotation_range=180,
    # width_shift_range=0.1,
    # height_shift_range=0.1,
    # brightness_range=[0.9, 1.1],
    # zoom_range=0.1,
    horizontal_flip=True,
    # fill_mode='nearest',
    validation_split=0.2
)

train_generator = train_datagen.flow_from_directory(
    "data",
    target_size=IMG_SIZE,
    batch_size=16,
    class_mode="binary",
    subset="training",
    shuffle=True
)

val_datagen = ImageDataGenerator(
    # preprocessing_function=preprocess_image,
    preprocessing_function=tf.keras.applications.mobilenet_v2.preprocess_input,
    # zoom_range=0.1,
    validation_split=0.2
)

val_generator = val_datagen.flow_from_directory(
    "data",
    target_size=IMG_SIZE,
    batch_size=16,
    class_mode="binary",
    subset="validation"
)

# --- Automatikus class_weight számítás a mappák alapján ---
data_dir = "data"
class_names = sorted(os.listdir(data_dir))  # ['bad', 'marks']
print("Osztályok:", class_names)

class_counts = []
for class_name in class_names:
    class_path = os.path.join(data_dir, class_name)
    num_images = len([
        f for f in os.listdir(class_path)
        if os.path.isfile(os.path.join(class_path, f))
    ])
    class_counts.append(num_images)

print("Képszámok osztályonként:", dict(zip(class_names, class_counts)))

labels = np.concatenate([
    np.full(count, i) for i, count in enumerate(class_counts)
])

class_weights = class_weight.compute_class_weight(
    class_weight="balanced",
    classes=np.unique(labels),
    y=labels
)

class_weights = dict(enumerate(class_weights))
print("Kiszámolt class_weight értékek:", class_weights)

# --- Modell ---
base_model = MobileNetV2(weights="imagenet", include_top=False, input_shape=(224,224,3))
base_model.trainable = False

model = models.Sequential([
    base_model,
    layers.GlobalAveragePooling2D(),
    layers.Dense(124, activation="relu"),
    layers.Dropout(0.3),
    layers.Dense(1, activation="sigmoid")
])

model.compile(
    optimizer=optimizers.Adam(learning_rate=1e-2),
    loss="binary_crossentropy",
    metrics=[
        metrics.Precision(name='precision'),
        metrics.Recall(name='recall'),
        metrics.AUC(name="auc", curve="ROC"),
        metrics.AUC(name="prc", curve="PR"),
        "accuracy"
    ]
)

# --- Callback-ek ---
early_stop = EarlyStopping(monitor="val_loss", patience=10, restore_best_weights=True)
checkpoint = ModelCheckpoint("best_model.h5", monitor="val_loss", save_best_only=True)

# --- Tanítás első kör ---
history = model.fit(
    train_generator,
    validation_data=val_generator,
    epochs=500,
    callbacks=[early_stop, checkpoint],
    class_weight=class_weights  # ⬅️ Súlyok alkalmazása itt
)

# # --- Finomhangolás (base_model feloldása) ---
# base_model.trainable = True
# model.compile(
#     optimizer=optimizers.Adam(learning_rate=1e-5),
#     loss="binary_crossentropy",
#     metrics=[
#         metrics.Precision(name='precision'),
#         metrics.Recall(name='recall'),
#         metrics.AUC(name="auc", curve="ROC"),
#         metrics.AUC(name="prc", curve="PR"),
#         "accuracy"
#     ]
# )

# history_finetune = model.fit(
#     train_generator,
#     validation_data=val_generator,
#     epochs=500,
#     callbacks=[early_stop, checkpoint],
#     class_weight=class_weights  # ⬅️ Ugyanazokat a súlyokat használja itt is
# )

# --- Finomhangolás: az utolsó rétegek feloldása ---
# 1) Oldjuk fel a bázismodell utolsó 20 rétegét
base_model.trainable = True

fine_tune_at = len(base_model.layers) - 100  # utolsó 20 réteg finomhangolása

for i, layer in enumerate(base_model.layers):
    layer.trainable = (i >= fine_tune_at)

print(f"Finomhangolás: az utolsó {100} réteg trainable.")

# 2) Kisebb learning rate a finomhangoláshoz (nagyon fontos!)
model.compile(
    optimizer=optimizers.Adam(learning_rate=1e-3),
    loss="binary_crossentropy",
    metrics=[
        metrics.Precision(name='precision'),
        metrics.Recall(name='recall'),
        metrics.AUC(name="auc", curve="ROC"),
        metrics.AUC(name="prc", curve="PR"),
        "accuracy"
    ]
)

# 3) Finomhangolás
history_finetune = model.fit(
    train_generator,
    validation_data=val_generator,
    epochs=500,
    callbacks=[early_stop, checkpoint],
    class_weight=class_weights
)
