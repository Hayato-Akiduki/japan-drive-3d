using System.Runtime.InteropServices;
using Raylib_cs;

namespace JapanDrive;

/// <summary>
/// 効果音・エンジン音・BGM。音源ファイルは使わず、すべてプログラムで合成する。
/// </summary>
public static unsafe class Audio
{
    private const int Rate = 44100;
    private const int BgmRate = 22050;
    private const int Chunk = 1024;

    public static bool Ready { get; private set; }
    public static bool BgmOn { get; set; } = true;
    public static bool SfxOn { get; set; } = true;

    public static Sound Cannon, Explosion, Splash, Stamp, Crash, Thud, Beep, Etc, Neigh, Siren, Fanfare, Whoosh, Select;
    private static Sound[] bgm = Array.Empty<Sound>();
    private static int bgmTrack = -1;

    // ---- 連続音（エンジンなど）の状態：毎フレーム App から設定する ----
    public static VehicleKind Kind;
    public static float Kmh, Throttle, Altitude;
    public static bool OnWater, Active;

    private static AudioStream stream;
    private static short* buf;
    private static readonly Random rng = new(7);
    private static double ph1, ph2, ph3, rotorPh;
    private static float lp1, lp2, lp3, vol;
    private static float clank, hoof, hoofTone;
    private static uint noiseState = 12345;

    public static void Init()
    {
        Raylib.InitAudioDevice();
        if (!Raylib.IsAudioDeviceReady()) return;
        Raylib.SetAudioStreamBufferSizeDefault(Chunk);
        stream = Raylib.LoadAudioStream(Rate, 16, 1);
        buf = (short*)NativeMemory.Alloc(Chunk * sizeof(short));
        Raylib.PlayAudioStream(stream);
        BuildSounds();
        bgm = new[] { MakeBgm(0), MakeBgm(1) };
        Ready = true;
    }

    public static void Shutdown()
    {
        if (!Ready) return;
        Raylib.CloseAudioDevice();
        NativeMemory.Free(buf);
        Ready = false;
    }

    // ================= 効果音 =================

    public static void Play(Sound s, float volume = 1, float pitch = 1)
    {
        if (!Ready || !SfxOn || volume <= 0.01f) return;
        Raylib.SetSoundVolume(s, Math.Clamp(volume, 0, 1));
        Raylib.SetSoundPitch(s, pitch);
        Raylib.PlaySound(s);
    }

    /// <summary>距離（m）で小さくなる音。</summary>
    public static void PlayAt(Sound s, double distance, float volume = 1) =>
        Play(s, volume / (1 + (float)distance / 150f), 0.9f + (float)rng.NextDouble() * 0.2f);

    private static float Noise()
    {
        noiseState ^= noiseState << 13;
        noiseState ^= noiseState >> 17;
        noiseState ^= noiseState << 5;
        return noiseState / (float)uint.MaxValue * 2 - 1;
    }

    private static Sound FromSamples(float[] s, int rate = Rate)
    {
        short* data = (short*)NativeMemory.Alloc((nuint)(s.Length * sizeof(short)));
        float peak = 0.001f;
        foreach (var v in s) peak = Math.Max(peak, Math.Abs(v));
        float gain = Math.Min(1f, 0.95f / peak);
        for (int i = 0; i < s.Length; i++) data[i] = (short)(Math.Clamp(s[i] * gain, -1, 1) * 32000);
        var wave = new Wave { SampleCount = (uint)s.Length, SampleRate = (uint)rate, SampleSize = 16, Channels = 1, Data = data };
        var sound = Raylib.LoadSoundFromWave(wave);
        NativeMemory.Free(data);
        return sound;
    }

    private static float[] Gen(float seconds, Func<float, float> f)
    {
        var s = new float[(int)(seconds * Rate)];
        for (int i = 0; i < s.Length; i++) s[i] = f(i / (float)Rate);
        return s;
    }

    /// <summary>一次のローパスをかけたノイズを作る関数（a が小さいほどこもった音）。</summary>
    private static Func<float> LowNoise(float a)
    {
        float y = 0;
        return () => y += (Noise() - y) * a;
    }

