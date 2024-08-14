
import io
import json
import pathlib
import sys
import tempfile
import time
import typing
import functools
import dataclasses
import concurrent.futures
import os
import shutil
import tarfile
import struct
from collections import defaultdict

from . import pkgdiff
from . import manifest
from . import iohelper
from . import pkgprov
from . import concurrent_cache
from . import dataproc

from .model import AddFile, FileActionRecord, PatchFile, RemoveFile, ReplaceFile

assert sys.version_info >= (3, 11)  # for ZipFile(metadata_encoding)

patch_cache_dir = 'cache/patch_cache'
temp_extract_dir = 'cache/pkg_extract'
chunk_temp_dir = 'output/temp'
outdir = 'output'

@dataclasses.dataclass(slots=True)
class PackageContentDiff:
    base_version: list[str]
    patch_base_version: str
    actions: list[FileActionRecord]


@dataclasses.dataclass(slots=True)
class PackageContentVersionHistory:
    version_changes: list[PackageContentDiff]
    unchanged_entries: list[str]


@dataclasses.dataclass(slots=True)
class CachedBinaryPatch:
    patch_file: PatchFile
    to_version: str
    type: manifest.PatchType
    cached_deltafile: os.PathLike
    estimated_compressed_size: int


@dataclasses.dataclass(slots=True)
class _FileChangeRecord:
    since_version: str
    dedup_key: typing.Hashable

lru_cached_sha256_file = functools.lru_cache(maxsize=None)(iohelper.sha256_file)
lru_cached_pkgdiff = functools.lru_cache(maxsize=640)(pkgdiff.package_diff)



def sort_versions(versions, nonlinear_versions, zips):
    # TODO: automatically find versions not from this channel
    local_versions = [x for x in versions if x not in nonlinear_versions]

    def calc_weighted_avgdiff(version_list):
        diffsize = [len(lru_cached_pkgdiff(zips[a], zips[b])) for a, b in zip(version_list[:-1], version_list[1:])]
        weighted_avgdiff = sum(x * (len(diffsize)-i)/len(diffsize) for i, x in enumerate(diffsize))
        return weighted_avgdiff

    while nonlinear_versions:
        version_to_insert = nonlinear_versions.pop()
        weighted_avgdiff_after_insert = []
        for i in range(len(local_versions)+1):
            new_version_list = local_versions[:]
            new_version_list.insert(i, version_to_insert)
            weighted_avgdiff_after_insert.append(calc_weighted_avgdiff(new_version_list))
        best_insert = weighted_avgdiff_after_insert.index(min(weighted_avgdiff_after_insert))
        local_versions.insert(best_insert, version_to_insert)
    

    sorted_versions = local_versions[:]
    return sorted_versions

def need_binary_patch(entry: pkgprov.PackageEntry):
    return entry.name.endswith('.dll') or entry.name.endswith('.exe')

def _package_full_name(pkg: pkgprov.Package):
    components = [pkg.name, pkg.version]
    if pkg.variant:
        components.append(pkg.variant)
    return '-'.join(components)

@concurrent_cache.once_cache
def concurrent_extract_file(pkg: pkgprov.Package, name: str):
    version = pkg.version
    zipinfo = pkg.get_entry(name)
    extract_targetdir = pathlib.Path(temp_extract_dir) / _package_full_name(pkg) / pathlib.Path(version)
    arcpath = pathlib.Path(name)
    if arcpath.is_absolute():
        raise ValueError("path must be relative")
    if '..' in arcpath.parts:
        raise ValueError("path traversal not allowed")
    if arcpath.is_reserved():
        raise ValueError("invalid path in this system: " + name)
    extracted_file = extract_targetdir / arcpath
    extracted_file.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(mode='wb', dir=extracted_file.parent, prefix=extracted_file.name, delete=False) as f:
        with pkg.open_entry(zipinfo) as zf:
            shutil.copyfileobj(zf, f, 262144)
    os.utime(f.name, (time.time(), zipinfo.mtime))
    os.replace(f.name, extracted_file)
    result = str(extracted_file)
    return result


def _entry_based_random(oldcrc: pkgprov.PackageEntry):
    return f"{oldcrc.size:08X}{oldcrc.checksum[:4].hex().upper()}"

