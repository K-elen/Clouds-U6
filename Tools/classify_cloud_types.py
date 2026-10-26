"""Разметка карты облаков планеты по типам облаков HDRP.

    python classify_cloud_types.py <карта облаков> <выход: карта типов .png> [превью .png]

Карта облаков — равнопромежуточная проекция 2:1 (долгота по горизонтали), облачность = яркость.
На выходе карта долей типов 2048×1024 (типы меняются от района к району, больше не нужно;
детали облаков остаются в исходной карте):

    R — доля кучевых       → Cumulus Map в HDRP
    G — доля слоистых      → Alto Stratus Map
    B — доля кучево-дождевых → Cumulonimbus Map
    A — облачность (уменьшенная исходная карта, только для превью и проверки)

В каждой точке R + G + B = 1. Границы между типами узкие: HDRP рисует смешанный тип там, где
и кучевых, и слоистых больше 1%, поэтому мягкие доли превратили бы почти всю карту в смешанный тип.

Правила (на масштабе ~20 км на пиксель):
  - кучевые — «зернистые» поля: много мелких деталей (ячейки, комки, просветы);
  - кучево-дождевые — яркие компактные массы, заметно ярче окрестности, в полосе ±30° от экватора
    (сверху это гладкие наковальни гроз — без отдельного правила они ушли бы в слоистые);
  - слоистые — всё остальное: ровные поля, фронты, пелены.
"""
import sys
import numpy as np
from PIL import Image

OUT_W, OUT_H = 2048, 1024   # ~20 км на пиксель — на этом масштабе подобраны пороги


def box(a, r):
    """Среднее в окне (2r+1)²: по долготе карта замкнута, по широте край повторяется."""
    if r <= 0:
        return a
    p = np.pad(a, ((r, r), (0, 0)), mode='edge')
    p = np.concatenate([p[:, -r:], p, p[:, :r]], axis=1)
    c = np.cumsum(np.cumsum(np.pad(p, ((1, 0), (1, 0))), 0), 1)
    k = 2 * r + 1
    return (c[k:, k:] - c[:-k, k:] - c[k:, :-k] + c[:-k, :-k]) / (k * k)


def blur(a, r):
    return box(box(a, r), r)   # два прохода ≈ гауссово размытие


def smooth(e0, e1, x):
    t = np.clip((x - e0) / (e1 - e0), 0, 1)
    return t * t * (3 - 2 * t)


def classify(src_path):
    img = Image.open(src_path).convert('L').resize((OUT_W, OUT_H), Image.BOX)   # усреднение, а не прореживание
    b = np.asarray(img).astype(np.float64) / 255
    b = np.clip(b / np.percentile(b, 99.5), 0, 1)           # самое яркое облако = 1
    lat = np.abs(np.linspace(90, -90, OUT_H))[:, None] * np.ones((1, OUT_W))

    # признаки
    m_loc = blur(b, 3)                                        # яркость в окрестности ~150 км
    m_reg = blur(b, 10)                                       # средняя яркость района ~800 км
    detail = b - blur(b, 2)                                   # мелкие детали: комки, ячейки, просветы
    texture = np.sqrt(np.maximum(blur(detail * detail, 3), 0)) / (m_loc + 0.1)

    # мягкие оценки
    cu_soft = smooth(0.06, 0.13, texture)
    cb_soft = smooth(0.55, 0.75, m_loc) * smooth(0.10, 0.25, m_loc - m_reg) * (1 - smooth(20, 32, lat))

    # узкие границы между типами
    w_cb = smooth(0.30, 0.60, cb_soft)
    w_cu = smooth(0.40, 0.60, cu_soft) * (1 - w_cb)
    w_st = 1 - w_cb - w_cu
    return b, w_cu, w_st, w_cb


def save_preview(path, b, w_cu, w_st, w_cb):
    from PIL import ImageDraw
    col = (b * w_cu)[..., None] * np.array([1.0, 0.85, 0.55]) \
        + (b * w_st)[..., None] * np.array([0.35, 0.7, 1.0]) \
        + (b * w_cb)[..., None] * np.array([1.0, 0.15, 0.1])
    p = Image.fromarray((np.clip(col, 0, 1) * 255).astype(np.uint8))
    W, H = p.size
    canvas = Image.new('RGB', (W, H + 60), (20, 20, 24))
    canvas.paste(p, (0, 0))
    dr = ImageDraw.Draw(canvas)
    x = 20
    for name, c, w in (("cumulus -> Cumulus Map", (255, 217, 140), w_cu),
                       ("stratus -> Alto Stratus Map", (89, 179, 255), w_st),
                       ("cumulonimbus -> Cumulonimbus Map", (255, 38, 25), w_cb)):
        dr.rectangle([x, H + 18, x + 26, H + 42], fill=c)
        dr.text((x + 34, H + 24), f"{name}   {(b * w).sum() / b.sum():.0%}", fill=(230, 230, 230))
        x += W // 3
    canvas.save(path)


def main():
    src, out = sys.argv[1], sys.argv[2]
    b, w_cu, w_st, w_cb = classify(src)
    rgba = np.stack([w_cu, w_st, w_cb, b], -1)
    Image.fromarray((np.clip(rgba, 0, 1) * 255 + 0.5).astype(np.uint8), 'RGBA').save(out)
    if len(sys.argv) > 3:
        save_preview(sys.argv[3], b, w_cu, w_st, w_cb)
    tot = b.sum()
    print(f"{out}: {OUT_W}x{OUT_H}; доли облачности: кучевые {(b * w_cu).sum() / tot:.0%}, "
          f"слоистые {(b * w_st).sum() / tot:.0%}, кучево-дождевые {(b * w_cb).sum() / tot:.0%}")
    mixed = ((w_cu > 0.01) & (w_st > 0.01) & (b > 0.1)).sum() / max((b > 0.1).sum(), 1)
    print(f"смешанный тип (и кучевые, и слоистые больше 1%): {mixed:.0%} облачных пикселей")


if __name__ == '__main__':
    main()
