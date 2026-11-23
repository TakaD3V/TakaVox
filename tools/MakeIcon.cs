// Gera os icones do TakaVox (.ico com varios tamanhos em PNG) a partir da arte em assets/:
//   makeicon.exe <arte1.jpg> <saida1.ico> [<arte2.jpg> <saida2.ico> ...]
// Todas as artes usam o MESMO recorte (o desenho nao "pula" quando o icone troca, ex.: mudo) e cada .ico
// ganha tambem um .png de 256 px ao lado.
// O fundo escuro da arte vira transparente (com bordas suaves), o desenho e recortado e centralizado num quadrado,
// e cada tamanho e reduzido por media de area (nitido ate em 16 px).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

static class MakeIcon
{
    // imagem RGBA em float, cores pre-multiplicadas pelo alfa
    sealed class Img
    {
        public readonly int W, H;
        public readonly float[] R, G, B, A;
        public Img(int w, int h) { W = w; H = h; R = new float[w * h]; G = new float[w * h]; B = new float[w * h]; A = new float[w * h]; }
    }

    static Img Load(string path, out float bgR, out float bgG, out float bgB)
    {
        using (var src = new Bitmap(path))
        {
            int w = src.Width, h = src.Height;
            var data = src.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var px = new int[w * h];
            Marshal.Copy(data.Scan0, px, 0, px.Length);
            src.UnlockBits(data);

            // cor do fundo: mediana da borda
            var rs = new List<int>(); var gs = new List<int>(); var bs = new List<int>();
            int border = Math.Max(4, w / 40);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (x >= border && x < w - border && y >= border && y < h - border) continue;
                    int c = px[y * w + x];
                    rs.Add((c >> 16) & 255); gs.Add((c >> 8) & 255); bs.Add(c & 255);
                }
            rs.Sort(); gs.Sort(); bs.Sort();
            bgR = rs[rs.Count / 2]; bgG = gs[gs.Count / 2]; bgB = bs[bs.Count / 2];

            // ruido do fundo (JPEG): percentil 99,5 da distancia na borda
            float noise = 0;
            for (int y = 0; y < h; y += 2)
                for (int x = 0; x < w; x += 2)
                {
                    if (x >= border && x < w - border && y >= border && y < h - border) continue;
                    int c = px[y * w + x];
                    float dr = ((c >> 16) & 255) - bgR, dg = ((c >> 8) & 255) - bgG, db = (c & 255) - bgB;
                    float d = (float)Math.Sqrt(dr * dr + dg * dg + db * db);
                    if (d > noise) noise = d;
                }
            float lo = Math.Min(Math.Max(noise * 0.8f, 10f), 22f), hi = lo + 26f;

            var img = new Img(w, h);
            for (int i = 0; i < px.Length; i++)
            {
                int c = px[i];
                float r = (c >> 16) & 255, g = (c >> 8) & 255, b = c & 255;
                float dr = r - bgR, dg = g - bgG, db = b - bgB;
                float d = (float)Math.Sqrt(dr * dr + dg * dg + db * db);
                float a = (d - lo) / (hi - lo);
                a = a <= 0 ? 0 : a >= 1 ? 1 : a * a * (3 - 2 * a);
                if (a <= 0) continue;
                // tira o fundo misturado nas bordas: cor = fundo + (pixel - fundo) / alfa
                float fr = Clamp255(bgR + dr / a), fg = Clamp255(bgG + dg / a), fb = Clamp255(bgB + db / a);
                img.A[i] = a; img.R[i] = fr / 255f * a; img.G[i] = fg / 255f * a; img.B[i] = fb / 255f * a;
            }
            Console.WriteLine($"  fundo=({bgR},{bgG},{bgB}) ruido={noise:0.0} limiar={lo:0}..{hi:0}");
            return img;
        }
    }

    static float Clamp255(float v) => v < 0 ? 0 : v > 255 ? 255 : v;

    /// <summary>Caixa do desenho (alfa &gt; 0,5), somada a caixa ja acumulada.</summary>
    static void Bounds(Img s, ref int x0, ref int y0, ref int x1, ref int y1)
    {
        for (int y = 0; y < s.H; y++)
            for (int x = 0; x < s.W; x++)
                if (s.A[y * s.W + x] > 0.5f) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; }
    }

    /// <summary>Recorta a caixa com uma margem e centraliza num quadrado.</summary>
    static Img CropSquare(Img s, float padFrac, int x0, int y0, int x1, int y1)
    {
        int bw = x1 - x0 + 1, bh = y1 - y0 + 1;
        int side = (int)Math.Ceiling(Math.Max(bw, bh) * (1 + 2 * padFrac));
        var o = new Img(side, side);
        int ox = (side - bw) / 2 - x0, oy = (side - bh) / 2 - y0;
        for (int y = 0; y < s.H; y++)
            for (int x = 0; x < s.W; x++)
            {
                int tx = x + ox, ty = y + oy;
                if (tx < 0 || ty < 0 || tx >= side || ty >= side) continue;
                int si = y * s.W + x, ti = ty * side + tx;
                o.R[ti] = s.R[si]; o.G[ti] = s.G[si]; o.B[ti] = s.B[si]; o.A[ti] = s.A[si];
            }
        Console.WriteLine($"  desenho {bw}x{bh} -> quadrado {side}");
        return o;
    }

    /// <summary>Reducao por media de area (separavel), em cores pre-multiplicadas.</summary>
    static Img Resize(Img s, int n)
    {
        var tmp = new Img(n, s.H);
        double sx = (double)s.W / n;
        for (int y = 0; y < s.H; y++)
            for (int x = 0; x < n; x++)
            {
                double a0 = x * sx, a1 = (x + 1) * sx;
                double r = 0, g = 0, b = 0, a = 0;
                for (int k = (int)a0; k < Math.Min(s.W, (int)Math.Ceiling(a1)); k++)
                {
                    double wgt = Math.Min(a1, k + 1) - Math.Max(a0, k);
                    int i = y * s.W + k;
                    r += s.R[i] * wgt; g += s.G[i] * wgt; b += s.B[i] * wgt; a += s.A[i] * wgt;
                }
                int t = y * n + x;
                tmp.R[t] = (float)(r / sx); tmp.G[t] = (float)(g / sx); tmp.B[t] = (float)(b / sx); tmp.A[t] = (float)(a / sx);
            }
        var o = new Img(n, n);
        double sy = (double)s.H / n;
        for (int x = 0; x < n; x++)
            for (int y = 0; y < n; y++)
            {
                double a0 = y * sy, a1 = (y + 1) * sy;
                double r = 0, g = 0, b = 0, a = 0;
                for (int k = (int)a0; k < Math.Min(s.H, (int)Math.Ceiling(a1)); k++)
                {
                    double wgt = Math.Min(a1, k + 1) - Math.Max(a0, k);
                    int i = k * n + x;
                    r += tmp.R[i] * wgt; g += tmp.G[i] * wgt; b += tmp.B[i] * wgt; a += tmp.A[i] * wgt;
                }
                int t = y * n + x;
                o.R[t] = (float)(r / sy); o.G[t] = (float)(g / sy); o.B[t] = (float)(b / sy); o.A[t] = (float)(a / sy);
            }
        // tamanhos pequenos: um pouco de nitidez (sem halo: so no alfa e nas cores ja visiveis)
        if (n <= 48) Sharpen(o, n <= 24 ? 0.55f : 0.35f);
        return o;
    }

    static void Sharpen(Img o, float amt)
    {
        int n = o.W;
        var src = new[] { (float[])o.R.Clone(), (float[])o.G.Clone(), (float[])o.B.Clone(), (float[])o.A.Clone() };
        var dst = new[] { o.R, o.G, o.B, o.A };
        for (int c = 0; c < 4; c++)
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float sum = 0; int cnt = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = x + dx, yy = y + dy;
                            if (xx < 0 || yy < 0 || xx >= n || yy >= n) continue;
                            sum += src[c][yy * n + xx]; cnt++;
                        }
                    float v = src[c][y * n + x];
                    dst[c][y * n + x] = Math.Max(0f, Math.Min(1f, v + amt * (v - sum / cnt)));
                }
        // cor pre-multiplicada nunca acima do alfa
        for (int i = 0; i < o.A.Length; i++)
        {
            float a = o.A[i];
            o.R[i] = Math.Min(o.R[i], a); o.G[i] = Math.Min(o.G[i], a); o.B[i] = Math.Min(o.B[i], a);
        }
    }

    static byte[] Png(Img m)
    {
        using (var bmp = new Bitmap(m.W, m.H, PixelFormat.Format32bppArgb))
        {
            var data = bmp.LockBits(new Rectangle(0, 0, m.W, m.H), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            var px = new int[m.W * m.H];
            for (int i = 0; i < px.Length; i++)
            {
                float a = m.A[i];
                int ai = (int)Math.Round(a * 255);
                if (ai <= 0) { px[i] = 0; continue; }
                int r = (int)Math.Round(Math.Min(1f, m.R[i] / a) * 255), g = (int)Math.Round(Math.Min(1f, m.G[i] / a) * 255), b = (int)Math.Round(Math.Min(1f, m.B[i] / a) * 255);
                px[i] = (ai << 24) | (r << 16) | (g << 8) | b;
            }
            Marshal.Copy(px, 0, data.Scan0, px.Length);
            bmp.UnlockBits(data);
            using (var ms = new MemoryStream()) { bmp.Save(ms, ImageFormat.Png); return ms.ToArray(); }
        }
    }

    static int Main(string[] args)
    {
        if (args.Length < 2 || args.Length % 2 != 0) { Console.WriteLine("uso: makeicon <arte1.jpg> <saida1.ico> [<arte2.jpg> <saida2.ico> ...]"); return 2; }
        var arts = new List<Img>();
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
        for (int i = 0; i < args.Length; i += 2)
        {
            Console.WriteLine("arte: " + args[i]);
            var img = Load(args[i], out _, out _, out _);
            Bounds(img, ref x0, ref y0, ref x1, ref y1);
            arts.Add(img);
        }
        for (int i = 0; i < arts.Count; i++) Write(CropSquare(arts[i], 0.03f, x0, y0, x1, y1), args[2 * i + 1]);
        return 0;
    }

    static void Write(Img art, string icoPath)
    {
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        var images = new List<byte[]>();
        foreach (int s in sizes) images.Add(Png(Resize(art, s)));
        using (var fs = File.Create(icoPath))
        using (var w = new BinaryWriter(fs))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                int s = sizes[i];
                w.Write((byte)(s >= 256 ? 0 : s)); w.Write((byte)(s >= 256 ? 0 : s));
                w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32);
                w.Write(images[i].Length); w.Write(offset);
                offset += images[i].Length;
            }
            foreach (var img in images) w.Write(img);
        }
        File.WriteAllBytes(Path.ChangeExtension(icoPath, ".png"), images[images.Count - 1]);
        Console.WriteLine("  -> " + icoPath);
    }
}
