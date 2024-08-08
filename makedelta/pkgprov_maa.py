from typing import Optional
from . import pkgprov

def open_package(package_name: str, version: str, variant: Optional[str]):
    assert package_name == 'MAA'
    assert variant == 'win-x64'
    filename = f"testdata/MAA-{version}-win-x64.zip"
    return pkgprov.ZipPackage(filename, package_name, version, variant)

