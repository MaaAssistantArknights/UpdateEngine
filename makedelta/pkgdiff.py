import sys
import dataclasses

from . import pkgprov

@dataclasses.dataclass
class PackageDiff:
    a_only: set[str]
    b_only: set[str]
    ab_diff: set[str]
    common: set[str]

    def __len__(self):
        return len(self.a_only) + len(self.b_only) + len(self.ab_diff)

def package_diff(a: pkgprov.Package, b: pkgprov.Package) -> PackageDiff:
    old_entries = set(a.get_entries())
    new_entries = set(b.get_entries())

    a_names = set(x.name for x in old_entries)
    b_names = set(x.name for x in new_entries)

    unchanged_entries = old_entries & new_entries
    unchanged_names = set(x.name for x in unchanged_entries)

    # maybe changed or unchanged
    common_names = a_names & b_names

    only_a = a_names - common_names
    only_b = b_names - common_names
    changed_names = common_names - unchanged_names

    return PackageDiff(only_a, only_b, changed_names, unchanged_names)

def main():
    if len(sys.argv) != 3:
        print("Usage: zipdiff.py <oldzip> <newzip>")
        sys.exit(1)
    oldzip = pkgprov.ZipPackage(sys.argv[1])
    newzip = pkgprov.ZipPackage(sys.argv[2])

    diff = package_diff(oldzip, newzip)

    for x in sorted(list(diff.a_only)):
        print(f"- {x}")
    for x in sorted(list(diff.b_only)):
        print(f"+ {x}")
    for x in sorted(list(diff.ab_diff)):
        print(f"* {x}")

if __name__ == "__main__":
    main()
