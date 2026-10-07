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
| Vector4 | `_CloudMisc` | (0, 0, 2000, 0) |
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

В инспекторе `SimplePlanetClouds` появилась группа «Детали сферы как у объёмных облаков».
Три 3D-текстуры HDRP подставляются сами при первом выделении объекта в редакторе:
`WorleyNoise128RGBA`, `WorleyNoise32RGB`, `PerlinNoise32RGB` из пакета HDRP.

- `shellOpacity` — плотность сферы относительно объёмных облаков (1 = как у них).
- `detailFadeStartKm` / `detailFadeEndKm` — где детали гаснут и остаётся только карта.

## Что важно знать

- Насколько облака «рваные», решает **Shape Factor** в Volumetric Clouds — так же, как
  для объёмных. 0.9 (пресет Cloudy) — отдельные кучевые комки даже при полном покрытии;
  0.5 (Overcast) — сплошная масса с ватными краями.
- Формула повторяет HDRP 17 (Unity 6) в режиме Advanced только с Cumulus Map — именно так
  настраивает облака скрипт. Высотный профиль кучевых взят из встроенной таблицы HDRP.
- Не учитывается ветер: если Global Wind Speed не 0, объёмные облака уплывают, а сфера нет.
- Planet Rendering Space в Visual Environment должен быть **World** (как и для патча).