def _patch_filename(patchfile: PatchFile, oldent: pkgprov.PackageEntry, newent: pkgprov.PackageEntry, extname: str):
    filename = f'{os.path.basename(patchfile.path)}-{_entry_based_random(oldent)}-{_entry_based_random(newent)}' + extname
    return os.path.join(patch_cache_dir, patchfile.from_version, filename)

def make_patch_zstd(patchfile: PatchFile, oldent: pkgprov.PackageEntry, newent: pkgprov.PackageEntry, to_version_: str, orig_file_: os.PathLike, new_file_: os.PathLike) -> CachedBinaryPatch:
    patchfilename = _patch_filename(patchfile, oldent, newent, '.zst')
    if not os.path.exists(patchfilename):
        os.makedirs(os.path.dirname(patchfilename), exist_ok=True)
        dataproc.zstd_generate_patch(orig_file_, new_file_, patchfilename)
    patchsize = os.path.getsize(patchfilename)

    # zstd minimum encoded stream is ~100 bytes per input MiB: https://github.com/facebook/zstd/issues/2576#issuecomment-818927743
    # the output file can be further compressed if it hits this limit
    # since we are including the patch file in compressed tar, estimate compressed size for patch type selection
    nested_patchfilename = patchfilename + '.zst'
    if patchsize < os.path.getsize(new_file_) * 0.0002:
        if not os.path.exists(nested_patchfilename):
            dataproc.zstd_compress_file(patchfilename, nested_patchfilename)
        patchsize = os.path.getsize(nested_patchfilename)

    return CachedBinaryPatch(patchfile, to_version_, "zstd", patchfilename, patchsize)

def make_patch_bsdiff(patchfile: PatchFile, oldent: pkgprov.PackageEntry, newent: pkgprov.PackageEntry, to_version_: str, orig_file_: os.PathLike, new_file_: os.PathLike) -> CachedBinaryPatch:
    patchfilename = _patch_filename(patchfile, oldent, newent, '.bsdiffx')
    if not os.path.exists(patchfilename):
        os.makedirs(os.path.dirname(patchfilename), exist_ok=True)
        dataproc.bsdiff_generate_patch(orig_file_, new_file_, patchfilename)
    return CachedBinaryPatch(patchfile, to_version_, "bsdiff", patchfilename, os.path.getsize(patchfilename))

# FIXME: the batch version doesn't perform better than single file version even in batch mode
# def make_patch_bsdiff_batch(patchfile: PatchFile, orig_file_: os.PathLike, oldcrc: int, new_version_file_crc: list[tuple[str, os.PathLike, int]]) -> list[GeneratedPatchFile]:
#     args = []
#     results = []
#     uncached_versions = []
#     for to_version_, new_file_, newcrc in new_version_file_crc:
#         tempfilename = f'versions/temp/patches/{os.path.basename(patchfile.path)}-{oldcrc:08X}-{newcrc:08X}.bsdiffx'
#         if os.path.exists(tempfilename):
#             results.append(GeneratedPatchFile(patchfile, to_version_, BinaryDeltaType.BSDIFFX, tempfilename, os.path.getsize(tempfilename)))
#         else:
#             uncached_versions.append((to_version_, new_file_, tempfilename))
#             os.makedirs(os.path.dirname(tempfilename), exist_ok=True)
#             args.extend([new_file_, tempfilename])
#
#     if uncached_versions:
#         subprocess.run([MAA_BSDIFF_BATCH_EXECUTABLE, orig_file_, *args], check=True)
#
#     for to_version_, new_file_, tempfilename in uncached_versions:
#         results.append(GeneratedPatchFile(patchfile, to_version_, BinaryDeltaType.BSDIFFX, tempfilename, os.path.getsize(tempfilename)))
#
#     return results


