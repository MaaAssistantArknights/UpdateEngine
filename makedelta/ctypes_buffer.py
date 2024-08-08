# The buffer protocol ABI is stable since Python 3.11
from collections.abc import Buffer
import ctypes


class _Py_buffer(ctypes.Structure):
    _fields_ = [
        ('buf', ctypes.c_void_p),
        ('obj', ctypes.py_object),
        ('len', ctypes.c_ssize_t),
        ('readonly', ctypes.c_int),
        ('itemsize', ctypes.c_ssize_t),
        ('ndim', ctypes.c_int),
        ('format', ctypes.c_char_p),
        ('shape', ctypes.POINTER(ctypes.c_ssize_t)),
        ('strides', ctypes.POINTER(ctypes.c_ssize_t)),
        ('suboffsets', ctypes.POINTER(ctypes.c_ssize_t)),
        ('internal', ctypes.c_void_p),
    ]

_PyObject_GetBuffer = ctypes.pythonapi.PyObject_GetBuffer
_PyObject_GetBuffer.restype = ctypes.c_int
_PyObject_GetBuffer.argtypes = [ctypes.py_object, ctypes.POINTER(_Py_buffer), ctypes.c_int]

_PyBuffer_Release = ctypes.pythonapi.PyBuffer_Release
_PyBuffer_Release.restype = None
_PyBuffer_Release.argtypes = [ctypes.POINTER(_Py_buffer)]

_PyBUF_SIMPLE = 0
_PyBUF_WRITABLE = 1

class ctypes_simple_buffer:
    """It is recommended to use ctypes._CData.from_buffer for writable buffers"""
    __slots__ = ('obj', '_view')
    def __init__(self, obj: Buffer, writable: bool = False):
        self.obj = obj
        self._view = _Py_buffer()
        flags = _PyBUF_SIMPLE
        if writable:
            flags |= _PyBUF_WRITABLE
        _PyObject_GetBuffer(self.obj, ctypes.byref(self._view), flags)
        # throws if GetBuffer fails
    def __len__(self):
        if self._view is None:
            return 0
        return self._view.len
    def __enter__(self):
        # throws if GetBuffer fails
        return self
    def __exit__(self, exc_type, exc_value, traceback):
        self.close()
        return False
    def __del__(self):
        self.close()
    def close(self):
        if self._view is not None:
            _PyBuffer_Release(ctypes.byref(self._view))
            self._view = None
    @property
    def _as_parameter_(self):
        if self._view is None:
            raise ValueError("buffer is closed")
        return self._view.buf
