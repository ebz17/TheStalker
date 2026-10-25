"""The Stalker: laughs recorded by the mod author (9 takes) -> game-ready WAVs.
Trim, light noise reduction, lower and darker, a corridor echo, same loudness for all."""
import sys, glob
import numpy as np
import soundfile as sf
import noisereduce as nr
import parselmouth
from parselmouth.praat import call
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "lib"))  # shared helpers (dsp.py etc.)
import make_audio as A

SR = 44100
RAW = "./user_raw"
OUT = "./out_user"
NEAR = {1, 3, 4, 9}         # the breathy, quiet takes (and take 4): used when he is close
SEMITONES = -3.5           # lower: darker, less "a guy in his room"


def active_region(x, sr):
    w = int(0.03 * sr)
    e = np.array([np.sqrt(np.mean(x[i:i + w] ** 2)) for i in range(0, len(x) - w, w)])
    floor = np.percentile(e, 10)
    act = np.where(e > max(floor * 4, np.abs(x).max() * 0.03))[0]
    s = max(0, act[0] * w - int(0.12 * sr))
    t = min(len(x), (act[-1] + 1) * w + int(0.45 * sr))
    return s, t


def pitch_down(x, sr, st):
    snd = parselmouth.Sound(x, sampling_frequency=sr)
    man = call(snd, "To Manipulation", 0.01, 60, 500)
    tier = call(man, "Extract pitch tier")
    call(tier, "Multiply frequencies", snd.xmin, snd.xmax, 2 ** (st / 12))
    call([tier, man], "Replace pitch tier")
    return call(man, "Get resynthesis (overlap-add)").values[0]


def echo(x, delay=0.32, fb=0.42, n=4):
    """Distinct repeats down a long concrete corridor: each one later, quieter and darker."""
    d = int(delay * SR)
    out = np.concatenate([x, np.zeros(d * n + int(0.5 * SR))])
    rep = x.copy()
    for k in range(1, n + 1):
        rep = A.butter(rep, lo=120, hi=max(1200, 4500 - 900 * k)) * fb
        out[k * d:k * d + len(rep)] += rep
    return out


def process(i):
    x, sr = sf.read(f"{RAW}/rec_{i}.wav")
    noise = x[: int(0.8 * sr)]                                   # the silence before each take
    x = nr.reduce_noise(y=x, sr=sr, y_noise=noise, prop_decrease=0.85, stationary=True)
    s, t = active_region(x, sr)
    x = x[s:t]
    x = A.butter(x, lo=60, hi=6500)                              # darker top
    x = pitch_down(x, sr, SEMITONES)
    x = A.norm(x, 1.0)
    x = np.tanh(x * 1.15) / np.tanh(1.15)                         # a hint of grit
    x = A.reverb(x, rt60=0.6, wet=0.08, bright=3500)             # just a little air
    x = echo(x)
    x = A.trim_tail(x, 1e-4)
    return A.fade(x, 0.01, 0.5)


def loudness_match(x, target_rms=0.11, peak=0.85):
    act = x[np.abs(x) > 0.02 * np.abs(x).max()]
    rms = np.sqrt(np.mean(act ** 2)) if len(act) else 1e-6
    y = x * (target_rms / rms)
    m = np.abs(y).max()
    return y * (peak / m) if m > peak else y


if __name__ == "__main__":
    import os
    os.makedirs(OUT, exist_ok=True)
    for old in glob.glob(f"{OUT}/*.wav"):
        os.remove(old)
    n_far = n_near = 0
    for i in range(1, 10):
        y = process(i)
        if i in NEAR:
            n_near += 1
            name = f"laugh_near_{n_near}"
            y = loudness_match(y, 0.08)
        else:
            n_far += 1
            name = f"laugh_{n_far}"
            y = loudness_match(y, 0.11)
        sf.write(f"{OUT}/{name}.wav", y.astype(np.float32), SR, subtype="PCM_16")
        print(f"take {i} -> {name:14s} {len(y)/SR:4.2f}s peak {np.abs(y).max():.2f}")
