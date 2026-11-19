"""Build an immutable full package, preserving every unmodified base member."""
import argparse
import hashlib
import json
from pathlib import Path
import stat
import zipfile


def digest(path):
    h = hashlib.sha256()
    with path.open("rb") as f:
        for data in iter(lambda: f.read(1024 * 1024), b""): h.update(data)
    return h.hexdigest()


def build(repo, base, overlay, target):
    meta = json.loads((repo / "PACKAGE.json").read_text(encoding="utf-8"))
    assert digest(base) == meta["base_package_sha256"], "Unexpected base ZIP"
    updates = {p.relative_to(overlay).as_posix(): p.read_bytes() for p in overlay.rglob("*") if p.is_file()}
    updates["game.json"] = (repo / "game.json").read_bytes()
    updates["COMPATIBILITY.md"] = (repo / "COMPATIBILITY.md").read_bytes()
    target.parent.mkdir(parents=True, exist_ok=True)
    changes = []
    prefix = meta["id"] + "/"
    with zipfile.ZipFile(base) as old, zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED, compresslevel=1) as new:
        for entry in old.infolist():
            assert entry.filename.startswith(prefix), entry.filename
            relative = entry.filename[len(prefix):]
            data = old.read(entry)
            if relative in updates:
                replacement = updates.pop(relative)
                if data != replacement:
                    changes.append({"path": relative, "before": hashlib.sha256(data).hexdigest(), "after": hashlib.sha256(replacement).hexdigest()})
                data = replacement
            # The previous receipts describe the old byte-for-byte package.
            if relative in {"BUILD-MANIFEST.json", "COMPATIBILITY-MANIFEST.json"}: continue
            new.writestr(entry, data, compress_type=zipfile.ZIP_DEFLATED, compresslevel=1)
        for relative, data in sorted(updates.items()):
            entry = zipfile.ZipInfo(prefix + relative)
            entry.create_system = 3
            entry.external_attr = (stat.S_IFREG | (0o755 if relative.endswith(".sh") else 0o644)) << 16
            new.writestr(entry, data, compress_type=zipfile.ZIP_DEFLATED, compresslevel=1)
            changes.append({"path": relative, "before": None, "after": hashlib.sha256(data).hexdigest()})
        new.writestr(prefix + "COMPATIBILITY-MANIFEST.json", json.dumps({
            "base_package_sha256": meta["base_package_sha256"], "changes": changes,
            "scope": "Packaging/integration only; no save files or user configuration",
        }, indent=2) + "\n")
    # Check the archive CRC and invariant contract; no game data is fabricated.
    with zipfile.ZipFile(target) as z:
        assert z.testzip() is None
        game = json.loads(z.read(prefix + "game.json"))
        assert prefix + game["exec"] in z.namelist()
        assert not any(part.lower() in {"saves", "logs", "__pycache__"} for name in z.namelist() for part in name.split("/"))
        assert b"\r" not in z.read(prefix + game["exec"]) if game["exec"].endswith(".sh") else True
    receipt = {"sha256": digest(target), "bytes": target.stat().st_size, "changed_files": changes}
    (target.parent / "verification.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    (target.parent / "SHA256SUMS.txt").write_text(receipt["sha256"] + "  " + target.name + "\n", encoding="ascii")
    print(json.dumps({"package": str(target), "sha256": receipt["sha256"], "bytes": receipt["bytes"], "changed_files": len(changes)}), flush=True)
    return receipt


if __name__ == "__main__":
    p = argparse.ArgumentParser()
    p.add_argument("base", type=Path); p.add_argument("overlay", type=Path); p.add_argument("output", type=Path)
    p.add_argument("--repo", type=Path, default=Path(__file__).resolve().parent)
    args = p.parse_args()
    build(args.repo, args.base, args.overlay, args.output)