    private static void BuildSounds()
    {
        const float Tau = MathF.PI * 2;

        // 砲撃：低いドーンと乾いた破裂音
        var n1 = LowNoise(0.08f);
        Cannon = FromSamples(Gen(1.6f, t =>
        {
            float freq = 35 + 55 * MathF.Exp(-t * 6);
            return MathF.Sin(Tau * freq * t) * MathF.Exp(-t * 4.5f) * 0.9f
                 + n1() * 3f * MathF.Exp(-t * 3f)
                 + (t < 0.025f ? Noise() * (1 - t / 0.025f) : 0);
        }));

        // 爆発：長い轟音とパチパチ
        var n2 = LowNoise(0.05f);
        Explosion = FromSamples(Gen(2.4f, t =>
            n2() * 4f * MathF.Exp(-t * 1.6f)
            + MathF.Sin(Tau * 42 * t) * MathF.Exp(-t * 2.2f) * 0.7f
            + (Noise() > 0.995f ? Noise() * 0.6f * MathF.Exp(-t) : 0)));

        // 水しぶき：シャーッという高めのノイズ
        var n3 = LowNoise(0.25f);
        Splash = FromSamples(Gen(1.3f, t =>
        {
            float x = Noise() - n3();
            float env = Math.Min(1, t / 0.03f) * MathF.Exp(-t * 3.2f);
            return x * env;
        }));

        // スタンプ：ポンと押す音と鈴の音
        Stamp = FromSamples(Gen(1.4f, t =>
        {
            float thump = MathF.Sin(Tau * 120 * t) * MathF.Exp(-t * 25) * 0.8f;
            float bell = (MathF.Sin(Tau * 1318.5f * t) + 0.5f * MathF.Sin(Tau * 2637 * t)) * MathF.Exp(-t * 4) * 0.4f;
            float t2 = t - 0.14f;
            float bell2 = t2 > 0 ? (MathF.Sin(Tau * 1760 * t2) + 0.4f * MathF.Sin(Tau * 3520 * t2)) * MathF.Exp(-t2 * 3.5f) * 0.45f : 0;
            return thump + bell + bell2;
        }));

        // 衝突：ガシャン
        Crash = FromSamples(Gen(0.9f, t =>
            Noise() * MathF.Exp(-t * 6) * 0.8f
            + (MathF.Sin(Tau * 523 * t) + MathF.Sin(Tau * 1433 * t) + MathF.Sin(Tau * 2210 * t)) * MathF.Exp(-t * 5) * 0.25f));

        // 着地・体当たり：ドスン
        var n4 = LowNoise(0.06f);
        Thud = FromSamples(Gen(0.6f, t => MathF.Sin(Tau * (55 + 30 * MathF.Exp(-t * 10)) * t) * MathF.Exp(-t * 7) + n4() * 2 * MathF.Exp(-t * 9)));

        // ピッ（汎用）
        Beep = FromSamples(Gen(0.12f, t => MathF.Sign(MathF.Sin(Tau * 1200 * t)) * 0.4f * Math.Min(1, (0.12f - t) * 40)));

        // ETC：ピッ・ピッ
        Etc = FromSamples(Gen(0.4f, t =>
        {
            bool on = t < 0.1f || (t > 0.18f && t < 0.28f);
            float f = t < 0.15f ? 1046 : 1568;
            return on ? MathF.Sin(Tau * f * t) * 0.6f : 0;
        }));

        // 馬のいななき（周波数を上下させる）
        Neigh = FromSamples(Gen(1.1f, t =>
        {
            float shape = t < 0.25f ? 500 + 500 * (t / 0.25f) : 1000 - 450 * ((t - 0.25f) / 0.85f);
            float f = shape + MathF.Sin(Tau * 28 * t) * 60;
            float env = Math.Min(1, t / 0.05f) * MathF.Exp(-t * 1.6f);
            ph1 += f / Rate;
            float x = MathF.Sin(Tau * (float)ph1) + 0.5f * MathF.Sin(Tau * 2 * (float)ph1) + 0.25f * MathF.Sin(Tau * 3 * (float)ph1);
            return x * env * 0.5f + Noise() * env * 0.08f;
        }));
        ph1 = 0;

        // サイレン（スピード違反）
        Siren = FromSamples(Gen(1.6f, t =>
        {
            float f = 750 + 250 * MathF.Sin(Tau * 1.5f * t);
            ph2 += f / Rate;
            float tri = 2 * MathF.Abs(2 * ((float)ph2 % 1) - 1) - 1;
            return tri * 0.5f * Math.Min(1, (1.6f - t) * 5);
        }));
        ph2 = 0;

        // ファンファーレ（日本一周達成）
        float[] notes = { 523.3f, 659.3f, 784f, 1046.5f, 784f, 1046.5f };
        float[] starts = { 0, 0.15f, 0.3f, 0.45f, 0.75f, 0.9f };
        Fanfare = FromSamples(Gen(2.2f, t =>
        {
            float sum = 0;
            for (int i = 0; i < notes.Length; i++)
            {
                float lt = t - starts[i];
                if (lt < 0) continue;
                float len = i == notes.Length - 1 ? 1.2f : 0.3f;
                float env = Math.Min(1, lt / 0.01f) * MathF.Exp(-lt * (3 / len));
                sum += (MathF.Sign(MathF.Sin(Tau * notes[i] * lt)) * 0.3f + MathF.Sin(Tau * notes[i] * lt) * 0.5f) * env;
            }
            return sum * 0.5f;
        }));

        // ヒュン（ワープ・乗り換え）
        var n5 = LowNoise(0.3f);
        Whoosh = FromSamples(Gen(0.7f, t =>
        {
            float env = MathF.Sin(MathF.PI * t / 0.7f);
            return (Noise() - n5()) * env * env * 0.8f;
        }));

        // 決定音
        Select = FromSamples(Gen(0.18f, t =>
            MathF.Sin(Tau * (t < 0.07f ? 880 : 1320) * t) * 0.5f * Math.Min(1, (0.18f - t) * 30)));
    }

