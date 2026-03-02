"""Tests for memory_sidecar.flows.codebase — filtering and walking logic."""

from __future__ import annotations

from pathlib import Path

from memory_sidecar.flows.codebase import _should_include, _walk_source_files


class TestShouldInclude:
    """Tests for the _should_include file filter."""

    def _path(self, rel: str, root: str = "/project") -> tuple[Path, Path]:
        return Path(root) / rel, Path(root)

    def test_includes_python_file(self):
        path, root = self._path("src/main.py")
        assert _should_include(path, root) is True

    def test_includes_cs_file(self):
        path, root = self._path("contracts/Core.cs")
        assert _should_include(path, root) is True

    def test_excludes_non_code_extension(self):
        path, root = self._path("image.png")
        assert _should_include(path, root) is False

    def test_excludes_hidden_directory(self):
        path, root = self._path(".git/config")
        assert _should_include(path, root) is False

    def test_excludes_dotfile(self):
        path, root = self._path(".hidden/script.py")
        assert _should_include(path, root) is False

    def test_excludes_node_modules(self):
        path, root = self._path("node_modules/package/index.js")
        assert _should_include(path, root) is False

    def test_excludes_pycache(self):
        path, root = self._path("src/__pycache__/module.py")
        assert _should_include(path, root) is False

    def test_excludes_bin_directory(self):
        path, root = self._path("bin/Debug/net8.0/App.dll")
        assert _should_include(path, root) is False

    def test_excludes_obj_directory(self):
        path, root = self._path("obj/project.assets.json")
        assert _should_include(path, root) is False

    def test_case_insensitive_extension(self):
        path, root = self._path("Main.CS")
        assert _should_include(path, root) is True

    def test_deeply_nested_valid_file(self):
        path, root = self._path("src/core/utils/helpers.ts")
        assert _should_include(path, root) is True

    def test_excludes_artifacts_directory(self):
        path, root = self._path("build/_artifacts/nuget/pkg.json")
        assert _should_include(path, root) is False

    def test_excludes_addons_directory(self):
        path, root = self._path("addons/plugin/script.py")
        assert _should_include(path, root) is False


class TestWalkSourceFiles:
    """Tests for the _walk_source_files directory walker."""

    def test_finds_code_files(self, tmp_path):
        (tmp_path / "src").mkdir()
        (tmp_path / "src" / "main.py").write_text("print('hello')")
        (tmp_path / "src" / "lib.cs").write_text("class Lib {}")

        files = _walk_source_files(tmp_path)
        names = {f.name for f in files}
        assert names == {"main.py", "lib.cs"}

    def test_prunes_hidden_directories(self, tmp_path):
        hidden = tmp_path / ".git"
        hidden.mkdir()
        (hidden / "config.py").write_text("secret")

        files = _walk_source_files(tmp_path)
        assert len(files) == 0

    def test_prunes_node_modules(self, tmp_path):
        nm = tmp_path / "node_modules" / "pkg"
        nm.mkdir(parents=True)
        (nm / "index.js").write_text("module.exports = {}")
        (tmp_path / "app.js").write_text("const x = 1")

        files = _walk_source_files(tmp_path)
        names = {f.name for f in files}
        assert names == {"app.js"}

    def test_prunes_artifacts(self, tmp_path):
        arts = tmp_path / "build" / "_artifacts" / "nuget"
        arts.mkdir(parents=True)
        (arts / "pkg.json").write_text("{}")
        (tmp_path / "src.py").write_text("x = 1")

        files = _walk_source_files(tmp_path)
        names = {f.name for f in files}
        assert names == {"src.py"}

    def test_excludes_non_code_extensions(self, tmp_path):
        (tmp_path / "image.png").write_text("binary")
        (tmp_path / "readme.txt").write_text("text")
        (tmp_path / "code.py").write_text("x = 1")

        files = _walk_source_files(tmp_path)
        names = {f.name for f in files}
        assert names == {"code.py"}

    def test_deterministic_order(self, tmp_path):
        for name in ["c.py", "a.py", "b.py"]:
            (tmp_path / name).write_text(f"# {name}")

        files = _walk_source_files(tmp_path)
        names = [f.name for f in files]
        assert names == ["a.py", "b.py", "c.py"]

    def test_excludes_hidden_files(self, tmp_path):
        (tmp_path / ".secret.py").write_text("secret = 'key'")
        (tmp_path / "visible.py").write_text("x = 1")

        files = _walk_source_files(tmp_path)
        names = {f.name for f in files}
        assert names == {"visible.py"}
