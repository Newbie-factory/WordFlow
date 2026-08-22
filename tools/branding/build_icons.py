#!/usr/bin/env python3
"""Build WordFlow's two deterministic, multi-resolution Windows icons."""

from __future__ import annotations

import argparse
from pathlib import Path

from PIL import Image, ImageOps


ICON_SIZES = (16, 24, 32, 48, 64, 128, 256)
MASTER_SIZE = 512
WORDMARK_HORIZONTAL_MARGIN = 0.08


def normalized_rgba(path: Path) -> Image.Image:
    with Image.open(path) as source:
        return ImageOps.exif_transpose(source).convert("RGBA")


def prepare_photo(path: Path) -> Image.Image:
    image = normalized_rgba(path)
    side = min(image.size)
    left = (image.width - side) // 2
    top = (image.height - side) // 2
    square = image.crop((left, top, left + side, top + side))
    return square.resize((MASTER_SIZE, MASTER_SIZE), Image.Resampling.LANCZOS)


def prepare_wordmark(path: Path) -> Image.Image:
    image = normalized_rgba(path)
    content = image.getbbox()
    if content is None:
        raise ValueError(f"Wordmark source has no visible content: {path}")

    # Retain every visible source pixel while removing only transparent padding.
    image = image.crop(content)
    maximum_width = round(MASTER_SIZE * (1 - (2 * WORDMARK_HORIZONTAL_MARGIN)))
    maximum_height = MASTER_SIZE
    scale = min(maximum_width / image.width, maximum_height / image.height)
    rendered_size = (
        max(1, round(image.width * scale)),
        max(1, round(image.height * scale)),
    )
    rendered = image.resize(rendered_size, Image.Resampling.LANCZOS)
    canvas = Image.new("RGBA", (MASTER_SIZE, MASTER_SIZE), (0, 0, 0, 0))
    position = (
        (MASTER_SIZE - rendered.width) // 2,
        (MASTER_SIZE - rendered.height) // 2,
    )
    canvas.alpha_composite(rendered, position)
    return canvas


def save_icon(image: Image.Image, destination: Path) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    image.save(
        destination,
        format="ICO",
        sizes=[(size, size) for size in ICON_SIZES],
        bitmap_format="png",
    )


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--photo", required=True, type=Path)
    parser.add_argument("--wordflow", required=True, type=Path)
    parser.add_argument("--out-dir", required=True, type=Path)
    return parser.parse_args()


def main() -> None:
    args = parse_args()
    photo = args.photo.resolve(strict=True)
    wordflow = args.wordflow.resolve(strict=True)
    output = args.out_dir.resolve()
    generated_paths = {
        output / "wordflow-photo.ico",
        output / "wordflow-classic.ico",
    }
    if photo in generated_paths or wordflow in generated_paths:
        raise ValueError("Generated icon paths must not overwrite either source image.")

    save_icon(prepare_photo(photo), output / "wordflow-photo.ico")
    save_icon(prepare_wordmark(wordflow), output / "wordflow-classic.ico")


if __name__ == "__main__":
    main()
