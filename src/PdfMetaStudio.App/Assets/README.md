# Application icons

- `app.png`: high-resolution transparent artwork for the application mark.
- `app-small.png`: optical variant for 16–28 px; omits lettering, fine document
  strokes and nested gear rings so the document/gear/teal silhouette stays legible.
- `app.ico`: generated Windows resource shared by the executable, all three WPF
  windows, the MSI installed-app entry and Start menu shortcut.

Keep the paper/gear/PDF identity and transparent padding. Never upscale a small
ICO frame to produce a larger one. The checked-in ICO has independent 32-bit
frames at 16, 20, 24, 28, 32, 36, 40, 48, 64, 72, 80, 96, 128, 192 and 256 px.
Frames below 256 px use alpha-enabled DIBs; the 256 px frame is PNG-compressed.

To regenerate from the checked-in masters, install ImageMagick 7 and run:

```powershell
./scripts/build-icons.ps1
```

ImageMagick is an optional artwork maintenance tool, not a build or application
dependency. Normal Windows builds embed the checked-in ICO. The source PNGs are
not loaded at runtime. Interface action/navigation icons remain scalable Segoe
Fluent/MDL2 glyphs, rendered with the shared `IconGlyph` style in `App.xaml`.

GUI smoke captures the actual small/large window HICONs and executable shell
icon as PNGs, checks their sizes and keeps the ordinary UI screenshots. MSI
acceptance checks that the embedded icon is referenced by both `ARPPRODUCTICON`
and the shortcut. These checks detect missing/misconnected resources; visual
inspection and physical DPI acceptance remain necessary for rendering quality.
