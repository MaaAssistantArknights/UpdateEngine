from typing import Protocol, Optional, Union, BinaryIO
import dataclasses
import zipfile
import os
import calendar
import struct

class PackageProvider(Protocol):
    def open_package(self, package_name: str, version: str, variant: Optional[str]) -> 'Package':
        ...

class Package(Protocol):
    name: str
    version: str
    variant: Optional[str]
    def get_entries(self) -> list['PackageEntry']:
        ...
    def get_entry(self, name) -> 'PackageEntry':
        ...
    def open_entry(self, entry: 'PackageEntry | str') -> BinaryIO:
        ...

@dataclasses.dataclass(slots=True, frozen=True)
class PackageEntry:
    name: str
    size: int
    checksum_type: str
    checksum: bytes
    mtime: int | float = dataclasses.field(compare=False)
    mode: int = dataclasses.field(compare=False)

class ZipPackage:
    def __init__(self, zipf: os.PathLike | zipfile.ZipFile, name, version, variant):
        if isinstance(zipf, zipfile.ZipFile):
            self.zipf = zipf
        else:
            self.zipf = zipfile.ZipFile(zipf, "r", metadata_encoding="utf-8")
        self.name = name
        self.version = version
        self.variant = variant
        self.entries = []
        self.entries_map = {}
        for x in self.zipf.infolist():
            if x.is_dir():
                continue
            mode = x.external_attr >> 16
            if mode == 0:
                mode = 0o100644
            mtime = calendar.timegm(x.date_time)
            entry = PackageEntry(x.filename, x.file_size, "crc32", struct.pack('>I', x.CRC), mtime, mode)
            self.entries.append(entry)
            self.entries_map[x.filename] = entry
    def get_entry(self, name):
        return self.entries_map[name]
    def get_entries(self):
        return self.entries
    def open_entry(self, entry: PackageEntry | str):
        if isinstance(entry, PackageEntry):
            entry = entry.name
        return self.zipf.open(entry)