    // ================= BGM =================

    /// <summary>和風の音階（ヨナ抜き）で自動作曲したBGM。0：ドライブ（軽快）、1：大空（ゆったり）。</summary>
    private static Sound MakeBgm(int style)
    {
        const float Tau = MathF.PI * 2;
        var r = new Random(style == 0 ? 2024 : 777);
        float bpm = style == 0 ? 128 : 84;
        float beat = 60f / bpm;
        int bars = 16;
        float length = bars * 4 * beat;
        var s = new float[(int)(length * BgmRate)];

        // ヨナ抜き長音階（ド レ ミ ソ ラ）
        int[] scale = { 0, 2, 4, 7, 9 };
        // コード進行（ルートの半音）
        int[] prog = style == 0 ? new[] { 0, 9, 5, 7 } : new[] { 0, 5, 9, 7 };
        float baseHz = style == 0 ? 293.66f : 261.63f; // D / C

        float Hz(int semi) => baseHz * MathF.Pow(2, semi / 12f);

        // メロディ：2小節のフレーズを作り、少し変えながら繰り返す
        var phrase = new List<(float Start, float Len, int Semi)>();
        float pos = 0;
        int deg = 2;
        while (pos < 8 * beat - 0.01f)
        {
            float len = beat * (r.NextDouble() < 0.6 ? (style == 0 ? 0.5f : 1f) : (style == 0 ? 1f : 2f));
            deg = Math.Clamp(deg + r.Next(-2, 3), 0, 9);
            int semi = scale[deg % 5] + 12 * (deg / 5);
            if (r.NextDouble() > 0.12) phrase.Add((pos, len * 0.92f, semi));
            pos += len;
        }

        for (int i = 0; i < s.Length; i++)
        {
            float t = i / (float)BgmRate;
            float bt = t / beat;
            int bar = (int)(bt / 4);
            int root = prog[bar % prog.Length];
            float inBeat = bt % 1;
            float v = 0;

            // ベース（ルートと5度）
            int bassSemi = root - 24 + ((int)bt % 2 == 1 ? 7 : 0);
            float bEnv = MathF.Exp(-inBeat * 3.5f);
            v += MathF.Sin(Tau * Hz(bassSemi) * t) * 0.35f * bEnv;

            // 和音（三角波のパッド）
            foreach (int iv in new[] { 0, 4, 7 })
            {
                float ph = Hz(root + iv - 12) * t % 1;
                v += (4 * MathF.Abs(ph - 0.5f) - 1) * (style == 0 ? 0.06f : 0.09f);
            }

            // メロディ（2小節ごとに繰り返し、後半は1オクターブ上）
            float pt = t % (8 * beat);
            int rep = (int)(t / (8 * beat));
            foreach (var (st, ln, semi) in phrase)
            {
                float lt = pt - st;
                if (lt < 0 || lt > ln) continue;
                float hz = Hz(semi + (rep % 4 == 3 ? 12 : 0));
                float env = Math.Min(1, lt / 0.01f) * MathF.Exp(-lt * (style == 0 ? 3f : 1.5f));
                float ph = hz * t % 1;
                float tone = style == 0 ? (ph < 0.5f ? 1 : -1) * 0.5f + MathF.Sin(Tau * hz * t) * 0.5f : MathF.Sin(Tau * hz * t) + 0.3f * MathF.Sin(Tau * 2 * hz * t);
                v += tone * env * 0.16f;
            }

            // リズム（ドライブだけ）
            if (style == 0)
            {
                float eighth = bt * 2 % 1;
                v += Noise() * MathF.Exp(-eighth * 40) * 0.05f;                          // ハイハット
                if ((int)bt % 2 == 0) v += MathF.Sin(Tau * (50 + 80 * MathF.Exp(-inBeat * 30)) * t) * MathF.Exp(-inBeat * 12) * 0.35f; // キック
                else v += Noise() * MathF.Exp(-inBeat * 18) * 0.12f;                     // スネア
            }
            else
            {
                // 大空：ゆっくりしたアルペジオ
                float q = bt * 2;
                int step = (int)q % 4;
                int semi = root + new[] { 0, 7, 12, 16 }[step];
                float lt = q % 1 * beat / 2;
                v += MathF.Sin(Tau * Hz(semi) * t) * MathF.Exp(-lt * 4) * 0.08f;
            }
            s[i] = v;
        }

        // ループのつなぎ目をなめらかに
        int fade = BgmRate / 20;
        for (int i = 0; i < fade; i++)
        {
            float k = i / (float)fade;
            s[i] *= k;
            s[s.Length - 1 - i] *= k;
        }
        return FromSamples(s, BgmRate);
    }

