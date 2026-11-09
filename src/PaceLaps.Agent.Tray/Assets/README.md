# The agent's icons

The PaceLaps mark on its dark rounded square, as on pacelaps.com (`website/public/favicon.svg` on
the website).

- `pacelaps-agent.ico`: the program icon (exe, windows, taskbar), 16 to 256 px.
- `tray-16.png` to `tray-48.png`: the mark at the tray's sizes; `TrayIcons` adds the state badge at start.

Made from the favicon with sharp (render each size) and Pillow (the `.ico`). When the logo changes, render them
again at the same sizes.
