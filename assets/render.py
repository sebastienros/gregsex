"""Render the SVG masters at 2x pixel density with Chromium through playwright-cli."""

import json
from pathlib import Path
import subprocess
import tempfile


def run(*args):
    result = subprocess.run(
        ["playwright-cli", f"-s={session}", *args],
        cwd=work,
        capture_output=True,
        text=True,
    )
    if result.returncode or "### Error" in result.stdout:
        raise RuntimeError(
            f"playwright-cli {args[0]} failed ({result.returncode}):\n"
            f"{result.stdout}\n{result.stderr}"
        )


root = Path(__file__).resolve().parent
pixel_ratio = 2
with tempfile.TemporaryDirectory(prefix="gregsex-branding-") as work:
    session = Path(work).name
    run("open", "--browser", "chromium")
    try:
        for source, output, size in [
            ("banner.svg", "banner.png", (1200, 360)),
            ("logo.svg", "logo.png", (512, 512)),
            ("logo.svg", "icon.png", (128, 128)),
        ]:
            svg = (root / source).read_text(encoding="utf-8")
            width, height = size
            code = (
                "async browserPage => {"
                "const context = await browserPage.context().browser().newContext({"
                f"viewport:{{width:{width},height:{height}}},deviceScaleFactor:{pixel_ratio}"
                "});"
                "try {"
                "const page = await context.newPage();"
                f"await page.setContent({json.dumps(svg)});"
                "await page.addStyleTag({content:'body{margin:0}svg{display:block;width:100%;height:100%}'});"
                "await page.evaluate(() => document.fonts.ready);"
                f"await page.locator('svg').screenshot({{path:{json.dumps(str(root / output))},"
                "omitBackground:true,scale:'device'});"
                "} finally { await context.close(); }}"
            )
            run("run-code", code)
            print(
                f"Rendered {output}: {width * pixel_ratio} x {height * pixel_ratio}"
                f" ({width} x {height} at {pixel_ratio}x)"
            )
    finally:
        run("close")
