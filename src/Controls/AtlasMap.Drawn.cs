using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ElansAddonHub.Services.DungeonMaps;
using ElansAddonHub.Services.Maps;

namespace ElansAddonHub
{
    // The Atlas map, part 2: an instance the game ships no map art for, drawn from the player's own game files (Services/DungeonMaps). The first look starts
    // DungeonPlans.Request: the stage shows the calm build stack (glyph, one Body line "Drawing Deadmines from your game files: 120 / 300 parts", the estimate
    // in Secondary, a 320 wide ThinProgress), or for a big one the estimate and a "Draw the map" Button.Primary.M; then the plan: its floors in the toolbar's
    // Segment, the floor picture fitted like any map, and a quiet caption bottom-left ("Drawn from your game files", or "Minimap from your game files" for the
    // Outside picture of a map whose terrain is part of the instance). A drawn floor is a role picture coloured here from the theme's tokens, so it follows the
    // theme. When no plan can be drawn the card says what it always said: "<name> has no map in the game files".
    public sealed partial class AtlasMap
    {
        int drawnMap, drawnFloor = -1;
        DungeonPlan plan;
        InstanceInfo drawnInst;
        public event Action<int> DrawnFloorChanged;

        public DungeonPlan DrawnPlan => drawnMap != 0 ? plan : null;          // the plan shown (null = a game map or nothing)
        public int DrawnFloor => drawnMap != 0 && plan != null ? drawnFloor : -1;
        public int DrawnMapForTest => drawnMap;
        public string CaptionForTest => caption.Visibility == Visibility.Visible ? captionText.Text : null;
        public bool ButtonShownForTest => stateButton.Visibility == Visibility.Visible;
        public string ButtonTextForTest => stateButton.Content as string;
        public double ProgressForTest => stateBar.Visibility == Visibility.Visible ? stateBar.Value : -1;
        public void ClickStateButtonForTest() => stateButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        readonly ProgressBar stateBar = new ProgressBar { Width = 320, Height = 4, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Center };
        readonly Button stateButton = new Button { Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Center, Content = "Draw the map" };
        readonly Border caption = new Border { Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(12), Padding = new Thickness(8, 2, 8, 2), CornerRadius = new CornerRadius(12), IsHitTestVisible = false };
        readonly TextBlock captionText = new TextBlock();

        void BuildDrawnParts()
        {
            StyleOf(stateBar, "ThinProgress");
            StyleOf(stateButton, "Button.Primary.M");
            stateButton.Click += (s, e) => { if (drawnMap != 0) { var m = drawnMap; DungeonPlans.Start(m); UpdateDrawn(); } };
            stateBox.Children.Add(stateBar); stateBox.Children.Add(stateButton);
            StyleOf(captionText, "Text.Caption");
            caption.SetResourceReference(Border.BackgroundProperty, "Brush.Surface2");
            caption.Child = captionText;
            stage.Children.Add(caption);
        }

        void LeaveDrawn() { drawnMap = 0; drawnInst = null; plan = null; drawnFloor = -1; caption.Visibility = Visibility.Collapsed; }

        void ShowDrawn(InstanceInfo inst)
        {
            ++gen;
            ClearPicture();
            UiMapId = 0;
            SwitchMarkers(0);
            LeaveDrawn();
            drawnMap = inst.MapId; drawnInst = inst;
            DungeonPlans.Changed -= OnPlansChanged; DungeonPlans.Changed += OnPlansChanged;     // also before the control is loaded the first time
            BuildChrome(MapData.Index, 0, inst);
            DungeonPlans.Request(inst.MapId);
            UpdateDrawn();
            MapChanged?.Invoke(0);
        }

        void ShowDrawnAgain()
        {
            var inst = drawnInst; int floor = drawnFloor;
            if (inst == null) return;
            ShowDrawn(inst);
            if (plan != null && floor > 0 && floor < plan.Floors.Count) SelectDrawnFloor(floor);
        }

        void OnPlansChanged(int mapId)
        {
            Dispatcher.BeginInvoke(new Action(() => { if (drawnMap == mapId && UiMapId == 0) UpdateDrawn(); }));
        }

