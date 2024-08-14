## Dependencies

Zstandard: https://github.com/facebook/zstd

Customized bsdiff: https://github.com/MaaAssistantArknights/bsdiff

set environment variable `MAA_BSDIFF` to the path of customized bsdiff executable

## Usage

Prepare test data

```console
$ aria2c -i testdata.aria2
```

Run makedelta

```console
$ python -m makedelta version_list_all.txt version_list_nonlinear.txt
```

Smoke test (in Cygwin/MSYS2)

```console
$ sh smoke_test.sh
```
