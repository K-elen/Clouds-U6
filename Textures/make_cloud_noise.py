import numpy as np, sys
from PIL import Image

N = 1024
rng = np.random.default_rng(12345)

def perlin(n, period, rng):
    """Периодический градиентный шум: решётка period x period, замыкается сама на себя."""
    ang = rng.uniform(0, 2*np.pi, (period, period))
    gx, gy = np.cos(ang), np.sin(ang)
    c = np.arange(n) * period / n
    x, y = np.meshgrid(c, c)            # x — столбцы, y — строки
    x0, y0 = np.floor(x).astype(int), np.floor(y).astype(int)
    fx, fy = x - x0, y - y0
    x1, y1 = (x0 + 1) % period, (y0 + 1) % period
    def dot(ix, iy, dx, dy):
        return gx[iy, ix]*dx + gy[iy, ix]*dy
    n00 = dot(x0, y0, fx, fy);     n10 = dot(x1, y0, fx-1, fy)
    n01 = dot(x0, y1, fx, fy-1);   n11 = dot(x1, y1, fx-1, fy-1)
    u = fx*fx*fx*(fx*(fx*6-15)+10); v = fy*fy*fy*(fy*(fy*6-15)+10)
    return (n00*(1-u) + n10*u)*(1-v) + (n01*(1-u) + n11*u)*v

def fbm(n, base, octaves, rng):
    s, amp, tot = np.zeros((n, n)), 1.0, 0.0
    for o in range(octaves):
        s += perlin(n, base * 2**o, rng) * amp
        tot += amp; amp *= 0.5
    return s / tot

f = fbm(N, 4, 7, rng)
# немного «облачности»: смесь обычного fBm и billow (|шум|), всё периодическое
b = 1 - np.abs(fbm(N, 8, 6, rng)) * 2
img = 0.65*f + 0.35*b

lo, hi = np.percentile(img, [0.5, 99.5])
img = np.clip((img - lo) / (hi - lo), 0, 1)
img = np.clip(img - img.mean() + 0.5, 0, 1)   # среднее ровно 0.5

out = sys.argv[1]
Image.fromarray((img*255 + 0.5).astype(np.uint8), 'L').save(out)

# проверка шва: скачок через край против обычного скачка между соседями
a = img
seam = np.abs(a[:, 0] - a[:, -1]).mean() + np.abs(a[0, :] - a[-1, :]).mean()
inner = np.abs(np.diff(a, axis=1)).mean() + np.abs(np.diff(a, axis=0)).mean()
print(f"mean={a.mean():.3f} min={a.min():.2f} max={a.max():.2f} seam={seam:.4f} inner={inner:.4f}")

t = Image.open(out); prev = Image.new('L', (N*2, N*2))
for i in (0, N):
    for j in (0, N): prev.paste(t, (i, j))
prev.resize((1024, 1024)).save(sys.argv[2])
