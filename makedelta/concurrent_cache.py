import threading
import concurrent.futures
import functools

def __make_key(args: tuple, kwargs: dict, _kwargs_delimiter=object()):
    sorted_kwargs = sorted(kwargs.items(), key=lambda x: x[0])
    return (*args, _kwargs_delimiter, *(x for pair in sorted_kwargs for x in pair))

def _make_key(args: tuple, kwargs: dict):
    return __make_key(args, kwargs)

def once_cache(func):
    completed = {}
    pending = {}
    lock = threading.RLock()

    def wrapper(*args, **kwargs):
        cache_key = _make_key(args, kwargs)
        need_compute = False
        with lock:
            if cache_key in completed:
                return completed[cache_key]
            elif cache_key in pending:
                future = pending[cache_key]
            else:
                need_compute = True
                future = concurrent.futures.Future()
                pending[cache_key] = future
        if need_compute:
            try:
                result = func(*args)
                with lock:
                    completed[cache_key] = result
                    del pending[cache_key]
                future.set_result(result)
            except Exception as e:
                with lock:
                    del pending[cache_key]
                future.set_exception(e)
    
        return future.result()
    return functools.update_wrapper(wrapper, func)
