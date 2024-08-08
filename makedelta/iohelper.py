import hashlib
import tarfile
import os
import io
import random
from collections.abc import Buffer
from contextlib import contextmanager

def read_file(path: os.PathLike) -> bytes:
    with open(path, 'rb') as f:
        return f.read()

def write_file(path: os.PathLike, data: Buffer) -> None:
    tmpfile = path + f'.tmp.{os.getpid():X}{random.randint(0, 0x7FFFFFFF):08X}'
    with open(tmpfile, 'wb') as f:
        f.write(data)
    os.replace(tmpfile, path)

def write_tar_file(tf: tarfile.TarFile, arcname: str, data: Buffer):
    ti = tarfile.TarInfo(arcname)
    ti.size = len(data)
    tf.addfile(ti, io.BytesIO(data))

def sha256_file(filename: os.PathLike):
    buffer = bytearray(65536)
    with open(filename, "rb") as f:
        h = hashlib.sha256()
        while True:
            chunk_len = f.readinto(buffer)
            if chunk_len == 0:
                return h.hexdigest()
            h.update(buffer[:chunk_len])

def format_size(size):
    if size < 1024:
        return f"{size} B"
    size /= 1024
    if size < 1024:
        return f"{size:.1f} KiB"
    size /= 1024
    if size < 1024:
        return f"{size:.1f} MiB"
    size /= 1024
    return f"{size:.1f} GiB"

@contextmanager
def safe_output_filename(name: os.PathLike):
    tmpfile = str(name) + f'.tmp{os.getpid():X}{random.randint(0, 0x7FFFFFFF):08X}'
    try:
        yield tmpfile
        os.replace(tmpfile, name)
    except:
        if os.path.exists(tmpfile):
            os.unlink(tmpfile)
        raise

@contextmanager
def safe_output_fileobj(name: os.PathLike, mode: str = 'wb', *open_args, **open_kwargs):
    assert mode[0] == 'w'
    tmpfile = name + f'.tmp{os.getpid():X}{random.randint(0, 0x7FFFFFFF):08X}'
    try:
        with open(tmpfile, mode, *open_args, **open_kwargs) as f:
            yield f
        os.replace(tmpfile, name)
    except:
        if os.path.exists(tmpfile):
            os.unlink(tmpfile)
        raise