def find_best_patch(pkgs: dict[str, pkgprov.Package], delta_records: list[PackageContentDiff], latest_version, sorted_previous_versions: list[str]) -> dict[PatchFile, CachedBinaryPatch]:
    each_patch: defaultdict[PatchFile, list[CachedBinaryPatch]] = defaultdict(list)

    executor = concurrent.futures.ThreadPoolExecutor(max_workers=os.cpu_count())
    completed_jobs = 0

    single_futures: list[concurrent.futures.Future[CachedBinaryPatch]] = []
    batch_futures: list[concurrent.futures.Future[list[CachedBinaryPatch]]] = []

    def future_callback(future):
        nonlocal completed_jobs
        completed_jobs += 1
        report_progress()

    def report_progress():
        sys.stderr.write(f"\rfind_best_patch: {completed_jobs}/{len(single_futures)+len(batch_futures)}")
        sys.stderr.flush()
    
    def as_future(callable, *args, **kwargs):
        future = executor.submit(callable, *args, **kwargs)
        future.add_done_callback(future_callback)
        return future

    file_changelog: defaultdict[str, list[_FileChangeRecord]] = defaultdict(list)
    file_hash_to_version_map: defaultdict[tuple[str, typing.Hashable], list[str]] = defaultdict(list)

    for delta_record in reversed(delta_records):
        for patch_file in delta_record.actions:
            if not isinstance(patch_file, PatchFile):
                continue
            file_info = pkgs[patch_file.from_version].get_entry(patch_file.path)
            file_dedup_key = file_info
            # if file_dedup_key not in file_changeset[patch_file.path]:
            #     file_changeset[patch_file.path].add(file_dedup_key)
            file_changelog[patch_file.path].append(_FileChangeRecord(patch_file.from_version, file_dedup_key))
            file_hash_to_version_map[(patch_file.path, file_dedup_key)].append(patch_file.from_version)


    for filename in file_changelog:
        file_info = pkgs[latest_version].get_entry(filename)
        file_dedup_key = file_info
        file_hash_to_version_map[(filename, file_dedup_key)].append(latest_version)

    # for each source version:
    #  find all files need to patch
    #  find target versions for each file
    #  dedup target versions
    #  find smallest patch among target versions


    for delta_record in delta_records:
        for patch_file in delta_record.actions:
            if not isinstance(patch_file, PatchFile):
                continue
            source_file_info = pkgs[patch_file.from_version].get_entry(patch_file.path)
            source_file_dedup_key = source_file_info
            target_versions = [latest_version]
            source_version_index = sorted_previous_versions.index(patch_file.from_version)

            for change_record in file_changelog[patch_file.path]:
                # don't patch to older version
                if sorted_previous_versions.index(change_record.since_version) >= source_version_index:
                    continue
                if change_record.since_version == patch_file.from_version:
                    continue
                if change_record.since_version not in target_versions:
                    target_versions.append(change_record.since_version)
            
            # find if we can forward to a newer version
            # to handle case like A -> B -> A
            versions_with_source_file = [x for x in file_hash_to_version_map[(patch_file.path, source_file_dedup_key)] if x in target_versions]

            if versions_with_source_file:
                forward_to_version = versions_with_source_file[-1]
                each_patch[patch_file].append(CachedBinaryPatch(patch_file, forward_to_version, "copy", None, 0))
                continue

            # dedup target versions based on file content
            # for same content, only keep the latest version
            dedup_set = set()
            dedupped_target_versions = []
            for version in target_versions:
                target_file_info = pkgs[version].get_entry(patch_file.path)
                target_file_dedup_key = target_file_info
                if target_file_dedup_key in dedup_set:
                    continue
                dedup_set.add(target_file_dedup_key)
                dedupped_target_versions.append(version)
            
            # find best patch for each dedupped target version
            for version in dedupped_target_versions:
                target_file_info = pkgs[version].get_entry(patch_file.path)
                target_file_dedup_key = target_file_info
                orig_file = concurrent_extract_file(pkgs[patch_file.from_version], patch_file.path)
                new_file = concurrent_extract_file(pkgs[version], patch_file.path)
                single_futures.append(as_future(make_patch_zstd, patch_file, source_file_dedup_key, target_file_dedup_key, version, orig_file, new_file))
                single_futures.append(as_future(make_patch_bsdiff, patch_file, source_file_dedup_key, target_file_dedup_key, version, orig_file, new_file))

    report_progress()

    def process_future_result(result: CachedBinaryPatch):
        each_patch[result.patch_file].append(result)

    try:
        for future in single_futures:
            result = future.result()
            process_future_result(result)
        for future in batch_futures:
            results = future.result()
            for result in results:
                process_future_result(result)
    except KeyboardInterrupt:
        sys.stderr.write("\n")
        sys.stderr.flush()
        executor.shutdown(wait=False, cancel_futures=True)
        raise

    executor.shutdown(wait=True)

    report_progress()

    sys.stderr.write("\n")

    resolved_patch ={k: min(vs, key=lambda x: x.estimated_compressed_size) for k, vs in each_patch.items()}
    return resolved_patch


