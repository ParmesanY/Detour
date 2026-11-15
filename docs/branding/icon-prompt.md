# Иконка Detour: промт для Gemini

Вставь промт ниже в Gemini (генерация изображений) как есть, на английском: так модель лучше держит стиль.
Если первый результат слишком «нарисованный», попроси: *«flatter, fewer details, thicker strokes»*.

## Промт

```
App icon for a Windows desktop network utility called "Detour" — a VPN / traffic-routing client.

Concept: a single route line that meets an obstacle and goes around it — a clean geometric detour.
The line comes in from the left, steps up and over a small solid square block in the middle, and continues to the right.
Two small square end-nodes at the start and end of the line, like terminals on a circuit or a network diagram.

Style: minimal, technical, engineering-grade. Think network diagram, transit map, precision instrument.
Flat vector, no gradients or very subtle ones, no 3D, no glow, no neon, no cyberpunk, no "hacker" matrix green, no shields, no padlocks, no globes, no keys.
Orthogonal lines with crisp 90-degree corners, uniform thick stroke weight.

Colors: route line in a calm signal blue #4F8CFF; the obstacle block in muted graphite grey #4A5260;
background tile in deep graphite #14181E with a very thin slightly lighter border #2C333D.

Shape: rounded-square tile (corner radius about 22% of the size), the motif centered with generous padding (~18% on each side).
Must stay readable at 16x16 pixels: few elements, bold shapes, high contrast between the line and the background.

Output: 1024x1024 PNG, centered, no text, no letters, no watermark, no mockup or device frame, plain tile only.
```

## Варианты, если захочется другого

- Без плитки (для светлого трея): добавь в конец `Transparent background, no tile, only the route, block and nodes.`
- Монограмма: замени абзац Concept на
  `A bold geometric letter "D" whose inner counter is formed by a route line bending around a small square block.`

## Что прислать мне

Один PNG 1024×1024 (лучше два: с плиткой и с прозрачным фоном). Из него я сделаю `app.ico` (16–256 px)
и значки трея четырёх состояний (выкл / VPN / без изменений / PAC) скриптом, который уже есть.
