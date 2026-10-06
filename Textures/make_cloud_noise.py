"""Бесшовный шум «как вата» для деталей облачной сферы.

Тот же рецепт, что у объёмных облаков (HDRP, Horizon Zero Dawn): Perlin-Worley.
Инвертированный Worley даёт круглые пухлые комки, несколько октав — «цветную
капусту» на краях комков, Perlin склеивает комки в облачные массы.
Всё считается на периодической решётке, поэтому текстура замыкается по обеим осям.

    python make_cloud_noise.py <выход.png> [размер]
"""
import sys
import numpy as np
from PIL import Image

rng = np.random.default_rng(7)


def coords(n, cells):
    c = (np.arange(n) + 0.5) * cells / n
    return np.meshgrid(c, c)            # x — столбцы, y — строки


def perlin(n, period):
    """Периодический градиентный шум, примерно -0.7..0.7."""
    ang = rng.uniform(0, 2 * np.pi, (period, period))
    gx, gy = np.cos(ang), np.sin(ang)
    x, y = coords(n, period)
    x0, y0 = np.floor(x).astype(int), np.floor(y).astype(int)
    fx, fy = x - x0, y - y0
    x0, y0 = x0 % period, y0 % period
    x1, y1 = (x0 + 1) % period, (y0 + 1) % period

    def dot(ix, iy, dx, dy):
        return gx[iy, ix] * dx + gy[iy, ix] * dy

    u = fx ** 3 * (fx * (fx * 6 - 15) + 10)
    v = fy ** 3 * (fy * (fy * 6 - 15) + 10)
    a = dot(x0, y0, fx, fy) * (1 - u) + dot(x1, y0, fx - 1, fy) * u
    b = dot(x0, y1, fx, fy - 1) * (1 - u) + dot(x1, y1, fx - 1, fy - 1) * u
    return a * (1 - v) + b * v


def perlin_fbm(n, base, octaves):
    s, amp, tot = 0.0, 1.0, 0.0
    for o in range(octaves):
        s = s + perlin(n, base * 2 ** o) * amp
        tot += amp
        amp *= 0.5
    return s / tot


def worley(n, cells, k=0.12):
    """Периодический клеточный шум с круглыми комками, 0..1.
    Вместо обычного минимума расстояний — мягкий (smooth-min), поэтому между
    соседними комками нет острых рёбер, они сливаются, как вата.
    Профиль комка — купол (1 - d^2)^2, а не конус."""
    jit = rng.uniform(0, 1, (cells, cells, 2))
    x, y = coords(n, cells)
    cx, cy = np.floor(x).astype(int), np.floor(y).astype(int)
    acc = np.zeros(x.shape)
    for oy in (-2, -1, 0, 1, 2):
        for ox in (-2, -1, 0, 1, 2):
            nx, ny = cx + ox, cy + oy
            p = jit[ny % cells, nx % cells]
            dx = nx + p[..., 0] - x
            dy = ny + p[..., 1] - y
            acc += np.exp(-np.sqrt(dx * dx + dy * dy) / k)
    d = np.clip(-k * np.log(acc), 0, 1)
    return (1.0 - d * d) ** 2


def remap(v, lo, hi, nlo, nhi):
    return nlo + (v - lo) / (hi - lo) * (nhi - nlo)


def main():
    out = sys.argv[1]
    n = int(sys.argv[2]) if len(sys.argv) > 2 else 1024

    # Worley-fBm: крупные комки + всё более мелкие пузыри по их краям
    w = (0.625 * worley(n, 7) +
         0.25 * worley(n, 13) +
         0.125 * worley(n, 29))
    w = remap(w, w.min(), w.max(), 0, 1)

    # Perlin-Worley: Perlin задаёт, где облачная масса, Worley делает её комковатой
    p = remap(perlin_fbm(n, 4, 4), -0.7, 0.7, 0, 1)
    pw = np.clip(remap(p, w - 1.0, 1.0, 0.0, 1.0), 0, 1)

    img = 0.55 * pw + 0.45 * w
    # чуть поднять контраст, чтобы комки читались, затем среднее ровно 0.5
    lo, hi = np.percentile(img, [1, 99.5])
    img = np.clip((img - lo) / (hi - lo), 0, 1)
    img = np.clip(img - img.mean() + 0.5, 0, 1)

    Image.fromarray((img * 255 + 0.5).astype(np.uint8), "L").save(out)

    seam = np.abs(img[:, 0] - img[:, -1]).mean() + np.abs(img[0] - img[-1]).mean()
    inner = np.abs(np.diff(img, axis=1)).mean() + np.abs(np.diff(img, axis=0)).mean()
    print(f"{out}: {n}x{n} mean={img.mean():.3f} шов={seam:.4f} соседи={inner:.4f}")


if __name__ == "__main__":
    main()
