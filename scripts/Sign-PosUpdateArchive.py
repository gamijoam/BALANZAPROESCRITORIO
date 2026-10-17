#!/usr/bin/env python3
"""Add a signed file inventory to an official POS ZIP on the private signing runner."""

import argparse
import hashlib
import json
import re
import shutil
import subprocess
import tempfile
import zipfile
from pathlib import Path

MANIFEST = "pos-update.manifest.json"
SIGNATURE = "pos-update.manifest.sig"
REQUIRED = {
    "server/Pos.Server.exe", "desktop/Pos.Desktop.exe", "scripts/Install-LocalPos.ps1",
    "scripts/Install-DownloadedPosUpdate.ps1", "trusted-license-keys.json", "pos-version.json",
    "trusted-publishers/pos-publisher-vps-2026.pem", "Actualizar POS.cmd",
}


def safe_name(name: str) -> bool:
    parts = name.split("/")
    return bool(name) and not name.startswith("/") and "\\" not in name and all(
        part not in ("", ".", "..") and ":" not in part for part in parts
    )


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--private-key", required=True, type=Path)
    parser.add_argument("--version", required=True)
    args = parser.parse_args()
    if not re.fullmatch(r"\d+\.\d+\.\d+(?:\.\d+)?", args.version):
        raise ValueError("La versión del POS debe tener tres o cuatro números.")
    if args.input.resolve() == args.output.resolve():
        raise ValueError("La salida debe ser un archivo distinto de la entrada.")

    files = []
    seen = set()
    with zipfile.ZipFile(args.input, "r") as source:
        entries = [entry for entry in source.infolist() if not entry.is_dir()]
        if len(entries) > 4096:
            raise ValueError("El paquete tiene demasiados archivos.")
        for entry in entries:
            name = entry.filename
            if not safe_name(name) or name.lower() in seen or name in (MANIFEST, SIGNATURE):
                raise ValueError(f"Ruta duplicada o insegura: {name}")
            seen.add(name.lower())
            digest = hashlib.sha256()
            size = 0
            with source.open(entry) as stream:
                for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                    size += len(chunk)
                    if size > 256 * 1024 * 1024:
                        raise ValueError(f"Archivo demasiado grande: {name}")
                    digest.update(chunk)
            files.append({"path": name, "size": size, "sha256": digest.hexdigest()})
        missing = [name for name in REQUIRED if name.lower() not in seen]
        if missing:
            raise ValueError(f"Faltan archivos obligatorios: {sorted(missing)}")
        if sum(item["size"] for item in files) > 2 * 1024 * 1024 * 1024:
            raise ValueError("El paquete descomprimido supera el tamaño permitido.")
        version_file = json.loads(source.read("pos-version.json"))
        if version_file.get("version") != args.version:
            raise ValueError("La versión estampada en el POS no coincide con la Release.")
    files.sort(key=lambda item: item["path"])
    manifest = json.dumps(
        {"schemaVersion": 1, "product": "balanzapro-pos-win-x64", "version": args.version, "files": files},
        ensure_ascii=False, separators=(",", ":"),
    ).encode("utf-8")

    with tempfile.TemporaryDirectory() as temporary:
        manifest_path = Path(temporary) / MANIFEST
        signature_path = Path(temporary) / SIGNATURE
        manifest_path.write_bytes(manifest)
        subprocess.run(
            ["openssl", "dgst", "-sha256", "-sign", str(args.private_key),
             "-sigopt", "rsa_padding_mode:pss", "-sigopt", "rsa_pss_saltlen:digest",
             "-out", str(signature_path), str(manifest_path)],
            check=True,
        )
        args.output.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(args.input, args.output)
        with zipfile.ZipFile(args.output, "a", compression=zipfile.ZIP_DEFLATED) as archive:
            archive.writestr(MANIFEST, manifest)
            archive.writestr(SIGNATURE, signature_path.read_bytes())

    checksum = hashlib.sha256()
    with args.output.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            checksum.update(chunk)
    args.output.with_name(args.output.name + ".sha256").write_text(
        f"{checksum.hexdigest()}  {args.output.name}\n", encoding="ascii"
    )
    print(f"Paquete POS {args.version} firmado: {args.output}")


if __name__ == "__main__":
    main()
