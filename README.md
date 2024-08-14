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

Smoke test

```console
$ rm -rf test
$ mkdir test
$ # handle weird encoding in zip files
$ python -m zipfile -e testdata/MAA-v5.3.2-alpha.1.d013.g1652647f1-win-x64.zip test/test_package
$ python -m zipfile -e testdata/MAA-v5.4.2-alpha.1.d104.g2428a4610-win-x64.zip test/ref_package
$ dotnet run --project MaaUpdateEngine/MaaUpdateEngine.csproj -- smoke_test_input.json test/test_package output/MAA-v5.4.2-alpha.1.d104.g2428a4610-win-x64-delta.tar.zst
$ diff -r test/test_package test/ref_package
$ # Alternatively, load package from HTTP server
$ rclone server http ./output -addr 127.0.0.1:8000  # random HTTP server that supports range requests
$ dotnet run --project MaaUpdateEngine/MaaUpdateEngine.csproj -- smoke_test_input.json test/test_package http://127.0.0.1:8000/MAA-v5.4.2-alpha.1.d104.g2428a4610-win-x64-delta.tar.zst
```
