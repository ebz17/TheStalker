"""The Stalker: laughs, whispers, jump-scare sting, shriek, PA warning.
All synthesized here (laughs, sting, shriek) or Piper TTS (whispers RU, PA RU/EN-ljspeech, public domain voice)."""
import os, sys, os, subprocess, importlib.util
import numpy as np
import soundfile as sf
from scipy import signal

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "lib"))  # shared helpers (dsp.py etc.)
import make_audio as A
import parselmouth
from parselmouth.praat import call

SR = A.SR
OUT = "./out"
RAW = "./raw"
os.makedirs(OUT, exist_ok=True)
os.makedirs(RAW, exist_ok=True)
RU = "./voices/ru_RU-irina-medium.onnx"
EN = "./voices/en_US-ljspeech-high.onnx"
rng = np.random.default_rng(13)


def write(name, x, peak=0.85):
    x = A.norm(np.asarray(x, dtype=np.float64), peak)
    sf.write(f"{OUT}/{name}.wav", x.astype(np.float32), SR, subtype="PCM_16")
    print(f"{name:22s} {len(x)/SR:5.2f}s peak {np.max(np.abs(x)):.2f} rms {np.sqrt(np.mean(x**2)):.3f}")


def formant_filter(x, formants):
    y = np.zeros_like(x)
    for f, bw, g in formants:
        b, a = signal.iirpeak(f / (SR / 2), f / bw)
        y += signal.lfilter(b, a, x) * g
    return y


def glottal(f0_curve, jitter=0.02):
    """Pulse train following f0 (Hz per sample), softened like a glottal source."""
    f = f0_curve * (1 + jitter * rng.standard_normal(len(f0_curve)).cumsum() / np.sqrt(len(f0_curve)) * 3)
    ph = np.cumsum(f / SR)
    saw = 2 * (ph % 1.0) - 1
    src = signal.lfilter([1], [1, -0.97], np.diff(np.concatenate([[0], np.sign(np.sin(2 * np.pi * ph)) * 0.5 + saw * 0.5])))
    return A.butter(src, lo=60, hi=4000)


def chuckle_syllable(dur, f0a, f0b, voiced, breath):
    n = int(dur * SR)
    t = np.linspace(0, 1, n)
    f0 = np.linspace(f0a, f0b, n)
    v = glottal(f0) * voiced
    h = rng.standard_normal(n) * breath
    # "h" onset: mostly breath, then the vowel; fast decay
    onset = np.clip(t / 0.25, 0, 1)
    env = np.minimum(1.0, t / 0.05) * np.exp(-t * 5.5)
    src = v * onset + h * (1.2 - 0.7 * onset)
    vowel = [(560, 90, 1.0), (1150, 120, 0.55), (2450, 180, 0.25), (3400, 260, 0.1)]   # dark "uh/eh"
    return formant_filter(src, vowel) * env


def laugh(seed, n_syl, f0_start, spacing, voiced, breath, end_breath=True):
    global rng
    rng = np.random.default_rng(seed)
    parts = []
    total = int((n_syl * spacing + 1.4) * SR)
    out = np.zeros(total)
    pos = int(0.05 * SR)
    for i in range(n_syl):
        k = i / max(1, n_syl - 1)
        f0a = f0_start * (1 - 0.18 * k) * (1 + rng.uniform(-0.03, 0.03))
        d = rng.uniform(0.11, 0.17)
        s = chuckle_syllable(d, f0a, f0a * 0.86, voiced * (1 - 0.35 * k), breath) * (1 - 0.45 * k)
        out[pos:pos + len(s)] += s
        pos += int((spacing * (1 + 0.25 * k) + rng.uniform(-0.02, 0.03)) * SR)
    if end_breath:   # slow nasal exhale / sniff after the laugh
        n = int(0.7 * SR)
        b = A.butter(rng.standard_normal(n), lo=600, hi=3000) * np.sin(np.linspace(0, np.pi, n)) ** 2 * 0.012
        p = pos + int(0.12 * SR)
        out[p:p + n] += b[: max(0, min(n, total - p))]
    out = A.trim_tail(out, 1e-4)
    # low, close, slightly distorted, a little room
    out = np.tanh(A.norm(out, 1.0) * 1.1)
    out = A.reverb(out, rt60=0.9, wet=0.12, bright=3500)
    return A.fade(out, 0.003, 0.2)


def whisperize(path_in):
    snd = parselmouth.Sound(path_in)
    snd = call(snd, "Resample", 22050, 50)
    lpc = call(snd, "To LPC (burg)", 22, 0.025, 0.005, 50)
    noise = call("Create Sound from formula", "n", 1, 0, snd.duration, 22050, "randomGauss(0,0.1)")
    wh = call([lpc, noise], "Filter", "no")
    x = wh.values[0]
    env = np.abs(signal.hilbert(snd.values[0]))
    env = A.smooth(env, int(0.02 * 22050))
    x = x / (np.sqrt(A.smooth(x ** 2, int(0.02 * 22050))) + 1e-4) * env
    x = signal.resample_poly(x, SR, 22050)
    x = A.butter(x, lo=350, hi=7500)
    return x


def tts(text, model, path, scale):
    subprocess.run(["python3", "-m", "piper", "-m", model, "--length-scale", str(scale), "--sentence-silence", "0.3", "-f", path],
                   input=text.encode("utf-8"), check=True, capture_output=True)


