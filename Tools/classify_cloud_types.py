import numpy as np, sys
from PIL import Image

def box(a, r):
    """Среднее в окне (2r+1)²: по долготе карта замкнута, по широте — край повторяется."""
    if r <= 0: return a
    p = np.pad(a, ((r, r), (0, 0)), mode='edge')
    p = np.concatenate([p[:, -r:], p, p[:, :r]], axis=1)
    c = np.cumsum(np.cumsum(np.pad(p, ((1, 0), (1, 0))), 0), 1)
    k = 2 * r + 1
    return (c[k:, k:] - c[:-k, k:] - c[k:, :-k] + c[:-k, :-k]) / (k * k)
def blur(a, r): return box(box(a, r), r)        # два прохода ≈ гауссово размытие
def smooth(e0, e1, x): t = np.clip((x - e0) / (e1 - e0), 0, 1); return t * t * (3 - 2 * t)

src = sys.argv[1]
b = np.asarray(Image.open(src).convert('L')).astype(np.float64) / 255
H, W = b.shape
b = np.clip(b / np.percentile(b, 99.5), 0, 1)           # самое яркое облако = 1
lat = np.abs(np.linspace(90, -90, H))[:, None] * np.ones((1, W))
px_km = 40075 / W                                         # км на пиксель по экватору

# признаки
m_fine = blur(b, 1)                                       # яркость, чуть сглаженная
m_loc = blur(b, 3)                                        # яркость в окрестности ~150 км
m_reg = blur(b, 10)                                       # средняя яркость района ~800 км
detail = b - blur(b, 2)                                   # мелкие детали: комки, ячейки, просветы
texture = np.sqrt(np.maximum(blur(detail * detail, 3), 0)) / (m_loc + 0.1)   # их энергия относительно яркости

# кучевые: облачно и «зернисто» — поле из комков и ячеек
w_cu = smooth(0.06, 0.13, texture)
# кучево-дождевые: яркие компактные массы, заметно ярче окрестности, в тропиках и субтропиках
# (сверху это наковальни гроз — ровные, поэтому без этого правила ушли бы в слоистые)
w_cb = smooth(0.55, 0.75, m_loc) * smooth(0.10, 0.25, m_loc - m_reg) * (1 - smooth(20, 32, lat))
w_cu = w_cu * (1 - w_cb)
# слоистые: ровные поля — всё, что не зернистое и не грозовое
w_cu = w_cu * (1 - w_cb)
w_st = np.clip(1 - w_cu - w_cb, 0, 1)

cu, st, cb = b * w_cu, b * w_st, b * w_cb
np.savez_compressed('layers.npz', cu=cu, st=st, cb=cb, b=b)

# превью: кучевые — тёплый белый, слоистые — голубой, кучево-дождевые — красный
col = (cu[..., None] * np.array([1.0, 0.85, 0.55]) + st[..., None] * np.array([0.35, 0.7, 1.0])
       + cb[..., None] * np.array([1.0, 0.15, 0.1]))
Image.fromarray((np.clip(col, 0, 1) * 255).astype(np.uint8)).save('preview.png')
tot = b.sum()
print(f"доли облачности: кучевые {cu.sum()/tot:.0%}, слоистые {st.sum()/tot:.0%}, кучево-дождевые {cb.sum()/tot:.0%}; {px_km:.0f} км/пиксель")
