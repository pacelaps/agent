# The app's icons, images and fonts

- `pacelaps-agent.ico`: the program icon (exe, windows, taskbar), 16 to 256 px: the PaceLaps mark on its dark rounded
  square, as on pacelaps.com (`website/public/favicon.svg`). `logo-256.png` is the same at 256 px for large places
  (first start, About, notifications).
- `tray-16.png` to `tray-48.png`: the mark at the tray's sizes; `Tray/TrayIconSet` adds the state badge at start.
- `Fonts/`: Inter (400 to 800, cut from the website's variable font with fontTools), Barlow Condensed (SemiBold, Bold
  Italic, ExtraBold Italic; the website's font package, WOFF converted to TTF), JetBrains Mono (Regular, Medium,
  SemiBold). All under the SIL Open Font License; the texts are in `licenses/`.

The images were rendered from the favicon with sharp (each size) and Pillow (the `.ico`). When the logo changes,
render them again at the same sizes.
