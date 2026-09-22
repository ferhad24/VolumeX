"""
Prepares the VolumeX artwork masters from the files Ferhad supplied.

    assets/src-icon.png      speaker + X, no text   -> assets/volumex-icon.png (square master)
    assets/src-wordmark.png  speaker + X + VolumeX  -> assets/volumex-wordmark.png

For both: faint stray pixels left by the background removal are erased
(isolated blobs smaller than SPECK_PX at a low alpha threshold), and the art is
trimmed to its visible bounds. The icon is then centred on a transparent
square with a small margin, so Windows can scale it to 16 px without clipping.

Run build/make-icon.ps1 afterwards to regenerate the .ico and the 256 px png.
"""
from collections import deque
from pathlib import Path

import numpy as np
from PIL import Image

ASSETS = Path(__file__).resolve().parent.parent / "assets"
ALPHA_FLOOR = 3        # anything fainter than this is treated as empty
SPECK_PX = 400         # smaller isolated blobs are background-removal debris
ICON_MARGIN = 0.04     # of the square side, each edge


def clean(image: Image.Image) -> tuple[Image.Image, int]:
    rgba = np.array(image.convert("RGBA"))
    alpha = rgba[:, :, 3]
    mask = alpha > ALPHA_FLOOR
    h, w = mask.shape
    seen = np.zeros_like(mask)
    removed = 0

    for y in range(h):
        for x in range(w):
            if not mask[y, x] or seen[y, x]:
                continue
            blob = []
            queue = deque([(y, x)])
            seen[y, x] = True
            while queue:
                cy, cx = queue.popleft()
                blob.append((cy, cx))
                for ny, nx in ((cy + 1, cx), (cy - 1, cx), (cy, cx + 1), (cy, cx - 1)):
                    if 0 <= ny < h and 0 <= nx < w and mask[ny, nx] and not seen[ny, nx]:
                        seen[ny, nx] = True
                        queue.append((ny, nx))
            if len(blob) < SPECK_PX:
                ys, xs = zip(*blob)
                rgba[ys, xs, 3] = 0
                removed += len(blob)

    # Pixels below the floor are invisible anyway; zero them so trimming works.
    rgba[alpha <= ALPHA_FLOOR, 3] = 0
    return Image.fromarray(rgba, "RGBA"), removed


def trim(image: Image.Image) -> Image.Image:
    box = image.getchannel("A").point(lambda a: 255 if a > ALPHA_FLOOR else 0).getbbox()
    return image.crop(box) if box else image


def square(image: Image.Image, margin: float) -> Image.Image:
    w, h = image.size
    side = int(round(max(w, h) / (1 - 2 * margin)))
    canvas = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    canvas.paste(image, ((side - w) // 2, (side - h) // 2), image)
    return canvas


def main() -> None:
    icon, removed_icon = clean(Image.open(ASSETS / "src-icon.png"))
    icon = square(trim(icon), ICON_MARGIN)
    icon.save(ASSETS / "volumex-icon.png", optimize=True)
    print(f"volumex-icon.png      {icon.size[0]}x{icon.size[1]}  (removed {removed_icon} stray px)")

    wordmark, removed_mark = clean(Image.open(ASSETS / "src-wordmark.png"))
    wordmark = trim(wordmark)
    wordmark.save(ASSETS / "volumex-wordmark.png", optimize=True)
    print(f"volumex-wordmark.png  {wordmark.size[0]}x{wordmark.size[1]}  (removed {removed_mark} stray px)")

    # The app shows it at ~260 px; embedding the full master would add a
    # megabyte to the exe for pixels nobody sees. 2x covers high-DPI screens.
    small = wordmark.copy()
    small.thumbnail((560, 560), Image.LANCZOS)
    small.save(ASSETS / "volumex-wordmark-560.png", optimize=True)
    print(f"volumex-wordmark-560.png  {small.size[0]}x{small.size[1]}")


if __name__ == "__main__":
    main()
