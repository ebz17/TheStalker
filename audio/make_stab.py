"""The Stalker: the hit sound (replaces the old horror sting).
A real stab instead of a movie sting: blade swish, wet stab, your gasp, then the ringing and a heartbeat
that fade with the white-out (~4 s). Synthesized; no samples."""
import os, sys
import numpy as np
import soundfile as sf
from scipy import signal
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "lib"))  # shared helpers (dsp.py etc.)
import make_audio as A

SR = A.SR
OUT = "./out_v2"


def env_exp(n, rate):
    return np.exp(-np.arange(n) / SR * rate)


def place(dst, src, t):
    i = int(t * SR)
    j = min(len(dst), i + len(src))
    dst[i:j] += src[: j - i]


def swish(rng, dur=0.16, f0=1800, f1=6500):
    n = int(dur * SR)
    t = np.linspace(0, 1, n)
    noise = rng.standard_normal(n)
    out = np.zeros(n)
    # sweep a band through the noise in short blocks
    blk = 256
    for k in range(0, n, blk):
        f = f0 + (f1 - f0) * (k / n) ** 0.7
        b, a = signal.iirpeak(min(0.99, f / (SR / 2)), 2.5)
        out[k:k + blk] = signal.lfilter(b, a, noise[k:k + blk])
    return out * np.sin(np.pi * t) ** 1.5


def stab(rng):
    n = int(0.45 * SR)
    t = np.arange(n) / SR
    thud = np.sin(2 * np.pi * (85 * t - 40 * t ** 2)) * np.exp(-t * 18)                 # body hit
    wet = A.butter(rng.standard_normal(n), lo=250, hi=2200) * np.exp(-t * 30)             # flesh
    b, a = signal.iirpeak(620 / (SR / 2), 4)                                              # squelch resonance
    wet = wet + signal.lfilter(b, a, rng.standard_normal(n)) * np.exp(-t * 22) * 0.8
    tick = A.butter(rng.standard_normal(n), lo=3000) * np.exp(-t * 400) * 0.6             # tip
    return thud * 1.3 + wet * 0.9 + tick


def gasp(rng, dur=0.42):
    n = int(dur * SR)
    t = np.linspace(0, 1, n)
    x = rng.standard_normal(n)
    vowel = [(700, 140, 1.0), (1250, 180, 0.6), (2600, 260, 0.3)]
    y = np.zeros(n)
    for f, bw, g in vowel:
        b, a = signal.iirpeak(f / (SR / 2), f / bw)
        y += signal.lfilter(b, a, x) * g
    e = np.minimum(1, t / 0.08) * (1 - t) ** 2.2
    return A.butter(y * e, lo=300, hi=6000)


def ringing(dur, f=6800):
    n = int(dur * SR)
    t = np.arange(n) / SR
    tone = np.sin(2 * np.pi * f * t) + 0.5 * np.sin(2 * np.pi * (f * 1.004) * t)     # slight beating
    e = np.minimum(1, t / 0.15) * np.exp(-t * (1.6 / dur) * 2.2)
    return tone * e


def heartbeat(dur, bpm=110):
    n = int(dur * SR)
    out = np.zeros(n)
    beat = int(60 / bpm * SR)
    tt = np.arange(int(0.18 * SR)) / SR
    thump = np.sin(2 * np.pi * 52 * tt) * np.exp(-tt * 28)
    for k, i in enumerate(range(int(0.25 * SR), n, beat)):
        g = (1 - i / n) ** 1.2
        for off, gg in ((0, 1.0), (int(0.17 * SR), 0.6)):                               # lub-dub
            j = i + off
            m = min(n, j + len(thump))
            if j < n:
                out[j:m] += thump[: m - j] * g * gg
    return A.butter(out, hi=180)


def make(seed, ring_f):
    rng = np.random.default_rng(seed)
    total = 4.6
    x = np.zeros(int(total * SR))
    place(x, swish(rng) * 0.55, 0.0)
    place(x, stab(rng) * 1.0, 0.12)
    place(x, gasp(rng) * 0.45, 0.20)
    place(x, ringing(4.2, ring_f) * 0.16, 0.25)
    place(x, heartbeat(4.2) * 0.9, 0.35)
    # a low swell under it, then gone
    tt = np.arange(len(x)) / SR
    sub = np.sin(2 * np.pi * 38 * tt) * np.clip((tt - 0.1) / 0.3, 0, 1) * np.exp(-np.clip(tt - 0.4, 0, None) * 0.9) * 0.25
    x = x + sub
    x = A.reverb(x, rt60=0.5, wet=0.08, bright=5000)
    return A.fade(x, 0.002, 0.6)


for name, seed, f in (("sting_1", 31, 6800), ("sting_2", 32, 7400)):
    y = A.norm(make(seed, f), 0.9)
    sf.write(f"{OUT}/{name}.wav", y.astype(np.float32), SR, subtype="PCM_16")
    print(name, f"{len(y)/SR:.2f}s")
