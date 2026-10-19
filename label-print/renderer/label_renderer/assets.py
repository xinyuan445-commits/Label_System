from dataclasses import dataclass
from contextvars import ContextVar
from pathlib import Path

@dataclass(frozen=True)
class Asset:
    storage_filename: str

_asset_dir: ContextVar[Path] = ContextVar("asset_directory")

def assets_dir() -> Path:
    return _asset_dir.get()
