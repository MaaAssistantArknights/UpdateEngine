from typing import TypedDict, Literal, NotRequired

PatchType = Literal["zstd", "bsdiff", "copy"]

ChunkTarget = list[str] | Literal["patch_fallback", "fallback"]

class Chunk(TypedDict):
    target: ChunkTarget
    """The target that this chunk can be applied to.

    * If target is a sequence of strings, this chunk can be applied to any of the versions in the list.
    * If target is "patch_fallback", this chunk contains complete files as fallback for binary patching.
    * If target is "fallback", this chunk contains all extra files to make a complete package.
    """
    offset: int
    """The offset of the compressed chunk in the package file, relative to the offset of manifest chunk"""
    size: int
    """The size of the compressed chunk"""
    hash: str

class PackageManifest(TypedDict):
    """The package manifest
    
    * has path `.maa_update/packages/<package_name>.json`
    * Consumer MAY use alternative mathods to populate the manifest and skip extracting this file
    * MUST be the first entry in a package file, compressed in a separate metadata chunk
    * the length of this compressed chunk is stored in the beignning of the package file as ignorable data for the choosed compression format
    
    ### Header
    
    For zstd-compressed package, the header is a skippable frame with the following structure:

    ```plaintext
    (total 16 bytes)
    5A 2A 4D 18      // magic number of zstd skippable frame
    08 00 00 00      // the length of following data
    'M' 'U' 'E' '1'  // magic number of MAA Update Engine package version 1
    ?? ?? ?? ??      // the length of the compressed package manifest chunk (little-endian)
    ```

    For gzip-compressed package, the header is a gzip member with comment and decompressed length of zero:

    ```plaintext
    (total 29 bytes)
    1F 8B 08 10 'M' 'U' 'E' '1' 00 FF  // gzip member header, flags=FCOMMENT, mtime='MUE1', xfl=0, OS=FF
    sprintf("%08X", len(compressed_manifest_chunk)) 00  // zero terminated comment
    03 00        // deflate(b'')
    00 00 00 00  // CRC32(b'')
    00 00 00 00  // decompressed length
    ```

    Consumer MUST NOT accept a package header in other valid representations of the compression format.
    (e.g. zstd skippable frame with different magic number, gzip member with different flags, etc.)
    
    A manifest chunk larger than 2^32 bytes is not supported in this version of the specification.
    """
    name: str
    """The name of the package."""
    version: str
    """The version of the package."""
    variant: NotRequired[str]
    """The variant of the package. This is used to differentiate between different target architecture/system for binary packages."""

class DeltaPackageManifest(TypedDict):
    """The delta package manifest
    
    * has path `.maa_update/delta/<RANDOM_NAME>/manifest.json`
    * MUST be the second entry in a delta package file, following the package entry, compressed in the metadata chunk"""
    for_version: list[str]
    """The versions that this delta package can be applied to.
    Consumer SHOULD stop processing the rest of the delta package if the current version is not in this list.
    If the package is streamed from a remote machine, consumer MAY stop receiving more data from remote."""
    chunks: list[Chunk]

class PatchFile(TypedDict):
    file: str
    """The file to patch"""
    patch: str
    """The patch entry in archive, MUST NOT conflict with other regular files.
    
    Programmed consumer SHOULD NOT keep the extracted patch file (if any) on file system after applying the update package.
    
    In the reference implementation, patch entries is stored as `.maa_update/temp/<random-prefix>{basename(file)}<random-suffix>.<patch_type>`
    """
    old_size: int
    """The size of the file before patch"""
    old_hash: str
    """The hash of the file before patch"""
    new_version: str
    """The version of the file after patch.

    If new_version is not package version, Consumer should check previous chunks with base_version=new_version for further patching."""
    new_size: int
    """The size of the file after patch"""
    new_hash: str
    """The hash of the file after patch"""
    patch_type: PatchType

class ChunkManifest(TypedDict):
    patch_base: str
    """if current version is `patch_base`, apply `patch_files` from this chunk"""
    base: list[str]
    """if current version is in `base`, apply `remove_files` and new files from this chunk"""
    remove_files: list[str]
    patch_files: list[PatchFile]
