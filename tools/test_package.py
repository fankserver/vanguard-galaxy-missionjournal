import pathlib
import stat
import tempfile
import unittest
import zipfile
from package import NAMES, validate


class PackageLayoutTests(unittest.TestCase):
    def check(self, change, rejected):
        with tempfile.TemporaryDirectory() as root:
            path = pathlib.Path(root) / "test.zip"
            with zipfile.ZipFile(path, "w") as archive:
                for name in NAMES:
                    if change == "missing" and name == "LICENSE":
                        continue
                    info = zipfile.ZipInfo("VGMissionJournal/" + name)
                    if change == "symlink" and name == "LICENSE":
                        info.create_system = 3
                        info.external_attr = (stat.S_IFLNK | 0o777) << 16
                    archive.writestr(info, b"" if change == "empty" else b"synthetic")
                if change == "extra":
                    archive.writestr("VGMissionJournal/Assembly-CSharp.dll", b"synthetic")
                if change == "traversal":
                    archive.writestr("../outside", b"synthetic")
            if rejected:
                with self.assertRaises(ValueError):
                    validate(path)
            else:
                validate(path)

    def test_valid_layout(self):
        self.check("valid", False)

    def test_rejections(self):
        for change in ("missing", "empty", "symlink", "extra", "traversal"):
            with self.subTest(change=change):
                self.check(change, True)


if __name__ == "__main__":
    unittest.main()
