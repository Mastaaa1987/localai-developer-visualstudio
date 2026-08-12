from __future__ import annotations

from pathlib import Path
from typing import Iterable

from PIL import Image, ImageDraw, ImageFont


SOURCE = Path(r"D:\Bandicam.7.1.3.2456.x64.Portable\Screenshots")
OUTPUT = Path(__file__).resolve().parents[1] / "assets" / "marketplace" / "localai-developer-walkthrough.gif"

FRAMES = [
    ("bandicam 2026-08-12 02-14-23-716.jpg", "Open Tools > LocalAI Developer Settings"),
    ("bandicam 2026-08-12 02-14-38-875.jpg", "Configure language, approval policy, and your AI provider"),
    ("bandicam 2026-08-12 02-15-24-028.jpg", "Open Tools > LocalAI Developer"),
    ("bandicam 2026-08-12 02-16-30-006.jpg", "Connect the backend to the active solution"),
    ("bandicam 2026-08-12 02-17-10-050.jpg", "Enter a clear development goal"),
    ("bandicam 2026-08-12 02-17-12-142.jpg", "Create a structured development plan"),
    ("bandicam 2026-08-12 02-17-32-175.jpg", "Review the generated plan and context budgets"),
    ("bandicam 2026-08-12 02-17-34-272.jpg", "Start or continue the workflow"),
    ("bandicam 2026-08-12 02-17-46-624.jpg", "Track the active step and overall progress"),
    ("bandicam 2026-08-12 02-17-52-137.jpg", "Review each generated diff before applying it"),
    ("bandicam 2026-08-12 02-18-28-133.jpg", "Inspect applied files directly in the editor"),
    ("bandicam 2026-08-12 02-18-16-432.jpg", "Complete validation and keep transaction history"),
    ("bandicam 2026-08-12 02-18-35-204.jpg", "Preview a transaction before rolling it back"),
    ("bandicam 2026-08-12 02-19-43-710.jpg", "Rollback restores the previous workspace state"),
]

WIDTH = 800
SCREEN_HEIGHT = 450
CAPTION_HEIGHT = 112
BACKGROUND = (16, 17, 24)
TEXT = (245, 247, 255)
MUTED = (170, 179, 205)
CYAN = (44, 214, 238)
PURPLE = (153, 92, 246)


def font(size: int, bold: bool = False) -> ImageFont.FreeTypeFont:
    windows_fonts = Path(r"C:\Windows\Fonts")
    name = "seguisb.ttf" if bold else "segoeui.ttf"
    return ImageFont.truetype(str(windows_fonts / name), size)


def fit_text(draw: ImageDraw.ImageDraw, text: str, max_width: int) -> ImageFont.FreeTypeFont:
    for size in range(25, 17, -1):
        candidate = font(size, bold=True)
        if draw.textbbox((0, 0), text, font=candidate)[2] <= max_width:
            return candidate
    return font(17, bold=True)


def render_frame(path: Path, caption: str, index: int, total: int) -> Image.Image:
    with Image.open(path) as source:
        screenshot = source.convert("RGB").resize((WIDTH, SCREEN_HEIGHT), Image.Resampling.LANCZOS)

    canvas = Image.new("RGB", (WIDTH, SCREEN_HEIGHT + CAPTION_HEIGHT), BACKGROUND)
    canvas.paste(screenshot, (0, 0))
    draw = ImageDraw.Draw(canvas)

    draw.rectangle((0, SCREEN_HEIGHT, 6, canvas.height), fill=CYAN)
    draw.text(
        (24, SCREEN_HEIGHT + 13),
        f"LOCALAI DEVELOPER  /  QUICK START  /  STEP {index + 1:02d}",
        font=font(12, bold=True),
        fill=MUTED,
    )
    caption_font = fit_text(draw, caption, WIDTH - 48)
    draw.text((24, SCREEN_HEIGHT + 36), caption, font=caption_font, fill=TEXT)

    left = 24
    right = WIDTH - 24
    y = canvas.height - 18
    gap = 5
    segment_width = (right - left - gap * (total - 1)) / total
    for position in range(total):
        x0 = round(left + position * (segment_width + gap))
        x1 = round(x0 + segment_width)
        color = CYAN if position == index else PURPLE if position < index else (56, 59, 76)
        draw.rounded_rectangle((x0, y, x1, y + 5), radius=2, fill=color)

    return canvas.quantize(colors=96, method=Image.Quantize.FASTOCTREE, dither=Image.Dither.FLOYDSTEINBERG)


def validate_sources(frames: Iterable[tuple[str, str]]) -> None:
    missing = [name for name, _ in frames if not (SOURCE / name).is_file()]
    if missing:
        raise FileNotFoundError("Missing screenshot(s): " + ", ".join(missing))


def main() -> None:
    validate_sources(FRAMES)
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    rendered = [
        render_frame(SOURCE / filename, caption, index, len(FRAMES))
        for index, (filename, caption) in enumerate(FRAMES)
    ]
    durations = [1900] * len(rendered)
    durations[-1] = 3200
    rendered[0].save(
        OUTPUT,
        save_all=True,
        append_images=rendered[1:],
        duration=durations,
        loop=0,
        optimize=True,
        disposal=2,
    )
    print(f"Created {OUTPUT} ({OUTPUT.stat().st_size / 1024 / 1024:.2f} MiB)")


if __name__ == "__main__":
    main()
