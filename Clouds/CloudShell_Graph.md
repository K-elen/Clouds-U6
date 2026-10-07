# Граф облачной сферы с эрозией HDRP

Сфера считает края облаков той же формулой и теми же 3D-шумами, что Volumetric Clouds:
форма (Shape), эрозия (Erosion), микроэрозия (Micro Erosion). Все настройки берутся из
Volume, рисовать отдельную текстуру шума больше не нужно.

## Файлы

- `CloudShellHDRP.hlsl` — положите в Assets (например, `Assets/Clouds/`).
- `SimplePlanetClouds.cs` — замените старый.

## Граф

Тот же Lit Shader Graph, Graph Settings без изменений: Transparent, Alpha, Front,
Depth Write выкл, Receive Fog вкл, Preserve Specular Lighting выкл.

### Свойства

Старые `_DetailNoise`, `_DetailTiling`, `_DetailParams` удалите. Нужны такие (Reference ровно такой):

| Тип | Reference | По умолчанию |
|---|---|---|
| Texture2D | `_BaseColorMap` | — |
| Color | `_BaseColor` | белый |
| Texture3D | `_ShapeNoise` | — |
| Texture3D | `_ErosionNoise` | — |
| Vector4 | `_CloudLayer` | (0, -6371000, 0, 6372200) |
| Vector4 | `_CloudShape` | (5, 0.9, 0, 0) |
| Vector4 | `_CloudErosion` | (107, 0.45, 200, 0) |
| Vector4 | `_CloudCoverage` | (0.2, 1, 0.32, 0) |
| Vector4 | `_CloudMisc` | (0, 0, 2000, 0.4) |
| Vector2 | `_CloudFade` | (150000, 800000) |

Значения по умолчанию нужны только для превью: в игре скрипт выставляет их сам каждый кадр.

### Custom Function

- **Type = File**, **Source** = `CloudShellHDRP.hlsl`, **Name** = `CloudShellAlpha`
- **Inputs** строго в этом порядке:

| Имя | Тип |
|---|---|
| CloudMap | Texture2D |
| UV | Vector2 |
| PositionWS | Vector3 |
| PositionAWS | Vector3 |
| ShapeNoise | Texture3D |
| ErosionNoise | Texture3D |
| Layer | Vector4 |
| Shape | Vector4 |
| Erosion | Vector4 |
| Coverage | Vector4 |
| Misc | Vector4 |
| Fade | Vector2 |
| BaseColor | Vector4 |

- **Outputs**: `Alpha`, Float.

### Провода

| Откуда | Куда |
|---|---|
| `_BaseColorMap` | CloudMap |
| нода **UV** (UV0) | UV |
| нода **Position**, Space = **World** | PositionWS |
| нода **Position**, Space = **Absolute World** | PositionAWS |
| `_ShapeNoise` | ShapeNoise |
| `_ErosionNoise` | ErosionNoise |
| `_CloudLayer` … `_CloudFade` | Layer … Fade |
| `_BaseColor` | BaseColor **и** блок **Base Color** |
| Alpha | блок **Alpha** |

Smoothness = 0, Metallic = 0.

## Скрипт

Все настройки `SimplePlanetClouds` подписаны во всплывающих подсказках инспектора. Коротко:

| Группа | Что там |
|---|---|
| Ссылки | камера, Volume с Volumetric Clouds, материал сферы |
| Планета | радиус (как Planet Radius в Visual Environment) и высота сферы |
| Переход между слоями | на какой высоте объёмные облака сменяются сферой |
| Карта облаков | точка старта на карте, размер патча, покрытие (`coverageThreshold`, `coverageMultiplier`, `coverageGamma`) |
| Сфера | цвет, плотность, где гаснут детали, 3D-шумы HDRP (подставляются сами в редакторе) |
| Отладка | панель с мини-картой |

Калибровки нет: размер и ориентация патча считаются по формулам HDRP
(`ComputeNormalizationFactor` и разворот карты в `CloudMapGenerator.compute`).

### Панель отладки

- **Мини-карта**: в центре патч с жёлтой рамкой, вокруг серые копии, которые HDRP повторяет дальше;
  белая точка — камера, красная линия — куда она смотрит, голубой круг — горизонт. +Z мира — вверх.
- **Карта планеты**: жёлтые точки — углы и центр патча, белая — камера.
- Текст: высота, процент перехода, над патчем ли камера, дальность горизонта, Far Clip и проблемы настройки.

### Как проверить совпадение слоёв

Поднимитесь на высоту между `fadeStartKm` и `fadeEndKm`, где видны оба слоя, и посмотрите вниз:
крупные массы объёмных облаков должны лежать под массами сферы. Если рисунок отражён «вперёд-назад»,
это одна строка — `PatchPixelToWorldXZ` в скрипте (знак у Z).

## Что важно знать

- Cumulus Map Multiplier скрипт во время игры держит равным 1 — вместо него `coverageMultiplier`.
  Карты Alto Stratus, Cumulonimbus и Rain скрипт убирает: шейдер сферы рассчитан только на кучевые.
- Насколько облака «рваные», решает **Shape Factor** в Volumetric Clouds — так же, как
  для объёмных. 0.9 (пресет Cloudy) — отдельные кучевые комки даже при полном покрытии;
  0.5 (Overcast) — сплошная масса с ватными краями.
- Формула повторяет HDRP 17 (Unity 6) в режиме Advanced только с Cumulus Map — именно так
  настраивает облака скрипт. Высотный профиль кучевых взят из встроенной таблицы HDRP.
- Не учитывается ветер: если Global Wind Speed не 0, объёмные облака уплывают, а сфера нет.
- В Visual Environment: Rendering Space = **World**, Center Mode = **Automatic**, Planet Radius = `planetRadiusKm`.
  Если Visual Environment лежит в том же Volume, скрипт проверит это сам.