class AmalgamatedPatch:
    def __init__(self, manifest: manifest.PackageManifest, for_version: list[str]):
        self.manifest = manifest
        self.chunks = []
        self.offset = 0
        self.for_version = for_version
    def add_chunk(self, target: manifest.ChunkTarget, compressed_chunk: os.PathLike):
        size = os.path.getsize(compressed_chunk)
        chunk_schema: manifest.Chunk = {"target": target, "offset": self.offset, "size": size, "hash": "sha256:" + lru_cached_sha256_file(compressed_chunk)}
        self.chunks.append((chunk_schema, compressed_chunk))
        self.offset += size
    def build(self, outfile: os.PathLike):
        delta_manifest: manifest.DeltaPackageManifest = {
            "for_version": self.for_version,
            "chunks": [x[0] for x in self.chunks]
        }
        bio = io.BytesIO()            
        tf = tarfile.open(fileobj=bio, mode='w', format=tarfile.PAX_FORMAT)
        manifest_bytes = json.dumps(self.manifest, indent=None).encode('utf-8')
        delta_manifest_bytes = json.dumps(delta_manifest, indent=None).encode('utf-8')
        iohelper.write_tar_file(tf, f'.maa_update/packages/{self.manifest["name"]}/manifest.json', manifest_bytes)
        iohelper.write_tar_file(tf, f'.maa_update/delta/{self.manifest["name"]}/{self.manifest["version"]}/delta_manifest.json', delta_manifest_bytes)
        manifest_chunk = bio.getvalue()
        compressed_manifest_chunk = dataproc.zstd_compress_bytes(manifest_chunk)

        header = b"\x5A\x2A\x4D\x18\x08\x00\x00\x00MUE1" + struct.pack('<I', len(compressed_manifest_chunk))

        with iohelper.safe_output_fileobj(outfile, 'wb') as f:
            f.write(header)
            f.write(compressed_manifest_chunk)
            for _, chunkfile in self.chunks:
                with open(chunkfile, 'rb') as cf:
                    shutil.copyfileobj(cf, f)


def generate_file_history(version_order: list[str], packages: typing.Mapping[str, pkgprov.Package]):
    latest, *previous = version_order
    latest_entries = set(packages[latest].get_entries())
    latest_names = set(x.name for x in latest_entries)

    global_replaced_names = set()
    global_removed_names = set()
    last_changed_entries = set()

    # to track unchanged files
    changed_names = set()
    processed_versions = []

    delta_records: list[PackageContentDiff] = []

    for version in previous:
        current_entries = packages[version].get_entries()
        current_names = set(x.name for x in current_entries)

        # set to keep track of changed files between this version and latest version
        changed_entries = set()

        for_version = [x for x in previous if x not in processed_versions]
        assert version in for_version
        actions = []
        # print(f"To update from {version} or older:")
        for entry in current_entries:
            entry_name = entry.name
            if entry in latest_entries:
                # (filename, EntryHashable) matched - common file
                continue
            if entry_name in latest_names:
                # file in current version, but content changed
                if need_binary_patch(entry):
                    # check if the file is different from the newer version
                    # case like A -> B -> A is handled later
                    actions.append(PatchFile(version, entry_name))
                    if entry not in last_changed_entries:
                        changed_entries.add(entry)
                else:
                    if entry_name not in global_replaced_names:
                        global_replaced_names.add(entry_name)
                        actions.append(ReplaceFile(entry_name))
                changed_names.add(entry_name)
            else:
                # file not in current version i.e. removed file
                if entry_name not in global_removed_names:
                    global_removed_names.add(entry_name)
                    actions.append(RemoveFile(entry_name))
        
        # find names in latest but not current - i.e. new files
        new_names = latest_names - current_names
        for entry_name in sorted(list(new_names)):
            if entry_name not in global_replaced_names:
                actions.append(AddFile(entry_name))
                global_replaced_names.add(entry_name)
            changed_names.add(entry_name)
        # if new_names:
        #     for entry in sorted(list(new_names)):
        #         print(f"  ADD      {entry}")
        # last_new_names = new_names.copy()
        last_changed_entries = changed_entries.copy()

        delta_records.append(PackageContentDiff(for_version, version, actions))
        processed_versions.insert(0, version)
    unchanged_names = sorted(latest_names - changed_names)
    return PackageContentVersionHistory(delta_records, unchanged_names)

