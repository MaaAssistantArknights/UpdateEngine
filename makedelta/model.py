class FileActionRecord:
    def __init__(self, path):
        self.path = path
    def __repr__(self) -> str:
        return f"{type(self).__name__}({self.path!r})"
    def __eq__(self, o: object) -> bool:
        return isinstance(o, type(self)) and self.path == o.path
    def __hash__(self) -> int:
        return hash((type(self), self.path))


class ReplaceFile(FileActionRecord):
    pass


class PatchFile(FileActionRecord):
    def __init__(self, from_version: str, path: str):
        super().__init__(path)
        self.from_version = from_version
        self.path = path
    def __repr__(self) -> str:
        return f"{type(self).__name__}(from_version={self.from_version!r}, {self.path!r})"
    def __eq__(self, o: object) -> bool:
        return isinstance(o, type(self)) and self.from_version == o.from_version and self.path == o.path
    def __hash__(self) -> int:
        return hash((type(self), self.from_version, self.path))


class AddFile(FileActionRecord):
    pass


class RemoveFile(FileActionRecord):
    pass