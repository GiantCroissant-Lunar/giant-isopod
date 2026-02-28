"""Install external agent skills from skills.json manifest."""

import json
import shutil
import subprocess
import tempfile
from pathlib import Path

SCRIPT_DIR = Path(__file__).parent
SKILLS_CONFIG = SCRIPT_DIR / "skills.json"
SKILLS_DIR = SCRIPT_DIR / "skills"


def install_skills():
    config = json.loads(SKILLS_CONFIG.read_text())

    for skill in config["skills"]:
        name = skill["name"]
        repo = skill["repo"]
        sub_path = skill["path"]
        ref = skill.get("ref", "main")

        target = SKILLS_DIR / name

        if target.exists():
            print(f"[skip] {name} already installed")
            continue

        print(f"[install] {name} from {repo} ({ref}:{sub_path})")

        with tempfile.TemporaryDirectory() as tmp:
            subprocess.run(
                ["git", "clone", "--depth", "1", "--branch", ref, repo, tmp],
                check=True,
                capture_output=True,
            )
            source = Path(tmp) / sub_path
            if not source.exists():
                print(f"[error] path '{sub_path}' not found in {repo}")
                continue

            shutil.copytree(source, target)
            print(f"[done] {name}")

    print("\nAll skills installed.")


if __name__ == "__main__":
    install_skills()