def sting():
    """Jump scare: hit + boom + screeching dissonant cluster, 1.8 s."""
    n = int(1.8 * SR)
    t = np.arange(n) / SR
    boom = np.sin(2 * np.pi * (48 * t - 10 * t ** 2)) * np.exp(-t * 3.0)
    hit = A.butter(rng.standard_normal(n), lo=800) * np.exp(-t * 28)
    cluster = np.zeros(n)
    for f in (740, 784, 1046, 1109, 1568):
        vib = 1 + 0.012 * np.sin(2 * np.pi * (6.5 + rng.uniform(-1, 1)) * t)
        ph = np.cumsum(f * vib / SR)
        cluster += signal.sawtooth(2 * np.pi * ph) * rng.uniform(0.6, 1.0)
    cluster = A.butter(cluster, lo=500, hi=7000) * np.minimum(1, t / 0.008) * np.exp(-t * 1.6)
    screech = A.butter(rng.standard_normal(n), lo=2500, hi=6000) * np.exp(-t * 4) * 0.5
    x = boom * 1.2 + hit * 0.8 + cluster * 0.55 + screech
    x = np.tanh(x * 1.5)
    return A.fade(A.reverb(x, rt60=1.4, wet=0.25, bright=6000), 0.001, 0.4)


def shriek(seed, dur=1.3, f_hi=520, f_lo=300):
    """Creature screech: harsh voiced tone + resonant noise, pitch falling."""
    global rng
    rng = np.random.default_rng(seed)
    n = int(dur * SR)
    t = np.linspace(0, 1, n)
    f0 = f_hi + (f_lo - f_hi) * t ** 0.7
    f0 = f0 * (1 + 0.03 * np.sin(2 * np.pi * 23 * t * dur) + 0.02 * rng.standard_normal(n).cumsum() / np.sqrt(n) * 4)
    ph = np.cumsum(f0 / SR)
    v = signal.sawtooth(2 * np.pi * ph) + 0.6 * signal.sawtooth(2 * np.pi * ph * 1.5 + 0.3)
    v = v * (1 + 0.5 * np.sin(2 * np.pi * 61 * np.arange(n) / SR))          # ring-mod roughness
    noise = rng.standard_normal(n)
    src = v * 0.7 + noise * 0.5
    form = [(1100, 140, 1.0), (2300, 220, 0.8), (3300, 300, 0.5)]
    y = formant_filter(src, form)
    env = np.minimum(1, t / 0.03) * (1 - t) ** 1.3
    y = np.tanh(A.norm(y * env, 1.0) * 2.2)
    return A.fade(A.reverb(y, rt60=1.3, wet=0.25, bright=5000), 0.002, 0.15)


if __name__ == "__main__":
    # laughs: far ones (lower, slower, more breath) and near ones (quieter, more whispered)
    write("laugh_1", laugh(1, 5, 128, 0.22, 0.9, 0.45), 0.8)
    write("laugh_2", laugh(2, 7, 118, 0.19, 0.8, 0.5), 0.8)
    write("laugh_3", laugh(3, 4, 105, 0.28, 1.0, 0.35), 0.8)
    write("laugh_4", laugh(4, 6, 140, 0.17, 0.6, 0.7), 0.8)
    write("laugh_5", laugh(5, 3, 96, 0.33, 0.9, 0.4, end_breath=False), 0.8)
    write("laugh_near_1", laugh(11, 5, 120, 0.2, 0.25, 1.0), 0.75)
    write("laugh_near_2", laugh(12, 4, 110, 0.24, 0.2, 1.0), 0.75)
    # whispers (Russian), the tell right before he strikes
    whispers = {
        "whisper_1": "Обернись...",
        "whisper_2": "Я рядом.",
        "whisper_3": "Тише... тише...",
        "whisper_4": "Я за тобой.",
        "whisper_5": "Я тебя вижу.",
    }
    for name, text in whispers.items():
        raw = f"{RAW}/{name}.wav"
        tts(text, RU, raw, 1.25)
        x = whisperize(raw)
        x = A.reverb(x, rt60=0.6, wet=0.12, bright=7000)
        write(name, A.fade(x, 0.01, 0.15), 0.7)
    write("sting_1", sting(), 0.95)
    rng = np.random.default_rng(99)
    write("sting_2", sting(), 0.95)
    write("shriek_1", shriek(21), 0.9)
    write("shriek_2", shriek(22, 1.0, 620, 360), 0.9)
    write("hiss_hurt", shriek(23, 0.7, 260, 200), 0.85)
    # PA warning, same chain as Blackout Addons' PA lines
    spec = importlib.util.spec_from_file_location("m", "./lib/match_pa.py")
    sys.argv = ["x", "none"]
    M = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(M)
    lines = {
        "vo_stalker_ru": ("Внимание, охрана. Неопознанное движение в техническом секторе. Повторяю: неопознанное движение.", RU, 1.0),
        "vo_stalker_en": ("Attention, security. Unidentified movement in the technical sector. Repeat: unidentified movement.", EN, 1.0),
    }
    for name, (text, model, scale) in lines.items():
        raw = f"{RAW}/{name}.wav"
        tts(text, model, raw, scale)
        write(name, M.labs_pa(raw), 0.8)
