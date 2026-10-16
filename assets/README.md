# GregsEx artwork

The `^` and `$` anchors frame a match: a visual counterpart to
**"Match with boundaries. Unlike Greg."** The violet/indigo/blue gradient,
rounded silhouettes, white line work, and cyan accent follow the visual family
of [Apex's package artwork](https://github.com/sebastienros/apex/tree/main/assets).
The regex mark is original to GregsEx.

The banner follows the two-line layout of
[CurlyRpc's banner](https://github.com/sebastienros/curlyrpc/blob/main/assets/banner.svg):
the wordmark above "RegEx evaluators for POSIX, ECMAScript, PCRE2, and XML".
XML covers the XML Schema and XPath/XQuery regex flavors.

| Asset | Display size | PNG pixels | Use |
| --- | --- | --- | --- |
| [logo.svg](logo.svg) / [logo.png](logo.png) | 512 x 512 | 1024 x 1024 | Square logo; SVG has no font dependency |
| [icon.png](icon.png) | 128 x 128 | 256 x 256 | NuGet package icon |
| [banner.svg](banner.svg) / [banner.png](banner.png) | 1200 x 360 | 2400 x 720 | README and project banner |

SVG files are the editable masters. With Python 3, `playwright-cli`, and its
Chromium browser available, regenerate all PNGs with:

```sh
python3 assets/render.py
```

The script follows [Xemele's rendering workflow](https://github.com/sebastienros/xemele/blob/main/assets/render.py):
render each SVG through Chromium, wait for fonts, and capture it with a
transparent background. A device scale factor of 2 exports twice the display
resolution in each dimension for sharp Retina/HiDPI rendering, rather than
upscaling an existing PNG. The README keeps the banner's 1200 x 360 display size.
`icon.png` is the square master rendered at 256 pixels for a 128-pixel display.
The banner uses Segoe UI, Helvetica Neue, Arial, or the browser's sans-serif
fallback; exact letter shapes can vary by platform. The committed PNGs were
rendered with Chromium on macOS. Keep both formats together when updating the
artwork.

The artwork is distributed under the repository's [MIT license](../LICENSE).
