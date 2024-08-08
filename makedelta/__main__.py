from . import makedelta
import sys

def main():
    from . import pkgprov_maa
    if len(sys.argv) != 3:
        print("Usage: makedelta.py <versions.txt> <nonlinear_versions.txt>")
        sys.exit(1)
    versions = [ x.strip() for x in open(sys.argv[1], 'r', encoding='utf-8') ]
    nonlinear_versions = [ x.strip() for x in open(sys.argv[2], 'r', encoding='utf-8') ]
    makedelta.main(pkgprov_maa, "MAA", "win-x64", versions, nonlinear_versions)

main()
