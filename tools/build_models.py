"""
Çeviri modellerini (en→tr ve tr→en) hazırlar: indirir, ONNX'e dönüştürür, int8 sıkıştırır
ve dağıtıma hazır zip dosyaları üretir.

Çıktı (dist/models/):
    opus-mt-en-tr.zip, opus-mt-tr-en.zip, models.json (sha256 + boyut)

Bu dosyaları Hugging Face, GitHub Releases veya herhangi bir genel adrese yükleyin; uygulama
ilk açılışta appsettings.json → TranslationModelsBaseUrl adresinden indirir.

Kullanım:  python tools/build_models.py            (her iki yön)
           python tools/build_models.py tr-en      (tek yön)

Gerekli paketler (bir kez):
    pip install torch --index-url https://download.pytorch.org/whl/cpu
    pip install "optimum[onnxruntime]" sentencepiece sacremoses
"""
import hashlib
import json
import os
import shutil
import subprocess
import sys
import zipfile
from pathlib import Path

MODELS = {
    "en-tr": "Helsinki-NLP/opus-mt-tc-big-en-tr",
    "tr-en": "Helsinki-NLP/opus-mt-tc-big-tr-en",
}
REQUIRED = ["encoder_model.onnx", "decoder_model.onnx", "source.spm", "vocab.json", "config.json"]
# Önbellekli kod çözücü: uygulama varsa kullanır, uzun cümlelerde çeviriyi kat kat hızlandırır
OPTIONAL = ["decoder_with_past_model.onnx"]

ROOT = Path(__file__).resolve().parent.parent
WORK = ROOT / "dist" / "models-work"
OUT = ROOT / "dist" / "models"


def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def build(direction: str, model_id: str) -> Path:
    from onnxruntime.quantization import QuantType, quantize_dynamic

    raw = WORK / f"{direction}-fp32"
    final = WORK / f"opus-mt-{direction}"
    for d in (raw, final):
        if d.exists():
            shutil.rmtree(d)

    print(f"\n=== {direction}: {model_id} indiriliyor ve ONNX'e dönüştürülüyor ===", flush=True)
    subprocess.run(
        [sys.executable, "-m", "optimum.exporters.onnx",
         "--model", model_id, "--task", "text2text-generation-with-past",
         "--no-post-process",  # birleştirilmiş tek dosya değil, ayrı decoder / decoder_with_past
         str(raw)],
        check=True,
    )

    final.mkdir(parents=True)
    for name in ("encoder_model.onnx", "decoder_model.onnx", "decoder_with_past_model.onnx"):
        if not (raw / name).exists():
            continue
        print(f"  int8 sıkıştırma: {name}", flush=True)
        quantize_dynamic(str(raw / name), str(final / name), weight_type=QuantType.QInt8)
    for name in ("source.spm", "vocab.json", "config.json"):
        shutil.copy2(raw / name, final / name)

    missing = [f for f in REQUIRED if not (final / f).exists()]
    if missing:
        raise SystemExit(f"Eksik dosyalar: {missing}")

    OUT.mkdir(parents=True, exist_ok=True)
    zip_path = OUT / f"opus-mt-{direction}.zip"
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
        for f in REQUIRED + [o for o in OPTIONAL if (final / o).exists()]:
            z.write(final / f, f)
    print(f"  → {zip_path} ({zip_path.stat().st_size / 1e6:.0f} MB)")
    return zip_path


def main():
    dirs = sys.argv[1:] or list(MODELS)
    manifest_path = OUT / "models.json"
    manifest = json.loads(manifest_path.read_text()) if manifest_path.exists() else {}

    for d in dirs:
        if d not in MODELS:
            raise SystemExit(f"Bilinmeyen yön: {d} (seçenekler: {', '.join(MODELS)})")
        z = build(d, MODELS[d])
        manifest[z.name] = {"sha256": sha256(z), "size": z.stat().st_size, "source": MODELS[d]}

    manifest_path.write_text(json.dumps(manifest, indent=2))

    # Bu bilgisayardaki uygulama da hemen kullansın
    local = os.environ.get("LOCALAPPDATA")
    if local:
        for d in dirs:
            dest = Path(local) / "KickTranslator" / "models" / f"opus-mt-{d}"
            if dest.exists():
                shutil.rmtree(dest)
            shutil.copytree(WORK / f"opus-mt-{d}", dest)
            # Uygulama bu kaydı yayınlanan models.json ile karşılaştırıp güncelleme gerekip gerekmediğine karar verir
            (dest / ".source").write_text(manifest[f"opus-mt-{d}.zip"]["sha256"])
            print(f"  → bu bilgisayara kuruldu: {dest}")
    print(f"\nTamam. Yüklenecek dosyalar: {OUT}")
    print("Model lisansı: CC-BY 4.0 (Helsinki-NLP / OPUS-MT) — Hakkında sayfasında kaynak belirtin.")


if __name__ == "__main__":
    main()
