# Shared web and desktop interface

The browser and Windows WebView2 shell use the same embedded UI assets.

The supplied transparent CherBot SVG is used for the sidebar and favicon. Its PNG companion is linked into the desktop assembly for window and tray icons; the tray retains its health badge.

- Keep CherBot's teal identity, with neutral dark surfaces and readable orange links.
- Use the locally bundled Tabler outline icons consistently. Preserve visible navigation labels and mark decorative icons as hidden from assistive technology.
- Use the shared spacing, corner and elevation variables. Cards use subtle elevation; dialogs and popovers use the strongest layer.
- Keep stream controls stationary on hover. Show interaction through surface, border and focus changes.
- Preserve native buttons, fields, selects, switches and dialogs. No component framework, runtime dependency or build step is added.
- Let content grids shrink to the available width. On narrow windows, navigation scrolls horizontally and forms remain within the viewport.
- Honor reduced-motion preferences and retain a clear keyboard focus ring.

Icon attribution is in `src/CherBot.Host/webui/icons/LICENSE.txt`; attribution for the Material-derived elevation values is in `src/CherBot.Host/webui/assets/MUI-LICENSE.txt`.

Validated on 2026-11-01 with desktop and 390px browser previews, including icon rendering, horizontally scrolling navigation, setup cards, dialogs and visible keyboard focus. The browser reported no warnings or errors. All nine Node UI tests passed, and the Release solution build, including the Windows desktop shell, completed with zero warnings or errors. Native WebView2 rendering was not separately exercised.
