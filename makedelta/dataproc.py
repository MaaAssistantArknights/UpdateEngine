import shutil
import os
import random
import subprocess

from . import iohelper


ZSTD_EXECUTABLE = os.environ.get("ZSTD", "zstd")
MAA_BSDIFF_EXECUTABLE = os.environ.get("MAA_BSDIFF", "maa_bsdiff")
# the batch version doesn't perform better than single file version even in batch mode
# MAA_BSDIFF_BATCH_EXECUTABLE = os.environ.get("MAA_BSDIFF_BATCH", r"D:\projects\bsdiff.net\src\BsDiffTool\bin\Release\net8.0\win-x64\publish\BsDiffTool.exe")

if not shutil.which(ZSTD_EXECUTABLE):
    raise Exception(f"ZSTD executable not found: {ZSTD_EXECUTABLE}")
if not shutil.which(MAA_BSDIFF_EXECUTABLE):
    raise Exception(f"MAA_BSDIFF executable not found: {MAA_BSDIFF_EXECUTABLE}")

def zstd_compress_file(infile, outfile):
    with iohelper.safe_output_filename(outfile) as tmpfile:
        subprocess.run([ZSTD_EXECUTABLE, '-q', '--ultra', '-22', '-f', infile, '-o', tmpfile], check=True)

try:
    from .zstd_ctypes import compress as _zstd_compress_bytes
    def zstd_compress_bytes(data: bytes) -> bytes:
        return _zstd_compress_bytes(data, 22)
except ImportError:
    def zstd_compress_bytes(data: bytes) -> bytes:
        result = subprocess.run([ZSTD_EXECUTABLE, '-q', '--ultra', '-22', '-c', f'--stream-size={len(data)}'], input=data, stdout=subprocess.PIPE, check=True)
        return result.stdout

def zstd_generate_patch(orig_file, new_file, patchfile):
    with iohelper.safe_output_filename(patchfile) as tmpfile:
        subprocess.run([ZSTD_EXECUTABLE, '-q', '--ultra', '-22', '-f', '--patch-from', orig_file, new_file, '-o', tmpfile], check=True)

def bsdiff_generate_patch(orig_file, new_file, patchfile):
    with iohelper.safe_output_filename(patchfile) as tmpfile:
        subprocess.run([MAA_BSDIFF_EXECUTABLE, orig_file, new_file, tmpfile], check=True)