        // the card follows the plan's state
        void UpdateDrawn()
        {
            if (drawnMap == 0) return;
            var st = DungeonPlans.StateOf(drawnMap);
            var name = drawnInst?.Name ?? st?.Name ?? "";
            if (st == null) { SetState(ViewState.Drawing, "Reading " + name + " from your game files", ""); return; }
            switch (st.Phase)
            {
                case "done":
                    if (plan == null && st.Plan != null) ApplyPlan(st.Plan);
                    return;
                case "ask":
                    SetState(ViewState.Ask, st.Sentence, "The game has no map of this " + (drawnInst?.Kind ?? "instance").ToLowerInvariant() + ". The Outpost can draw a plan from your game files: " + st.EstimateText + ". Nothing is uploaded and nothing is written to the WoW folder.");
                    stateButton.Content = "Draw the map";
                    stateButton.Visibility = Visibility.Visible; stateBox.IsHitTestVisible = true;
                    return;
                case "failed":
                case "unavailable":
                    NoArtText(st.Phase == "failed" && !string.IsNullOrEmpty(st.Error) ? " A plan could not be drawn from your game files (" + st.Error.TrimEnd('.') + ")." : "");
                    return;
                default:
                    SetState(ViewState.Drawing, st.Sentence.Length > 0 ? st.Sentence : "Reading " + name + " from your game files", st.Phase == "drawing" ? "The first time only: " + st.EstimateText + "." : "");
                    stateBar.Visibility = Visibility.Visible;
                    stateBar.IsIndeterminate = st.Total <= 0;
                    stateBar.Value = st.Total <= 0 ? 0 : Math.Min(1, (double)st.Done / st.Total);
                    return;
            }
        }

        // what the card said before plans existed (also the fallback)
        void NoArtText(string extra)
        {
            var inst = drawnInst;
            SetState(ViewState.NoArt, inst == null ? "This map is not in the game files" : inst.Name + " has no map in the game files", (inst == null ? "" : "The game does not ship a picture of this " + inst.Kind.ToLowerInvariant() + ".") + extra);
        }

        void ApplyPlan(DungeonPlan p)
        {
            plan = p;
            // the floors in the toolbar's segment
            floorPanel.Children.Clear(); floorNames.Clear(); floorButtons.Clear();
            if (p.Floors.Count > 1)
            {
                for (int i = 0; i < p.Floors.Count; i++)
                {
                    int fi = i;
                    var rb = new RadioButton { Content = p.Floors[i].Name, GroupName = group, IsChecked = i == 0 };
                    StyleOf(rb, "Segment");
                    rb.Checked += (s, e) => { if (fi != drawnFloor) SelectDrawnFloor(fi); };
                    floorPanel.Children.Add(rb); floorNames.Add(p.Floors[i].Name); floorButtons.Add(rb);
                }
                floorHost.Visibility = Visibility.Visible;
            }
            else floorHost.Visibility = Visibility.Collapsed;
            SelectDrawnFloor(0);
        }

        public void SelectDrawnFloor(int i)
        {
            if (plan == null || i < 0 || i >= plan.Floors.Count) return;
            int my = ++gen;
            drawnFloor = i; floorSelected = i;
            for (int k = 0; k < floorButtons.Count; k++) if (floorButtons[k].IsChecked != (k == i)) floorButtons[k].IsChecked = k == i;
            var f = plan.Floors[i];
            var path = plan.FloorPath(i);
            var colors = f.IsPicture ? null : ThemeColors();
            if (State != ViewState.Ready) SetState(ViewState.Loading, "", "");
            Task.Run(() =>
            {
                if (f.IsPicture) return MapRender.Load(path);
                var px = DungeonPlanImage.ReadPng(path, out var w, out var h);
                return px == null ? null : (ImageSource)DungeonPlanImage.Colour(px, w, h, colors);
            }).ContinueWith(t =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (my != gen) return;
                    if (t.IsFaulted || t.Result == null) { NoArtText(" The drawn plan could not be read."); return; }
                    ApplyPicture(t.Result, true);
                    captionText.Text = f.IsPicture ? "Minimap from your game files" : "Drawn from your game files";
                    caption.Visibility = Visibility.Visible;
                    DrawnFloorChanged?.Invoke(i);
                }));
            });
        }

        // the drawn plan's colours from the theme (the UI thread reads the resources)
        public static DungeonPlanImage.Colors ThemeColors()
        {
            Color C(string key, Color fallback) => (Res<Brush>(key) as SolidColorBrush)?.Color ?? fallback;
            Color Mix(Color a, Color b, double t) => Color.FromArgb((byte)(a.A + (b.A - a.A) * t), (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
            var s3 = C("Brush.Surface3", Color.FromRgb(0x26, 0x2B, 0x30));
            var s1 = C("Brush.Surface1", Color.FromRgb(0x16, 0x19, 0x1C));
            var bg = C("Brush.Bg", Color.FromRgb(0x0F, 0x11, 0x13));
            var t2 = C("Brush.Text.Secondary", Color.FromRgb(0x8A, 0x91, 0x99));
            var t3 = C("Brush.Text.Tertiary", Color.FromRgb(0x6F, 0x76, 0x7E));
            var high = Mix(s3, t2, 0.3);
            return new DungeonPlanImage.Colors { FloorLow = s3, FloorHigh = high, Ghost = Mix(bg, s1, 0.9), Edge = t2, Ledge = Mix(Mix(s3, high, 0.5), t3, 0.6) };
        }
    }
}
