import ctypes
import ctypes.util

from collections.abc import Buffer

from . import ctypes_buffer

CLEVEL_DEFAULT = 3
CLEVEL_MAX = 22

_libname = ctypes.util.find_library('zstd') or ctypes.util.find_library('libzstd')
if not _libname:
    raise ImportError('zstd library not found')
_lib = ctypes.CDLL(_libname)

_ZSTD_compress = _lib.ZSTD_compress
_ZSTD_compress.restype = ctypes.c_size_t
_ZSTD_compress.argtypes = [ctypes.c_void_p, ctypes.c_size_t, ctypes.c_void_p, ctypes.c_size_t, ctypes.c_int]

_ZSTD_compressBound = _lib.ZSTD_compressBound
_ZSTD_compressBound.restype = ctypes.c_size_t
_ZSTD_compressBound.argtypes = [ctypes.c_size_t]

_array_type = ctypes.c_uint8 * 0


def compress(data: Buffer, level: int = CLEVEL_DEFAULT) -> bytearray:
    with ctypes_buffer.ctypes_simple_buffer(data) as inbuf:
        outbuflen = _ZSTD_compressBound(len(inbuf))
        out = bytearray(outbuflen)
        outlen = _ZSTD_compress(_array_type.from_buffer(out), outbuflen, inbuf, len(inbuf), level)
    out = out[:outlen]
    return out
