## Dependencies

Zstandard: https://github.com/facebook/zstd

Customized bsdiff: https://github.com/MaaAssistantArknights/bsdiff

set environment variable `MAA_BSDIFF` to the path of customized bsdiff executable

## Usage

Prepare test data

```bash
$ aria2c -i testdata.aria2
```

Run makedelta

```bash
$ python -m makedelta version_list_all.txt version_list_nonlinear.txt
```