def copy_from_pkg_to_tar(zipfile_: pkgprov.Package, name: str, tarfile_: tarfile.TarFile):
    file_info = zipfile_.get_entry(name)
    ti = tarfile.TarInfo(name)
    ti.size = file_info.size
    ti.mtime = file_info.mtime
    ti.mode = file_info.mode
    with zipfile_.open_entry(name) as f:
        tarfile_.addfile(ti, f)


def main(package_provider: pkgprov.PackageProvider, package_name: str, package_variant: str | None, versions: list[str], nonlinear_versions: list[str]):
    pkgs = { x: package_provider.open_package(package_name, x, package_variant) for x in versions }

    report_file = open(os.path.join(outdir, 'delta_report.txt'), 'w', encoding='utf-8')

    def print_and_report(*args, **kwargs):
        print(*args, **kwargs)
        if report_file is not sys.stdout:
            print(*args, **kwargs, file=report_file)

    def report(*args, **kwargs):
        print(*args, **kwargs, file=report_file)

    latest, *previous = versions

    package_manifest: manifest.PackageManifest = {
        "name": package_name,
        "version": latest,
        "variant": package_variant,
    }

    print_and_report("Target version:", latest)
    print_and_report("Previous versions:")
    for version in previous:
        print_and_report(f"  {version}")
    
    previous = sort_versions(previous, nonlinear_versions, pkgs)

    print_and_report("Sorted previous versions:")
    for version in previous:
        print_and_report(f"  {version}")

    report("")

    file_history = generate_file_history([latest, *previous], pkgs)

    delta_records = file_history.version_changes
    unchanged_names = file_history.unchanged_entries

    patch_strategy = find_best_patch(pkgs, delta_records, latest, previous)

    for delta_record in delta_records:
        report(f"To update from version {delta_record.base_version}")
        for action in delta_record.actions:
            report(f"  {action}")
        report("")

    # pprint.pprint(delta_records)
    report("Binary patch strategy:")
    patchfile_to_str: typing.Callable[[PatchFile]] = lambda patch_file: f"{patch_file.from_version}/{patch_file.path}"
    keys = sorted(patch_strategy.keys(), key=lambda x: previous.index(x.from_version))
    for key in keys:
        gpf = patch_strategy[key]
        report(f"  {patchfile_to_str(key)} \t->\t {gpf.to_version} \t({gpf.type}, est. compressed {iohelper.format_size(gpf.estimated_compressed_size)})")
    
    report("Unchanged files:")
    for keep_name in unchanged_names:
            report(f"  KEEP     {keep_name}")

    executor = concurrent.futures.ThreadPoolExecutor(max_workers=os.cpu_count())

    chunk_count = len(delta_records) + 3  # header + versions + patch fallback + unchanged files
    seq_length = len(str(chunk_count))
    def format_chunkseq(seq):
        return ("0" * seq_length + str(seq))[-seq_length:]

    os.makedirs(chunk_temp_dir, exist_ok=True)


    def create_delta_chunk(chunkfile, delta_record: PackageContentDiff):
        print("creating delta chunk", chunkfile, flush=True)
        patch_base = delta_record.patch_base_version
        chunk_manifest : manifest.ChunkManifest = {
            "patch_base": patch_base,
            "base": delta_record.base_version,
            "patch_files": [],
            "remove_files": [],
        }
        with iohelper.safe_output_fileobj(chunkfile, 'wb') as outfile:
            tf = tarfile.open(fileobj=outfile, mode='w', format=tarfile.PAX_FORMAT)
            pending_files: list[tuple[str, str]] = []
            for action in delta_record.actions:
                if isinstance(action, RemoveFile):
                    chunk_manifest["remove_files"].append(action.path)
                elif isinstance(action, PatchFile):
                    ti = tarfile.TarInfo(action.path)
                    patch = patch_strategy[action]
                    if patch.cached_deltafile is not None:
                        ti.size = os.path.getsize(patch.cached_deltafile)
                    old_file = concurrent_extract_file(pkgs[action.from_version], action.path)
                    old_size = os.path.getsize(old_file)
                    old_hash = "sha256:" + lru_cached_sha256_file(old_file)

                    if patch.type == "copy":
                        new_size = old_size
                        new_hash = old_hash
                    else:
                        new_file = concurrent_extract_file(pkgs[patch.to_version], action.path)
                        new_size = os.path.getsize(new_file)
                        new_hash = "sha256:" + lru_cached_sha256_file(new_file)

                    if patch.cached_deltafile is not None:
                        patch_hash = lru_cached_sha256_file(patch.cached_deltafile)
                        archive_path = f".maa_update/temp/{os.path.basename(action.path)}.{patch_hash[:8]}.{patch.type}"
                        pending_files.append((patch.cached_deltafile, archive_path))
                    else:
                        archive_path = ""

                    pf: manifest.PatchFile = {
                        "file": action.path,
                        "patch": archive_path,
                        "patch_type": patch.type,
                        "old_hash": old_hash,
                        "old_size": old_size,
                        "new_version": patch.to_version,
                        "new_hash": new_hash,
                        "new_size": new_size,
                    }
                    chunk_manifest["patch_files"].append(pf)
            iohelper.write_tar_file(tf, f'.maa_update/delta/{package_name}/{patch_base}/chunk_manifest.json', json.dumps(chunk_manifest, indent=None).encode('utf-8'))
            for filename, archive_path in pending_files:
                tf.add(filename, arcname=archive_path)
            for action in delta_record.actions:
                if isinstance(action, AddFile) or isinstance(action, ReplaceFile):
                    copy_from_pkg_to_tar(pkgs[latest], action.path, tf)
            # don't close the tarfile to avoid writing EOF mark
        dataproc.zstd_compress_file(chunkfile, f'{chunkfile}.zst')

    delta_chunks = []
    futures = []

    # create delta chunks
    for seq, delta_record in enumerate(delta_records, 1):
        chunkfile = f'{chunk_temp_dir}/{format_chunkseq(seq)}-{delta_record.patch_base_version}.tar'
        delta_chunks.append(chunkfile + '.zst')
        futures.append(executor.submit(create_delta_chunk, chunkfile, delta_record))

    # create fallback patch chunk
    patch_fallback_chunk = f'{chunk_temp_dir}/{format_chunkseq(chunk_count - 1)}-delta-fallback.tar'
    compressed_patch_fallback_chunk = patch_fallback_chunk + '.zst'

    def create_patch_fallback_chunk():
        patched_files = sorted(set(x.path for x in patch_strategy))
        print("creating patch fallback chunk", patch_fallback_chunk, flush=True)
        with iohelper.safe_output_fileobj(patch_fallback_chunk, 'wb') as outfile:
            tf = tarfile.open(fileobj=outfile, mode='w', format=tarfile.PAX_FORMAT)
            for filename in patched_files:
                cached_file = concurrent_extract_file(pkgs[latest], filename)
                tf.add(cached_file, arcname=filename)
            # don't close the tarfile to avoid writing EOF mark
        dataproc.zstd_compress_file(patch_fallback_chunk, patch_fallback_chunk + '.zst')
    futures.append(executor.submit(create_patch_fallback_chunk))

    # create unchanged files chunk
    unchanged_chunk = f'{chunk_temp_dir}/{format_chunkseq(chunk_count)}-delta-unchanged.tar'
    compressed_unchanged_chunk = unchanged_chunk + '.zst'
    def create_unchanged_chunk():
        print("creating unchanged chunk", unchanged_chunk, flush=True)
        with iohelper.safe_output_fileobj(unchanged_chunk, 'wb') as outfile:
            tf = tarfile.open(fileobj=outfile, mode='w', format=tarfile.PAX_FORMAT)
            for filename in unchanged_names:
                copy_from_pkg_to_tar(pkgs[latest], filename, tf)
            # write EOF mark for the last chunk
            tf.close()
        dataproc.zstd_compress_file(unchanged_chunk, unchanged_chunk + '.zst')
    futures.append(executor.submit(create_unchanged_chunk))

    for future in futures:
        # collect exceptions
        future.result()
    executor.shutdown(wait=True)


    print("Creating delta package")

    amal = AmalgamatedPatch(package_manifest, previous)

    for i, delta_record in enumerate(delta_records):
        chunkfile = delta_chunks[i]
        target = delta_records[i].base_version
        amal.add_chunk(target, chunkfile)
    
    amal.add_chunk("patch_fallback", compressed_patch_fallback_chunk)
    amal.add_chunk("fallback", compressed_unchanged_chunk)

    amal.build(os.path.join(outdir, f'{package_name}-{latest}{'-' + package_variant if package_variant else ''}-delta.tar.zst'))

    

if __name__ == '__main__':
    main()
