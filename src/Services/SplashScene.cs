using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ElansAddonHub
{
    // The "lighting the campfire" scene of the splash screen: pure WPF vector art, every frame a function of the time t
    // (seconds since the splash opened) plus one fixed random seed, so a frame can be rendered to a PNG deterministically.
    // Built from code with its own frozen brushes only (no app resources): it also runs on the splash's own UI thread.
    //   0.0-0.5 sky and stars   0.1-0.6 tent rises   0.3-0.55 ember   0.55-1.1 flames grow   0.7-1.3 title   sparks from 0.9
    public sealed class SplashScene : Canvas
    {
        public const double W = 480, H = 300;
        const double FX = 240, FY = 178;          // base of the fire

        // ---------------------------------------------------------------- tiny helpers
        static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
        static SolidColorBrush Br(string hex) { var b = new SolidColorBrush(C(hex)); b.Freeze(); return b; }
        static Geometry G(string d) { var g = Geometry.Parse(d); g.Freeze(); return g; }
        static double Clamp01(double x) => x < 0 ? 0 : x > 1 ? 1 : x;
        static double Smooth(double x) { x = Clamp01(x); return x * x * (3 - 2 * x); }
        static double OutBack(double x) { x = Clamp01(x); var c1 = 1.25; var c3 = c1 + 1; return 1 + c3 * Math.Pow(x - 1, 3) + c1 * Math.Pow(x - 1, 2); }
        static double Lerp(double a, double b, double u) => a + (b - a) * u;
        static Color Mix(Color a, Color b, double u) =>
            Color.FromArgb((byte)Lerp(a.A, b.A, u), (byte)Lerp(a.R, b.R, u), (byte)Lerp(a.G, b.G, u), (byte)Lerp(a.B, b.B, u));

        static LinearGradientBrush Lin(double x0, double y0, double x1, double y1, params (string c, double o)[] stops)
        {
            var b = new LinearGradientBrush { StartPoint = new Point(x0, y0), EndPoint = new Point(x1, y1) };
            foreach (var s in stops) b.GradientStops.Add(new GradientStop(C(s.c), s.o));
            b.Freeze(); return b;
        }
        static RadialGradientBrush Rad(double cx, double cy, double r, params (string c, double o)[] stops)
        {
            var b = new RadialGradientBrush { Center = new Point(cx, cy), GradientOrigin = new Point(cx, cy), RadiusX = r, RadiusY = r };
            foreach (var s in stops) b.GradientStops.Add(new GradientStop(C(s.c), s.o));
            b.Freeze(); return b;
        }
        static T At<T>(T e, double x, double y) where T : UIElement { SetLeft(e, x); SetTop(e, y); return e; }

        // ---------------------------------------------------------------- parts
        readonly Rectangle sky, vignette;
        readonly Ellipse skyGlow, groundGlow, emberCore, emberHalo;
        readonly Path tent, tentLit, door, flag, hillsFar;
        readonly Canvas tentLayer, fire, tentFlag, sparkLayer;
        readonly TranslateTransform tentShift = new TranslateTransform();
        readonly ScaleTransform flagWave = new ScaleTransform(1, 1);
        readonly Ellipse[] stars = new Ellipse[26];
        readonly double[] starPhase = new double[26], starSpeed = new double[26], starBase = new double[26];
        readonly Path[] flames = new Path[5];
        readonly ScaleTransform[] flameScale = new ScaleTransform[5];
        readonly SkewTransform[] flameSkew = new SkewTransform[5];
        readonly double[] flamePhase = { 0.0, 1.9, 4.1, 2.7, 5.3 };
        readonly double[] flameSize = { 1.0, 1.0, 1.0, 0.66, 0.74 };   // outer, mid, inner, left tongue, right tongue
        readonly Path[] logGlow = new Path[2];
        readonly Ellipse[] sparks = new Ellipse[20];
        readonly double[] spLife = new double[20], spStart = new double[20], spX = new double[20], spDrift = new double[20],
                          spRise = new double[20], spSize = new double[20], spWob = new double[20];
        readonly TextBlock title, status;
        readonly Rectangle barFillL, barFillR;
        readonly Canvas titleBox;

        // ---------------------------------------------------------------- status line (real startup steps)
        string statusText = "";
        double statusAt = -10;
        double progFrom, progTo, progAt = -10;

        public SplashScene(string titleText)
        {
            Width = W; Height = H;
            ClipToBounds = true;
            Clip = new RectangleGeometry(new Rect(0, 0, W, H), 18, 18);
            Background = Br("#05080B");
            var rnd = new Random(7);

            // --- night sky and the first stars
            sky = new Rectangle { Width = W, Height = H, Fill = Lin(0, 0, 0, 1, ("#0E2740", 0), ("#0A1727", 0.34), ("#070D17", 0.62), ("#05080B", 1)) };
            Children.Add(sky);
            // a faint colour in the east, left of the tent
            Children.Add(At(new Ellipse { Width = 280, Height = 120, Fill = Rad(0.5, 0.5, 0.5, ("#1A2C6FA8", 0), ("#00000000", 1)) }, -40, 20));
            for (int i = 0; i < stars.Length; i++)
            {
                var x = 10 + rnd.NextDouble() * (W - 20);
                var y = 8 + Math.Pow(rnd.NextDouble(), 1.2) * 118;
                var size = 0.9 + rnd.NextDouble() * (i % 6 == 0 ? 1.6 : 0.9);
                var warm = i % 5 == 0;
                stars[i] = At(new Ellipse { Width = size, Height = size, Fill = Br(warm ? "#FFE9BC" : "#D6EAF5"), Opacity = 0 }, x, y);
                starPhase[i] = rnd.NextDouble() * Math.PI * 2; starSpeed[i] = 0.9 + rnd.NextDouble() * 2.4; starBase[i] = 0.55 + rnd.NextDouble() * 0.45;
                Children.Add(stars[i]);
            }
            // two four-point sparkles like the ones on the hub icon
            Children.Add(new Path { Data = G("M92,34 L93.4,37.6 L97,39 L93.4,40.4 L92,44 L90.6,40.4 L87,39 L90.6,37.6 Z"), Fill = Br("#FFF1CF"), Opacity = 0.85, Tag = "twinkle" });
            Children.Add(new Path { Data = G("M401,52 L402.2,55 L405.2,56.2 L402.2,57.4 L401,60.4 L399.8,57.4 L396.8,56.2 L399.8,55 Z"), Fill = Br("#E6F1F7"), Opacity = 0.8, Tag = "twinkle" });

            // --- far hills with a thin treeline, then the glow that the fire throws up on them
            hillsFar = new Path { Data = G("M0,148 C46,126 96,142 160,134 C226,126 262,112 330,128 C392,142 432,122 480,136 L480,210 L0,210 Z"), Fill = Br("#0D1E2E") };
            Children.Add(hillsFar);
            var pines = new System.Text.StringBuilder();
            foreach (var px in new[] { 22, 36, 58, 392, 410, 430, 452, 468 })
            {
                var by = 138 - (px > 300 ? 0 : 0) + (px % 3) * 1.5; var h = 15 + (px % 7) * 1.4;
                pines.Append(FormattableString.Invariant($"M{px},{by - h} L{px + 5},{by - h * 0.45} L{px + 2.5},{by - h * 0.45} L{px + 7},{by} L{px - 7},{by} L{px - 2.5},{by - h * 0.45} L{px - 5},{by - h * 0.45} Z ")); 
            }
            Children.Add(new Path { Data = G(pines.ToString()), Fill = Br("#0A1824") });
            skyGlow = At(new Ellipse { Width = 440, Height = 280, Opacity = 0, Fill = Rad(0.5, 0.62, 0.5, ("#55FF8A30", 0), ("#22E0601A", 0.45), ("#00E0601A", 1)) }, FX - 220, FY - 190);
            Children.Add(skyGlow);

            // --- near hills / ground
            Children.Add(new Path { Data = G("M0,176 C58,162 120,172 190,168 C270,164 340,158 410,168 C440,172 462,168 480,166 L480,300 L0,300 Z"), Fill = Lin(0, 0, 0, 1, ("#0C1822", 0), ("#070C11", 0.5), ("#05080B", 1)) });
            // a darker ridge of grass tufts along the front edge of the ground
            var tufts = new System.Text.StringBuilder();
            for (int i = 0; i < 24; i++)
            {
                var gx = 6 + i * 20 + rnd.Next(-5, 6); var gy = 172 + Math.Sin(i * 0.8) * 3;
                tufts.Append(FormattableString.Invariant($"M{gx},{gy} L{gx + 1.5},{gy - 5 - rnd.Next(0, 4)} L{gx + 3},{gy} M{gx + 3},{gy} L{gx + 5},{gy - 4 - rnd.Next(0, 3)} L{gx + 6.5},{gy} "));
            }
            Children.Add(new Path { Data = G(tufts.ToString()), Fill = Br("#0A141C") });

            // --- the tent (same family as the hub icon: ridge tent in warm canvas, pennant, glowing doorway)
            tentLayer = new Canvas { Width = W, Height = H, RenderTransform = tentShift, Opacity = 0, IsHitTestVisible = false };
            // guy ropes and pegs
            tentLayer.Children.Add(new Path { Data = G("M178,150 L120,184 M302,150 L360,184 M206,120 L162,176"), Stroke = Br("#2B3340"), StrokeThickness = 0.9, Opacity = 0.9 });
            tentLayer.Children.Add(new Path { Data = G("M118,186 L121.5,181 M358,186 L361.5,181 M160,178 L163.5,173"), Stroke = Br("#3B4350"), StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
            const string tentGeo = "M240,58 L314,178 L166,178 Z";
            tent = new Path { Data = G(tentGeo), Fill = Lin(0, 0, 0, 1, ("#46444F", 0), ("#554A4D", 0.55), ("#66524A", 1)), Stroke = Br("#0D0A0A"), StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
            tentLayer.Children.Add(tent);
            tentLayer.Children.Add(new Path { Data = G("M240,58 L166,178 L196,178 L240,84 Z"), Fill = Br("#55070A10") });                 // shaded west face
            tentLayer.Children.Add(new Path { Data = G("M240,58 L314,178 L290,178 L240,84 Z"), Fill = Br("#18FFE6C2") });                // moonlit east face
            tentLayer.Children.Add(new Path { Data = G("M232,88 L248,88 M222,106 L258,106 M212,124 L268,124"), Stroke = Br("#14000000"), StrokeThickness = 1 }); // canvas seams
            tentLit = new Path { Data = G(tentGeo), Opacity = 0, Fill = new RadialGradientBrush { Center = new Point(0.5, 1.0), GradientOrigin = new Point(0.5, 1.0), RadiusX = 0.7, RadiusY = 0.85,
                GradientStops = { new GradientStop(C("#E8FFB866"), 0), new GradientStop(C("#8CFF8A30"), 0.45), new GradientStop(C("#00FF6A18"), 1) } } };
            tentLayer.Children.Add(tentLit);
            door = new Path { Data = G("M240,104 L264,178 L216,178 Z"), Opacity = 0, Fill = Lin(0, 0, 0, 1, ("#2A0F08", 0), ("#9A3A12", 0.55), ("#FFA544", 1)) };
            tentLayer.Children.Add(new Path { Data = G("M240,104 L264,178 L216,178 Z"), Fill = Br("#120A08") });
            tentLayer.Children.Add(door);
            tentLayer.Children.Add(new Path { Data = G("M240,58 L240,104 M240,104 L216,178 M240,104 L264,178"), Stroke = Br("#120A08"), StrokeThickness = 1.4, StrokeLineJoin = PenLineJoin.Round });
            // pennant
            tentLayer.Children.Add(new Path { Data = G("M240,46 L240,64"), Stroke = Br("#2C2218"), StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round });
            tentFlag = new Canvas { Width = 30, Height = 20, RenderTransform = flagWave };
            flag = new Path { Data = G("M0,0 L25,7 L0,14 Z"), Fill = Br("#F28C34"), Stroke = Br("#3A1A08"), StrokeThickness = 1, StrokeLineJoin = PenLineJoin.Round };
            tentFlag.Children.Add(flag);
            tentFlag.RenderTransformOrigin = new Point(0, 0.5);
            tentLayer.Children.Add(At(tentFlag, 241, 42));
            Children.Add(tentLayer);

            // --- fire light on the ground and the evening air
            groundGlow = At(new Ellipse { Width = 440, Height = 150, Opacity = 0, Fill = Rad(0.5, 0.5, 0.5, ("#CCFFA040", 0), ("#66FF7A20", 0.42), ("#00FF7A20", 1)) }, FX - 220, FY - 70);
            Children.Add(groundGlow);

            // --- the campfire: coal bed, flames, logs, stones
            fire = new Canvas { Width = 0, Height = 0, IsHitTestVisible = false };
            SetLeft(fire, FX); SetTop(fire, FY);
            emberHalo = new Ellipse { Width = 70, Height = 24, Opacity = 0, Fill = Rad(0.5, 0.5, 0.5, ("#99FFB347", 0), ("#44FF6A1A", 0.5), ("#00FF6A1A", 1)) };
            fire.Children.Add(At(emberHalo, -35, -8));
            fire.Children.Add(At(new Ellipse { Width = 64, Height = 14, Fill = Br("#1A0C07") }, -32, -2));
            emberCore = new Ellipse { Width = 44, Height = 9, Opacity = 0, Fill = Rad(0.5, 0.5, 0.5, ("#FFFFD27A", 0), ("#FFFF7A1E", 0.55), ("#00D8300C", 1)) };
            fire.Children.Add(At(emberCore, -22, 0));
            // flames (outer, mid, inner, left tongue, right tongue)
            var geo = new[]
            {
                G("M3,-68 C8,-52 24,-40 24,-20 C24,-6 13,2 0,2 C-13,2 -24,-6 -24,-20 C-24,-33 -15,-38 -11,-54 C-8,-46 -3,-50 3,-68 Z"),
                G("M-1,-48 C4,-37 17,-30 17,-15 C17,-5 9,1 0,1 C-9,1 -17,-5 -17,-15 C-17,-24 -9,-28 -5,-38 C-3,-33 -2,-37 -1,-48 Z"),
                G("M0,-27 C3,-20 10,-16 10,-8 C10,-2 5,1 0,1 C-5,1 -10,-2 -10,-8 C-10,-14 -4,-17 0,-27 Z"),
                G("M-2,-40 C3,-30 12,-24 12,-11 C12,-3 6,2 0,2 C-6,2 -12,-3 -12,-11 C-12,-20 -4,-24 -2,-40 Z"),
                G("M2,-38 C6,-29 12,-23 12,-11 C12,-3 6,2 0,2 C-6,2 -12,-3 -12,-11 C-12,-19 -2,-24 2,-38 Z"),
            };
            var fills = new Brush[]
            {
                Lin(0, 0, 0, 1, ("#E5C23010", 0), ("#F0F2561A", 0.45), ("#FFFF8E22", 1)),
                Lin(0, 0, 0, 1, ("#E6FF8A1E", 0), ("#F4FFAE35", 0.5), ("#FFFFC24A", 1)),
                Lin(0, 0, 0, 1, ("#EEFFEFA0", 0), ("#FAFFF2B8", 0.5), ("#FFFFFBE0", 1)),
                Lin(0, 0, 0, 1, ("#C8C8300F", 0), ("#E0F2561A", 0.5), ("#F5FF8E22", 1)),
                Lin(0, 0, 0, 1, ("#C8C8300F", 0), ("#E0F2561A", 0.5), ("#F5FF8E22", 1)),
            };
            var fx = new[] { 0.0, 0.0, 0.0, -19.0, 18.0 };
            var fy = new[] { 0.0, 0.0, 1.0, 1.0, 1.0 };
            for (int i = 0; i < 5; i++)
            {
                flameScale[i] = new ScaleTransform(0, 0);
                flameSkew[i] = new SkewTransform(0, 0);
                var tg = new TransformGroup(); tg.Children.Add(flameScale[i]); tg.Children.Add(flameSkew[i]);
                flames[i] = new Path { Data = geo[i], Fill = fills[i], RenderTransform = tg, RenderTransformOrigin = new Point(0, 0), Opacity = 0 };
                if (i >= 3) flames[i].RenderTransform = new TransformGroup { Children = { flameScale[i], flameSkew[i], new RotateTransform(i == 3 ? -9 : 10) } };
                fire.Children.Add(At(flames[i], fx[i], fy[i]));
            }
            // logs: two crossed, glowing along their upper edge once the fire is lit
            fire.Children.Add(new Path { Data = G("M-40,12 L34,-2 L38,6 L-36,20 Z"), Fill = Lin(0, 0, 0, 1, ("#6E4524", 0), ("#3A2312", 1)), Stroke = Br("#1A0E07"), StrokeThickness = 1.4, StrokeLineJoin = PenLineJoin.Round });
            fire.Children.Add(new Path { Data = G("M40,12 L-34,-2 L-38,6 L36,20 Z"), Fill = Lin(0, 0, 0, 1, ("#7A4E2A", 0), ("#42281A", 1)), Stroke = Br("#1A0E07"), StrokeThickness = 1.4, StrokeLineJoin = PenLineJoin.Round });
            logGlow[0] = new Path { Data = G("M-39,12.5 L33,-1.2"), Stroke = Br("#FFC070"), StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Opacity = 0 };
            logGlow[1] = new Path { Data = G("M39,12.5 L-33,-1.2"), Stroke = Br("#FFA24A"), StrokeThickness = 1.4, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Opacity = 0 };
            fire.Children.Add(logGlow[0]); fire.Children.Add(logGlow[1]);
            fire.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = Br("#2A1A10"), Stroke = Br("#120A06"), StrokeThickness = 1, Tag = null }.Place(-31.5, 5));
            fire.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = Br("#2A1A10"), Stroke = Br("#120A06"), StrokeThickness = 1 }.Place(30, 5));
            // stones ring (front)
            double[] sx = { -46, -36, -22, -8, 8, 22, 36, 46 }; double[] sy = { 11, 17, 21, 23, 23, 21, 17, 11 };
            for (int i = 0; i < sx.Length; i++)
                fire.Children.Add(new Ellipse { Width = 9 + (i % 3) * 2, Height = 6 + (i % 2) * 2, Fill = Lin(0, 0, 0, 1, ("#4A4F57", 0), ("#22262B", 1)), Stroke = Br("#0E1114"), StrokeThickness = 1 }.Place(sx[i] - 6, sy[i] - 3));
            Children.Add(fire);

            // --- sparks (pooled)
            sparkLayer = new Canvas { Width = W, Height = H, IsHitTestVisible = false };
            for (int i = 0; i < sparks.Length; i++)
            {
                spLife[i] = 1.3 + rnd.NextDouble() * 1.4; spStart[i] = 0.85 + rnd.NextDouble() * 1.2; spX[i] = (rnd.NextDouble() - 0.5) * 34;
                spDrift[i] = (rnd.NextDouble() - 0.35) * 46; spRise[i] = 70 + rnd.NextDouble() * 80; spSize[i] = 1.7 + rnd.NextDouble() * 2.0; spWob[i] = rnd.NextDouble() * 6.28;
                sparks[i] = new Ellipse { Width = spSize[i], Height = spSize[i], Opacity = 0 };
                sparkLayer.Children.Add(sparks[i]);
            }
            Children.Add(sparkLayer);

            // --- vignette
            vignette = new Rectangle { Width = W, Height = H, IsHitTestVisible = false, Fill = new RadialGradientBrush { Center = new Point(0.5, 0.55), GradientOrigin = new Point(0.5, 0.55), RadiusX = 0.78, RadiusY = 0.78,
                GradientStops = { new GradientStop(C("#00000000"), 0.55), new GradientStop(C("#8A000000"), 1) } } };
            Children.Add(vignette);

            // --- the name and the status line
            titleBox = new Canvas { Width = W, Height = 120, Opacity = 0 };
            title = new TextBlock
            {
                Text = titleText, Width = W, TextAlignment = TextAlignment.Center, FontSize = 35, FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Constantia, Palatino Linotype, Georgia, Segoe UI"),
                Foreground = Lin(0, 0, 0, 1, ("#FFF7E6", 0), ("#F6DCA8", 0.6), ("#E4B26A", 1)),
            };
            TextOptions.SetTextFormattingMode(title, TextFormattingMode.Ideal);
            titleBox.Children.Add(At(title, 0, 0));
            // little ornament under the name: a hairline with a diamond
            titleBox.Children.Add(new Path { Data = G("M164,47 L228,47 M252,47 L316,47"), Stroke = Br("#2EE4B26A"), StrokeThickness = 1 });
            barFillL = At(new Rectangle { Width = 0, Height = 1, Fill = Lin(1, 0, 0, 0, ("#E4B26A", 0), ("#00E4B26A", 1)) }, 228, 46.5);
            barFillR = At(new Rectangle { Width = 0, Height = 1, Fill = Lin(0, 0, 1, 0, ("#E4B26A", 0), ("#00E4B26A", 1)) }, 252, 46.5);
            titleBox.Children.Add(barFillL); titleBox.Children.Add(barFillR);
            titleBox.Children.Add(new Path { Data = G("M240,43 L244,47 L240,51 L236,47 Z"), Fill = Br("#E4B26A") });
            status = new TextBlock { Width = W, TextAlignment = TextAlignment.Center, FontSize = 12, FontFamily = new FontFamily("Segoe UI"), Foreground = Br("#9AA3AD"), Opacity = 0 };
            titleBox.Children.Add(At(status, 0, 56));
            Children.Add(At(titleBox, 0, 214));
            // hairline frame on top (the window edge)
            Children.Add(new Rectangle { Width = W - 1, Height = H - 1, RadiusX = 17.5, RadiusY = 17.5, Stroke = Br("#33FFFFFF"), StrokeThickness = 1, IsHitTestVisible = false }.Place(0.5, 0.5));
            Update(0);
        }

        /// <summary>Show a startup step; the progress hairline moves to <paramref name="progress"/> (0..1).</summary>
        public void SetStatus(string text, double t, double progress)
        {
            statusText = text; statusAt = t;
            progFrom = ProgressNow(t); progTo = progress; progAt = t;
        }
        double ProgressNow(double t) => Lerp(progFrom, progTo, Smooth((t - progAt) / 0.45));

        // ---------------------------------------------------------------- the frame at time t
        public void Update(double t)
        {
            var sk = Smooth(t / 0.45);
            sky.Opacity = Math.Max(sk, 0.0);
            for (int i = 0; i < stars.Length; i++)
                stars[i].Opacity = sk * starBase[i] * (0.45 + 0.55 * (0.5 + 0.5 * Math.Sin(t * starSpeed[i] + starPhase[i])));
            foreach (var ch in Children) if (ch is Path p && "twinkle".Equals(p.Tag)) p.Opacity = sk * (0.55 + 0.45 * Math.Sin(t * 1.7 + p.Data.Bounds.X));
            hillsFar.Opacity = sk;

            var tp = Smooth((t - 0.1) / 0.5);
            tentLayer.Opacity = tp; tentShift.Y = -12 + (1 - tp) * 18;
            flagWave.ScaleX = 0.94 + 0.06 * Math.Sin(t * 5.2); flagWave.ScaleY = 1 + 0.05 * Math.Sin(t * 4.1 + 1);

            var em = Smooth((t - 0.3) / 0.28);               // ember
            var fl = Clamp01((t - 0.55) / 0.55);              // flames growing
            var grow = OutBack(fl);
            var inten = Math.Max(em * 0.3 * (0.85 + 0.15 * Math.Sin(t * 6)), Smooth(fl));
            var flick = 0.86 + 0.08 * Math.Sin(t * 11.3) + 0.06 * Math.Sin(t * 23.1 + 1.3);

            emberCore.Opacity = em * (0.75 + 0.25 * Math.Sin(t * 5.5));
            emberHalo.Opacity = em * (1 - 0.5 * Smooth(fl)) * (0.8 + 0.2 * Math.Sin(t * 5.5 + 1));
            for (int i = 0; i < 5; i++)
            {
                var ph = flamePhase[i]; var size = flameSize[i];
                var gi = i == 0 ? grow : OutBack(Clamp01((t - 0.55 - 0.04 * i) / 0.55));
                var ry = 1 + 0.08 * Math.Sin(t * 9 + ph) + 0.05 * Math.Sin(t * 17.3 + ph * 2) + 0.03 * Math.Sin(t * 29 + ph * 3);
                var rx = 1 + 0.06 * Math.Sin(t * 7.3 + ph) + 0.03 * Math.Sin(t * 13.7 + ph);
                var s = (i < 3 ? 1.0 : 1.0) * gi;
                var k = size;
                flameScale[i].ScaleX = rx * s * k * 1.2;
                flameScale[i].ScaleY = ry * s * k * 1.08;
                flameSkew[i].AngleX = (6 * Math.Sin(t * 5.1 + ph) + 3.5 * Math.Sin(t * 11.3 + ph * 1.7)) * (i == 2 ? 0.6 : 1);
                flames[i].Opacity = Clamp01(gi * 3);
            }
            logGlow[0].Opacity = Smooth(fl) * (0.55 + 0.45 * flick); logGlow[1].Opacity = Smooth(fl) * (0.45 + 0.4 * flick);

            var g = inten * flick;
            groundGlow.Opacity = Clamp01(g); groundGlow.RenderTransform = new ScaleTransform(0.85 + 0.15 * inten + 0.015 * Math.Sin(t * 9), 0.9 + 0.1 * inten) { CenterX = 220, CenterY = 70 };
            skyGlow.Opacity = Clamp01(inten * (0.75 + 0.25 * flick));
            tentLit.Opacity = Clamp01(inten * (0.55 + 0.35 * flick));
            door.Opacity = Clamp01(inten * (0.7 + 0.3 * flick));
            vignette.Opacity = 1;

            // sparks
            for (int i = 0; i < sparks.Length; i++)
            {
                var age = t - spStart[i];
                if (age < 0 || fl < 0.75) { sparks[i].Opacity = 0; continue; }
                var u = (age % spLife[i]) / spLife[i];
                var x = FX + spX[i] * 0.5 + spDrift[i] * u + Math.Sin(u * 7 + spWob[i]) * 4 * u;
                var y = FY - 24 - spRise[i] * Math.Pow(u, 0.82);
                SetLeft(sparks[i], x); SetTop(sparks[i], y);
                sparks[i].Fill = new SolidColorBrush(Mix(C("#FFF2B0"), C("#FF6A1E"), u));
                sparks[i].Opacity = Math.Pow(1 - u, 1.25) * Math.Min(1, u / 0.06) * (0.65 + 0.35 * Math.Sin(t * 20 + i));
                var sz = spSize[i] * (1 - 0.45 * u); sparks[i].Width = sz; sparks[i].Height = sz;
            }

            // title + status
            var tt = Smooth((t - 0.7) / 0.6);
            titleBox.Opacity = tt; SetTop(titleBox, 214 + (1 - tt) * 10);
            title.Foreground = title.Foreground; // (frozen brush: nothing to refresh)
            status.Text = statusText;
            status.Opacity = Smooth((t - statusAt) / 0.2) * Smooth((t - 0.45) / 0.3);
            var pr = ProgressNow(t) * 64;   // the hairlines beside the diamond fill outwards with the real startup steps
            barFillL.Width = pr; SetLeft(barFillL, 228 - pr); barFillR.Width = pr;
        }
    }

    static class SplashExt
    {
        public static T Place<T>(this T e, double x, double y) where T : UIElement { Canvas.SetLeft(e, x); Canvas.SetTop(e, y); return e; }
    }
}