    /// <summary>流すBGMを選ぶ（-1 で止める）。</summary>
    public static void SetBgm(int track)
    {
        if (!Ready) return;
        if (!BgmOn) track = -1;
        if (track != bgmTrack)
        {
            if (bgmTrack >= 0) Raylib.StopSound(bgm[bgmTrack]);
            bgmTrack = track;
        }
        if (bgmTrack >= 0 && !Raylib.IsSoundPlaying(bgm[bgmTrack]))
        {
            Raylib.SetSoundVolume(bgm[bgmTrack], 0.32f);
            Raylib.PlaySound(bgm[bgmTrack]);
        }
    }

    // ================= エンジン音など（連続音） =================

    /// <summary>ひづめ・足音を鳴らす（App が足の動きに合わせて呼ぶ）。</summary>
    public static void Step(bool horse)
    {
        hoof = 1;
        hoofTone = horse ? 1 : 0.6f;
    }

    public static void Update()
    {
        if (!Ready) return;
        while (Raylib.IsAudioStreamProcessed(stream))
        {
            Fill();
            Raylib.UpdateAudioStream(stream, buf, Chunk);
        }
    }

    private static void Fill()
    {
        const float Tau = MathF.PI * 2;
        float target = Active && SfxOn ? 1 : 0;
        float kmh = Math.Abs(Kmh);
        for (int i = 0; i < Chunk; i++)
        {
            vol += (target - vol) * 0.0005f;
            float x = 0;
            switch (Kind)
            {
                case VehicleKind.Tank:
                case VehicleKind.Amphibious:
                {
                    // ディーゼルのうなり＋履帯のガラガラ
                    float f0 = (Kind == VehicleKind.Tank ? 32 : 40) + Math.Min(kmh, 400) * 0.6f + Throttle * 8;
                    ph1 = (ph1 + f0 / Rate) % 1;
                    float saw = (float)ph1 * 2 - 1;
                    lp1 += (saw - lp1) * 0.05f;
                    x += lp1 * 0.9f;
                    float clankRate = Math.Min(kmh, 300) * 0.35f;
                    ph2 += clankRate / Rate;
                    if (ph2 >= 1) { ph2 -= 1; clank = 1; }
                    clank *= 0.9985f;
                    x += Noise() * clank * clank * 0.35f;
                    if (OnWater) { lp2 += (Noise() - lp2) * 0.08f; x += lp2 * Math.Min(1, kmh / 30) * 0.8f; }
                    break;
                }
                case VehicleKind.Horse:
                case VehicleKind.Hikyaku:
                    // ひづめ・足音は Step() で鳴らす
                    break;
                case VehicleKind.Helicopter:
                {
                    // ローターのバタバタ音とタービン音
                    rotorPh = (rotorPh + 11.0 / Rate) % 1;
                    float pulse = MathF.Pow(0.5f + 0.5f * MathF.Cos(Tau * (float)rotorPh), 10);
                    lp1 += (Noise() - lp1) * 0.12f;
                    x += lp1 * (0.35f + pulse * 1.6f);
                    ph1 = (ph1 + (900 + Math.Min(kmh, 500) * 0.4f) / Rate) % 1;
                    x += MathF.Sin(Tau * (float)ph1) * 0.04f;
                    break;
                }
                case VehicleKind.Airplane:
                {
                    // プロペラのブーン
                    float f0 = 70 + Math.Min(kmh, 1200) * 0.12f + Throttle * 20;
                    ph1 = (ph1 + f0 / Rate) % 1;
                    float sq = ph1 < 0.3 ? 1 : -1;
                    lp1 += (sq - lp1) * 0.15f;
                    x += lp1 * 0.55f;
                    break;
                }
                default:
                {
                    // ガソリン車：ギアに合わせて回転数が上下する
                    float gearW = 45;
                    int gear = (int)Math.Min(5, kmh / gearW);
                    float within = (kmh - gear * gearW) / gearW;
                    float rpm = 900 + Math.Clamp(within, 0, 1) * 4300 + Throttle * 300;
                    if (kmh > gearW * 6) rpm = Math.Min(9500, 5200 + (kmh - gearW * 6) * 4);
                    float f0 = rpm / 30;
                    ph1 = (ph1 + f0 / Rate) % 1;
                    ph2 = (ph2 + f0 * 0.5 / Rate) % 1;
                    float saw = (float)ph1 * 2 - 1;
                    float sub = ph2 < 0.5 ? 1 : -1;
                    lp1 += (saw * 0.7f + sub * 0.3f - lp1) * (0.08f + Throttle * 0.08f);
                    x += lp1 * (0.45f + Throttle * 0.35f);
                    break;
                }
            }

            // ひづめ・足音
            if (hoof > 0.001f)
            {
                ph3 = (ph3 + (hoofTone > 0.8f ? 190 : 110) / (double)Rate) % 1;
                x += (MathF.Sin(Tau * (float)ph3) * 0.8f + Noise() * 0.6f) * hoof * hoof * (hoofTone > 0.8f ? 0.9f : 0.5f);
                hoof *= 0.9975f;
            }

            // 風切り音（速いほど・高いほど大きい）
            float wind = Math.Min(1, kmh / 350) * 0.5f + Math.Min(1, Altitude / 2000) * 0.15f;
            lp3 += (Noise() - lp3) * 0.03f;
            x += lp3 * wind * 2.2f;

            buf[i] = (short)(Math.Clamp(x * vol * 0.35f, -1, 1) * 30000);
        }
    }
}
